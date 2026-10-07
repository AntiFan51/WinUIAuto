const fs = require("fs");
const path = require("path");

const OUTPUT_SCHEMA = {
  type: "object",
  additionalProperties: false,
  required: ["filename", "code", "summary", "warnings"],
  properties: {
    filename: { type: "string", pattern: "^[a-z0-9_]+\\.py$" },
    code: { type: "string", minLength: 80 },
    summary: { type: "string" },
    warnings: { type: "array", items: { type: "string" } },
  },
};

const ALLOWED_KIT_METHODS = new Set([
  "step", "screenshot", "click", "right_click", "click_after_hover", "input_text", "press", "scroll", "drag",
  "wait_seconds", "wait_exists", "wait_not_exists", "exists", "exists_in_section", "expect_exists", "expect_not_exists",
  "expect_text_contains", "expect_text_equals", "expect_property_equals", "expect_keywords",
]);

function readUtf8(filePath) {
  return fs.readFileSync(filePath, "utf8");
}

function assertSafeCaseId(caseId) {
  const value = typeof caseId === "string" ? caseId.trim() : "";
  const invalid = /[<>:"/\\|?*\u0000-\u001F]/u.test(value);
  if (!value || value.length > 180 || value === "." || value === ".." ||
      value !== caseId || value.endsWith(".") || invalid || path.basename(value) !== value) {
    throw new Error("用例 ID 不正确");
  }
}

function stripMarkdownFence(value) {
  const text = String(value || "").trim();
  const match = /^```(?:python)?\s*\n([\s\S]*?)\n```$/i.exec(text);
  return match ? match[1].trim() : text;
}

function requiredTimelineEvents(cacheCase) {
  return Array.isArray(cacheCase?.timeline_events) ? cacheCase.timeline_events : [];
}

function escapePythonRegex(value) {
  return String(value || "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function stableClassPattern(value) {
  const firstToken = String(value || "").trim().split(/\s+/u)[0] || "";
  if (!firstToken) return null;
  const stableToken = firstToken.replace(/_{2,3}[A-Za-z0-9_-]+$/u, "");
  if (!stableToken || stableToken === "Group") return null;
  return {
    token: stableToken,
    pattern: `(?:^|\\s)${escapePythonRegex(stableToken)}(?:_{2,3}[A-Za-z0-9_-]+)?(?:\\s|$)`,
  };
}

function isMeaningfulLocatorCandidate(candidate) {
  if (!candidate || !candidate.type) return false;
  if (candidate.type === "control_type") return false;
  if (candidate.type === "coordinate_fallback") return false;
  if (candidate.type === "class_name" && !String(candidate.value || "").trim()) return false;
  if (candidate.type === "class_name_re" && !String(candidate.value || "").trim()) return false;
  return true;
}

function locatorCandidatesFromBundle(bundle) {
  if (!bundle || typeof bundle !== "object") return [];
  const candidates = [];
  const controlType = bundle.control_type || null;
  if (bundle.automation_id) {
    candidates.push({
      type: "automation_id",
      value: String(bundle.automation_id),
      control_type: controlType,
      confidence: 1.0,
      stability: /^view_\d+$/u.test(String(bundle.automation_id)) ? "session_only" : "stable",
    });
  }
  if (bundle.name && controlType) {
    candidates.push({
      type: "name_and_control_type",
      value: { name: String(bundle.name), control_type: String(controlType) },
      confidence: 0.9,
      stability: "semantic",
    });
  }
  const classPattern = stableClassPattern(bundle.class_name);
  if (classPattern && controlType) {
    candidates.push({
      type: "class_name_re",
      value: classPattern.pattern,
      stable_token: classPattern.token,
      control_type: String(controlType),
      confidence: 0.75,
      stability: "stable_token",
    });
  }
  if (bundle.class_name && controlType) {
    candidates.push({
      type: "control_type_and_class",
      value: { control_type: String(controlType), class_name: String(bundle.class_name).split(/\s+/u)[0] },
      confidence: 0.65,
      stability: "exact_snapshot",
    });
  }
  return candidates.filter(isMeaningfulLocatorCandidate);
}

function locatorCandidatesFromDiagnostic(node, source) {
  if (!node || typeof node !== "object") return [];
  return locatorCandidatesFromBundle({
    automation_id: node.automation_id || "",
    name: node.name || "",
    control_type: node.control_type || "",
    class_name: node.class_name || "",
  }).map((candidate) => ({ ...candidate, source }));
}

function sameBounds(left, right) {
  if (!left || !right) return false;
  return ["x", "y", "width", "height"].every((key) =>
    Math.abs(Number(left[key] ?? NaN) - Number(right[key] ?? NaN)) <= 2);
}

function uniqueLocatorCandidates(candidates) {
  const seen = new Set();
  const output = [];
  for (const candidate of candidates.filter(isMeaningfulLocatorCandidate)) {
    const key = `${candidate.type}:${JSON.stringify(candidate.value)}:${candidate.control_type || ""}`;
    if (seen.has(key)) continue;
    seen.add(key);
    output.push(candidate);
  }
  return output;
}

function targetLocatorContracts(cacheCase) {
  const contracts = [];
  for (const step of cacheCase?.steps || []) {
    const candidates = uniqueLocatorCandidates(step.target?.locators || []);
    if (candidates.length > 1) {
      contracts.push({
        kind: "step",
        id: step.id,
        action: step.action || null,
        label: step.label || null,
        candidates,
      });
    }
  }
  for (const assertion of cacheCase?.assertions || []) {
    const diagnostics = assertion.evidence?.capture_diagnostics || {};
    const targetBounds = assertion.target?.node?.bounds || diagnostics.after_snapshot_target?.bounds || null;
    const hitLeaf = diagnostics.hit_leaf_target || null;
    const baseCandidates = assertion.target?.locators ||
      locatorCandidatesFromBundle(assertion.target?.locator_bundle);
    const hitLeafCandidates = sameBounds(targetBounds, hitLeaf?.bounds)
      ? locatorCandidatesFromDiagnostic(hitLeaf, "hit_leaf_target")
      : [];
    const candidates = uniqueLocatorCandidates([...baseCandidates, ...hitLeafCandidates]);
    if (candidates.length > 1) {
      contracts.push({ kind: "assertion", id: assertion.id, label: assertion.label || null, candidates });
    }
  }
  return contracts;
}

function codeContainsLocatorCandidate(code, candidate) {
  if (!candidate) return false;
  if (candidate.type === "automation_id") {
    return code.includes(String(candidate.value));
  }
  if (candidate.type === "class_name_re") {
    return code.includes(String(candidate.value)) || code.includes(String(candidate.stable_token || ""));
  }
  if (candidate.type === "name_and_control_type" || candidate.type === "name_control_type") {
    const name = candidate.value?.name;
    const controlType = candidate.value?.control_type;
    return Boolean(name && controlType && code.includes(String(name)) && code.includes(String(controlType)));
  }
  if (candidate.type === "control_type_and_class") {
    const className = candidate.value?.class_name;
    const controlType = candidate.value?.control_type;
    return Boolean(className && controlType && code.includes(String(className)) && code.includes(String(controlType)));
  }
  if (candidate.type === "class_name") {
    return code.includes(String(candidate.value));
  }
  return false;
}

function clickTargetDeclaration(code, stepId) {
  const markerIndex = code.indexOf(`cache:${stepId}`);
  if (markerIndex < 0) return "";
  const nextMarkerIndex = code.indexOf("cache:", markerIndex + `cache:${stepId}`.length);
  const stepCode = code.slice(markerIndex, nextMarkerIndex < 0 ? code.length : nextMarkerIndex);
  const call = /kit\.(?:click|right_click)\(\s*([A-Z][A-Z0-9_]*)/u.exec(stepCode);
  if (!call) return stepCode;
  const targetName = call[1];
  const declarationPattern = new RegExp(`^${targetName}\\s*=\\s*Target\\(`, "mu");
  const declaration = declarationPattern.exec(code);
  if (!declaration) return stepCode;
  const tail = code.slice(declaration.index);
  const nextDeclaration = /\n(?:[A-Z][A-Z0-9_]*\s*=\s*Target\(|class\s+)/u.exec(tail.slice(declaration[0].length));
  const end = nextDeclaration
    ? declaration[0].length + nextDeclaration.index
    : tail.length;
  return tail.slice(0, end);
}

function validateLocatorContracts(code, cacheCase) {
  for (const contract of targetLocatorContracts(cacheCase)) {
    const stableCandidates = contract.candidates.filter((candidate) =>
      ["stable", "semantic", "stable_token"].includes(candidate.stability) ||
      ["automation_id", "name_and_control_type", "name_control_type", "class_name_re"].includes(candidate.type));
    if (contract.kind === "step" && contract.action === "scroll") {
      // A scroll target identifies a safe interaction region, not a business
      // leaf that must keep the same generated answer text. One stable
      // structural locator is sufficient for the gesture.
      const structuralCandidates = stableCandidates.filter((candidate) =>
        ["automation_id", "class_name_re", "control_type_and_class"].includes(candidate.type));
      if (!structuralCandidates.length) continue;
      if (!structuralCandidates.some((candidate) => codeContainsLocatorCandidate(code, candidate))) {
        throw new Error(`AI generated code violates scroll locator contract: ${contract.id} must preserve at least one stable structural locator candidate; recorded response text is not required.`);
      }
      continue;
    }
    const locatorCode = contract.kind === "step" && ["click", "right_click"].includes(contract.action)
      ? clickTargetDeclaration(code, contract.id)
      : code;
    const matched = stableCandidates.filter((candidate) => codeContainsLocatorCandidate(locatorCode, candidate));
    const requiredHitLeaf = stableCandidates.filter((candidate) => candidate.source === "hit_leaf_target");
    if (contract.kind === "step" && ["click", "right_click"].includes(contract.action) &&
        (!stableCandidates.length || !matched.length)) {
      throw new Error(`AI generated code violates locator contract: ${contract.kind} ${contract.id} must use a live UIA locator in the Target passed to the click; coordinate fallback is recording evidence only.`);
    }
    if (requiredHitLeaf.length && !requiredHitLeaf.some((candidate) => codeContainsLocatorCandidate(locatorCode, candidate))) {
      throw new Error(`AI generated code violates locator contract: ${contract.kind} ${contract.id} must preserve a stable hit_leaf_target locator captured under the user's pointer; coordinates are not a substitute for this UIA identity.`);
    }
    if (stableCandidates.length < 2) continue;
    if (matched.length < 2) {
      throw new Error(`AI generated code violates locator contract: ${contract.kind} ${contract.id} must preserve at least two stable locator candidates when Cache Case provides them.`);
    }
  }
}

function validateTimelineOrder(code, cacheCase) {
  const events = requiredTimelineEvents(cacheCase);
  if (!events.length) return;
  const unbound = events.filter((event) => event.kind === "assertion" && !event.after_step_id);
  if (unbound.length) {
    throw new Error(`Cache Case 存在未绑定到动作的断言，无法安全生成：${unbound.map((event) => event.assertion_id).join("、")}`);
  }
  let previousOffset = -1;
  for (const event of events) {
    const cacheId = event.kind === "action" ? event.step_id : event.assertion_id;
    const marker = `cache:${cacheId}`;
    const offset = code.indexOf(marker);
    if (offset < 0) throw new Error(`AI 生成代码遗漏有序事件标记：${marker}`);
    if (offset <= previousOffset) {
      throw new Error(`AI 生成代码未保持 Cache Case 事件顺序：${marker} 出现在前一事件之前`);
    }
    previousOffset = offset;
  }
}

function appendMarkerToStepLine(line, marker) {
  const match = /^(.*kit\.step\(\s*)(["'])(.*)(\2\s*\)\s*)$/u.exec(line);
  if (!match) return null;
  const prefix = match[1];
  const quote = match[2];
  const label = match[3].trimEnd();
  const suffix = match[4];
  const separator = label ? " " : "";
  return `${prefix}${quote}${label}${separator}[${marker}]${suffix}`;
}

function addMissingTimelineMarkers(result, cacheCase) {
  const events = requiredTimelineEvents(cacheCase);
  if (!events.length || !result || typeof result !== "object") return result;
  const originalCode = stripMarkdownFence(result.code);
  const lines = originalCode.split(/\r?\n/u);
  const stepLines = lines
    .map((line, index) => ({ line, index }))
    .filter((item) => /\bkit\.step\s*\(/u.test(item.line));
  if (!stepLines.length) return result;

  let cursor = 0;
  const added = [];
  for (const event of events) {
    const cacheId = event.kind === "action" ? event.step_id : event.assertion_id;
    const marker = `cache:${cacheId}`;
    const existingIndex = lines.findIndex((line) => line.includes(marker));
    if (existingIndex >= 0) {
      cursor = Math.max(cursor, existingIndex + 1);
      continue;
    }
    const target = stepLines.find((item) =>
      item.index >= cursor && !/cache:(?:step|assertion)_\d+/u.test(lines[item.index]));
    if (!target) return result;
    const patched = appendMarkerToStepLine(lines[target.index], marker);
    if (!patched) return result;
    lines[target.index] = patched;
    cursor = target.index + 1;
    added.push(marker);
  }

  if (!added.length) return result;
  return {
    ...result,
    code: `${lines.join("\n").trim()}\n`,
    warnings: [
      ...(Array.isArray(result.warnings) ? result.warnings : []),
      `已自动补齐执行事件标记：${added.join("、")}`,
    ],
  };
}

function validateAssertionLogging(code, cacheCase) {
  for (const assertion of cacheCase?.assertions || []) {
    if (!assertion.id) continue;
    const marker = `cache:${assertion.id}`;
    const stepPattern = new RegExp(`kit\\.step\\(\\s*[^\\n]*${marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`);
    if (!stepPattern.test(code)) {
      throw new Error(`AI 生成代码遗漏断言执行日志：${assertion.id} 必须先调用 kit.step 并保留 [${marker}]`);
    }
  }
}

function validateAssertionTimeouts(code, cacheCase) {
  for (const assertion of cacheCase?.assertions || []) {
    if (!assertion.id || !assertion.timeout_ms) continue;
    const marker = `cache:${assertion.id}`;
    const markerOffset = code.indexOf(marker);
    const nextMarkerOffset = markerOffset < 0
      ? -1
      : code.indexOf("cache:", markerOffset + marker.length);
    const assertionCode = markerOffset < 0
      ? ""
      : code.slice(markerOffset, nextMarkerOffset < 0 ? code.length : nextMarkerOffset);
    const timeoutSeconds = Math.max(1, Math.ceil(Number(assertion.timeout_ms) / 1000));
    if (!new RegExp(`timeout\\s*=\\s*${timeoutSeconds}\\b`).test(assertionCode)) {
      throw new Error(`AI generated code did not preserve assertion timeout: ${assertion.id} must use timeout=${timeoutSeconds} for asynchronous polling.`);
    }
  }
}

function validateHoverRevealSteps(code, cacheCase) {
  for (const step of cacheCase?.steps || []) {
    if (!step.hover_reveal || !step.id) continue;
    const marker = `cache:${step.id}`;
    const markerOffset = code.indexOf(marker);
    const nextMarkerOffset = markerOffset < 0
      ? -1
      : code.indexOf("cache:", markerOffset + marker.length);
    const stepCode = markerOffset < 0
      ? ""
      : code.slice(markerOffset, nextMarkerOffset < 0 ? code.length : nextMarkerOffset);
    if (!/kit\.click_after_hover\s*\(/u.test(stepCode)) {
      throw new Error(`AI generated code dropped hover reveal semantics: ${step.id} must use kit.click_after_hover with the recorded hover anchor.`);
    }
  }
}

function validateGeneratedResult(result, cacheCase = null) {
  if (!result || typeof result !== "object") throw new Error("AI 返回结果不是对象");
  const filename = String(result.filename || "").trim();
  if (!/^[a-z0-9_]+\.py$/.test(filename)) throw new Error("AI 返回的 Python 文件名不安全");
  const code = stripMarkdownFence(result.code);
  if (!/\bdef\s+run_test\s*\(/u.test(code) && /`?BLOCKED:/u.test(code)) {
    const reason = code.replace(/\s+/gu, " ").trim().slice(0, 1000);
    throw new Error(`AI refused to generate Python and returned BLOCKED prose: ${reason}`);
  }
  if (code.length < 80) throw new Error("AI 返回的代码内容过短");
  const requiredSignals = [
    "from qta_windows_base.base import YBWindowsQtaBase",
    "from yuanbao_windows_kit import Locator, Target",
    "YBWindowsQtaBase",
    "run_test",
  ];
  const missing = requiredSignals.filter((signal) => !code.includes(signal));
  const validationWarnings = [];
  if (missing.length) validationWarnings.push(`可能缺少必要结构：${missing.join("、")}`);
  const forbidden = [
    { pattern: /expect\s*\([^\n,]+,\s*True\s*\)/, label: "恒真 expect" },
    { pattern: /assert\s+True\b/, label: "恒真 assert" },
    { pattern: /backend\s*=\s*["']win32["']/, label: "错误的 win32 backend" },
    { pattern: /(?:from|import)\s+pywinauto\b/, label: "绕过公共 Kit 直接导入 pywinauto" },
    { pattern: /class\s+\w*(?:Driver|Kit)\b/, label: "在用例中重复实现 Driver/Kit" },
    { pattern: /\bself\.fail\s*\(/, label: "绕过公共断言直接 self.fail" },
  ];
  const hits = forbidden.filter((item) => item.pattern.test(code)).map((item) => item.label);
  if (hits.length) validationWarnings.push(`发现不推荐写法：${hits.join("、")}`);
  const unknownKitMethods = Array.from(code.matchAll(/\bkit\.(\w+)\s*\(/g))
    .map((match) => match[1])
    .filter((name) => !ALLOWED_KIT_METHODS.has(name));
  if (unknownKitMethods.length) {
    validationWarnings.push(`可能调用未知 Kit API：${Array.from(new Set(unknownKitMethods)).join("、")}`);
  }
  const requiredStepIds = (cacheCase?.steps || []).map((step) => step.id).filter(Boolean);
  const missingStepIds = requiredStepIds.filter((id) => !code.includes(id));
  if (missingStepIds.length) {
    validationWarnings.push(`生成代码遗漏 Cache Case 步骤：${missingStepIds.join("、")}`);
  }
  validateTimelineOrder(code, cacheCase);
  const scopedConditions = (cacheCase?.steps || []).filter((step) =>
    step.action === "condition" && step.condition?.scope?.type === "section");
  for (const step of scopedConditions) {
    const startName = String(step.condition.scope.start_name || "");
    const endName = String(step.condition.scope.end_name || "");
    const stepOffset = code.indexOf(step.id);
    const conditionCode = stepOffset >= 0 ? code.slice(stepOffset, stepOffset + 1200) : code;
    const preservesScope = conditionCode.includes("kit.exists_in_section(") &&
      conditionCode.includes(JSON.stringify(startName)) && conditionCode.includes(JSON.stringify(endName));
    if (!preservesScope) {
      throw new Error(`AI 生成代码丢失条件范围语义：${step.id} 必须在 ${startName} 到 ${endName} 区域内判断，禁止降级为全窗口 exists`);
    }
  }
  const windowKeywordAssertions = (cacheCase?.assertions || []).filter((assertion) =>
    assertion.type === "keywords_match_count" && assertion.scope === "window");
  for (const assertion of windowKeywordAssertions) {
    const submittedText = (cacheCase?.steps || [])
      .filter((step) => step.action === "input_text")
      .map((step) => String(step.input?.text || ""))
      .join("\n");
    const promptHits = (assertion.keywords || []).filter((keyword) => submittedText.includes(String(keyword)));
    if (promptHits.length >= Number(assertion.minimum_matches || 1)) {
      throw new Error(`窗口关键词断言可能被输入文本误触发：${assertion.id} 必须改为回答目标范围`);
    }
    const assertionOffset = code.indexOf(assertion.id);
    const assertionCode = assertionOffset >= 0 ? code.slice(assertionOffset, assertionOffset + 1200) : code;
    if (!/kit\.expect_keywords\(\s*None\s*,/.test(assertionCode)) {
      throw new Error(`AI 生成代码缩小了窗口关键词断言范围：${assertion.id} 必须使用 kit.expect_keywords(None, ...)`);
    }
    const timeoutSeconds = Math.max(1, Math.ceil(Number(assertion.timeout_ms || 10000) / 1000));
    if (!new RegExp(`timeout\\s*=\\s*${timeoutSeconds}\\b`).test(assertionCode)) {
      throw new Error(`AI 生成代码未保留断言超时：${assertion.id} 必须使用 timeout=${timeoutSeconds}`);
    }
  }
  validateAssertionTimeouts(code, cacheCase);
  validateHoverRevealSteps(code, cacheCase);
  validateLocatorContracts(code, cacheCase);
  validateAssertionLogging(code, cacheCase);
  if (code.split(/\r?\n/).length > 220) validationWarnings.push("代码超过 220 行，建议继续复用公共 Kit 精简");
  return {
    filename,
    code: `${code.trim()}\n`,
    summary: String(result.summary || "").trim(),
    warnings: [
      ...(Array.isArray(result.warnings) ? result.warnings.map(String) : []),
      ...validationWarnings,
    ],
  };
}

function compactCacheCase(cacheCase) {
  const clone = JSON.parse(JSON.stringify(cacheCase || {}));
  const trimEvidence = (value) => {
    // Semantic arrays (especially steps, assertions and locator bundles) must
    // remain complete. Truncating every array silently removed actions after
    // step 8 from the model input for longer recorded cases.
    if (Array.isArray(value)) return value.map(trimEvidence);
    if (!value || typeof value !== "object") return value;
    const output = {};
    for (const [key, nested] of Object.entries(value)) {
      if (["raw_uia_tree", "hierarchy", "full_tree", "image_base64"].includes(key)) continue;
      output[key] = trimEvidence(nested);
    }
    return output;
  };
  return trimEvidence(clone);
}

function extractOutputText(payload) {
  const chatContent = payload.choices?.[0]?.message?.content;
  if (typeof chatContent === "string" && chatContent.trim()) return chatContent;
  if (typeof payload.output_text === "string" && payload.output_text.trim()) return payload.output_text;
  for (const item of payload.output || []) {
    for (const content of item.content || []) {
      if (typeof content.text === "string" && content.text.trim()) return content.text;
    }
  }
  throw new Error("AI API 未返回可用文本");
}

class QtaCodeGenerator {
  constructor({ rootDirectory, fetchImpl = fetch, exportDirectory } = {}) {
    this.rootDirectory = rootDirectory;
    this.fetchImpl = fetchImpl;
    this.skillDirectory = path.join(rootDirectory, "skills", "yuanbao-windows-qta-codegen");
    this.exportDirectory = path.resolve(
      exportDirectory || path.join(rootDirectory, "..", "qta_runtime", "qta_cases", "windows_generated"),
    );
  }

  getConfig() {
    const localPath = path.join(this.rootDirectory, "config.local.json");
    let local = {};
    if (fs.existsSync(localPath)) {
      try {
        local = JSON.parse(readUtf8(localPath));
      } catch (error) {
        throw new Error(`config.local.json 格式不正确：${error.message}`);
      }
    }
    const baseUrl = String(process.env.AI_BASE_URL || process.env.OPENAI_BASE_URL || local.baseUrl || "https://api.openai.com/v1").replace(/\/+$/, "");
    const usesOpenAI = /^https:\/\/api\.openai\.com(?:\/|$)/i.test(baseUrl);
    const hasProviderKey = Object.prototype.hasOwnProperty.call(local, "openaiApiKey");
    const localApiKey = usesOpenAI && hasProviderKey ? local.openaiApiKey : local.apiKey;
    const apiKey = String(process.env.AI_API_KEY || process.env.OPENAI_API_KEY || localApiKey || "").trim();
    const model = String(process.env.AI_MODEL || process.env.OPENAI_MODEL || local.model || "gpt-5").trim();
    const apiStyle = String(process.env.AI_API_STYLE || local.apiStyle || "responses").trim();
    const placeholder = /请在这里|your[-_ ]?(key|api)/i.test(apiKey);
    if (!["responses", "chat_completions"].includes(apiStyle)) {
      throw new Error("apiStyle 只支持 responses 或 chat_completions");
    }
    return {
      configured: Boolean(apiKey) && !placeholder,
      apiKey,
      baseUrl,
      model,
      apiStyle,
      source: fs.existsSync(localPath) ? "config.local.json" : "environment",
    };
  }

  status() {
    const config = this.getConfig();
    return {
      configured: config.configured,
      model: config.model,
      baseUrl: config.baseUrl,
      apiStyle: config.apiStyle,
      source: config.source,
      skill: "yuanbao-windows-qta-codegen",
    };
  }

  buildInstructions() {
    return [
      readUtf8(path.join(this.skillDirectory, "SKILL.md")),
      readUtf8(path.join(this.skillDirectory, "references", "qta-style.md")),
      readUtf8(path.join(this.skillDirectory, "references", "windows-runtime.md")),
    ].join("\n\n---\n\n");
  }

  outputPath(caseId) {
    assertSafeCaseId(caseId);
    return path.join(this.rootDirectory, "cases-native", caseId, "generated-qta.json");
  }

  diagnosticDirectory(caseId) {
    assertSafeCaseId(caseId);
    return path.join(this.rootDirectory, "cases-native", caseId, "codegen-diagnostics");
  }

  saveGenerationAttempt(caseId, payload) {
    const directory = this.diagnosticDirectory(caseId);
    fs.mkdirSync(directory, { recursive: true });
    const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
    const record = {
      schema_version: "0.1-codegen-diagnostic",
      case_id: caseId,
      saved_at: new Date().toISOString(),
      ...payload,
    };
    const body = `${JSON.stringify(record, null, 2)}\n`;
    fs.writeFileSync(path.join(directory, `attempt-${timestamp}.json`), body, "utf8");
    fs.writeFileSync(path.join(directory, "latest-failure.json"), body, "utf8");
  }

  load(caseId) {
    const filePath = this.outputPath(caseId);
    return fs.existsSync(filePath) ? JSON.parse(readUtf8(filePath)) : null;
  }

  export(caseId) {
    const generated = this.load(caseId);
    if (!generated) throw new Error("当前用例还没有生成 QTA 代码");
    const filename = String(generated.filename || "").trim();
    if (!/^[a-z0-9_]+\.py$/.test(filename)) throw new Error("生成代码的文件名不安全");
    const code = String(generated.code || "");
    if (!code.trim()) throw new Error("生成代码为空，无法导出");

    fs.mkdirSync(this.exportDirectory, { recursive: true });
    const candidatePath = path.resolve(this.exportDirectory, filename);
    const exportPrefix = `${this.exportDirectory}${path.sep}`;
    if (!candidatePath.startsWith(exportPrefix)) throw new Error("导出路径不安全");

    let outputPath = candidatePath;
    let sequence = 2;
    while (fs.existsSync(outputPath) && readUtf8(outputPath) !== code) {
      const parsed = path.parse(filename);
      outputPath = path.join(this.exportDirectory, `${parsed.name}_${sequence}${parsed.ext}`);
      sequence += 1;
    }
    const reused = fs.existsSync(outputPath);
    if (!reused) {
      const temporaryPath = `${outputPath}.${process.pid}.${Date.now()}.tmp`;
      fs.writeFileSync(temporaryPath, code, "utf8");
      fs.renameSync(temporaryPath, outputPath);
    }
    return { path: outputPath, filename: path.basename(outputPath), reused };
  }

  async generate(caseId, cacheCase, options = {}) {
    const config = this.getConfig();
    if (!config.configured) throw new Error("尚未配置 AI API Key，请填写 config.local.json 或设置 AI_API_KEY");
    const context = {
      task: "使用已经固定的 YBWindowsQtaBase 和 YBWindowsKit，根据 Cache Case 只声明目标并组合测试步骤，生成精简、可审查的元宝 Windows QTA Python 用例。",
      owner: String(options.owner || "yuanbao-ai").trim().slice(0, 80),
      required_step_manifest: (cacheCase?.steps || []).map((step) => ({
        id: step.id,
        source_action_id: step.source_action_id || step.evidence?.source_action || null,
        action: step.action,
        label: step.label,
        condition: step.condition || null,
      })),
      required_assertion_manifest: (cacheCase?.assertions || []).map((assertion) => ({
        id: assertion.id,
        type: assertion.type,
        assertion_mode: assertion.assertion_mode || null,
        after_action_id: assertion.after_action_id,
        after_step_id: assertion.after_step_id || null,
      })),
      required_event_manifest: requiredTimelineEvents(cacheCase).map((event) => ({
        event_id: event.event_id,
        kind: event.kind,
        cache_marker: `cache:${event.kind === "action" ? event.step_id : event.assertion_id}`,
        step_id: event.step_id || null,
        assertion_id: event.assertion_id || null,
        action: event.action || null,
        assertion_type: event.type || null,
        after_step_id: event.after_step_id || null,
      })),
      target_locator_contracts: targetLocatorContracts(cacheCase),
      locator_contract_policy: {
        contracts_only_list_extra_preservation_requirements: true,
        absence_from_target_locator_contracts_does_not_mean_missing_locator_data: true,
        use_step_target_locators_as_source_of_truth: true,
        coordinate_fallback_is_recording_evidence_only_and_never_sufficient_for_click: true,
        click_requires_a_live_uia_locator_in_the_target_actually_passed_to_kit_click: true,
        scroll_requires_stable_region_or_scaled_coordinate_not_volatile_answer_text: true,
        never_return_blocked_prose_in_code: true,
      },
      cache_case: compactCacheCase(cacheCase),
    };
    const instructions = this.buildInstructions();
    const isChatCompletions = config.apiStyle === "chat_completions";
    const requestUrl = `${config.baseUrl}/${isChatCompletions ? "chat/completions" : "responses"}`;
    const requestGeneration = async (input, repairInstructions = "") => {
      const effectiveInstructions = repairInstructions ? `${instructions}\n\n${repairInstructions}` : instructions;
      const requestBody = isChatCompletions
        ? {
            model: config.model,
            messages: [
              { role: "system", content: effectiveInstructions },
              { role: "user", content: input },
            ],
            response_format: {
              type: "json_schema",
              json_schema: {
                name: "yuanbao_windows_qta_code",
                strict: true,
                schema: OUTPUT_SCHEMA,
              },
            },
            max_completion_tokens: 8192,
          }
        : {
            model: config.model,
            instructions: effectiveInstructions,
            input,
            text: {
              format: {
                type: "json_schema",
                name: "yuanbao_windows_qta_code",
                strict: true,
                schema: OUTPUT_SCHEMA,
              },
            },
          };
      if (isChatCompletions && /moonshot\.(cn|ai)/i.test(config.baseUrl)) {
        requestBody.thinking = { type: "enabled" };
        requestBody.response_format = { type: "json_object" };
      }
      const response = await this.fetchImpl(requestUrl, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${config.apiKey}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify(requestBody),
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        const message = payload.error?.message || payload.error || `AI API 请求失败：HTTP ${response.status}`;
        throw new Error(String(message));
      }
      try {
        return JSON.parse(extractOutputText(payload));
      } catch (error) {
        throw new Error(`AI 返回结果无法解析：${error.message}`);
      }
    };

    const maximumRepairAttempts = 2;
    let repairAttempts = 0;
    let previousResult = null;
    let validationError = null;
    let generated;
    while (!generated) {
      const isRepair = repairAttempts > 0;
      const input = isRepair
        ? JSON.stringify({
            task: "修复上一轮生成的 QTA Python 代码。必须完整返回修复后的 JSON 结果，不得只返回补丁或说明。",
            original_context: context,
            previous_result: previousResult,
            validator_feedback: validationError,
            repair_rules: [
              "If previous_result.code contains BLOCKED prose, replace it with a complete Python case. Never keep explanatory prose in code.",
              "target_locator_contracts lists only extra multi-candidate requirements. A step absent from that list is not missing locator data; use cache_case.steps[].target.locators.",
              "For click/right-click, coordinate_fallback is recording evidence only. The Target actually passed to kit.click/right_click must contain a live UIA locator, including any required hit_leaf_target candidate.",
              "只修复 validator_feedback 指出的缺陷，同时保留所有已正确的有序事件标记和断言。",
              "不得删除步骤、断言或将事件重排。",
              "修复后逐项确认 required_event_manifest 中每个 cache_marker 均在代码中恰好按顺序出现。",
            ],
          }, null, 2)
        : JSON.stringify(context, null, 2);
      const parsed = await requestGeneration(input, isRepair
        ? "这是一次验证失败后的定点修复轮次。必须以 validator_feedback 为准修复完整代码；不要解释失败原因，不要输出局部补丁。"
        : "");
      let patched = null;
      try {
        patched = addMissingTimelineMarkers(parsed, cacheCase);
        generated = validateGeneratedResult(patched, cacheCase);
      } catch (error) {
        this.saveGenerationAttempt(caseId, {
          repair_attempt: repairAttempts,
          is_repair: isRepair,
          validation_error: String(error.message || error),
          parsed_result: parsed,
          patched_result: patched,
        });
        if (repairAttempts >= maximumRepairAttempts) {
          throw new Error(`AI 自动修复 ${maximumRepairAttempts} 次后仍未通过生成校验：${error.message}`);
        }
        previousResult = parsed;
        validationError = String(error.message || error);
        repairAttempts += 1;
      }
    }
    if (repairAttempts) {
      generated.warnings.push(`已根据生成校验反馈自动修复 ${repairAttempts} 次。`);
    }
    const record = {
      schema_version: "0.1-generated-qta",
      case_id: caseId,
      generated_at: new Date().toISOString(),
      model: config.model,
      api_style: config.apiStyle,
      skill: "yuanbao-windows-qta-codegen",
      ...generated,
    };
    const outputPath = this.outputPath(caseId);
    const temporaryPath = `${outputPath}.tmp`;
    fs.writeFileSync(temporaryPath, `${JSON.stringify(record, null, 2)}\n`, "utf8");
    fs.renameSync(temporaryPath, outputPath);
    return record;
  }
}

module.exports = {
  QtaCodeGenerator,
  addMissingTimelineMarkers,
  compactCacheCase,
  extractOutputText,
  requiredTimelineEvents,
  validateGeneratedResult,
};
