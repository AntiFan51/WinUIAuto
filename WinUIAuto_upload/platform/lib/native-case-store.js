const fs = require("fs");
const os = require("os");
const path = require("path");

const SUPPORTED_ACTION_TYPES = new Set(["click", "right_click", "input_text", "key_press", "scroll", "drag", "wait_time", "wait_for_target", "condition"]);
const SUPPORTED_ASSERTION_TYPES = new Set([
  "target_exists",
  "target_not_exists",
  "text_contains",
  "text_equals",
  "property_equals",
  "keywords_match_count",
  "descendant_count",
  "table_dimensions",
  "horizontal_bounds_within_window",
  "descendants_within_bounds",
]);
const SUPPORTED_ASSERTION_PROPERTIES = new Set([
  "name",
  "automation_id",
  "class_name",
  "control_type",
  "enabled",
  "value",
]);

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJson(filePath, value) {
  const temporaryPath = `${filePath}.tmp`;
  fs.writeFileSync(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  fs.renameSync(temporaryPath, filePath);
}

function assertCaseId(caseId) {
  const value = typeof caseId === "string" ? caseId.trim() : "";
  const hasInvalidWindowsCharacter = /[<>:"/\\|?*\u0000-\u001F]/u.test(value);
  if (!value || value.length > 180 || value === "." || value === ".." ||
      value !== caseId || value.endsWith(".") || hasInvalidWindowsCharacter || path.basename(value) !== value) {
    throw new Error("用例 ID 不正确");
  }
}

function toWebPath(...segments) {
  return segments.join("/").replaceAll("\\", "/");
}

function millisecondsBetween(startedAt, timestamp) {
  const value = new Date(timestamp).getTime() - new Date(startedAt).getTime();
  return Number.isFinite(value) ? Math.max(0, value) : 0;
}

function labelForAction(action, target) {
  if (action.type === "input_text") {
    return action.text ? `输入“${action.text}”` : "输入文本（待复核）";
  }
  if (action.type === "key_press") {
    return `按键 ${action.key || "ENTER"}`;
  }
  if (action.type === "right_click") {
    return `右键 ${target?.semantic_role || "坐标位置"}`;
  }
  if (action.type === "scroll") return action.wheel_delta > 0 ? "向上滚动" : "向下滚动";
  if (action.type === "drag") return `拖动 ${target?.semantic_role || "界面位置"}`;
  if (action.type === "wait_for_target") return `等待 ${target?.semantic_role || "目标控件"}${action.condition_type === "target_not_exists" ? "消失" : "出现"}`;
  if (action.type === "wait_time") return `固定等待 ${Math.round(Number(action.timeout_ms || 10000) / 1000)} 秒`;
  if (action.type === "condition" && action.condition_type === "group_list_not_empty") return "如果分组列表中存在任意分组";
  if (action.type === "condition" && action.condition_type === "same_kind_exists") return "如果存在任意同类控件";
  if (action.type === "condition" && action.condition_type === "same_kind_not_exists") return "如果不存在同类控件";
  if (action.type === "condition") return `${action.condition_type === "target_not_exists" ? "如果不存在" : "如果存在"} ${target?.semantic_role || "目标控件"}`;
  return `点击 ${target?.semantic_role || "坐标位置"}`;
}

function locatorLabel(locator) {
  if (!locator) return null;
  if (locator.automation_id) return `automation_id=${locator.automation_id}`;
  if (locator.name) return `name=${locator.name}`;
  if (locator.class_name) return `class_name=${locator.class_name}`;
  if (locator.control_type) return `control_type=${locator.control_type}`;
  if (locator.fallback_window_point) {
    return `coordinate=${locator.fallback_window_point.x},${locator.fallback_window_point.y}`;
  }
  return null;
}

function validateControlFlowActions(actions) {
  actions.forEach((action, index) => {
    if (action.type === "wait_for_target") {
      if (!action.target) throw new Error(`第 ${index + 1} 步等待控件缺少目标信息`);
      if (action.timeout_ms < 1000 || action.timeout_ms > 120000) {
        throw new Error(`第 ${index + 1} 步等待时间必须在 1 到 120 秒之间`);
      }
      return;
    }
    if (action.type === "wait_time") {
      if (action.timeout_ms < 1000 || action.timeout_ms > 120000) {
        throw new Error(`第 ${index + 1} 步固定等待时间必须在 1 到 120 秒之间`);
      }
      return;
    }
    if (action.type !== "condition") return;
    if (!action.target) throw new Error(`第 ${index + 1} 步条件分支缺少判断目标`);
    const trueCount = action.true_step_count;
    const falseCount = action.false_step_count;
    if (trueCount < 1 || trueCount > 20 || falseCount < 0 || falseCount > 20) {
      throw new Error(`第 ${index + 1} 步分支长度超出允许范围`);
    }
    if (index + trueCount + falseCount >= actions.length) {
      throw new Error(`第 ${index + 1} 步分支引用的后续步骤数量不足`);
    }
    const branchActions = actions.slice(index + 1, index + 1 + trueCount + falseCount);
    if (branchActions.some((branchAction) => branchAction.type === "condition")) {
      throw new Error(`第 ${index + 1} 步暂不支持嵌套条件分支`);
    }
  });
}

function snapshotMetadata(caseDirectory, relativePath, cache) {
  if (!relativePath) return null;
  if (cache.has(relativePath)) return cache.get(relativePath);
  const filePath = path.join(caseDirectory, relativePath);
  if (!fs.existsSync(filePath)) return null;
  let snapshot;
  try {
    snapshot = readJson(filePath);
  } catch {
    cache.set(relativePath, null);
    return null;
  }
  const root = snapshot.nodes?.[0];
  const bounds = root?.bounds || {};
  const metadata = {
    nodeCount: Number(snapshot.node_count || snapshot.nodes?.length || 0),
    capturedAt: snapshot.captured_at || null,
    title: snapshot.window_title || null,
    originX: Number(bounds.x || 0),
    originY: Number(bounds.y || 0),
    width: Number(bounds.width || 0),
    height: Number(bounds.height || 0),
  };
  cache.set(relativePath, metadata);
  return metadata;
}

function baseClassName(value) {
  return String(value || "").split(/\s+/u)[0].split("__")[0] || null;
}

function isSharedSidebarClass(value) {
  const className = baseClassName(value);
  return className === "Item_chatOrProjectItem" || className === "ProjectsSection_item";
}

function deriveHoverAnchor(caseDirectory, rawAction) {
  const targetClass = baseClassName(rawAction.target?.class_name || rawAction.locator?.class_name);
  const ancestorPath = rawAction.locator?.ancestor_path || [];
  if (targetClass !== "Item_dropdown-trigger" ||
      !ancestorPath.some((segment) => baseClassName(segment.class_name) === "Item_chatOrProjectItem") ||
      !rawAction.uia_snapshot) return { locator: null, matchMode: null };
  let snapshot;
  try {
    snapshot = readJson(path.join(caseDirectory, rawAction.uia_snapshot));
  } catch {
    return { locator: null, matchMode: null };
  }
  const nodes = snapshot.nodes || [];
  const byIndex = new Map(nodes.map((node) => [node.index, node]));
  let target = nodes.find((node) => rawAction.target?.automation_id && node.automation_id === rawAction.target.automation_id);
  if (!target) target = nodes.find((node) => baseClassName(node.class_name) === targetClass);
  let row = target;
  while (row && baseClassName(row.class_name) !== "Item_chatOrProjectItem") row = byIndex.get(row.parent_index);
  if (!row) return { locator: null, matchMode: null };
  const bounds = (node) => node?.bounds || {};
  const rowBounds = bounds(row);
  const chatHeader = nodes
    .filter((node) => String(node.name || "").trim() === "\u804a\u5929")
    .filter((node) => Number(bounds(node).y) + Number(bounds(node).height) <= Number(rowBounds.y) + 2)
    .sort((left, right) =>
      (Number(bounds(right).y) + Number(bounds(right).height)) -
      (Number(bounds(left).y) + Number(bounds(left).height)))[0];
  if (chatHeader) {
    const headerBottom = Number(bounds(chatHeader).y) + Number(bounds(chatHeader).height);
    const visibleChatRows = nodes
      .filter((node) => baseClassName(node.class_name) === "Item_chatOrProjectItem")
      .filter((node) => node.offscreen !== true)
      .filter((node) => Number(bounds(node).width) > 0 && Number(bounds(node).height) > 0)
      .filter((node) => Number(bounds(node).y) >= headerBottom - 2)
      .sort((left, right) => Number(bounds(left).y) - Number(bounds(right).y));
    if (visibleChatRows[0]?.index === row.index) {
      return {
        locator: {
          control_type: "Group",
          class_name: "Item_chatOrProjectItem",
        },
        matchMode: "first_chat_row",
      };
    }
  }
  const descendants = nodes.filter((node) => {
    let parent = byIndex.get(node.parent_index);
    while (parent) {
      if (parent.index === row.index) return true;
      parent = byIndex.get(parent.parent_index);
    }
    return false;
  });
  const text = descendants
    .filter((node) => node.control_type === "Text" && String(node.name || "").trim())
    .filter((node) => !/^[\uE000-\uF8FF\s]+$/u.test(String(node.name)))
    .sort((left, right) => String(right.name).length - String(left.name).length)[0];
  if (!text) return { locator: null, matchMode: null };
  return {
    locator: {
      name: text.name,
      control_type: "Text",
      ancestor_path: [
        { control_type: "Group", class_name: "Item_chatOrProjectItem" },
        { name: text.name, control_type: "Text" },
      ],
    },
    matchMode: "exact_row_text",
  };
}

function buildReplayLocator(action) {
  const locator = action.target?.locator_bundle || action.raw_action?.locator || null;
  if (!locator) return null;
  const targetClass = baseClassName(locator.class_name || action.raw_action?.target?.class_name);
  const isDynamicRowMenu = targetClass === "Item_dropdown-trigger" &&
    (locator.ancestor_path || []).some((segment) =>
      baseClassName(segment.class_name) === "Item_chatOrProjectItem");
  if (!isDynamicRowMenu) return locator;
  return {
    ...locator,
    automation_id: null,
    ancestor_path: (locator.ancestor_path || []).map((segment) => ({
      ...segment,
      automation_id: baseClassName(segment.class_name) === "Item_dropdown-trigger"
        ? null
        : segment.automation_id,
    })),
  };
}

function buildTarget(rawAction, snapshot) {
  const node = rawAction.target;
  const locator = rawAction.locator || {};
  if (!node && !locator.fallback_window_point) return null;
  const bounds = node?.bounds;
  const localBounds = bounds && snapshot
    ? [
        bounds.x - snapshot.originX,
        bounds.y - snapshot.originY,
        bounds.x - snapshot.originX + bounds.width,
        bounds.y - snapshot.originY + bounds.height,
      ]
    : null;
  const semanticRole = inferSemanticRole(node);
  return {
    semantic_role: semanticRole,
    primary_locator: locatorLabel(locator),
    screen_bounds: localBounds,
    node: node
      ? {
          automation_id: node.automation_id || "",
          name: node.name || "",
          class_name: node.class_name || "",
          control_type: node.control_type || "",
          framework_id: node.framework_id || "",
          enabled: Boolean(node.enabled),
          focusable: Boolean(node.focusable),
          patterns: node.patterns || [],
          bounds: localBounds,
          bounds_screen: bounds
            ? [bounds.x, bounds.y, bounds.x + bounds.width, bounds.y + bounds.height]
            : null,
        }
      : null,
    locator_bundle: locator,
    candidate_count: node ? 1 : 0,
    requires_review: Boolean(rawAction.needs_review || !node),
  };
}

function buildLocatorFromDiagnostic(diagnostic, rawAction) {
  if (!diagnostic) return null;
  return {
    automation_id: diagnostic.automation_id || null,
    name: diagnostic.name || null,
    control_type: diagnostic.control_type || null,
    class_name: diagnostic.class_name || null,
    ancestor_path: [],
    fallback_window_point: rawAction.window_point || null,
  };
}

function normalizeCausalRawAction(rawAction) {
  if (rawAction.capture_diagnostics?.selection_source !== "semantic_snapshot_promotion" ||
      !rawAction.capture_diagnostics.immediate_target ||
      rawAction.target?.control_type !== "Edit" ||
      rawAction.capture_diagnostics.immediate_target.control_type === "Edit") return rawAction;
  const originalTarget = rawAction.target;
  const originalLocator = rawAction.locator;
  return {
    ...rawAction,
    target: rawAction.capture_diagnostics.immediate_target,
    locator: buildLocatorFromDiagnostic(rawAction.capture_diagnostics.immediate_target, rawAction),
    effects: [
      ...(rawAction.effects || []),
      ...(originalTarget ? [{
        type: "target_appeared",
        target: originalTarget,
        locator: originalLocator,
        source: "legacy_snapshot_promotion_repair",
      }] : []),
    ],
    causal_repair: "legacy_snapshot_promotion_repaired",
  };
}

function sidebarSectionScope(caseDirectory, rawAction) {
  const isSharedRow = isSharedSidebarClass(rawAction.target?.class_name) ||
    (rawAction.locator?.ancestor_path || []).some((segment) =>
      isSharedSidebarClass(segment.class_name));
  const rowBounds = rawAction.target?.bounds;
  if (!isSharedRow || !rowBounds || !rawAction.uia_snapshot) return {};
  let snapshot;
  try {
    snapshot = readJson(path.join(caseDirectory, rawAction.uia_snapshot));
  } catch {
    return {};
  }
  const headerNames = new Set(["\u5206\u7ec4", "\u6700\u8fd1", "\u804a\u5929"]);
  const unique = new Map();
  for (const node of snapshot.nodes || []) {
    const name = String(node.name || "").trim();
    const bounds = node.bounds || {};
    if (!headerNames.has(name) || node.offscreen === true ||
        Number(bounds.width) <= 0 || Number(bounds.height) <= 0) continue;
    const key = `${name}:${Math.round(Number(bounds.y))}`;
    if (!unique.has(key) || Number(unique.get(key).bounds?.width) < Number(bounds.width)) unique.set(key, node);
  }
  const headers = [...unique.values()].sort((left, right) =>
    Number(left.bounds.y) - Number(right.bounds.y));
  const rowTop = Number(rowBounds.y);
  const start = headers.filter((node) =>
    Number(node.bounds.y) + Number(node.bounds.height) <= rowTop + 2).at(-1);
  if (!start) return {};
  const end = headers.find((node) => Number(node.bounds.y) > rowTop + 2);
  return {
    section_start_name: String(start.name).trim(),
    section_end_name: end ? String(end.name).trim() : null,
  };
}

function normalizeGenericChromeTarget(rawAction, caseDirectory) {
  const selected = rawAction.target;
  const hitLeaf = rawAction.capture_diagnostics?.hit_leaf_target;
  const isPointerAction = rawAction.type === "click" || rawAction.type === "right_click";
  const selectedIsGenericChromeHost =
    baseClassName(selected?.class_name) === "Chrome_RenderWidgetHostHWND" ||
    selected?.name === "Chrome Legacy Window";
  const selectedIsSharedSidebarRow = isSharedSidebarClass(selected?.class_name);
  const hitLeafHasBusinessIdentity = Boolean(String(hitLeaf?.name || "").trim()) &&
    !["Pane", "Window", "Document"].includes(hitLeaf?.control_type);
  if (!isPointerAction || (!selectedIsGenericChromeHost && !selectedIsSharedSidebarRow) ||
      !hitLeafHasBusinessIdentity) return rawAction;
  const sectionScope = selectedIsSharedSidebarRow ? sidebarSectionScope(caseDirectory, rawAction) : {};
  const ancestorPath = selectedIsSharedSidebarRow
    ? [
        ...(rawAction.locator?.ancestor_path || []),
        {
          name: hitLeaf.name || null,
          automation_id: hitLeaf.automation_id || null,
          control_type: hitLeaf.control_type || null,
          class_name: hitLeaf.class_name || null,
        },
      ]
    : [];
  return {
    ...rawAction,
    target: {
      ...hitLeaf,
      enabled: true,
      offscreen: false,
      focusable: false,
      patterns: [],
    },
    locator: {
      ...buildLocatorFromDiagnostic(hitLeaf, rawAction),
      ancestor_path: ancestorPath,
      ...sectionScope,
    },
    target_repair: selectedIsSharedSidebarRow
      ? "shared_sidebar_row_replaced_by_named_hit_leaf"
      : "generic_chrome_host_replaced_by_hit_leaf",
  };
}

function isAmbiguousSharedSidebarEffect(effect) {
  const locator = effect?.locator || {};
  return isSharedSidebarClass(locator.class_name) &&
    !String(locator.automation_id || "").trim() &&
    !String(locator.name || "").trim() &&
    !String(locator.section_start_name || "").trim();
}

function sameBounds(left, right, tolerance = 3) {
  if (!left || !right) return false;
  return Math.abs(Number(left.x) - Number(right.x)) <= tolerance &&
    Math.abs(Number(left.y) - Number(right.y)) <= tolerance &&
    Math.abs(Number(left.width) - Number(right.width)) <= tolerance &&
    Math.abs(Number(left.height) - Number(right.height)) <= tolerance;
}

function buildLocatorFromSnapshotNode(node, nodes, rawAction) {
  const byIndex = new Map(nodes.map((item) => [item.index, item]));
  const path = [];
  let current = node;
  for (let depth = 0; current && depth < 12; depth += 1) {
    path.push({
      name: String(current.name || "").trim() || null,
      automation_id: String(current.automation_id || "").trim() || null,
      control_type: current.control_type || null,
      class_name: baseClassName(current.class_name),
    });
    current = byIndex.get(current.parent_index);
  }
  path.reverse();
  return {
    automation_id: String(node.automation_id || "").trim() || null,
    name: String(node.name || "").trim() || null,
    control_type: node.control_type || null,
    class_name: baseClassName(node.class_name),
    ancestor_path: path,
    fallback_window_point: rawAction.window_point || null,
  };
}

function normalizeClickFromPreviousTransientSnapshot(rawAction, previousRawAction, caseDirectory) {
  if (!(rawAction.type === "click" || rawAction.type === "right_click") ||
      !rawAction.screen_point || !previousRawAction?.uia_snapshot) return rawAction;
  const elapsed = Date.parse(rawAction.timestamp) - Date.parse(previousRawAction.timestamp);
  if (!Number.isFinite(elapsed) || elapsed < 0 || elapsed > 12000) return rawAction;
  let snapshot;
  try {
    snapshot = readJson(path.join(caseDirectory, previousRawAction.uia_snapshot));
  } catch {
    return rawAction;
  }
  const nodes = snapshot.nodes || [];
  const byIndex = new Map(nodes.map((node) => [node.index, node]));
  const isInTransientMenu = (node) => {
    let current = node;
    while (current) {
      const className = baseClassName(current.class_name);
      if (className === "t-popup" || className === "t-portal-wrapper" ||
          String(className || "").startsWith("yb-dropdown")) return true;
      current = byIndex.get(current.parent_index);
    }
    return false;
  };
  const { x, y } = rawAction.screen_point;
  const target = nodes
    .filter((node) => {
      const bounds = node.bounds || {};
      return node.offscreen !== true && Number(bounds.width) > 0 && Number(bounds.height) > 0 &&
        Number(x) >= Number(bounds.x) && Number(x) <= Number(bounds.x) + Number(bounds.width) &&
        Number(y) >= Number(bounds.y) && Number(y) <= Number(bounds.y) + Number(bounds.height) &&
        ["ListItem", "MenuItem"].includes(node.control_type) &&
        String(node.name || "").trim() && isInTransientMenu(node);
    })
    .sort((left, right) =>
      Number(left.bounds.width) * Number(left.bounds.height) -
      Number(right.bounds.width) * Number(right.bounds.height))[0];
  if (!target) return rawAction;
  return {
    ...rawAction,
    target,
    locator: buildLocatorFromSnapshotNode(target, nodes, rawAction),
    capture_diagnostics: {
      ...(rawAction.capture_diagnostics || {}),
      selection_source: "previous_transient_snapshot_point_match",
      transient_target: target,
      transient_decision: "pre_dismissal_menu_item_from_previous_snapshot",
    },
    target_repair: "post_dismissal_target_replaced_by_previous_transient_menu_item",
  };
}

function normalizeNamedContainerTarget(rawAction, caseDirectory) {
  const targetClass = baseClassName(rawAction.target?.class_name || rawAction.locator?.class_name);
  if (!(rawAction.type === "click" || rawAction.type === "right_click") ||
      targetClass !== "item_itemContainer" || rawAction.locator?.name ||
      rawAction.locator?.automation_id || rawAction.locator?.descendant_name ||
      !rawAction.uia_snapshot) return rawAction;
  let snapshot;
  try {
    snapshot = readJson(path.join(caseDirectory, rawAction.uia_snapshot));
  } catch {
    return rawAction;
  }
  const nodes = snapshot.nodes || [];
  const byIndex = new Map(nodes.map((node) => [node.index, node]));
  const container = nodes.find((node) =>
    baseClassName(node.class_name) === targetClass && sameBounds(node.bounds, rawAction.target?.bounds));
  if (!container) return rawAction;
  const isDescendant = (node) => {
    let parent = byIndex.get(node.parent_index);
    while (parent) {
      if (parent.index === container.index) return true;
      parent = byIndex.get(parent.parent_index);
    }
    return false;
  };
  const descendant = nodes
    .filter((node) => node.control_type === "Text" && String(node.name || "").trim() && isDescendant(node))
    .filter((node) => !/^[\uE000-\uF8FF\s]+$/u.test(String(node.name)))
    .sort((left, right) =>
      Number(left.depth) - Number(right.depth) ||
      Number(left.bounds?.y) - Number(right.bounds?.y) ||
      Number(left.bounds?.x) - Number(right.bounds?.x))[0];
  if (!descendant) return rawAction;
  return {
    ...rawAction,
    locator: {
      ...rawAction.locator,
      descendant_name: String(descendant.name).trim(),
      descendant_control_type: descendant.control_type || "Text",
    },
    target_repair: "unnamed_container_bound_to_named_descendant",
  };
}

function sameRuntimeId(left, right) {
  const leftId = left?.raw_action?.target?.runtime_id;
  const rightId = right?.raw_action?.target?.runtime_id;
  return Array.isArray(leftId) && Array.isArray(rightId) && leftId.length > 0 &&
    leftId.length === rightId.length && leftId.every((value, index) => value === rightId[index]);
}

function sameEditableTransaction(left, right) {
  if (left?.type !== "input_text" || right?.type !== "input_text") return false;
  if (sameRuntimeId(left, right)) return true;
  const leftLocator = left?.raw_action?.locator || {};
  const rightLocator = right?.raw_action?.locator || {};
  return Boolean(leftLocator.name) && leftLocator.name === rightLocator.name &&
    leftLocator.control_type === "Edit" && rightLocator.control_type === "Edit" &&
    leftLocator.class_name === rightLocator.class_name &&
    JSON.stringify(leftLocator.ancestor_path || []) === JSON.stringify(rightLocator.ancestor_path || []);
}

function normalizeInputTransactions(actions) {
  const normalized = [];
  for (let index = 0; index < actions.length; index += 1) {
    const current = actions[index];
    if (current.type !== "input_text") {
      normalized.push(current);
      continue;
    }

    let nextIndex = index + 1;
    const intermediateKeys = [];
    while (nextIndex < actions.length && actions[nextIndex].type === "key_press" && intermediateKeys.length < 2) {
      const key = String(actions[nextIndex].value || "ENTER").toUpperCase();
      if (!["ENTER", "TAB"].includes(key) || (actions[nextIndex].effects || []).length > 0) break;
      intermediateKeys.push(actions[nextIndex]);
      nextIndex += 1;
    }

    const nextInput = actions[nextIndex];
    const elapsed = Number(nextInput?.timestamp_ms) - Number(current.timestamp_ms);
    if (intermediateKeys.length > 0 && sameEditableTransaction(current, nextInput) &&
        Number.isFinite(elapsed) && elapsed >= 0 && elapsed <= 8000) {
      normalized.push({
        ...nextInput,
        input_transaction_repair: {
          type: "coalesced_same_editable_commits",
          removed_action_ids: [current.id, ...intermediateKeys.map((action) => action.id)],
        },
      });
      index = nextIndex;
      continue;
    }
    normalized.push(current);
  }
  return normalized.filter((action, index, collection) => {
    if (action.type !== "key_press" || String(action.value || "ENTER").toUpperCase() !== "ENTER" ||
        (action.effects || []).length > 0) return true;
    const previous = collection[index - 1];
    const next = collection[index + 1];
    if (previous?.type !== "input_text" || next?.type !== "click") return true;
    const keyTarget = action.raw_action?.target;
    const inputTarget = previous.raw_action?.target;
    const nextTarget = next.raw_action?.target;
    const sameEditable = Array.isArray(keyTarget?.runtime_id) && Array.isArray(inputTarget?.runtime_id) &&
      keyTarget.runtime_id.length === inputTarget.runtime_id.length &&
      keyTarget.runtime_id.every((value, runtimeIndex) => value === inputTarget.runtime_id[runtimeIndex]);
    const elapsed = Number(next.timestamp_ms) - Number(action.timestamp_ms);
    const nextIsExplicitButton = nextTarget?.control_type === "Button";
    if (!sameEditable || !nextIsExplicitButton || !Number.isFinite(elapsed) || elapsed < 0 || elapsed > 8000) return true;
    previous.input_transaction_repair = {
      ...(previous.input_transaction_repair || {}),
      type: "coalesced_editable_commit",
      removed_action_ids: [
        ...((previous.input_transaction_repair || {}).removed_action_ids || []),
        action.id,
      ],
    };
    return false;
  });
}

function inferSemanticRole(node) {
  if (!node) return "坐标位置";
  const className = String(node.class_name || "");
  if (className.includes("group-plus_trigger")) return "新增元宝派";
  return node.automation_id || node.name || node.control_type || "坐标位置";
}

const GENERIC_CONTROL_TYPES = new Set(["Group", "Pane", "Text", "Document", "Window"]);
const TRANSIENT_CLASS_TOKENS = new Set([
  "active", "blank", "disabled", "enabled", "focus", "focused", "hidden",
  "hover", "normal", "relative", "selected", "undefined", "visible",
]);

function escapeRegex(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

function stableClassToken(className) {
  const candidates = String(className || "").split(/\s+/u)
    .map((token) => token.trim())
    .filter((token) => token.length >= 3 && !TRANSIENT_CLASS_TOKENS.has(token.toLowerCase()))
    .map((token) => {
      const match = /^(.*)__[A-Za-z0-9_-]{4,}$/u.exec(token);
      return { raw: token, stable: match ? match[1].replace(/_+$/u, "") : token };
    })
    .filter(({ stable }) => stable.length >= 3 && !TRANSIENT_CLASS_TOKENS.has(stable.toLowerCase()));
  if (!candidates.length) return null;
  const chosen = candidates.sort((left, right) => {
    const leftSpecificity = /[-_]/u.test(left.stable) ? 1 : 0;
    const rightSpecificity = /[-_]/u.test(right.stable) ? 1 : 0;
    return rightSpecificity - leftSpecificity || right.stable.length - left.stable.length ||
      left.stable.localeCompare(right.stable);
  })[0];
  const optionalHash = chosen.raw === chosen.stable ? "" : "(?:_{2,3}[A-Za-z0-9_-]+)?";
  return {
    token: chosen.stable,
    pattern: `(?:^|\\s)${escapeRegex(chosen.stable)}${optionalHash}(?:\\s|$)`,
  };
}

function isStableAutomationId(value) {
  const id = String(value || "").trim();
  return Boolean(id) && !/^view_\d+$/u.test(id) && !/^gc-it-id-\d+$/u.test(id);
}

function buildLocators(action, recordedWindowSize) {
  const target = action.target;
  const node = target?.node || {};
  const locators = [];
  const appendNodeLocators = (candidateNode, source = "uia", includeWeakControlType = true) => {
    if (!candidateNode || typeof candidateNode !== "object") return;
    if (candidateNode.automation_id) {
      const stable = isStableAutomationId(candidateNode.automation_id);
      locators.push({
        type: "automation_id", value: candidateNode.automation_id, priority: stable ? 100 : 55,
        source: source === "uia" ? (stable ? "uia" : "uia_dynamic") : source,
        stability: stable ? "stable" : "session_only",
        control_type: candidateNode.control_type || null,
      });
    }
    if (candidateNode.name) {
      locators.push({
        type: "name_control_type",
        value: { name: candidateNode.name, control_type: candidateNode.control_type || null },
        priority: 90, source, stability: "semantic",
      });
    }
    if (candidateNode.class_name) {
      const stableClass = stableClassToken(candidateNode.class_name);
      if (stableClass) {
        locators.push({
          type: "class_name_re", value: stableClass.pattern, stable_token: stableClass.token,
          priority: 75, source: source === "uia" ? "uia_derived" : source,
          stability: "stable_token", control_type: candidateNode.control_type || null,
        });
      }
      locators.push({
        type: "class_name", value: candidateNode.class_name, priority: 65,
        source, stability: "exact_snapshot", control_type: candidateNode.control_type || null,
      });
    }
    if (includeWeakControlType && candidateNode.control_type &&
        !GENERIC_CONTROL_TYPES.has(candidateNode.control_type)) {
      locators.push({
        type: "control_type", value: candidateNode.control_type, priority: 35,
        source, stability: "weak",
      });
    }
  };

  appendNodeLocators(node);

  // A Chromium/WebView click can be recorded against a semantic container
  // while the actual UIA element under the pointer is a stable interactive
  // descendant. Preserve that hit leaf as a real locator candidate so code
  // generation does not have to replace missing UIA identity with coordinates.
  if (action.type === "click") {
    appendNodeLocators(action.capture_diagnostics?.hit_leaf_target, "hit_leaf_target", false);
  }

  if (Array.isArray(action.coordinate)) {
    const width = Number(recordedWindowSize?.width || 0);
    const height = Number(recordedWindowSize?.height || 0);
    const normalizedValue = width > 0 && height > 0
      ? action.coordinate.map((value, index) => Number((Number(value) / (index === 0 ? width : height)).toFixed(6)))
      : null;
    locators.push({
      type: "coordinate_fallback", value: action.coordinate, normalized_value: normalizedValue,
      reference_window_size: width > 0 && height > 0 ? { width, height } : null,
      priority: 10, source: "manual_action", stability: "scaled_fallback",
    });
  }
  const uniqueLocators = [];
  const locatorKeys = new Set();
  for (const locator of locators) {
    const key = `${locator.type}:${JSON.stringify(locator.value)}:${locator.control_type || ""}`;
    if (locatorKeys.has(key)) continue;
    locatorKeys.add(key);
    uniqueLocators.push(locator);
  }
  return uniqueLocators.sort((left, right) => right.priority - left.priority ||
    String(left.type).localeCompare(String(right.type)) ||
    JSON.stringify(left.value).localeCompare(JSON.stringify(right.value)));
}

const ASYNC_UI_ACTIONS = new Set([
  "click", "right_click", "input_text", "key_press", "scroll", "drag",
]);

function buildCacheTimeline(steps, assertions, actions = []) {
  const stepBySourceAction = new Map();
  const actionBySourceId = new Map();
  for (const action of actions || []) {
    const sourceId = action.source_action_id || action.id;
    if (sourceId) actionBySourceId.set(sourceId, action);
  }
  for (const step of steps) {
    const sourceActionId = step.source_action_id || step.evidence?.source_action;
    if (sourceActionId) stepBySourceAction.set(sourceActionId, step.id);
  }

  const normalizedAssertions = (assertions || []).map((assertion) => {
    const afterStepId = stepBySourceAction.get(assertion.after_action_id) || null;
    const step = steps.find((item) => item.id === afterStepId) || null;
    const action = actionBySourceId.get(assertion.after_action_id) || null;
    const recordedTimeoutMs = Math.max(1000, Number(assertion.timeout_ms || 10000));
    let timeoutMs = recordedTimeoutMs;
    let observedDelayMs = null;
    let waitAfterMs = null;
    if (step && action && ASYNC_UI_ACTIONS.has(step.action)) {
      waitAfterMs = Math.max(0, Number(step.wait_after?.timeout_seconds || 0) * 1000);
      const actionAt = Date.parse(action.timestamp || action.raw_action?.timestamp || "");
      const assertionAt = Date.parse(assertion.evidence?.captured_at || assertion.captured_at || "");
      if (Number.isFinite(actionAt) && Number.isFinite(assertionAt) && assertionAt >= actionAt) {
        observedDelayMs = assertionAt - actionAt;
      }
      const observedBudgetMs = observedDelayMs === null ? 0 : observedDelayMs + 2000;
      const asynchronousBudgetMs = waitAfterMs > 0
        ? waitAfterMs + 2000
        : observedBudgetMs;
      timeoutMs = Math.min(120000, Math.ceil(Math.max(
        recordedTimeoutMs,
        asynchronousBudgetMs,
      ) / 1000) * 1000);
    }
    return {
      ...assertion,
      after_step_id: afterStepId,
      timeout_ms: timeoutMs,
      timeout_policy: timeoutMs > recordedTimeoutMs
        ? {
            type: "poll_async_result",
            recorded_timeout_ms: recordedTimeoutMs,
            wait_after_ms: waitAfterMs,
            observed_delay_ms: observedDelayMs,
            grace_ms: 2000,
            observed_delay_is_bounded_by_wait_after: waitAfterMs > 0,
          }
        : assertion.timeout_policy || null,
    };
  });
  const assertionsByStep = new Map();
  for (const assertion of normalizedAssertions) {
    if (!assertion.after_step_id) continue;
    const attached = assertionsByStep.get(assertion.after_step_id) || [];
    attached.push(assertion);
    assertionsByStep.set(assertion.after_step_id, attached);
  }

  let sequence = 0;
  const nextEventId = () => `event_${String(++sequence).padStart(3, "0")}`;
  const timelineEvents = [];
  for (const step of steps) {
    timelineEvents.push({
      event_id: nextEventId(), kind: "action", step_id: step.id,
      source_action_id: step.source_action_id,
      action: step.action, label: step.label,
    });
    for (const assertion of assertionsByStep.get(step.id) || []) {
      timelineEvents.push({
        event_id: nextEventId(), kind: "assertion", assertion_id: assertion.id,
        after_step_id: step.id, after_source_action_id: assertion.after_action_id || null,
        type: assertion.type, label: assertion.label || null,
      });
    }
  }
  // Keep unbound assertions visible for review, but never pretend their position is known.
  for (const assertion of normalizedAssertions.filter((item) => !item.after_step_id)) {
    timelineEvents.push({
      event_id: nextEventId(), kind: "assertion", assertion_id: assertion.id,
      after_step_id: null, after_source_action_id: assertion.after_action_id || null,
      type: assertion.type, label: assertion.label || null, review_status: "needs_review",
      review_reason: "after_action_id_not_found_in_recorded_actions",
    });
  }
  return { assertions: normalizedAssertions, timelineEvents };
}

function buildCacheCase(session) {
  const recordedWindowSize = Array.isArray(session.device?.resolution)
    ? { width: Number(session.device.resolution[0]), height: Number(session.device.resolution[1]) }
    : null;
  const steps = session.actions.map((action, index) => ({
    id: `step_${String(index + 1).padStart(3, "0")}`,
    source_action_id: action.source_action_id || action.id,
    action: action.type,
    label: action.label,
    target: action.target
      ? { semantic_role: action.target.semantic_role, locators: buildLocators(action, recordedWindowSize) }
      : null,
    hover_reveal: action.hover_anchor_locator
      ? {
          match_mode: action.hover_anchor_match_mode || "first_match",
          target: {
            semantic_role: action.hover_anchor_match_mode === "first_chat_row"
              ? "first chat row"
              : "hover reveal anchor",
            locators: buildLocators({
              type: "hover",
              target: { node: action.hover_anchor_locator },
            }, recordedWindowSize),
          },
        }
      : null,
    input: action.type === "input_text" ? { text: action.value || "" } : null,
    key: action.type === "key_press" ? action.value || "ENTER" : null,
    gesture: action.type === "scroll"
      ? { wheel_delta: action.wheel_delta || -120 }
      : action.type === "drag"
        ? { end_coordinate: action.end_coordinate || null, duration_ms: action.duration_ms || 500 }
        : null,
    condition: action.type === "condition"
      ? {
          type: action.condition_type || "target_exists",
          true_step_count: action.true_step_count || action.skip_if_false || 1,
          false_step_count: action.false_step_count || 0,
          match_mode: action.condition_match_mode || "exact",
          scope: action.condition_scope_type
            ? { type: action.condition_scope_type, start_name: action.condition_scope_start_name || null, end_name: action.condition_scope_end_name || null }
            : null,
          fallback_policy: action.condition_scope_type ? "strict" : "window_allowed",
        }
      : null,
    synchronization: action.type === "wait_for_target"
      ? { type: action.condition_type || "target_exists", timeout_ms: action.timeout_ms || 10000 }
      : action.type === "wait_time" ? { type: "fixed_delay", duration_ms: action.timeout_ms || 10000 } : null,
    wait_after: { type: "screen_stable", timeout_seconds: 8, observed_frame: action.frame_after },
    evidence: { source_action: action.source_action_id || action.id, frame_before: action.frame_before, frame_after: action.frame_after, hierarchy_before: action.hierarchy_before, hierarchy_after: action.hierarchy_after },
    confidence: action.confidence ?? (action.target?.requires_review ? 0.5 : 0.9),
    review_status: action.edited ? "edited" : action.target?.requires_review ? "needs_review" : "captured",
  }));
  const timeline = buildCacheTimeline(steps, session.assertions || [], session.actions || []);
  return {
    schema_version: "0.3-windows-native-cache",
    case: {
      id: session.case_id,
      name: session.case_name,
      module: "yuanbao_windows",
      source_session_id: session.session_id,
      review_status: session.actions.some((action) => action.target?.requires_review) ? "needs_review" : "draft",
    },
    applicability: {
      platform: "windows",
      app_name: session.app.name,
      window_title: session.app.window_title,
      runtime_requirement: "interactive_unlocked_windows_desktop",
      recorded_window_size: recordedWindowSize,
    },
    preconditions: [
      { type: "yuanbao_window_available", value: true },
      { type: "captured_initial_state", evidence: session.frames[0]?.id || null },
    ],
    steps,
    assertions: timeline.assertions,
    timeline_events: timeline.timelineEvents,
    generation_notes: [
      "Raw Trace is immutable; tester edits are stored in review-trace.json.",
      "Coordinates are low-priority fallbacks behind Windows UIA locators.",
      "Coordinate fallbacks include reference window size and normalized values for scaled replay.",
      "Generic control-type-only locators are excluded; dynamic CSS classes include stable-token regex candidates.",
      "Assertions are added explicitly by testers.",
      "timeline_events is the only authoritative execution order for actions and assertions.",
    ],
  };
}

function buildReplaySpec(session, options = {}) {
  return {
    caseId: session.case_id,
    caseName: session.case_name,
    target: {
      process_name: "yuanbao",
      window_title: session.app.window_title || "腾讯元宝",
      restart_before_replay: Boolean(options.restartBeforeReplay),
      recorded_window_size: Array.isArray(session.device?.resolution)
        ? {
            width: Number(session.device.resolution[0]),
            height: Number(session.device.resolution[1]),
          }
        : null,
    },
    actions: session.actions.map((action, index) => ({
      id: action.id || `action_${String(index + 1).padStart(4, "0")}`,
      type: action.type,
      value: action.type === "input_text" || action.type === "key_press" ? action.value || "" : null,
      coordinate: Array.isArray(action.coordinate)
        ? { x: Number(action.coordinate[0]), y: Number(action.coordinate[1]) }
        : null,
      target_relative_point: buildTargetRelativePoint(action),
      locator: buildReplayLocator(action),
      delay_after_ms: buildReplayDelay(session.actions, index),
      end_coordinate: Array.isArray(action.end_coordinate)
        ? { x: Number(action.end_coordinate[0]), y: Number(action.end_coordinate[1]) }
        : null,
      wheel_delta: Number(action.wheel_delta || 0),
      duration_ms: Number(action.duration_ms || 0),
      condition_type: action.condition_type || null,
      skip_if_false: Number(action.skip_if_false || 0),
      true_step_count: Number(action.true_step_count || action.skip_if_false || 0),
      false_step_count: Number(action.false_step_count || 0),
      timeout_ms: Number(action.timeout_ms || 0),
      condition_match_mode: action.condition_match_mode || null,
      condition_scope_type: action.condition_scope_type || null,
      condition_scope_start_name: action.condition_scope_start_name || null,
      condition_scope_end_name: action.condition_scope_end_name || null,
      condition_fallback_policy: action.condition_scope_type ? "strict" : "window_allowed",
      effects: (action.effects || action.raw_action?.effects || []).filter((effect) =>
        !isAmbiguousSharedSidebarEffect(effect)).map((effect) => ({
        type: effect.type,
        locator: effect.locator || null,
        timeout_ms: Number(effect.timeout_ms || 5000),
      })).filter((effect) => effect.type && effect.locator),
      hover_anchor_locator: action.hover_anchor_locator || null,
      hover_anchor_match_mode: action.hover_anchor_match_mode || null,
    })),
    assertions: (session.assertions || []).map((assertion) => ({
      id: assertion.id,
      after_action_id: assertion.after_action_id,
      label: assertion.label,
      type: assertion.type,
      expected: assertion.expected ?? null,
      property: assertion.property || null,
      scope: assertion.scope || null,
      scope_control_type: assertion.scope_control_type || null,
      keywords: assertion.keywords || null,
      minimum_matches: Number(assertion.minimum_matches || 0),
      descendant_control_type: assertion.descendant_control_type || null,
      count_operator: assertion.count_operator || null,
      expected_count: Number(assertion.expected_count || 0),
      expected_rows: Number(assertion.expected_rows || 0),
      expected_columns: Number(assertion.expected_columns || 0),
      bounds_tolerance_px: Number(assertion.bounds_tolerance_px || 0),
      timeout_ms: Math.max(1000, Math.min(120000, Number(assertion.timeout_ms || 10000))),
      target: assertion.target
        ? {
            semantic_role: assertion.target.semantic_role || null,
            locator: assertion.target.locator_bundle || null,
          }
        : null,
    })),
  };
}

function buildReplayDelay(actions, index) {
  const action = actions[index];
  const next = actions[index + 1];
  if (!action || !next || action.type !== "key_press" ||
      String(action.value || "").toUpperCase() !== "ENTER") return 500;
  const elapsed = Number(next.timestamp_ms) - Number(action.timestamp_ms);
  if (!Number.isFinite(elapsed) || elapsed <= 500) return 500;
  return Math.max(500, Math.min(10000, Math.round(elapsed)));
}

function buildTargetRelativePoint(action) {
  const point = action.raw_action?.screen_point;
  const bounds = action.raw_action?.target?.bounds;
  const width = Number(bounds?.width);
  const height = Number(bounds?.height);
  if (!point || !Number.isFinite(width) || width <= 0 || !Number.isFinite(height) || height <= 0) return null;
  const x = (Number(point.x) - Number(bounds.x)) / width;
  const y = (Number(point.y) - Number(bounds.y)) / height;
  if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
  return {
    x: Math.max(0, Math.min(1, x)),
    y: Math.max(0, Math.min(1, y)),
  };
}

function validateReplayAssertions(assertions) {
  for (const assertion of assertions || []) {
    const scope = assertion.scope || (!assertion.target && ["text_contains", "text_equals", "keywords_match_count"].includes(assertion.type)
      ? "window"
      : "target");
    if (!["window", "target", "nearest_ancestor"].includes(scope)) {
      throw new Error(`断言“${assertion.label || assertion.id}”的验证范围无效`);
    }
    if (scope !== "window" && !assertion.target?.locator) {
      throw new Error(`断言“${assertion.label || assertion.id}”缺少目标控件`);
    }
    if (scope === "nearest_ancestor" && !assertion.scope_control_type) {
      throw new Error(`断言“${assertion.label || assertion.id}”缺少祖先控件类型`);
    }
    if (assertion.type === "keywords_match_count" && (!assertion.keywords?.length || assertion.minimum_matches <= 0)) {
      throw new Error(`断言“${assertion.label || assertion.id}”缺少关键词或最少命中数量`);
    }
    if (assertion.type === "keywords_match_count" && assertion.minimum_matches > assertion.keywords.length) {
      throw new Error(`断言“${assertion.label || assertion.id}”的最少命中数量不能超过关键词总数`);
    }
    if (assertion.type === "table_dimensions" && (assertion.expected_rows < 0 || assertion.expected_columns <= 0)) {
      throw new Error(`断言“${assertion.label || assertion.id}”缺少有效的表格行列数量`);
    }
    if (assertion.type === "table_dimensions" && (scope !== "nearest_ancestor" || assertion.scope_control_type !== "Table")) {
      throw new Error(`断言“${assertion.label || assertion.id}”必须作用于目标所属表格`);
    }
  }
}

class NativeCaseStore {
  constructor({ rootDirectory }) {
    this.rootDirectory = rootDirectory;
    this.caseRoot = path.join(rootDirectory, "cases-native");
    this.caseTrashRoot = path.join(rootDirectory, "runtime", "trash", "cases-native");
  }

  loadCaseMetadata(caseId) {
    const metadataPath = path.join(this.caseRoot, caseId, "case-metadata.json");
    return fs.existsSync(metadataPath) ? readJson(metadataPath) : {};
  }

  listCases() {
    if (!fs.existsSync(this.caseRoot)) return [];
    return fs.readdirSync(this.caseRoot, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => {
        const tracePath = path.join(this.caseRoot, entry.name, "raw-trace.json");
        if (!fs.existsSync(tracePath)) return null;
        const rawTrace = readJson(tracePath);
        const metadata = this.loadCaseMetadata(entry.name);
        const reviewPath = path.join(this.caseRoot, entry.name, "review-trace.json");
        const latestReplayPath = path.join(this.caseRoot, entry.name, "reports", "latest-replay-report.json");
        const actionCount = fs.existsSync(reviewPath)
          ? readJson(reviewPath).actions?.length || 0
          : rawTrace.actions?.length || 0;
        return {
          id: entry.name,
          name: metadata.display_name || rawTrace.case_name || entry.name,
          appTarget: rawTrace.target_window || "腾讯元宝",
          actionCount,
          startedAt: rawTrace.started_at,
          finishedAt: rawTrace.finished_at || null,
          reviewStatus: fs.existsSync(reviewPath) ? "edited" : "raw",
          latestReplay: fs.existsSync(latestReplayPath) ? readJson(latestReplayPath) : null,
          source: `cases-native/${entry.name}`,
        };
      })
      .filter(Boolean)
      .sort((left, right) => String(right.startedAt).localeCompare(String(left.startedAt)));
  }

  loadCase(caseId) {
    assertCaseId(caseId);
    const caseDirectory = path.join(this.caseRoot, caseId);
    const rawPath = path.join(caseDirectory, "raw-trace.json");
    if (!fs.existsSync(rawPath)) throw new Error("原生用例不存在");
    const rawTrace = readJson(rawPath);
    const reviewPath = path.join(caseDirectory, "review-trace.json");
    const reviewTrace = fs.existsSync(reviewPath) ? readJson(reviewPath) : null;
    const session = this.buildSession(caseId, caseDirectory, rawTrace, reviewTrace);
    const metadata = this.loadCaseMetadata(caseId);
    if (metadata.display_name) session.case_name = metadata.display_name;
    const cacheCase = buildCacheCase(session);
    return { session, cacheCase, rawTrace };
  }

  renameCase(caseId, submittedName) {
    assertCaseId(caseId);
    const caseDirectory = path.join(this.caseRoot, caseId);
    if (!fs.existsSync(path.join(caseDirectory, "raw-trace.json"))) throw new Error("原生用例不存在");
    const displayName = String(submittedName || "").trim();
    if (!displayName) throw new Error("用例名称不能为空");
    if (displayName.length > 100) throw new Error("用例名称不能超过 100 个字符");
    if (/\p{Control}/u.test(displayName)) throw new Error("用例名称包含无效控制字符");
    const metadata = {
      schema_version: "0.1-case-metadata",
      case_id: caseId,
      display_name: displayName,
      updated_at: new Date().toISOString(),
    };
    writeJson(path.join(caseDirectory, "case-metadata.json"), metadata);
    const refreshed = this.loadCase(caseId);
    writeJson(path.join(caseDirectory, "cache-case.json"), refreshed.cacheCase);
    return { case: this.listCases().find((item) => item.id === caseId), ...refreshed };
  }

  deleteCase(caseId) {
    assertCaseId(caseId);
    const caseDirectory = path.join(this.caseRoot, caseId);
    if (!fs.existsSync(path.join(caseDirectory, "raw-trace.json"))) throw new Error("原生用例不存在");
    fs.mkdirSync(this.caseTrashRoot, { recursive: true });
    const suffix = new Date().toISOString().replace(/[:.]/g, "-");
    const trashDirectory = path.join(this.caseTrashRoot, `${caseId}-${suffix}`);
    fs.renameSync(caseDirectory, trashDirectory);
    return {
      deleted: true,
      caseId,
      recoverablePath: path.relative(this.rootDirectory, trashDirectory).replaceAll("\\", "/"),
    };
  }

  saveReviewActions(caseId, submittedActions) {
    if (!Array.isArray(submittedActions)) throw new Error("actions 必须是数组");
    if (submittedActions.length > 500) throw new Error("单个用例最多支持 500 个步骤");
    const loaded = this.loadCase(caseId);
    const usedIds = new Set();
    const actions = submittedActions.map((action, index) => {
      if (!SUPPORTED_ACTION_TYPES.has(action.type)) throw new Error(`不支持的动作类型：${action.type}`);
      const preferredId = String(action.id || `review_action_${index + 1}`);
      const id = usedIds.has(preferredId) ? `review_action_${String(index + 1).padStart(3, "0")}` : preferredId;
      usedIds.add(id);
      const coordinate = Array.isArray(action.coordinate) && action.coordinate.length === 2
        ? action.coordinate.map((value) => Math.round(Number(value)))
        : null;
      return {
        ...action,
        id,
        type: action.type,
        label: String(action.label || labelForAction(action, action.target)).slice(0, 200),
        value: action.type === "input_text" || action.type === "key_press" ? String(action.value || "") : undefined,
        coordinate: coordinate?.every(Number.isFinite) ? coordinate : null,
        end_coordinate: Array.isArray(action.end_coordinate) && action.end_coordinate.length === 2
          ? action.end_coordinate.map((value) => Math.round(Number(value))) : null,
        wheel_delta: action.type === "scroll" ? Math.round(Number(action.wheel_delta || -120)) : undefined,
        duration_ms: action.type === "drag" ? Math.max(1, Math.round(Number(action.duration_ms || 500))) : undefined,
        condition_type: ["condition", "wait_for_target"].includes(action.type) &&
          ["target_exists", "target_not_exists", "same_kind_exists", "same_kind_not_exists", "group_list_not_empty"].includes(action.condition_type)
          ? action.condition_type
          : ["condition", "wait_for_target"].includes(action.type) ? "target_exists" : undefined,
        skip_if_false: action.type === "condition" ? Math.max(1, Math.min(20, Math.round(Number(action.true_step_count || action.skip_if_false || 1)))) : undefined,
        true_step_count: action.type === "condition" ? Math.max(1, Math.min(20, Math.round(Number(action.true_step_count || action.skip_if_false || 1)))) : undefined,
        false_step_count: action.type === "condition" ? Math.max(0, Math.min(20, Math.round(Number(action.false_step_count || 0)))) : undefined,
        timeout_ms: ["wait_for_target", "wait_time"].includes(action.type) ? Math.max(1000, Math.min(120000, Math.round(Number(action.timeout_ms || 10000)))) : undefined,
        condition_match_mode: action.type === "condition" ? action.condition_match_mode || undefined : undefined,
        condition_scope_type: action.type === "condition" ? action.condition_scope_type || undefined : undefined,
        condition_scope_start_name: action.type === "condition" ? action.condition_scope_start_name || undefined : undefined,
        condition_scope_end_name: action.type === "condition" ? action.condition_scope_end_name || undefined : undefined,
        edited: true,
        cache_step_id: `step_${String(index + 1).padStart(3, "0")}`,
      };
    });
    validateControlFlowActions(actions);
    const previousIds = new Set(loaded.session.actions.map((action) => action.id));
    const currentIds = new Set(actions.map((action) => action.id));
    const removedActionIds = [...previousIds].filter((id) => !currentIds.has(id));
    const reviewTrace = {
      schema_version: "0.1-review-trace",
      case_id: caseId,
      source_raw_trace: `cases-native/${caseId}/raw-trace.json`,
      edited_at: new Date().toISOString(),
      actions,
      assertions: (loaded.session.assertions || []).filter((assertion) => currentIds.has(assertion.after_action_id)),
      edit_history: [
        ...(loaded.session.edit_history || []),
        { edited_at: new Date().toISOString(), action_count: actions.length, removed_action_ids: removedActionIds },
      ],
    };
    const caseDirectory = path.join(this.caseRoot, caseId);
    writeJson(path.join(caseDirectory, "review-trace.json"), reviewTrace);
    const refreshed = this.loadCase(caseId);
    writeJson(path.join(caseDirectory, "cache-case.json"), refreshed.cacheCase);
    return {
      ...refreshed,
      editSummary: { removedActionIds, removedFrameIds: [] },
    };
  }

  buildReplaySpec(caseId, options = {}) {
    const spec = buildReplaySpec(this.loadCase(caseId).session, options);
    validateReplayAssertions(spec.assertions);
    return spec;
  }

  loadLatestReplayReport(caseId) {
    assertCaseId(caseId);
    const reportPath = path.join(this.caseRoot, caseId, "reports", "latest-replay-report.json");
    if (!fs.existsSync(reportPath)) return null;
    return readJson(reportPath);
  }

  saveReplayReport(caseId, task) {
    assertCaseId(caseId);
    const caseDirectory = path.join(this.caseRoot, caseId);
    if (!fs.existsSync(path.join(caseDirectory, "raw-trace.json"))) throw new Error("原生用例不存在");
    const reportsDirectory = path.join(caseDirectory, "reports");
    fs.mkdirSync(reportsDirectory, { recursive: true });
    const report = {
      schema_version: "0.1-windows-replay-report",
      task_id: task.id,
      case_id: task.caseId,
      case_name: task.caseName,
      status: task.status,
      created_at: task.createdAt,
      started_at: task.startedAt,
      finished_at: task.finishedAt,
      agent_id: task.agentId,
      completed_steps: task.currentStep,
      action_count: task.actionCount,
      error: task.error,
      results: task.results || [],
    };
    const timestamp = String(task.finishedAt || new Date().toISOString()).replace(/[:.]/g, "-");
    writeJson(path.join(reportsDirectory, `replay-${timestamp}.json`), report);
    writeJson(path.join(reportsDirectory, "latest-replay-report.json"), report);
    return report;
  }

  buildAcceptanceReport({ limit = 10 } = {}) {
    const selected = this.listCases().slice(0, Math.max(1, Math.min(100, Number(limit) || 10)));
    const cases = selected.map((item) => {
      try {
        const loaded = this.loadCase(item.id);
        const report = item.latestReplay;
        const actions = loaded.session.actions || [];
        const caseDirectory = path.join(this.caseRoot, item.id);
        const cachePath = path.join(caseDirectory, "cache-case.json");
        const reviewPath = path.join(caseDirectory, "review-trace.json");
        const rawPath = path.join(caseDirectory, "raw-trace.json");
        const generatedPath = path.join(caseDirectory, "generated-qta.json");
        const generatedExists = fs.existsSync(generatedPath);
        const generationInputPath = [cachePath, reviewPath, rawPath].find((candidate) => fs.existsSync(candidate));
        const generatedFresh = generatedExists &&
          fs.statSync(generatedPath).mtimeMs >= fs.statSync(generationInputPath).mtimeMs;
        const needsReviewCount = actions.filter((action) => action.needs_review).length;
        return {
          case_id: item.id,
          case_name: item.name,
          module: "未分类",
          action_count: actions.length,
          assertion_count: (loaded.session.assertions || []).length,
          input_text_count: actions.filter((action) => action.type === "input_text").length,
          needs_review_count: needsReviewCount,
          capture_status: loaded.rawTrace.finished_at ? "completed" : "incomplete",
          cache_status: loaded.cacheCase ? "generated" : "missing",
          replay_status: report?.status || "not_run",
          replay_finished_at: report?.finished_at || null,
          replay_completed_steps: report?.completed_steps || 0,
          replay_error: report?.error || null,
          generated_qta_status: !generatedExists ? "missing" : generatedFresh ? "fresh" : "stale",
          ready_for_qta_run: report?.status === "succeeded" && needsReviewCount === 0 && generatedFresh,
        };
      } catch (error) {
        return {
          case_id: item.id,
          case_name: item.name,
          module: "未分类",
          action_count: item.actionCount,
          assertion_count: 0,
          input_text_count: 0,
          needs_review_count: 1,
          capture_status: "load_error",
          cache_status: "unknown",
          generated_qta_status: "unknown",
          ready_for_qta_run: false,
          replay_status: item.latestReplay?.status || "not_run",
          replay_finished_at: item.latestReplay?.finished_at || null,
          replay_completed_steps: item.latestReplay?.completed_steps || 0,
          replay_error: `历史证据读取失败：${error.message}`,
        };
      }
    });
    const replayed = cases.filter((item) => item.replay_status !== "not_run");
    return {
      schema_version: "0.1-windows-acceptance-report",
      generated_at: new Date().toISOString(),
      requested_case_count: Number(limit) || 10,
      included_case_count: cases.length,
      summary: {
        capture_completed: cases.filter((item) => item.capture_status === "completed").length,
        cache_generated: cases.filter((item) => item.cache_status === "generated").length,
        replayed: replayed.length,
        replay_succeeded: replayed.filter((item) => item.replay_status === "succeeded").length,
        replay_failed: replayed.filter((item) => item.replay_status === "failed").length,
        needs_review: cases.reduce((sum, item) => sum + item.needs_review_count, 0),
        generated_qta: cases.filter((item) => ["fresh", "stale"].includes(item.generated_qta_status)).length,
        generated_qta_stale: cases.filter((item) => item.generated_qta_status === "stale").length,
        ready_for_qta_run: cases.filter((item) => item.ready_for_qta_run).length,
      },
      cases,
    };
  }

  saveReviewAssertions(caseId, submittedAssertions) {
    if (!Array.isArray(submittedAssertions)) throw new Error("assertions 必须是数组");
    if (submittedAssertions.length > 200) throw new Error("单个用例最多支持 200 个断言");
    const loaded = this.loadCase(caseId);
    const actionIds = new Set(loaded.session.actions.map((action) => action.id));
    const usedIds = new Set();
    const assertions = submittedAssertions.map((assertion, index) => {
      if (!SUPPORTED_ASSERTION_TYPES.has(assertion.type)) {
        throw new Error(`不支持的断言类型：${assertion.type}`);
      }
      if (!actionIds.has(assertion.after_action_id)) {
        throw new Error("断言关联的步骤不存在");
      }
      const preferredId = String(assertion.id || `assertion_${index + 1}`);
      const id = usedIds.has(preferredId)
        ? `assertion_${String(index + 1).padStart(3, "0")}`
        : preferredId;
      usedIds.add(id);
      const property = assertion.type === "property_equals" ? String(assertion.property || "") : null;
      if (property && !SUPPORTED_ASSERTION_PROPERTIES.has(property)) {
        throw new Error(`不支持的断言属性：${property}`);
      }
      if (["text_contains", "text_equals", "property_equals"].includes(assertion.type) && !String(assertion.expected ?? "")) {
        throw new Error("当前断言类型必须提供期望值");
      }
      const target = assertion.target && assertion.target.locator_bundle
        ? {
            semantic_role: String(assertion.target.semantic_role || "断言目标").slice(0, 200),
            locator_bundle: assertion.target.locator_bundle,
          }
        : null;
      const scope = assertion.scope || (!target && ["text_contains", "text_equals", "keywords_match_count"].includes(assertion.type)
        ? "window"
        : "target");
      if (!["window", "target", "nearest_ancestor"].includes(scope)) {
        throw new Error("断言验证范围无效");
      }
      if (scope !== "window" && !target) {
        throw new Error("当前断言类型必须提供目标 Locator");
      }
      if (assertion.assertion_mode === "component_name_equals") {
        const locator = target?.locator_bundle || {};
        const expectedName = String(assertion.expected ?? "").trim();
        if (assertion.type !== "property_equals" || assertion.property !== "name" || !expectedName || locator.name !== expectedName || !locator.control_type) {
          throw new Error("component_name_equals requires an exact named control locator");
        }
      }
      if (assertion.assertion_mode === "input_value_equals") {
        const locator = target?.locator_bundle || {};
        if (assertion.type !== "property_equals" || assertion.property !== "value" || !String(assertion.expected ?? "").trim() || !locator.control_type) {
          throw new Error("input_value_equals requires an exact value assertion and a typed control locator");
        }
      }
      if (scope === "nearest_ancestor" && !assertion.scope_control_type) {
        throw new Error("祖先范围断言必须提供控件类型");
      }
      const keywords = Array.isArray(assertion.keywords)
        ? assertion.keywords.map((item) => String(item).trim()).filter(Boolean).slice(0, 100)
        : [];
      if (assertion.type === "keywords_match_count" && keywords.length === 0) {
        throw new Error("关键词断言至少需要一个关键词");
      }
      const minimumMatches = Math.max(0, Number(assertion.minimum_matches || 0));
      if (assertion.type === "keywords_match_count" && (minimumMatches <= 0 || minimumMatches > keywords.length)) {
        throw new Error("关键词断言的最少命中数量必须大于零且不能超过关键词总数");
      }
      return {
        id,
        after_action_id: assertion.after_action_id,
        label: String(assertion.label || `断言 ${index + 1}`).slice(0, 200),
        type: assertion.type,
        assertion_mode: assertion.assertion_mode || null,
        target,
        expected: assertion.type === "target_exists" || assertion.type === "target_not_exists"
          ? null
          : String(assertion.expected ?? "").slice(0, 2000),
        property,
        scope,
        scope_control_type: assertion.scope_control_type || null,
        keywords,
        minimum_matches: minimumMatches,
        descendant_control_type: assertion.descendant_control_type || null,
        count_operator: assertion.count_operator || null,
        expected_count: Math.max(0, Number(assertion.expected_count || 0)),
        expected_rows: Math.max(0, Number(assertion.expected_rows || 0)),
        expected_columns: Math.max(0, Number(assertion.expected_columns || 0)),
        bounds_tolerance_px: Math.max(0, Number(assertion.bounds_tolerance_px || 0)),
        timeout_ms: Math.max(1000, Math.min(120000, Math.round(Number(assertion.timeout_ms || 10000)))),
        source: assertion.source || "manual_review",
        evidence: assertion.evidence || null,
        edited: true,
      };
    });
    const reviewTrace = {
      schema_version: "0.2-review-trace",
      case_id: caseId,
      source_raw_trace: `cases-native/${caseId}/raw-trace.json`,
      edited_at: new Date().toISOString(),
      actions: loaded.session.actions,
      assertions,
      edit_history: [
        ...(loaded.session.edit_history || []),
        { edited_at: new Date().toISOString(), assertion_count: assertions.length },
      ],
    };
    const caseDirectory = path.join(this.caseRoot, caseId);
    writeJson(path.join(caseDirectory, "review-trace.json"), reviewTrace);
    const refreshed = this.loadCase(caseId);
    writeJson(path.join(caseDirectory, "cache-case.json"), refreshed.cacheCase);
    return refreshed;
  }

  buildSession(caseId, caseDirectory, rawTrace, reviewTrace) {
    const snapshotCache = new Map();
    const rawActions = rawTrace.actions || [];
    const frameEntries = [];
    const frameByHierarchy = new Map();
    for (const rawAction of rawActions) {
      if (!rawAction.uia_snapshot && !rawAction.screenshot) continue;
      const key = rawAction.uia_snapshot || rawAction.screenshot;
      if (frameByHierarchy.has(key)) continue;
      const metadata = snapshotMetadata(caseDirectory, rawAction.uia_snapshot, snapshotCache);
      const frame = {
        id: `frame_${String(frameEntries.length + 1).padStart(3, "0")}`,
        timestamp_ms: millisecondsBetween(rawTrace.started_at, rawAction.timestamp),
        captured_at: metadata?.capturedAt || rawAction.timestamp,
        page_state: `after_${rawAction.id}`,
        src: rawAction.screenshot ? toWebPath("cases-native", caseId, rawAction.screenshot) : null,
        hierarchy: rawAction.uia_snapshot ? toWebPath("cases-native", caseId, rawAction.uia_snapshot) : null,
        hierarchy_format: "windows_uia_flat_json",
        node_count: metadata?.nodeCount || 0,
        window_bounds: metadata
          ? [metadata.originX, metadata.originY, metadata.originX + metadata.width, metadata.originY + metadata.height]
          : null,
        window_title: metadata?.title || rawTrace.target_window,
      };
      frameEntries.push(frame);
      frameByHierarchy.set(key, frame);
    }

    let previousFrame = frameEntries[0] || null;
    const mappedRawActions = rawActions.map((sourceRawAction, index) => {
      const rawAction = normalizeNamedContainerTarget(
        normalizeGenericChromeTarget(normalizeClickFromPreviousTransientSnapshot(
          normalizeCausalRawAction(sourceRawAction),
          index > 0 ? rawActions[index - 1] : null,
          caseDirectory), caseDirectory),
        caseDirectory);
      const currentFrame = frameByHierarchy.get(rawAction.uia_snapshot || rawAction.screenshot) || previousFrame;
      const beforeFrame = frameByHierarchy.get(rawAction.evidence_before_snapshot) || previousFrame || currentFrame;
      const afterFrame = frameByHierarchy.get(rawAction.evidence_after_snapshot) || currentFrame || beforeFrame;
      const metadata = snapshotMetadata(caseDirectory, rawAction.uia_snapshot, snapshotCache);
      const target = buildTarget(rawAction, metadata);
      const hoverAnchor = deriveHoverAnchor(caseDirectory, rawAction);
      const action = {
        id: rawAction.id || `action_${String(index + 1).padStart(4, "0")}`,
        source_action_id: rawAction.id || null,
        timestamp_ms: millisecondsBetween(rawTrace.started_at, rawAction.timestamp),
        type: rawAction.type,
        label: labelForAction(rawAction, target),
        value: rawAction.text || rawAction.key || undefined,
        coordinate: rawAction.window_point ? [rawAction.window_point.x, rawAction.window_point.y] : null,
        end_coordinate: rawAction.end_window_point ? [rawAction.end_window_point.x, rawAction.end_window_point.y] : null,
        wheel_delta: rawAction.wheel_delta || undefined,
        duration_ms: rawAction.duration_ms || undefined,
        condition_type: rawAction.condition_type || undefined,
        skip_if_false: rawAction.true_step_count || undefined,
        true_step_count: rawAction.true_step_count || undefined,
        false_step_count: rawAction.false_step_count || undefined,
        timeout_ms: rawAction.timeout_ms || undefined,
        condition_match_mode: rawAction.condition_match_mode || undefined,
        condition_scope_type: rawAction.condition_scope_type || undefined,
        condition_scope_start_name: rawAction.condition_scope_start_name || undefined,
        condition_scope_end_name: rawAction.condition_scope_end_name || undefined,
        frame_before: beforeFrame?.id || null,
        frame_after: afterFrame?.id || null,
        hierarchy_before: rawAction.evidence_before_snapshot
          ? toWebPath("cases-native", caseId, rawAction.evidence_before_snapshot)
          : beforeFrame?.hierarchy || null,
        hierarchy_after: rawAction.evidence_after_snapshot
          ? toWebPath("cases-native", caseId, rawAction.evidence_after_snapshot)
          : afterFrame?.hierarchy || null,
        page_before: beforeFrame?.page_state || "initial",
        page_after: afterFrame?.page_state || "captured",
        cache_step_id: `step_${String(index + 1).padStart(3, "0")}`,
        target,
        capture_diagnostics: rawAction.capture_diagnostics || null,
        confidence: rawAction.confidence,
        needs_review: Boolean(rawAction.needs_review),
        review_reason: rawAction.review_reason || null,
        capture_error: rawAction.capture_error || null,
        effects: rawAction.effects || [],
        hover_anchor_locator: hoverAnchor.locator,
        hover_anchor_match_mode: hoverAnchor.matchMode,
        raw_action: rawAction,
      };
      previousFrame = afterFrame || currentFrame || previousFrame;
      return action;
    });

    const normalizedRawActions = normalizeInputTransactions(mappedRawActions);
    const mappedRawActionsById = new Map(normalizedRawActions.map((action) => [action.source_action_id || action.id, action]));
    const actions = reviewTrace?.actions
      ? reviewTrace.actions.map((action) => {
          const sourceId = action.source_action_id || action.raw_action?.id || action.id;
          const repaired = mappedRawActionsById.get(sourceId);
          if (!repaired) return action;
          if (!repaired.raw_action?.causal_repair) {
            return {
              ...action,
              hover_anchor_locator: repaired.hover_anchor_locator,
              hover_anchor_match_mode: repaired.hover_anchor_match_mode,
            };
          }
          return {
            ...action,
            label: labelForAction(repaired.raw_action, repaired.target),
            target: repaired.target,
            effects: repaired.effects,
            capture_diagnostics: repaired.capture_diagnostics,
            hover_anchor_locator: repaired.hover_anchor_locator,
            hover_anchor_match_mode: repaired.hover_anchor_match_mode,
            raw_action: repaired.raw_action,
          };
        })
      : normalizedRawActions;
    actions.forEach((action, index) => {
      action.cache_step_id = `step_${String(index + 1).padStart(3, "0")}`;
    });
    const firstSnapshotPath = rawActions.find((action) => action.uia_snapshot)?.uia_snapshot;
    const firstMetadata = snapshotMetadata(caseDirectory, firstSnapshotPath, snapshotCache);
    return {
      schema_version: "0.1-native-review-session",
      session_id: rawTrace.session_id,
      case_id: caseId,
      case_name: rawTrace.case_name || caseId,
      capture_adapter: "YuanbaoRecorder.Agent",
      agent: rawTrace.agent || null,
      capture_adapter_status: rawTrace.finished_at ? "captured" : "incomplete",
      started_at: rawTrace.started_at,
      finished_at: rawTrace.finished_at || null,
      duration_ms: millisecondsBetween(rawTrace.started_at, rawTrace.finished_at || rawTrace.started_at),
      device: {
        platform: "windows",
        model: "Windows 执行机",
        os_version: os.release(),
        resolution: [firstMetadata?.width || 1216, firstMetadata?.height || 839],
        runtime: "interactive_desktop",
      },
      app: { name: "腾讯元宝", window_title: rawTrace.target_window || "腾讯元宝" },
      frames: frameEntries,
      actions,
      assertions: reviewTrace?.assertions ?? rawTrace.assertions ?? [],
      edit_history: reviewTrace?.edit_history || [],
      raw_trace_path: `cases-native/${caseId}/raw-trace.json`,
      review_trace_path: reviewTrace ? `cases-native/${caseId}/review-trace.json` : null,
    };
  }
}

module.exports = { NativeCaseStore, buildCacheCase, buildReplaySpec };
