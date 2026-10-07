const state = {
  session: null,
  cacheCase: null,
  rawTrace: null,
  selectedCaseId: null,
  currentTimeMs: 0,
  activeTab: "trace",
  mode: "replay",
  playing: false,
  busy: false,
  timerId: null,
  stepEditor: null,
  assertionEditor: null,
  lastReplayTask: null,
  caseList: [],
  replayAgentAvailable: false,
  replayAssertionAvailable: false,
  replayAgentStatus: "未检测到 Agent",
  generatedQta: null,
  codegenStatus: null,
};

const elements = Object.fromEntries(
  [
    "phoneFrame",
    "phoneShell",
    "phoneBusy",
    "phoneHint",
    "targetBox",
    "currentActionTitle",
    "currentActionMeta",
    "playButton",
    "seekBar",
    "currentTime",
    "totalTime",
    "timeline",
    "actionCounter",
    "summaryCard",
    "dataViewer",
    "downloadButton",
    "replayReportButton",
    "sourceBadge",
    "captureSourceNote",
    "sessionStatus",
    "sessionTitle",
    "caseSelect",
    "caseSearch",
    "loadCaseButton",
    "renameCaseButton",
    "deleteCaseButton",
    "operationMessage",
    "replayCaseButton",
    "restartBeforeReplay",
    "flowCapture",
    "flowTrace",
    "flowCache",
    "flowQta",
    "codegenToolbar",
    "codegenStatus",
    "codegenOwner",
    "generateQtaButton",
    "copyQtaButton",
    "addStepButton",
    "stepDialog",
    "stepForm",
    "stepDialogTitle",
    "closeStepDialogButton",
    "cancelStepButton",
    "stepType",
    "stepLabel",
    "stepValue",
    "stepConditionType",
    "stepConditionTypeField",
    "stepSkipCount",
    "stepSkipCountField",
    "stepFalseCount",
    "stepFalseCountField",
    "stepTimeout",
    "stepTimeoutField",
    "stepTargetRole",
    "stepLocatorType",
    "stepLocatorValue",
    "stepX",
    "stepY",
    "stepEndX",
    "stepEndXField",
    "stepEndY",
    "stepEndYField",
    "stepDuration",
    "stepDurationField",
    "stepWheelDelta",
    "stepWheelDeltaField",
    "stepValueField",
    "stepTargetField",
    "stepLocatorTypeField",
    "stepLocatorValueField",
    "stepXField",
    "stepYField",
    "assertionDialog",
    "assertionForm",
    "assertionDialogTitle",
    "closeAssertionDialogButton",
    "cancelAssertionButton",
    "assertionType",
    "assertionLabel",
    "assertionScope",
    "assertionTargetRole",
    "assertionLocatorType",
    "assertionLocatorValue",
    "assertionProperty",
    "assertionExpected",
    "assertionTargetRoleField",
    "assertionLocatorTypeField",
    "assertionLocatorValueField",
    "assertionPropertyField",
    "assertionExpectedField",
    "assertionMinimum",
    "assertionMinimumField",
    "assertionTimeout",
    "assertionTimeoutField",
    "assertionRows",
    "assertionRowsField",
    "assertionColumns",
    "assertionColumnsField",
    "assertionControlType",
    "assertionControlTypeField",
  ].map((id) => [id, document.getElementById(id)]),
);
elements.tabs = Array.from(document.querySelectorAll(".tab"));

async function apiRequest(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: options.body ? { "Content-Type": "application/json" } : undefined,
  });
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.error || `请求失败：${response.status}`);
  }
  return payload;
}

function setMessage(message, isError = false) {
  elements.operationMessage.textContent = message;
  elements.operationMessage.classList.toggle("error", isError);
}

function setBusy(busy) {
  state.busy = busy;
  elements.phoneBusy.hidden = !busy;
  updateControlState();
}

async function loadCaseList(preferredCaseId) {
  const [payload] = await Promise.all([
    apiRequest("/api/cases"),
    refreshReplayStatus(),
    refreshCodegenStatus(),
  ]);
  state.caseList = payload.cases || [];
  const query = elements.caseSearch.value.trim().toLocaleLowerCase();
  const visibleCases = state.caseList.filter((item) =>
    !query || item.name.toLocaleLowerCase().includes(query) || item.id.toLocaleLowerCase().includes(query));
  elements.caseSelect.innerHTML = visibleCases
    .map(
      (item) =>
        `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)} · ${item.actionCount} 步</option>`,
    )
    .join("");

  if (preferredCaseId && visibleCases.some((item) => item.id === preferredCaseId)) {
    elements.caseSelect.value = preferredCaseId;
  }

  if (!state.session && visibleCases.length > 0) {
    await loadCase(elements.caseSelect.value);
  }
}

async function refreshCodegenStatus() {
  try {
    state.codegenStatus = await apiRequest("/api/codegen/status");
  } catch {
    state.codegenStatus = { configured: false, model: "未知", baseUrl: "", skill: "" };
  }
  if (elements.codegenStatus) {
    elements.codegenStatus.textContent = state.codegenStatus.configured
      ? `${state.codegenStatus.model} · ${state.codegenStatus.apiStyle} · ${state.codegenStatus.skill}`
      : "API 未配置：请填写 config.local.json 后重启服务";
  }
  updateControlState();
}

async function renameCurrentCase() {
  const caseId = elements.caseSelect.value;
  if (!caseId) return;
  const current = state.caseList.find((item) => item.id === caseId);
  const name = window.prompt("请输入新的用例名称", current?.name || "");
  if (name === null || name.trim() === current?.name) return;
  setBusy(true);
  try {
    await apiRequest(`/api/cases/${encodeURIComponent(caseId)}`, {
      method: "PATCH",
      body: JSON.stringify({ name: name.trim() }),
    });
    elements.caseSearch.value = "";
    await loadCaseList(caseId);
    await loadCase(caseId);
    setMessage(`用例已重命名为“${name.trim()}”。`);
  } finally {
    setBusy(false);
  }
}

async function deleteCurrentCase() {
  const caseId = elements.caseSelect.value;
  if (!caseId) return;
  const current = state.caseList.find((item) => item.id === caseId);
  if (!window.confirm(`确定删除用例“${current?.name || caseId}”吗？删除后会移入可恢复回收目录。`)) return;
  setBusy(true);
  try {
    const result = await apiRequest(`/api/cases/${encodeURIComponent(caseId)}`, { method: "DELETE" });
    if (state.selectedCaseId === caseId) {
      state.session = null;
      state.cacheCase = null;
      state.rawTrace = null;
      state.selectedCaseId = null;
      state.lastReplayTask = null;
    }
    elements.caseSearch.value = "";
    await loadCaseList();
    setMessage(`用例已删除，可从 ${result.recoverablePath} 恢复。`);
  } finally {
    setBusy(false);
  }
}

async function refreshReplayStatus() {
  try {
    const payload = await apiRequest("/api/replay/status");
    state.replayAgentAvailable = Boolean(payload.available);
    state.replayAssertionAvailable = Boolean(payload.assertionAvailable);
    const onlineAgent = payload.agents?.find((agent) => agent.online);
    state.replayAgentStatus = onlineAgent
      ? onlineAgent.compatible
        ? `${onlineAgent.version} / ${onlineAgent.buildConfiguration} / ${onlineAgent.gitCommit || "unknown"}`
        : onlineAgent.compatibilityReason
      : "未检测到 Agent";
  } catch {
    state.replayAgentAvailable = false;
    state.replayAssertionAvailable = false;
    state.replayAgentStatus = "管理服务不可用";
  }
  updateControlState();
}

async function loadCase(caseId) {
  if (!caseId) {
    return;
  }
  pause(false);
  setBusy(true);
  try {
    const payload = await apiRequest(`/api/cases/${encodeURIComponent(caseId)}`);
    state.session = payload.session;
    state.cacheCase = payload.cacheCase;
    state.rawTrace = payload.rawTrace;
    state.generatedQta = payload.generatedQta || null;
    state.selectedCaseId = caseId;
    state.lastReplayTask = null;
    state.mode = "replay";
    state.currentTimeMs = 0;
    setMessage(`已加载：${state.session.case_name}`);
    renderTimeline();
    render();
  } finally {
    setBusy(false);
  }
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function formatTime(timeMs) {
  const totalSeconds = Math.max(0, Number(timeMs) || 0) / 1000;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = (totalSeconds % 60).toFixed(1).padStart(4, "0");
  return `${String(minutes).padStart(2, "0")}:${seconds}`;
}

function findActiveFrame() {
  if (!state.session?.frames?.length) {
    return null;
  }
  if (state.mode === "recording") {
    return state.session.frames.at(-1);
  }
  const activeAction = findActiveAction();
  if (!activeAction) {
    return state.session.frames[0];
  }
  return (
    state.session.frames.find((frame) => frame.id === activeAction.frame_after) ||
    state.session.frames[0]
  );
}

function findActiveAction() {
  if (!state.session?.actions?.length) {
    return null;
  }
  if (state.mode === "recording") {
    return state.session.actions.at(-1);
  }
  const pastActions = state.session.actions.filter(
    (action) => action.timestamp_ms <= state.currentTimeMs,
  );
  return pastActions.at(-1) || null;
}

function findCacheStep(activeAction) {
  if (!activeAction?.cache_step_id || !state.cacheCase) {
    return null;
  }
  return state.cacheCase.steps.find((step) => step.id === activeAction.cache_step_id) || null;
}

function render() {
  if (!state.session) {
    elements.dataViewer.textContent = "暂无用例数据";
    return;
  }

  const activeFrame = findActiveFrame();
  const activeAction = findActiveAction();
  const activeCacheStep = findCacheStep(activeAction);

  if (activeFrame) {
    const sourceKey = `frame:${activeFrame.src}:${activeFrame.timestamp_ms}`;
    if (elements.phoneFrame.dataset.sourceKey !== sourceKey) {
      elements.phoneFrame.dataset.sourceKey = sourceKey;
      elements.phoneFrame.src = `${activeFrame.src}?v=${activeFrame.timestamp_ms}`;
    }
  }
  elements.seekBar.max = String(state.session.duration_ms || 0);
  elements.seekBar.value = String(
    state.mode === "recording" ? state.session.duration_ms : state.currentTimeMs,
  );
  elements.currentTime.textContent = formatTime(elements.seekBar.value);
  elements.totalTime.textContent = formatTime(state.session.duration_ms);
  elements.playButton.textContent = state.playing ? "⏸" : "▶";
  elements.actionCounter.textContent = `${state.session.actions.length} 个动作`;
  elements.sessionTitle.textContent = state.session.case_name;
  elements.sourceBadge.lastChild.textContent = ` ${state.session.capture_adapter}`;
  const recordedAgent = state.session.agent;
  elements.captureSourceNote.textContent = recordedAgent
    ? `${recordedAgent.version} · ${recordedAgent.build_configuration} · ${recordedAgent.git_commit} · ${state.session.app.window_title}`
    : `旧版 Trace（缺少 Agent 身份）· ${state.session.app.window_title}`;

  renderMode();
  renderTargetBox(activeAction);
  renderCurrentAction(activeAction, activeCacheStep);
  renderTimelineSelection(activeAction);
  renderDataPanel(activeAction, activeCacheStep);
  updateControlState();
}

function renderMode() {
  elements.sessionStatus.textContent = "复核模式";
  elements.sessionStatus.classList.remove("recording");
  elements.phoneShell.classList.remove("interactive");
  elements.phoneHint.textContent = "证据回放：通过播放按钮或时间线查看原生 Agent 采集的截图。";
  elements.flowCapture.classList.remove("active");
  elements.flowTrace.classList.add("active");
  elements.flowCache.classList.toggle("active", Boolean(state.cacheCase));
  elements.flowQta.classList.toggle("active", Boolean(state.generatedQta));
  elements.flowQta.classList.toggle("muted", !state.generatedQta);
}

function updateControlState() {
  elements.loadCaseButton.disabled = state.busy;
  elements.caseSelect.disabled = state.busy;
  elements.caseSearch.disabled = state.busy;
  elements.renameCaseButton.disabled = state.busy || !elements.caseSelect.value;
  elements.deleteCaseButton.disabled = state.busy || !elements.caseSelect.value;
  elements.playButton.disabled = state.busy;
  elements.seekBar.disabled = state.busy;
  elements.addStepButton.disabled = !state.session || state.busy;
  const requiresAssertionAgent = Boolean(state.session?.assertions?.length);
  elements.replayCaseButton.disabled =
    !state.session ||
    !state.session.actions?.length ||
    state.mode === "recording" ||
    state.busy ||
    !state.replayAgentAvailable ||
    (requiresAssertionAgent && !state.replayAssertionAvailable);
  elements.replayCaseButton.title = !state.replayAgentAvailable
    ? `Agent 不可用：${state.replayAgentStatus}。请运行 start-agent.ps1`
    : requiresAssertionAgent && !state.replayAssertionAvailable
      ? "当前 Agent 不支持断言，请启动最新版本"
      : "将当前复核后的步骤和断言下发给原生 Agent";
  elements.generateQtaButton.disabled =
    state.busy || !state.cacheCase || !state.codegenStatus?.configured;
  elements.copyQtaButton.disabled = state.busy || !state.generatedQta?.code;
}

function getDisplayedImageMetrics() {
  const rect = elements.phoneFrame.getBoundingClientRect();
  const [sourceWidth, sourceHeight] = state.session.device.resolution;
  const sourceRatio = sourceWidth / sourceHeight;
  const boxRatio = rect.width / rect.height;
  let width = rect.width;
  let height = rect.height;
  let left = rect.left;
  let top = rect.top;

  if (sourceRatio > boxRatio) {
    height = width / sourceRatio;
    top += (rect.height - height) / 2;
  } else {
    width = height * sourceRatio;
    left += (rect.width - width) / 2;
  }

  return { left, top, width, height, sourceWidth, sourceHeight };
}

function renderTargetBox(activeAction) {
  if (state.mode === "recording" || !activeAction?.target?.screen_bounds) {
    elements.targetBox.hidden = true;
    return;
  }

  const metrics = getDisplayedImageMetrics();
  const shellRect = elements.phoneShell.getBoundingClientRect();
  const [left, top, right, bottom] = activeAction.target.screen_bounds;
  const scaleX = metrics.width / metrics.sourceWidth;
  const scaleY = metrics.height / metrics.sourceHeight;

  elements.targetBox.hidden = false;
  elements.targetBox.style.left = `${metrics.left - shellRect.left + left * scaleX}px`;
  elements.targetBox.style.top = `${metrics.top - shellRect.top + top * scaleY}px`;
  elements.targetBox.style.width = `${Math.max(8, (right - left) * scaleX)}px`;
  elements.targetBox.style.height = `${Math.max(8, (bottom - top) * scaleY)}px`;
}

function renderCurrentAction(activeAction, activeCacheStep) {
  if (!activeAction) {
    elements.currentActionTitle.textContent = state.mode === "recording" ? "等待人工操作" : "准备播放";
    elements.currentActionMeta.innerHTML = `
      <dt>状态</dt><dd>${state.mode === "recording" ? "等待元宝客户端操作" : "等待播放"}</dd>
      <dt>数据源</dt><dd>${escapeHtml(state.session.capture_adapter)}</dd>
      <dt>初始帧</dt><dd>${escapeHtml(state.session.frames[0]?.id || "—")}</dd>
    `;
    return;
  }

  elements.currentActionTitle.textContent = activeAction.label;
  elements.currentActionMeta.innerHTML = `
    <dt>时间</dt><dd>${formatTime(activeAction.timestamp_ms)}</dd>
    <dt>动作</dt><dd>${escapeHtml(activeAction.type)}</dd>
    <dt>目标</dt><dd>${escapeHtml(activeAction.target?.semantic_role || "系统按键")}</dd>
    <dt>定位</dt><dd>${escapeHtml(activeAction.target?.primary_locator || "—")}</dd>
    <dt>Cache</dt><dd>${escapeHtml(activeCacheStep?.id || activeAction.cache_step_id || "采集结束后生成")}</dd>
  `;
}

function buildTimelineBranchMeta(actions) {
  const metadata = actions.map(() => null);
  actions.forEach((action, conditionIndex) => {
    if (action.type !== "condition") return;
    const trueCount = Math.max(0, Number(action.true_step_count || action.skip_if_false || 0));
    const falseCount = Math.max(0, Number(action.false_step_count || 0));
    metadata[conditionIndex] = { kind: "condition", trueCount, falseCount };
    for (let offset = 1; offset <= trueCount && conditionIndex + offset < actions.length; offset += 1) {
      metadata[conditionIndex + offset] = { kind: "true", position: offset, count: trueCount };
    }
    for (let offset = 1; offset <= falseCount && conditionIndex + trueCount + offset < actions.length; offset += 1) {
      metadata[conditionIndex + trueCount + offset] = { kind: "false", position: offset, count: falseCount };
    }
  });
  return metadata;
}

function branchBadge(meta) {
  if (!meta) return "";
  if (meta.kind === "condition") {
    return `<span class="branch-badge condition-badge">IF / ELSE · TRUE ${meta.trueCount} 步 · FALSE ${meta.falseCount} 步</span>`;
  }
  const label = meta.kind === "true" ? "TRUE" : "FALSE";
  return `<span class="branch-badge ${meta.kind}-badge">${label} 分支 · ${meta.position}/${meta.count}</span>`;
}

function renderTimeline() {
  if (!state.session) {
    return;
  }
  const branchMetadata = buildTimelineBranchMeta(state.session.actions);
  elements.timeline.innerHTML = state.session.actions
    .map(
      (action, index) => `
        <article class="timeline-item${branchMetadata[index] ? ` branch-${branchMetadata[index].kind}` : ""}" data-action-id="${escapeHtml(action.id)}" data-time="${action.timestamp_ms}" data-index="${index}">
          <button class="timeline-item-main" type="button" data-step-operation="select">
            ${branchBadge(branchMetadata[index])}
            <strong>${index + 1}. ${escapeHtml(action.label)}</strong>
            <span>${formatTime(action.timestamp_ms)} · ${escapeHtml(action.type)}${action.edited ? " · 已编辑" : ""}</span>
          </button>
          <div class="timeline-item-actions">
            <button class="timeline-action-button" type="button" data-step-operation="insert" title="在此步骤后新增">＋</button>
            <button class="timeline-action-button" type="button" data-step-operation="wait" title="在此步骤前等待当前控件出现">等待</button>
            <button class="timeline-action-button" type="button" data-step-operation="condition" title="基于当前控件在此步骤前添加 IF / ELSE">分支</button>
            <button class="timeline-action-button" type="button" data-step-operation="assert" title="补充录制时遗漏的验证点">补充断言</button>
            <button class="timeline-action-button" type="button" data-step-operation="up" title="前移" ${index === 0 ? "disabled" : ""}>↑</button>
            <button class="timeline-action-button" type="button" data-step-operation="down" title="后移" ${index === state.session.actions.length - 1 ? "disabled" : ""}>↓</button>
            <button class="timeline-action-button" type="button" data-step-operation="edit">编辑</button>
            <button class="timeline-action-button delete" type="button" data-step-operation="delete">删除</button>
          </div>
          ${renderAssertionsForAction(action.id)}
        </article>
      `,
    )
    .join("");

  elements.timeline.querySelectorAll(".timeline-item").forEach((card) => {
    card.addEventListener("click", (event) => {
      const assertionOperation = event.target.closest("[data-assertion-operation]");
      if (assertionOperation) {
        handleAssertionOperation(
          assertionOperation.dataset.assertionOperation,
          assertionOperation.dataset.assertionId,
        );
        return;
      }
      const operation = event.target.closest("[data-step-operation]")?.dataset.stepOperation;
      if (!operation) {
        return;
      }
      const index = Number(card.dataset.index);
      handleTimelineOperation(operation, index);
    });
  });
}

function assertionTypeLabel(type) {
  return {
    target_exists: "控件存在",
    target_not_exists: "不存在",
    text_contains: "文本包含",
    text_equals: "文本精确相等（历史兼容）",
    property_equals_name: "控件标题/占位提示精确一致",
    property_equals_value: "输入内容精确一致",
    property_equals: "属性相等（历史兼容）",
    keywords_match_count: "关键词命中数",
    descendant_count: "控件数量",
    table_dimensions: "表格行列",
    horizontal_bounds_within_window: "窗口内不横向溢出",
    descendants_within_bounds: "子控件不越界",
  }[type] || type;
}

function renderAssertionsForAction(actionId) {
  const assertions = (state.session.assertions || []).filter(
    (assertion) => assertion.after_action_id === actionId,
  );
  if (!assertions.length) {
    return "";
  }
  return `
    <div class="assertion-list">
      ${assertions
        .map(
          (assertion) => `
            <div class="assertion-item">
              <span>✓ ${escapeHtml(assertion.label)} · ${escapeHtml(assertionTypeLabel(assertion.type))}</span>
              <button type="button" data-assertion-operation="edit" data-assertion-id="${escapeHtml(assertion.id)}">编辑</button>
              <button class="delete" type="button" data-assertion-operation="delete" data-assertion-id="${escapeHtml(assertion.id)}">删除</button>
            </div>
          `,
        )
        .join("")}
    </div>
  `;
}

function renderTimelineSelection(activeAction) {
  elements.timeline.querySelectorAll(".timeline-item").forEach((card) => {
    card.classList.toggle("active", card.dataset.actionId === activeAction?.id);
  });
}

function selectTimelineAction(index) {
  if (state.mode === "recording") {
    return;
  }
  const action = state.session?.actions[index];
  if (!action) {
    return;
  }
  pause(false);
  state.currentTimeMs = action.timestamp_ms;
  render();
}

function locatorFromAction(action) {
  const target = action?.target;
  const node = target?.node;
  if (node?.automation_id) {
    return { type: "automation_id", value: node.automation_id };
  }
  if (node?.name) {
    return { type: "name_control_type", value: node.name };
  }
  if (node?.class_name) {
    return { type: "class_name", value: node.class_name };
  }
  if (action?.coordinate) {
    return { type: "coordinate", value: action.coordinate.join(",") };
  }
  return { type: "automation_id", value: "" };
}

function updateStepFormVisibility() {
  const type = elements.stepType.value;
  const hasTarget = ["click", "right_click", "input_text", "scroll", "drag", "wait_for_target", "condition"].includes(type);
  const hasPointerCoordinate = ["click", "right_click", "scroll", "drag"].includes(type);
  if (!hasPointerCoordinate && elements.stepLocatorType.value === "coordinate") {
    elements.stepLocatorType.value = "automation_id";
  }
  elements.stepValueField.hidden = type !== "input_text";
  elements.stepValue.required = type === "input_text";
  elements.stepConditionTypeField.hidden = !["condition", "wait_for_target"].includes(type);
  elements.stepSkipCountField.hidden = type !== "condition";
  elements.stepFalseCountField.hidden = type !== "condition";
  elements.stepTimeoutField.hidden = !["wait_for_target", "wait_time"].includes(type);
  elements.stepTargetField.hidden = !hasTarget;
  elements.stepLocatorTypeField.hidden = !hasTarget;
  elements.stepLocatorValueField.hidden = !hasTarget || elements.stepLocatorType.value === "coordinate";
  elements.stepXField.hidden = !hasPointerCoordinate;
  elements.stepYField.hidden = !hasPointerCoordinate;
  elements.stepEndXField.hidden = type !== "drag";
  elements.stepEndYField.hidden = type !== "drag";
  elements.stepDurationField.hidden = type !== "drag";
  elements.stepWheelDeltaField.hidden = type !== "scroll";
  elements.stepLocatorType.querySelector('option[value="coordinate"]').disabled = !hasPointerCoordinate;
}

function defaultLabelForType(type) {
  return {
    wait_for_target: "等待目标控件出现",
    wait_time: "固定等待 10 秒",
    click: "点击目标控件",
    right_click: "右键目标控件",
    input_text: "输入文本",
    key_press: "按键 ENTER",
    scroll: "向下滚动",
    drag: "拖动界面",
    condition: "如果目标存在则执行下一步",
  }[type];
}

function openStepDialog({ action = null, insertIndex = null, templateAction = null } = {}) {
  if (!state.session) {
    return;
  }
  state.stepEditor = action
    ? { mode: "edit", actionId: action.id }
    : { mode: "insert", insertIndex, templateAction };
  const sourceAction = action || templateAction;
  const locator = locatorFromAction(sourceAction);
  elements.stepDialogTitle.textContent = action ? "编辑步骤" : "新增步骤";
  elements.stepType.value = sourceAction?.type || "click";
  elements.stepLabel.value = sourceAction?.label || defaultLabelForType("click");
  elements.stepValue.value = sourceAction?.value || "";
  elements.stepConditionType.value = sourceAction?.condition_type || "target_exists";
  elements.stepSkipCount.value = sourceAction?.true_step_count || sourceAction?.skip_if_false || 1;
  elements.stepFalseCount.value = sourceAction?.false_step_count || 0;
  elements.stepTimeout.value = Math.max(1, Math.round((sourceAction?.timeout_ms || 10000) / 1000));
  elements.stepTargetRole.value = sourceAction?.target?.semantic_role || "";
  elements.stepLocatorType.value = locator.type;
  elements.stepLocatorValue.value = locator.type === "coordinate" ? "" : locator.value;
  elements.stepX.value = sourceAction?.coordinate?.[0] ?? "";
  elements.stepY.value = sourceAction?.coordinate?.[1] ?? "";
  elements.stepEndX.value = sourceAction?.end_coordinate?.[0] ?? "";
  elements.stepEndY.value = sourceAction?.end_coordinate?.[1] ?? "";
  elements.stepDuration.value = sourceAction?.duration_ms || 500;
  elements.stepWheelDelta.value = sourceAction?.wheel_delta || -360;
  updateStepFormVisibility();
  elements.stepDialog.showModal();
  elements.stepLabel.focus();
}

function closeStepDialog() {
  state.stepEditor = null;
  elements.stepDialog.close();
}

function buildTargetFromForm(existingTarget, coordinate) {
  const semanticRole = elements.stepTargetRole.value.trim() || "未命名控件";
  const locatorType = elements.stepLocatorType.value;
  const locatorValue = elements.stepLocatorValue.value.trim();
  const existingNode = existingTarget?.node || {};
  const node = {
    ...existingNode,
    automation_id: "",
    name: "",
    class_name: "",
  };

  if (locatorType !== "coordinate" && locatorValue) {
    if (locatorType === "name_control_type") {
      node.name = locatorValue;
    } else {
      node[locatorType] = locatorValue;
    }
  }

  return {
    ...(existingTarget || {}),
    semantic_role: semanticRole,
    primary_locator:
      locatorType === "coordinate"
        ? coordinate
          ? `coordinate=${coordinate.join(",")}`
          : null
        : locatorValue
          ? `${locatorType}=${locatorValue}`
          : null,
    screen_bounds: existingTarget?.screen_bounds || null,
    node: locatorType === "coordinate" ? null : node,
    candidate_count: locatorValue ? 1 : 0,
    requires_review: true,
  };
}

function buildStepFromForm(existingAction, insertIndex) {
  const type = elements.stepType.value;
  const x = Number(elements.stepX.value);
  const y = Number(elements.stepY.value);
  const coordinate =
    ["click", "right_click", "scroll", "drag"].includes(type) && Number.isFinite(x) && Number.isFinite(y) && elements.stepX.value && elements.stepY.value
      ? [Math.round(x), Math.round(y)]
      : null;
  const endX = Number(elements.stepEndX.value);
  const endY = Number(elements.stepEndY.value);
  const endCoordinate = type === "drag" && Number.isFinite(endX) && Number.isFinite(endY) &&
    elements.stepEndX.value && elements.stepEndY.value ? [Math.round(endX), Math.round(endY)] : null;
  const actions = state.session.actions;
  const previousAction = insertIndex > 0 ? actions[insertIndex - 1] || null : null;
  const nextAction = actions[insertIndex] || null;
  const firstFrame = state.session.frames[0] || null;
  const frameBefore = previousAction?.frame_after || nextAction?.frame_before || firstFrame?.id || null;
  const frameAfter = nextAction?.frame_before || previousAction?.frame_after || firstFrame?.id || null;
  const hierarchyBefore =
    previousAction?.hierarchy_after || nextAction?.hierarchy_before || firstFrame?.hierarchy || null;
  const hierarchyAfter =
    nextAction?.hierarchy_before || previousAction?.hierarchy_after || firstFrame?.hierarchy || null;

  return {
    ...(existingAction || {}),
    type,
    label: elements.stepLabel.value.trim(),
    value: type === "input_text" ? elements.stepValue.value : undefined,
    condition_type: ["condition", "wait_for_target"].includes(type) ? elements.stepConditionType.value : undefined,
    skip_if_false: type === "condition" ? Number(elements.stepSkipCount.value) : undefined,
    true_step_count: type === "condition" ? Number(elements.stepSkipCount.value) : undefined,
    false_step_count: type === "condition" ? Number(elements.stepFalseCount.value) : undefined,
    timeout_ms: ["wait_for_target", "wait_time"].includes(type) ? Number(elements.stepTimeout.value) * 1000 : undefined,
    coordinate,
    end_coordinate: type === "drag" ? endCoordinate : undefined,
    duration_ms: type === "drag" ? Number(elements.stepDuration.value) : undefined,
    wheel_delta: type === "scroll" ? Number(elements.stepWheelDelta.value) : undefined,
    target:
      ["click", "right_click", "input_text", "scroll", "drag", "wait_for_target", "condition"].includes(type)
        ? buildTargetFromForm(existingAction?.target, coordinate)
        : null,
    frame_before: existingAction?.frame_before || frameBefore,
    frame_after: existingAction?.frame_after || frameAfter,
    hierarchy_before: existingAction?.hierarchy_before || hierarchyBefore,
    hierarchy_after: existingAction?.hierarchy_after || hierarchyAfter,
    page_before: existingAction?.page_before || "manual_edit",
    page_after: existingAction?.page_after || "manual_edit",
    edited: true,
  };
}

async function persistTimelineActions(actions, message, selectedIndex = 0) {
  if (!state.session || state.busy) {
    return;
  }
  pause(false);
  setBusy(true);
  try {
    const payload = await apiRequest(
      `/api/cases/${encodeURIComponent(state.session.case_id)}/actions`,
      {
        method: "PUT",
        body: JSON.stringify({ actions }),
      },
    );
    state.session = payload.session;
    state.cacheCase = payload.cacheCase;
    state.rawTrace = payload.rawTrace || state.rawTrace;
    const selectedAction = state.session.actions[Math.max(0, Math.min(selectedIndex, state.session.actions.length - 1))];
    state.currentTimeMs = selectedAction?.timestamp_ms || 0;
    const removedEvidenceCount = payload.editSummary?.removedFrameIds?.length || 0;
    setMessage(
      removedEvidenceCount > 0
        ? `${message} 同步清理 ${removedEvidenceCount} 个不再引用的证据帧。`
        : message,
    );
    renderTimeline();
    render();
  } catch (error) {
    setMessage(error.message, true);
  } finally {
    setBusy(false);
  }
}

async function moveTimelineAction(index, offset) {
  const targetIndex = index + offset;
  if (targetIndex < 0 || targetIndex >= state.session.actions.length) {
    return;
  }
  const actions = state.session.actions.slice();
  const [action] = actions.splice(index, 1);
  actions.splice(targetIndex, 0, action);
  await persistTimelineActions(actions, `已将第 ${index + 1} 步移动到第 ${targetIndex + 1} 步。`, targetIndex);
}

async function deleteTimelineAction(index) {
  const action = state.session.actions[index];
  if (!action || !window.confirm(`确定删除第 ${index + 1} 步“${action.label}”吗？`)) {
    return;
  }
  const actions = state.session.actions.filter((_, actionIndex) => actionIndex !== index);
  await persistTimelineActions(actions, `已删除“${action.label}”，后续步骤已重新编号。`, index - 1);
}

function handleTimelineOperation(operation, index) {
  const action = state.session?.actions[index];
  if (operation === "select") {
    selectTimelineAction(index);
  } else if (operation === "insert") {
    openStepDialog({ insertIndex: index + 1 });
  } else if (operation === "wait" && action) {
    openStepDialog({
      insertIndex: index,
      templateAction: {
        type: "wait_time",
        label: "固定等待 10 秒",
        timeout_ms: 10000,
        frame_before: action.frame_before,
        frame_after: action.frame_before,
        hierarchy_before: action.hierarchy_before,
        hierarchy_after: action.hierarchy_before,
      },
    });
  } else if (operation === "condition" && action) {
    openStepDialog({
      insertIndex: index,
      templateAction: {
        type: "condition",
        label: `如果存在“${action.target?.semantic_role || action.label}”则执行下一步`,
        condition_type: "target_exists",
        skip_if_false: 1,
        true_step_count: 1,
        false_step_count: 0,
        target: action.target,
        coordinate: action.coordinate,
        frame_before: action.frame_before,
        frame_after: action.frame_before,
        hierarchy_before: action.hierarchy_before,
        hierarchy_after: action.hierarchy_before,
      },
    });
  } else if (operation === "edit" && action) {
    openStepDialog({ action });
  } else if (operation === "assert" && action) {
    openAssertionDialog({ action });
  } else if (operation === "delete") {
    deleteTimelineAction(index);
  } else if (operation === "up") {
    moveTimelineAction(index, -1);
  } else if (operation === "down") {
    moveTimelineAction(index, 1);
  }
}

function locatorFromAssertionTarget(target) {
  const locator = target?.locator_bundle || {};
  if (locator.automation_id) return { type: "automation_id", value: locator.automation_id };
  if (locator.name) return { type: "name", value: locator.name };
  if (locator.class_name) return { type: "class_name", value: locator.class_name };
  if (locator.control_type) return { type: "control_type", value: locator.control_type };
  return { type: "none", value: "" };
}

function locatorFromActionForAssertion(action) {
  const locator = action?.target?.locator_bundle || action?.raw_action?.locator || {};
  if (locator.automation_id) return { type: "automation_id", value: locator.automation_id };
  if (locator.name) return { type: "name", value: locator.name };
  if (locator.class_name) return { type: "class_name", value: locator.class_name };
  if (locator.control_type) return { type: "control_type", value: locator.control_type };
  return { type: "none", value: "" };
}

function actionHasTableAncestor(action) {
  const path = action?.target?.locator_bundle?.ancestor_path || action?.raw_action?.locator?.ancestor_path || [];
  return path.some((item) => item.control_type === "Table");
}

function updateAssertionFormVisibility() {
  const type = elements.assertionType.value;
  const exactProperty = ["property_equals_name", "property_equals_value"].includes(type);
  if (type === "table_dimensions") elements.assertionScope.value = "table";
  if (exactProperty) elements.assertionScope.value = "target";
  elements.assertionScope.disabled = type === "table_dimensions" || exactProperty;
  const windowScoped = elements.assertionScope.value === "window";
  const targetRequired = !windowScoped;
  if (windowScoped) elements.assertionLocatorType.value = "none";
  if (targetRequired && elements.assertionLocatorType.value === "none") {
    const action = state.session?.actions.find(
      (item) => item.id === state.assertionEditor?.afterActionId,
    );
    const locator = locatorFromActionForAssertion(action);
    elements.assertionLocatorType.value = locator.type === "none" ? "automation_id" : locator.type;
    elements.assertionLocatorValue.value = locator.value;
    elements.assertionTargetRole.value = action?.target?.semantic_role || "";
  }
  const locatorRequired = !windowScoped && elements.assertionLocatorType.value !== "none";
  elements.assertionTargetRoleField.hidden = !locatorRequired;
  elements.assertionLocatorValueField.hidden = !locatorRequired;
  elements.assertionLocatorValue.required = targetRequired || locatorRequired;
  elements.assertionPropertyField.hidden = type !== "property_equals" || exactProperty;
  elements.assertionExpectedField.hidden = !["text_contains", "text_equals", "property_equals", "property_equals_name", "property_equals_value", "keywords_match_count"].includes(type);
  elements.assertionMinimumField.hidden = !["keywords_match_count", "descendant_count"].includes(type);
  elements.assertionRowsField.hidden = type !== "table_dimensions";
  elements.assertionColumnsField.hidden = type !== "table_dimensions";
  elements.assertionControlTypeField.hidden = !["descendant_count", "property_equals_name", "property_equals_value"].includes(type);
  elements.assertionExpected.required = !elements.assertionExpectedField.hidden;
  elements.assertionLocatorType.querySelector('option[value="none"]').disabled = targetRequired;
}

function openAssertionDialog({ action, assertion = null }) {
  if (!action) return;
  state.assertionEditor = {
    mode: assertion ? "edit" : "insert",
    assertionId: assertion?.id || null,
    afterActionId: action.id,
  };
  const locator = assertion
    ? locatorFromAssertionTarget(assertion.target)
    : { type: "none", value: "" };
  elements.assertionDialogTitle.textContent = assertion ? "编辑断言" : `第 ${state.session.actions.indexOf(action) + 1} 步后添加断言`;
  elements.assertionType.value = assertion?.type === "property_equals"
    ? (assertion?.assertion_mode === "input_value_equals" || assertion?.property === "value"
      ? "property_equals_value"
      : "property_equals_name")
    : assertion?.type || "target_exists";
  elements.assertionLabel.value = assertion?.label || "";
  elements.assertionTargetRole.value = assertion?.target?.semantic_role || "";
  elements.assertionLocatorType.value = locator.type;
  elements.assertionLocatorValue.value = locator.value;
  elements.assertionProperty.value = assertion?.property || "name";
  const tableOption = elements.assertionScope.querySelector('option[value="table"]');
  const tableAvailable = actionHasTableAncestor(action) || assertion?.scope_control_type === "Table";
  tableOption.disabled = !tableAvailable;
  elements.assertionType.querySelector('option[value="table_dimensions"]').disabled = !tableAvailable;
  elements.assertionScope.value = assertion?.scope === "nearest_ancestor"
    ? "table"
    : assertion?.scope || (assertion?.type === "keywords_match_count" ? "window" : "target");
  elements.assertionExpected.value = assertion?.expected || "";
  if (assertion?.type === "keywords_match_count") elements.assertionExpected.value = (assertion.keywords || []).join("，");
  elements.assertionMinimum.value = assertion?.minimum_matches || assertion?.expected_count || 3;
  elements.assertionTimeout.value = Math.max(1, Math.round((assertion?.timeout_ms || 10000) / 1000));
  elements.assertionRows.value = assertion?.expected_rows ?? 2;
  elements.assertionColumns.value = assertion?.expected_columns || 3;
  elements.assertionControlType.value = assertion?.descendant_control_type || "Text";
  updateAssertionFormVisibility();
  elements.assertionDialog.showModal();
  if (["text_contains", "text_equals", "property_equals", "property_equals_name", "property_equals_value"].includes(elements.assertionType.value)) elements.assertionExpected.focus();
}

function closeAssertionDialog() {
  state.assertionEditor = null;
  elements.assertionDialog.close();
}

function buildAssertionTargetFromForm(existingTarget = null) {
  const locatorType = elements.assertionLocatorType.value;
  const locatorValue = elements.assertionLocatorValue.value.trim();
  if (locatorType === "none") return null;
  return {
    semantic_role: elements.assertionTargetRole.value.trim() || "断言目标",
    locator_bundle: {
      ...(existingTarget?.locator_bundle || {}),
      [locatorType]: locatorValue,
      ancestor_path: existingTarget?.locator_bundle?.ancestor_path || [],
    },
    ...(existingTarget?.node ? { node: existingTarget.node } : {}),
  };
}

function buildAssertionFromForm(existingAssertion) {
  const aggregateLabel = {
    keywords_match_count: `至少命中 ${elements.assertionMinimum.value} 个关键词`,
    table_dimensions: `表格应为 ${elements.assertionRows.value} 行 ${elements.assertionColumns.value} 列`,
    descendant_count: `控件数量应为 ${elements.assertionMinimum.value}`,
    horizontal_bounds_within_window: "内容不应横向超出窗口",
    descendants_within_bounds: "子控件不应越界",
  }[elements.assertionType.value];
  const selectedType = elements.assertionType.value;
  const type = ["property_equals_name", "property_equals_value"].includes(selectedType)
    ? "property_equals"
    : selectedType;
  const expected = elements.assertionExpected.value.trim();
  const selectedScope = elements.assertionScope.value;
  let target = selectedScope === "window"
    ? null
    : buildAssertionTargetFromForm(existingAssertion?.target);
  if (selectedType === "property_equals_name") {
    if (!expected) throw new Error("“控件标题精确一致”必须填写期望标题");
    target = {
      ...(target || {}),
      semantic_role: elements.assertionTargetRole.value.trim() || expected,
      locator_bundle: {
        ...(target?.locator_bundle || {}),
        name: expected,
        control_type: elements.assertionControlType.value || "Text",
      },
    };
  }
  const role = target?.semantic_role || "界面";
  if (selectedType === "property_equals_value" && !expected) {
    throw new Error("“输入内容精确一致”必须填写期望输入内容");
  }
  const generatedLabel = selectedType === "property_equals_name"
    ? `${role}控件标题应精确等于“${expected}”`
    : selectedType === "property_equals_value"
    ? `${role}输入内容应精确等于“${expected}”`
    : type === "text_contains"
    ? `${role}应包含“${expected}”`
    : type === "target_not_exists" ? `${role}应消失` : `${role}应出现`;
  return {
    ...(existingAssertion || {}),
    after_action_id: state.assertionEditor.afterActionId,
    label: elements.assertionLabel.value.trim() || aggregateLabel || generatedLabel,
    type,
    assertion_mode: type === "target_exists"
      ? "component_exists"
      : selectedType === "property_equals_name" ? "component_name_equals"
      : selectedType === "property_equals_value" ? "input_value_equals" : null,
    target,
    expected: ["text_contains", "text_equals", "property_equals"].includes(type) ? expected : null,
    property: selectedType === "property_equals_name" ? "name"
      : selectedType === "property_equals_value" ? "value"
      : type === "property_equals" ? elements.assertionProperty.value : null,
    scope: selectedScope === "table" ? "nearest_ancestor" : selectedScope,
    scope_control_type: selectedScope === "table" ? "Table" : null,
    keywords: type === "keywords_match_count"
      ? expected.split(/[，,;；\n]/).map((item) => item.trim()).filter(Boolean) : [],
    minimum_matches: type === "keywords_match_count" ? Number(elements.assertionMinimum.value) : 0,
    descendant_control_type: type === "descendant_count" ? elements.assertionControlType.value : null,
    count_operator: type === "descendant_count" ? "equals" : null,
    expected_count: type === "descendant_count" ? Number(elements.assertionMinimum.value) : 0,
    expected_rows: type === "table_dimensions" ? Number(elements.assertionRows.value) : 0,
    expected_columns: type === "table_dimensions" ? Number(elements.assertionColumns.value) : 0,
    bounds_tolerance_px: ["horizontal_bounds_within_window", "descendants_within_bounds"].includes(type) ? 1 : 0,
    timeout_ms: Number(elements.assertionTimeout.value) * 1000,
    source: existingAssertion?.source || "manual_review",
    edited: true,
  };
}

async function persistAssertions(assertions, message) {
  if (!state.session || state.busy) return;
  setBusy(true);
  try {
    const payload = await apiRequest(
      `/api/cases/${encodeURIComponent(state.session.case_id)}/assertions`,
      { method: "PUT", body: JSON.stringify({ assertions }) },
    );
    state.session = payload.session;
    state.cacheCase = payload.cacheCase;
    state.rawTrace = payload.rawTrace || state.rawTrace;
    setMessage(message);
    renderTimeline();
    render();
  } catch (error) {
    setMessage(error.message, true);
  } finally {
    setBusy(false);
  }
}

async function submitAssertionForm(event) {
  event.preventDefault();
  if (!state.assertionEditor || !elements.assertionForm.reportValidity()) return;
  const assertions = (state.session.assertions || []).slice();
  if (state.assertionEditor.mode === "edit") {
    const index = assertions.findIndex((item) => item.id === state.assertionEditor.assertionId);
    if (index < 0) return closeAssertionDialog();
    assertions[index] = buildAssertionFromForm(assertions[index]);
    closeAssertionDialog();
    await persistAssertions(assertions, "断言已更新。");
    return;
  }
  assertions.push(buildAssertionFromForm(null));
  closeAssertionDialog();
  await persistAssertions(assertions, "断言已添加。");
}

function handleAssertionOperation(operation, assertionId) {
  const assertion = (state.session.assertions || []).find((item) => item.id === assertionId);
  if (!assertion) return;
  const action = state.session.actions.find((item) => item.id === assertion.after_action_id);
  if (operation === "edit") {
    openAssertionDialog({ action, assertion });
    return;
  }
  if (operation === "delete" && window.confirm(`确定删除断言“${assertion.label}”吗？`)) {
    persistAssertions(
      state.session.assertions.filter((item) => item.id !== assertionId),
      `已删除断言“${assertion.label}”。`,
    );
  }
}

async function submitStepForm(event) {
  event.preventDefault();
  if (!state.stepEditor || !elements.stepForm.reportValidity()) {
    return;
  }

  const actions = state.session.actions.slice();
  if (state.stepEditor.mode === "edit") {
    const index = actions.findIndex((action) => action.id === state.stepEditor.actionId);
    if (index < 0) {
      closeStepDialog();
      return;
    }
    actions[index] = buildStepFromForm(actions[index], index);
    closeStepDialog();
    await persistTimelineActions(actions, `第 ${index + 1} 步已更新。`, index);
    return;
  }

  const insertIndex = Number.isInteger(state.stepEditor.insertIndex)
    ? state.stepEditor.insertIndex
    : actions.length;
  actions.splice(
    insertIndex,
    0,
    buildStepFromForm(state.stepEditor.templateAction || null, insertIndex),
  );
  closeStepDialog();
  await persistTimelineActions(actions, `已新增第 ${insertIndex + 1} 步。`, insertIndex);
}

function renderDataPanel(activeAction, activeCacheStep) {
  elements.codegenToolbar.hidden = state.activeTab !== "qta";
  elements.downloadButton.textContent = state.activeTab === "qta" ? "导出到 QTA 仓库" : "导出当前内容";
  if (state.activeTab === "trace") {
    const { actions = [], assertions = [], frames = [], edit_history = [], ...sessionMetadata } = state.session;
    const replayReport = state.lastReplayTask;
    elements.summaryCard.innerHTML = activeAction
      ? `<strong>${escapeHtml(activeAction.id)}</strong> 正在复核；已配置 ${assertions.filter((item) => item.after_action_id === activeAction.id).length} 个断言。`
      : `当前复核稿共 ${actions.length} 个有效动作、${assertions.length} 个断言、${frames.length} 个证据帧。${replayReport ? `<div class="replay-report">最近回放：${escapeHtml(replayReport.status)} · ${replayReport.currentStep}/${replayReport.actionCount} 步</div>` : ""}`;
    elements.dataViewer.textContent = JSON.stringify(
      {
        immutable_raw_trace: state.rawTrace,
        review_action_count: actions.length,
        review_actions: actions,
        review_assertion_count: assertions.length,
        review_assertions: assertions,
        last_replay_report: replayReport,
        evidence_frame_count: frames.length,
        frames,
        edit_history,
        session_metadata: sessionMetadata,
      },
      null,
      2,
    );
    return;
  }

  if (state.activeTab === "cache") {
    elements.summaryCard.innerHTML = state.cacheCase
      ? activeCacheStep
        ? `<strong>${escapeHtml(activeCacheStep.id)}</strong> 已沉淀语义目标、多级 Locator、等待条件和原始证据。`
        : "Cache Case 已生成。坐标仅作为兜底，稳定定位优先使用 AutomationId、Name 和 ControlType。"
      : "当前用例尚未生成 Cache Case。";
    elements.dataViewer.textContent = state.cacheCase
      ? JSON.stringify(state.cacheCase, null, 2)
      : "尚未生成 Cache Case";
    return;
  }

  if (!state.generatedQta) {
    elements.summaryCard.textContent = state.codegenStatus?.configured
      ? "尚未生成代码。点击“生成 / 重新生成”，AI 将直接消费当前 Cache Case 并输出完整 Windows QTA Python。"
      : "AI API 尚未配置。请在服务端填写 config.local.json 后生成。";
    elements.dataViewer.textContent = "# Generated QTA\n# 等待从当前 Cache Case 生成。\n";
    return;
  }
  const warningText = state.generatedQta.warnings?.length
    ? ` · ${state.generatedQta.warnings.length} 项需人工复核`
    : " · 无模型警告";
  elements.summaryCard.innerHTML =
    `<strong>${escapeHtml(state.generatedQta.filename)}</strong> · ${escapeHtml(state.generatedQta.model)} · ${escapeHtml(state.generatedQta.summary || "已生成")}${escapeHtml(warningText)}`;
  elements.dataViewer.textContent = state.generatedQta.code;
}

async function generateCurrentQta() {
  if (!state.session || !state.cacheCase || state.busy) return;
  setBusy(true);
  try {
    state.activeTab = "qta";
    elements.tabs.forEach((item) => item.classList.toggle("active", item.dataset.tab === "qta"));
    setMessage("正在构建 Cache Case 上下文并调用 AI 生成完整 QTA Python，请稍候……");
    render();
    const payload = await apiRequest(
      `/api/cases/${encodeURIComponent(state.session.case_id)}/generate-qta`,
      {
        method: "POST",
        body: JSON.stringify({ owner: elements.codegenOwner.value.trim() || "yuanbao-ai" }),
      },
    );
    state.generatedQta = payload.generated;
    setMessage(`代码生成完成：${state.generatedQta.filename}`);
    render();
  } catch (error) {
    setMessage(`代码生成失败：${error.message}`, true);
  } finally {
    setBusy(false);
  }
}

async function copyGeneratedQta() {
  if (!state.generatedQta?.code) return;
  try {
    await navigator.clipboard.writeText(state.generatedQta.code);
    setMessage("生成代码已复制到剪贴板。");
  } catch {
    setMessage("浏览器未允许剪贴板访问，请使用“导出当前内容”。", true);
  }
}

function play() {
  if (!state.session || state.mode === "recording") {
    return;
  }
  if (state.currentTimeMs >= state.session.duration_ms) {
    state.currentTimeMs = 0;
  }

  state.playing = true;
  const tickStartedAt = performance.now() - state.currentTimeMs;
  state.timerId = window.setInterval(() => {
    state.currentTimeMs = Math.min(
      state.session.duration_ms,
      performance.now() - tickStartedAt,
    );
    if (state.currentTimeMs >= state.session.duration_ms) {
      pause(false);
    }
    render();
  }, 100);
  render();
}

function pause(shouldRender = true) {
  state.playing = false;
  if (state.timerId) {
    window.clearInterval(state.timerId);
    state.timerId = null;
  }
  if (shouldRender) {
    render();
  }
}

async function replayCurrentCase() {
  if (!state.session?.actions?.length) {
    setMessage("当前用例没有可回放动作，请先录制或添加步骤。", true);
    return;
  }
  if (!state.session || !state.cacheCase) {
    setMessage("当前用例尚未完成加载，无法下发真实回放。", true);
    return;
  }
  if (state.mode === "recording") {
    setMessage("正在录制，请先结束录制再执行真实回放。", true);
    return;
  }
  if (state.busy) {
    setMessage("平台正在处理上一项操作，请稍候。", true);
    return;
  }
  pause(false);
  setBusy(true);
  try {
    setMessage("真实回放按钮已触发，正在向 Agent 创建任务……");
    const result = await apiRequest(
      `/api/cases/${encodeURIComponent(state.session.case_id)}/replay`,
      { method: "POST", body: JSON.stringify({ restartBeforeReplay: elements.restartBeforeReplay.checked }) },
    );
    setMessage(`真实回放任务 ${result.task.id.slice(0, 8)} 已创建：${result.task.restartBeforeReplay ? "将关闭并重启元宝" : "普通回放，不会关闭元宝"}；等待 Agent 领取……`);
    const completedTask = await waitForReplayTask(result.task.id);
    state.lastReplayTask = completedTask;
    render();
    if (completedTask.status === "failed") {
      throw new Error(completedTask.error || "原生 Agent 执行失败");
    }
    const assertionResults = completedTask.results.flatMap((step) => step.assertionResults || []);
    setMessage(`回放完成：已执行 ${completedTask.currentStep} 个动作，${assertionResults.length} 个断言通过。`);
  } catch (error) {
    setMessage(`回放失败：${error.message}`, true);
  } finally {
    setBusy(false);
  }
}

async function waitForReplayTask(taskId) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < 120000) {
    const payload = await apiRequest(`/api/replay-tasks/${encodeURIComponent(taskId)}`);
    const task = payload.task;
    if (task.status === "succeeded" || task.status === "failed" || task.status === "cancelled") {
      return task;
    }
    const progress = task.status === "queued"
      ? "等待原生 Agent 领取任务"
      : `正在执行第 ${Math.min(task.actionCount, task.currentStep + 1)}/${task.actionCount} 步`;
    setMessage(`已下发回放任务，${progress}……`);
    await new Promise((resolve) => window.setTimeout(resolve, 700));
  }
  throw new Error("等待回放结果超时");
}

async function downloadCurrentData() {
  if (state.activeTab === "qta") {
    if (!state.session || !state.generatedQta?.code) {
      setMessage("请先生成 QTA 代码后再导出", true);
      return;
    }
    const payload = await apiRequest(
      `/api/cases/${encodeURIComponent(state.session.case_id)}/export-qta`,
      { method: "POST", body: JSON.stringify({}) },
    );
    const prefix = payload.exported.reused ? "代码已在仓库中" : "代码已导出到仓库";
    setMessage(`${prefix}：${payload.exported.path}`);
    return;
  }
  let filename = "session.json";
  let content = JSON.stringify(state.session, null, 2);
  if (state.activeTab === "cache") {
    filename = "cache_case.json";
    content = JSON.stringify(state.cacheCase, null, 2);
  } else if (state.activeTab === "qta") {
    filename = state.generatedQta?.filename || "generated_qta.py";
    content = state.generatedQta?.code || "# 尚未生成 QTA 代码\n";
  }

  const blob = new Blob([content], { type: "text/plain;charset=utf-8" });
  const downloadUrl = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = downloadUrl;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(downloadUrl);
}

elements.loadCaseButton.addEventListener("click", () => loadCase(elements.caseSelect.value));
elements.caseSearch.addEventListener("input", () =>
  loadCaseList(elements.caseSelect.value).catch((error) => setMessage(error.message, true)),
);
elements.renameCaseButton.addEventListener("click", () =>
  renameCurrentCase().catch((error) => setMessage(error.message, true)),
);
elements.deleteCaseButton.addEventListener("click", () =>
  deleteCurrentCase().catch((error) => setMessage(error.message, true)),
);
elements.replayCaseButton.addEventListener("click", replayCurrentCase);
elements.generateQtaButton.addEventListener("click", generateCurrentQta);
elements.copyQtaButton.addEventListener("click", copyGeneratedQta);
elements.playButton.addEventListener("click", () => (state.playing ? pause() : play()));
elements.seekBar.addEventListener("input", (event) => {
  pause(false);
  state.currentTimeMs = Number(event.target.value);
  render();
});
elements.addStepButton.addEventListener("click", () =>
  openStepDialog({ insertIndex: state.session?.actions.length || 0 }),
);
elements.stepType.addEventListener("change", () => {
  const previousDefaultLabels = new Set(
    ["click", "right_click", "input_text", "key_press", "scroll", "drag", "wait_time", "wait_for_target", "condition"].map(defaultLabelForType),
  );
  if (!elements.stepLabel.value || previousDefaultLabels.has(elements.stepLabel.value)) {
    elements.stepLabel.value = defaultLabelForType(elements.stepType.value);
  }
  updateStepFormVisibility();
});
elements.stepLocatorType.addEventListener("change", updateStepFormVisibility);
elements.stepForm.addEventListener("submit", submitStepForm);
elements.closeStepDialogButton.addEventListener("click", closeStepDialog);
elements.cancelStepButton.addEventListener("click", closeStepDialog);
elements.assertionType.addEventListener("change", () => {
  if (elements.assertionType.value === "keywords_match_count") elements.assertionScope.value = "window";
  if (elements.assertionType.value === "table_dimensions") elements.assertionScope.value = "table";
  updateAssertionFormVisibility();
});
elements.assertionScope.addEventListener("change", updateAssertionFormVisibility);
elements.assertionLocatorType.addEventListener("change", updateAssertionFormVisibility);
elements.assertionForm.addEventListener("submit", submitAssertionForm);
elements.closeAssertionDialogButton.addEventListener("click", closeAssertionDialog);
elements.cancelAssertionButton.addEventListener("click", closeAssertionDialog);

window.setInterval(refreshReplayStatus, 3000);
elements.stepDialog.addEventListener("cancel", (event) => {
  event.preventDefault();
  closeStepDialog();
});
elements.assertionDialog.addEventListener("cancel", (event) => {
  event.preventDefault();
  closeAssertionDialog();
});
elements.downloadButton.addEventListener("click", () =>
  downloadCurrentData().catch((error) => setMessage(`导出失败：${error.message}`, true)),
);
elements.replayReportButton.addEventListener("click", async () => {
  if (!state.session) return;
  try {
    const payload = await apiRequest(`/api/cases/${encodeURIComponent(state.session.case_id)}/reports/latest`);
    const blob = new Blob([`${JSON.stringify(payload.report, null, 2)}\n`], { type: "application/json" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = `${state.session.case_name || state.session.case_id}-回放报告.json`;
    link.click();
    URL.revokeObjectURL(link.href);
    setMessage("回放报告已导出。即使回放失败，报告也会保留失败步骤和断言详情。");
  } catch (error) {
    setMessage(error.message, true);
  }
});
elements.tabs.forEach((tab) => {
  tab.addEventListener("click", () => {
    state.activeTab = tab.dataset.tab;
    elements.tabs.forEach((item) => item.classList.toggle("active", item === tab));
    render();
  });
});
window.addEventListener("resize", () => renderTargetBox(findActiveAction()));

loadCaseList().catch((error) => {
  setMessage(`平台加载失败：${error.message}`, true);
  elements.dataViewer.textContent = error.stack;
});
