const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const test = require("node:test");
const {
  QtaCodeGenerator,
  addMissingTimelineMarkers,
  compactCacheCase,
  extractOutputText,
  validateGeneratedResult,
} = require("../lib/qta-code-generator");

test("compactCacheCase preserves every semantic step and assertion", () => {
  const cacheCase = {
    steps: Array.from({ length: 11 }, (_, index) => ({ id: `step_${index + 1}` })),
    assertions: Array.from({ length: 10 }, (_, index) => ({ id: `assertion_${index + 1}` })),
  };

  const compacted = compactCacheCase(cacheCase);

  assert.equal(compacted.steps.length, 11);
  assert.equal(compacted.assertions.length, 10);
  assert.equal(compacted.steps.at(-1).id, "step_11");
});

const validCode = `# -*- coding: utf-8 -*-
from qta_windows_base.base import YBWindowsQtaBase
from yuanbao_windows_kit import Locator, Target

CHAT_INPUT = Target("聊天输入框", (Locator("automation_id", "searchbar-editor"),))

class YuanbaoGeneratedCase(YBWindowsQtaBase):
    def run_test(self):
        kit = self.kit
        kit.step("步骤1：连接元宝")
        kit.expect_exists(CHAT_INPUT)
`;

test("validates and normalizes a direct AI code result", () => {
  const result = validateGeneratedResult({
    filename: "ask_vpn.py",
    code: validCode,
    summary: "验证提问",
    warnings: [],
  });
  assert.equal(result.filename, "ask_vpn.py");
  assert.ok(result.code.endsWith("\n"));
});

test("reports BLOCKED prose as the real generation failure before timeline validation", () => {
  assert.throws(() => validateGeneratedResult({
    filename: "blocked.py",
    code: "`BLOCKED:` step_002 has only one stable locator, so no Python was generated. This explanatory text is intentionally long enough to pass the result schema length check.",
    summary: "",
    warnings: [],
  }, {
    steps: [{ id: "step_001" }],
    timeline_events: [{ kind: "action", step_id: "step_001" }],
  }), /returned BLOCKED prose/);
});

test("reports code quality issues as warnings without blocking the draft", () => {
  const unconditional = validateGeneratedResult({
      filename: "bad.py",
      code: `${validCode}\nassert True\n`,
      summary: "",
      warnings: [],
    });
  assert.match(unconditional.warnings.join("\n"), /恒真 assert/);
});

test("keeps direct driver imports and unknown kit APIs visible as warnings", () => {
  const direct = validateGeneratedResult({ filename: "bad.py", code: `${validCode}\nimport pywinauto\n`, summary: "", warnings: [] });
  assert.match(direct.warnings.join("\n"), /直接导入 pywinauto/);
  const unknown = validateGeneratedResult({ filename: "bad.py", code: `${validCode}\nkit.magic_click(CHAT_INPUT)\n`, summary: "", warnings: [] });
  assert.match(unknown.warnings.join("\n"), /未知 Kit API/);
});

test("reports omitted Cache Case steps", () => {
  const result = validateGeneratedResult(
    { filename: "missing.py", code: `${validCode}\n# cache:step_001\n`, summary: "", warnings: [] },
    { steps: [{ id: "step_001" }, { id: "step_002" }] },
  );
  assert.match(result.warnings.join("\n"), /step_002/);
});

test("rejects code that moves an assertion before its recorded click", () => {
  const cacheCase = {
    steps: [{ id: "step_007" }, { id: "step_008" }],
    assertions: [{ id: "assertion_0005" }],
    timeline_events: [
      { event_id: "event_001", kind: "action", step_id: "step_007" },
      { event_id: "event_002", kind: "action", step_id: "step_008" },
      { event_id: "event_003", kind: "assertion", assertion_id: "assertion_0005", after_step_id: "step_008" },
    ],
  };
  assert.throws(() => validateGeneratedResult({
    filename: "wrong_order.py",
    code: `${validCode}\n# cache:step_007\n# cache:assertion_0005\n# cache:step_008\n`, summary: "", warnings: [],
  }, cacheCase), /未保持 Cache Case 事件顺序/);
  const accepted = validateGeneratedResult({
    filename: "right_order.py",
    code: `${validCode}\n# cache:step_007\n# cache:step_008\nkit.step("断言 [cache:assertion_0005]")\n`, summary: "", warnings: [],
  }, cacheCase);
  assert.equal(accepted.filename, "right_order.py");
});

test("adds missing ordered event markers when step calls are still in order", () => {
  const cacheCase = {
    steps: [{ id: "step_001" }, { id: "step_002" }],
    assertions: [{ id: "assertion_0001", after_step_id: "step_002" }],
    timeline_events: [
      { event_id: "event_001", kind: "action", step_id: "step_001" },
      { event_id: "event_002", kind: "action", step_id: "step_002" },
      { event_id: "event_003", kind: "assertion", assertion_id: "assertion_0001", after_step_id: "step_002" },
    ],
  };
  const generated = addMissingTimelineMarkers({
    filename: "marker_repair.py",
    code: `${validCode}
kit.step("点击输入框")
kit.click(CHAT_INPUT)
kit.step("再次点击")
kit.click(CHAT_INPUT)
kit.step("断言输入框出现")
kit.expect_exists(CHAT_INPUT)
`,
    summary: "",
    warnings: [],
  }, cacheCase);

  assert.match(generated.code, /cache:step_001/);
  assert.match(generated.code, /cache:step_002/);
  assert.match(generated.code, /cache:assertion_0001/);
  const result = validateGeneratedResult(generated, cacheCase);
  assert.equal(result.filename, "marker_repair.py");
  assert.match(result.warnings.join("\n"), /已自动补齐执行事件标记/);
});

test("rejects assertions that would be invisible in the execution log", () => {
  const cacheCase = { assertions: [{ id: "assertion_0002" }] };
  assert.throws(() => validateGeneratedResult({
    filename: "silent_assertion.py",
    code: `${validCode}\n# cache:assertion_0002\nkit.expect_exists(CHAT_INPUT)\n`, summary: "", warnings: [],
  }, cacheCase), /遗漏断言执行日志/);
  const result = validateGeneratedResult({
    filename: "logged_assertion.py",
    code: `${validCode}\nkit.step("断言：输入框应出现 [cache:assertion_0002]")\nkit.expect_exists(CHAT_INPUT)\n`, summary: "", warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "logged_assertion.py");
});

test("rejects generated targets that drop stable locator alternatives", () => {
  const cacheCase = {
    assertions: [{
      id: "assertion_0006",
      type: "target_exists",
      assertion_mode: "component_exists",
      target: {
        semantic_role: "project-guide-step1",
        locator_bundle: {
          automation_id: "project-guide-step1",
          control_type: "Group",
          class_name: "command_container__pJ7ai",
        },
      },
    }],
  };
  const automationOnly = `${validCode}
PROJECT_GUIDE_STEP1 = Target(
    role="project-guide-step1",
    locators=(Locator("automation_id", "project-guide-step1", control_type="Group", confidence=1.00),),
)
kit.step("assert [cache:assertion_0006]")
kit.expect_exists(PROJECT_GUIDE_STEP1)
`;
  assert.throws(() => validateGeneratedResult({
    filename: "locator_contract.py",
    code: automationOnly,
    summary: "",
    warnings: [],
  }, cacheCase), /violates locator contract/);

  const withAlternative = `${validCode}
PROJECT_GUIDE_STEP1 = Target(
    role="project-guide-step1",
    locators=(
        Locator("automation_id", "project-guide-step1", control_type="Group", confidence=1.00),
        Locator("class_name_re", r"(?:^|\\s)command_container(?:_{2,3}[A-Za-z0-9_-]+)?(?:\\s|$)", control_type="Group", confidence=0.75),
    ),
)
kit.step("assert [cache:assertion_0006]")
kit.expect_exists(PROJECT_GUIDE_STEP1)
`;
  const result = validateGeneratedResult({
    filename: "locator_contract.py",
    code: withAlternative,
    summary: "",
    warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "locator_contract.py");
});

test("allows scroll steps to use one stable structural locator without volatile response text", () => {
  const cacheCase = {
    steps: [{
      id: "step_007",
      action: "scroll",
      target: {
        locators: [
          {
            type: "name_control_type",
            value: { name: "volatile generated answer text", control_type: "ListItem" },
            stability: "semantic",
          },
          {
            type: "class_name_re",
            value: "(?:^|\\s)ybc-li-component_ul(?:\\s|$)",
            stable_token: "ybc-li-component_ul",
            stability: "stable_token",
          },
        ],
      },
    }],
  };
  const code = `${validCode}
SCROLL_REGION = Target("answer scroll region", (
    Locator("class_name_re", r"(?:^|\\s)ybc-li-component_ul(?:\\s|$)", confidence=0.75),
), (2145, 963), (2582, 1550))
# cache:step_007
kit.scroll(wheel_delta=-240, target=SCROLL_REGION)
`;
  const result = validateGeneratedResult({
    filename: "scroll_contract.py",
    code,
    summary: "",
    warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "scroll_contract.py");
  assert.doesNotMatch(result.code, /volatile generated answer text/);
});

test("requires generated click code to preserve the recorded stable UIA hit leaf", () => {
  const cacheCase = {
    steps: [{
      id: "step_001",
      action: "click",
      target: {
        locators: [
          { type: "automation_id", value: "searchbar-editor", stability: "stable", source: "uia" },
          {
            type: "class_name_re",
            value: "(?:^|\\s)index_v2_is-not-temp-mode(?:\\s|$)",
            stable_token: "index_v2_is-not-temp-mode",
            stability: "stable_token",
            source: "uia_derived",
          },
          {
            type: "class_name_re",
            value: "(?:^|\\s)ql-editor(?:\\s|$)",
            stable_token: "ql-editor",
            control_type: "Group",
            stability: "stable_token",
            source: "hit_leaf_target",
          },
          { type: "coordinate_fallback", value: [1127, 1406], stability: "scaled_fallback" },
        ],
      },
    }],
  };
  const withoutHitLeaf = `${validCode}
# cache:step_001
kit.click(CHAT_INPUT)
INDEX_CONTAINER = Locator("class_name_re", r"(?:^|\\s)index_v2_is-not-temp-mode(?:\\s|$)")
`;

  assert.throws(() => validateGeneratedResult({
    filename: "missing_click_leaf.py",
    code: withoutHitLeaf,
    summary: "",
    warnings: [],
  }, cacheCase), /must preserve a stable hit_leaf_target locator/);

  const withHitLeaf = `${validCode}
CHAT_CLICK_TARGET = Target("聊天输入区域", (
    Locator("automation_id", "searchbar-editor", control_type="Group"),
    Locator("class_name_re", r"(?:^|\\s)index_v2_is-not-temp-mode(?:\\s|$)", control_type="Group"),
    Locator("class_name_re", r"(?:^|\\s)ql-editor(?:\\s|$)", control_type="Group"),
))
# cache:step_001
kit.click(CHAT_CLICK_TARGET)
`;
  assert.equal(validateGeneratedResult({
    filename: "click_leaf.py",
    code: withHitLeaf,
    summary: "",
    warnings: [],
  }, cacheCase).filename, "click_leaf.py");
});

test("requires hover-revealed controls to use the public hover-click API", () => {
  const cacheCase = { steps: [{
    id: "step_022",
    action: "click",
    hover_reveal: {
      match_mode: "first_chat_row",
      target: { locators: [{ type: "class_name_re", value: "Item_chatOrProjectItem" }] },
    },
  }] };
  assert.throws(() => validateGeneratedResult({
    filename: "missing_hover.py",
    code: `${validCode}\n# cache:step_022\nkit.click(CHAT_INPUT)\n`,
    summary: "", warnings: [],
  }, cacheCase), /must use kit\.click_after_hover/);

  const result = validateGeneratedResult({
    filename: "hover_click.py",
    code: `${validCode}\n# cache:step_022\nkit.click_after_hover(CHAT_INPUT, CHAT_INPUT)\n`,
    summary: "", warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "hover_click.py");
});

test("still rejects scroll steps that drop every stable structural locator", () => {
  const cacheCase = {
    steps: [{
      id: "step_007",
      action: "scroll",
      target: {
        locators: [
          { type: "name_control_type", value: { name: "volatile text", control_type: "ListItem" }, stability: "semantic" },
          { type: "class_name_re", value: "stable-scroll-token", stable_token: "stable-scroll-token", stability: "stable_token" },
        ],
      },
    }],
  };
  assert.throws(() => validateGeneratedResult({
    filename: "bad_scroll_contract.py",
    code: `${validCode}\n# cache:step_007\nkit.scroll(wheel_delta=-240)\n`,
    summary: "",
    warnings: [],
  }, cacheCase), /scroll locator contract/);
});

test("requires stable hit leaf locator when it shares bounds with assertion target", () => {
  const cacheCase = {
    assertions: [{
      id: "assertion_0006",
      type: "target_exists",
      assertion_mode: "component_exists",
      target: {
        semantic_role: "project-guide-step1",
        locator_bundle: {
          automation_id: "project-guide-step1",
          control_type: "Group",
          class_name: "command_container__pJ7ai",
        },
        node: {
          bounds: { x: 718, y: 249, width: 714, height: 96 },
        },
      },
      evidence: {
        capture_diagnostics: {
          hit_leaf_target: {
            automation_id: "",
            name: "",
            control_type: "Group",
            class_name: "command_leftAreaContainer__telcZ",
            bounds: { x: 718, y: 249, width: 714, height: 96 },
          },
        },
      },
    }],
  };
  const missingHitLeaf = `${validCode}
PROJECT_GUIDE_STEP1 = Target(
    role="project-guide-step1",
    locators=(
        Locator("automation_id", "project-guide-step1", control_type="Group", confidence=1.00),
        Locator("class_name_re", r"(?:^|\\s)command_container(?:_{2,3}[A-Za-z0-9_-]+)?(?:\\s|$)", control_type="Group", confidence=0.75),
    ),
)
kit.step("assert [cache:assertion_0006]")
kit.expect_exists(PROJECT_GUIDE_STEP1)
`;
  assert.throws(() => validateGeneratedResult({
    filename: "hit_leaf_contract.py",
    code: missingHitLeaf,
    summary: "",
    warnings: [],
  }, cacheCase), /hit_leaf_target locator/);

  const withHitLeaf = `${validCode}
PROJECT_GUIDE_STEP1 = Target(
    role="project-guide-step1",
    locators=(
        Locator("automation_id", "project-guide-step1", control_type="Group", confidence=1.00),
        Locator("class_name_re", r"(?:^|\\s)command_container(?:_{2,3}[A-Za-z0-9_-]+)?(?:\\s|$)", control_type="Group", confidence=0.75),
        Locator("class_name_re", r"(?:^|\\s)command_leftAreaContainer(?:_{2,3}[A-Za-z0-9_-]+)?(?:\\s|$)", control_type="Group", confidence=0.74),
    ),
)
kit.step("assert [cache:assertion_0006]")
kit.expect_exists(PROJECT_GUIDE_STEP1)
`;
  const result = validateGeneratedResult({
    filename: "hit_leaf_contract.py",
    code: withHitLeaf,
    summary: "",
    warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "hit_leaf_contract.py");
});

test("rejects a section-scoped condition downgraded to window-wide exists", () => {
  const cacheCase = { steps: [{
    id: "step_001",
    action: "condition",
    condition: {
      scope: { type: "section", start_name: "分组", end_name: "聊天" },
      fallback_policy: "strict",
    },
  }] };
  assert.throws(
    () => validateGeneratedResult({
      filename: "bad_scope.py",
      code: `${validCode}\n# cache:step_001\nif kit.exists(CHAT_INPUT):\n    pass\n`,
      summary: "",
      warnings: [],
    }, cacheCase),
    /丢失条件范围语义/,
  );
});

test("accepts a section-scoped condition using the fixed runtime API", () => {
  const cacheCase = { steps: [{
    id: "step_001",
    action: "condition",
    condition: { scope: { type: "section", start_name: "分组", end_name: "聊天" } },
  }] };
  const result = validateGeneratedResult({
    filename: "good_scope.py",
    code: `${validCode}\n# cache:step_001\nif kit.exists_in_section(CHAT_INPUT, start_name="分组", end_name="聊天"):\n    pass\n`,
    summary: "",
    warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "good_scope.py");
});

test("preserves window scope and timeout for keyword assertions", () => {
  const cacheCase = { assertions: [{
    id: "assertion_0001", type: "keywords_match_count", scope: "window", timeout_ms: 30000,
  }] };
  assert.throws(() => validateGeneratedResult({
    filename: "bad_keyword_scope.py",
    code: `${validCode}\n# cache:assertion_0001\nkit.expect_keywords(CHAT_INPUT, ("a", "b", "c"), minimum=3, timeout=1)\n`,
    summary: "", warnings: [],
  }, cacheCase), /缩小了窗口关键词断言范围/);

  const result = validateGeneratedResult({
    filename: "good_keyword_scope.py",
    code: `${validCode}\nkit.step("断言：窗口关键词 [cache:assertion_0001]")\nkit.expect_keywords(None, ("a", "b", "c"), minimum=3, timeout=30)\n`,
    summary: "", warnings: [],
  }, cacheCase);
  assert.equal(result.filename, "good_keyword_scope.py");
});

test("requires every generated assertion to preserve its asynchronous polling timeout", () => {
  const cacheCase = { assertions: [{
    id: "assertion_0001", type: "text_contains", timeout_ms: 10000,
  }] };
  const tooShort = `${validCode}
kit.step("assert search result [cache:assertion_0001]")
kit.expect_text_contains(CHAT_INPUT, "avatar", timeout=1)
`;
  assert.throws(() => validateGeneratedResult({
    filename: "short_timeout.py", code: tooShort, summary: "", warnings: [],
  }, cacheCase), /must use timeout=10 for asynchronous polling/);

  const preserved = `${validCode}
kit.step("assert search result [cache:assertion_0001]")
kit.expect_text_contains(CHAT_INPUT, "avatar", timeout=10)
`;
  assert.equal(validateGeneratedResult({
    filename: "async_timeout.py", code: preserved, summary: "", warnings: [],
  }, cacheCase).filename, "async_timeout.py");
});

test("rejects window keyword assertions satisfied by the submitted prompt", () => {
  const cacheCase = {
    steps: [{ action: "input_text", input: { text: "求解a+b=c" } }],
    assertions: [{
      id: "assertion_0001", type: "keywords_match_count", scope: "window",
      keywords: ["a", "b", "c"], minimum_matches: 3, timeout_ms: 60000,
    }],
  };
  assert.throws(() => validateGeneratedResult({
    filename: "false_positive.py",
    code: `${validCode}\n# cache:assertion_0001\nkit.expect_keywords(None, ("a", "b", "c"), minimum=3, timeout=60)\n`,
    summary: "", warnings: [],
  }, cacheCase), /输入文本误触发/);
});

test("compacts large raw UI evidence but preserves semantic steps", () => {
  const compacted = compactCacheCase({
    steps: [{ id: "step-1", action: "click", hierarchy: { huge: true } }],
    raw_uia_tree: "large",
  });
  assert.equal(compacted.steps[0].id, "step-1");
  assert.equal(compacted.steps[0].hierarchy, undefined);
  assert.equal(compacted.raw_uia_tree, undefined);
});

test("exports generated cases into the sibling QTA runtime without an override", () => {
  const projectRoot = fs.mkdtempSync(path.join(os.tmpdir(), "yuanbao-project-export-"));
  const rootDirectory = path.join(projectRoot, "platform");
  const caseDirectory = path.join(rootDirectory, "cases-native", "export-case");
  try {
    fs.mkdirSync(caseDirectory, { recursive: true });
    fs.writeFileSync(path.join(caseDirectory, "generated-qta.json"), JSON.stringify({
      filename: "export_case.py", code: validCode,
    }));
    const generator = new QtaCodeGenerator({ rootDirectory });
    const exported = generator.export("export-case");
    const expectedPath = path.join(projectRoot, "qta_runtime", "qta_cases", "windows_generated", "export_case.py");
    assert.equal(exported.path, expectedPath);
    assert.equal(fs.readFileSync(expectedPath, "utf8"), validCode);
  } finally {
    fs.rmSync(projectRoot, { recursive: true, force: true });
  }
});

test("extracts output text from Responses API content", () => {
  assert.equal(extractOutputText({ output: [{ content: [{ text: "{\"ok\":true}" }] }] }), '{"ok":true}');
  assert.equal(extractOutputText({ choices: [{ message: { content: '{"chat":true}' } }] }), '{"chat":true}');
});

test("loads Moonshot provider config and applies its Chat Completions format", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "yuanbao-provider-"));
  const sourceSkill = path.resolve(__dirname, "..", "skills", "yuanbao-windows-qta-codegen");
  fs.cpSync(sourceSkill, path.join(tempRoot, "skills", "yuanbao-windows-qta-codegen"), { recursive: true });
  fs.mkdirSync(path.join(tempRoot, "cases-native", "case-kimi"), { recursive: true });
  fs.writeFileSync(path.join(tempRoot, "config.local.json"), JSON.stringify({
    apiKey: "kimi-test-key",
    model: "kimi-k3",
    baseUrl: "https://api.moonshot.cn/v1",
    apiStyle: "chat_completions",
  }));
  let submitted;
  const generator = new QtaCodeGenerator({
    rootDirectory: tempRoot,
    fetchImpl: async (url, options) => {
      submitted = { url, body: JSON.parse(options.body) };
      return {
        ok: true,
        status: 200,
        async json() {
          return { choices: [{ message: { content: JSON.stringify({
            filename: "kimi_case.py",
            code: validCode,
            summary: "Kimi 生成",
            warnings: [],
          }) } }] };
        },
      };
    },
  });
  try {
    const status = generator.status();
    assert.equal(status.configured, true);
    assert.equal(status.apiStyle, "chat_completions");
    await generator.generate("case-kimi", { case: { name: "Kimi" }, steps: [] });
    assert.equal(submitted.url, "https://api.moonshot.cn/v1/chat/completions");
    assert.equal(submitted.body.messages[0].role, "system");
    assert.equal(submitted.body.response_format.type, "json_object");
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("does not reuse a provider key for the OpenAI endpoint", () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "yuanbao-openai-config-"));
  fs.writeFileSync(path.join(tempRoot, "config.local.json"), JSON.stringify({
    apiKey: "kimi-key-that-must-not-be-used",
    openaiApiKey: "",
    model: "gpt-5.6-sol",
    baseUrl: "https://api.openai.com/v1",
    apiStyle: "responses",
  }));
  try {
    const generator = new QtaCodeGenerator({ rootDirectory: tempRoot });
    assert.equal(generator.status().configured, false);
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("calls Responses API with skill instructions and persists generated code", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "yuanbao-codegen-"));
  const sourceSkill = path.resolve(__dirname, "..", "skills", "yuanbao-windows-qta-codegen");
  const targetSkill = path.join(tempRoot, "skills", "yuanbao-windows-qta-codegen");
  fs.cpSync(sourceSkill, targetSkill, { recursive: true });
  fs.mkdirSync(path.join(tempRoot, "cases-native", "case-1"), { recursive: true });
  let submitted;
  const generator = new QtaCodeGenerator({
    rootDirectory: tempRoot,
    exportDirectory: path.join(tempRoot, "windows-generated"),
    fetchImpl: async (url, options) => {
      submitted = { url, options, body: JSON.parse(options.body) };
      return {
        ok: true,
        status: 200,
        async json() {
          return {
            output_text: JSON.stringify({
              filename: "ask_vpn.py",
              code: validCode,
              summary: "验证提问",
              warnings: [],
            }),
          };
        },
      };
    },
  });
  const previous = {
    key: process.env.OPENAI_API_KEY,
    model: process.env.OPENAI_MODEL,
    base: process.env.OPENAI_BASE_URL,
  };
  process.env.OPENAI_API_KEY = "test-key";
  process.env.OPENAI_MODEL = "test-model";
  process.env.OPENAI_BASE_URL = "https://example.test/v1";
  try {
    const result = await generator.generate("case-1", { case: { name: "VPN" }, steps: [] });
    assert.equal(result.filename, "ask_vpn.py");
    assert.equal(submitted.url, "https://example.test/v1/responses");
    assert.equal(submitted.body.model, "test-model");
    assert.match(submitted.body.instructions, /yuanbao-windows-qta-codegen/);
    assert.equal(submitted.body.text.format.type, "json_schema");
    assert.ok(fs.existsSync(path.join(tempRoot, "cases-native", "case-1", "generated-qta.json")));
    const exported = generator.export("case-1");
    assert.equal(exported.filename, "ask_vpn.py");
    assert.equal(fs.readFileSync(exported.path, "utf8"), validCode.trim() + "\n");
    assert.equal(generator.export("case-1").reused, true);
  } finally {
    if (previous.key === undefined) delete process.env.OPENAI_API_KEY; else process.env.OPENAI_API_KEY = previous.key;
    if (previous.model === undefined) delete process.env.OPENAI_MODEL; else process.env.OPENAI_MODEL = previous.model;
    if (previous.base === undefined) delete process.env.OPENAI_BASE_URL; else process.env.OPENAI_BASE_URL = previous.base;
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("repairs a validator-rejected generation before reporting failure", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "yuanbao-codegen-repair-"));
  const sourceSkill = path.resolve(__dirname, "..", "skills", "yuanbao-windows-qta-codegen");
  fs.cpSync(sourceSkill, path.join(tempRoot, "skills", "yuanbao-windows-qta-codegen"), { recursive: true });
  fs.mkdirSync(path.join(tempRoot, "cases-native", "case-repair"), { recursive: true });
  let calls = 0;
  const generator = new QtaCodeGenerator({
    rootDirectory: tempRoot,
    fetchImpl: async (_url, options) => {
      calls += 1;
      const body = JSON.parse(options.body);
      if (calls === 2) {
        assert.match(body.input, /validator_feedback/);
        assert.match(body.input, /cache:step_001/);
      }
      return {
        ok: true,
        status: 200,
        async json() {
          return { output_text: JSON.stringify({
            filename: "repaired_case.py",
            code: calls === 1
              ? `${validCode}\n# cache:step_002\n# cache:step_001\n`
              : `${validCode}\n# cache:step_001\n# cache:step_002\n`,
            summary: "修复后的用例",
            warnings: [],
          }) };
        },
      };
    },
  });
  const previous = { key: process.env.OPENAI_API_KEY, base: process.env.OPENAI_BASE_URL };
  process.env.OPENAI_API_KEY = "test-key";
  process.env.OPENAI_BASE_URL = "https://example.test/v1";
  try {
    const result = await generator.generate("case-repair", {
      case: { name: "repair" },
      steps: [{ id: "step_001", action: "click" }, { id: "step_002", action: "click" }],
      timeline_events: [
        { event_id: "event_001", kind: "action", step_id: "step_001" },
        { event_id: "event_002", kind: "action", step_id: "step_002" },
      ],
    });
    assert.equal(calls, 2);
    assert.match(result.code, /cache:step_001/);
    assert.match(result.code, /cache:step_002/);
    assert.match(result.warnings.join("\n"), /自动修复 1 次/);
  } finally {
    if (previous.key === undefined) delete process.env.OPENAI_API_KEY; else process.env.OPENAI_API_KEY = previous.key;
    if (previous.base === undefined) delete process.env.OPENAI_BASE_URL; else process.env.OPENAI_BASE_URL = previous.base;
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("persists failed AI generation attempts for diagnosis", async () => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "yuanbao-codegen-diagnostic-"));
  const sourceSkill = path.resolve(__dirname, "..", "skills", "yuanbao-windows-qta-codegen");
  fs.cpSync(sourceSkill, path.join(tempRoot, "skills", "yuanbao-windows-qta-codegen"), { recursive: true });
  fs.mkdirSync(path.join(tempRoot, "cases-native", "case-diagnostic"), { recursive: true });
  const generator = new QtaCodeGenerator({
    rootDirectory: tempRoot,
    fetchImpl: async () => ({
      ok: true,
      status: 200,
      async json() {
        return { output_text: JSON.stringify({
          filename: "diagnostic_case.py",
          code: validCode,
          summary: "",
          warnings: [],
        }) };
      },
    }),
  });
  const previous = { key: process.env.OPENAI_API_KEY, base: process.env.OPENAI_BASE_URL };
  process.env.OPENAI_API_KEY = "test-key";
  process.env.OPENAI_BASE_URL = "https://example.test/v1";
  try {
    await assert.rejects(() => generator.generate("case-diagnostic", {
      case: { name: "diagnostic" },
      steps: [{ id: "step_001", action: "click" }],
      timeline_events: [{ event_id: "event_001", kind: "action", step_id: "step_001" }],
      assertions: [{
        id: "assertion_0001",
        type: "target_exists",
        after_step_id: "step_001",
      }],
    }));
    const latest = path.join(tempRoot, "cases-native", "case-diagnostic", "codegen-diagnostics", "latest-failure.json");
    assert.ok(fs.existsSync(latest));
    const diagnostic = JSON.parse(fs.readFileSync(latest, "utf8"));
    assert.equal(diagnostic.case_id, "case-diagnostic");
    assert.match(diagnostic.validation_error, /assertion_0001|cache:step_001/);
    assert.equal(diagnostic.parsed_result.filename, "diagnostic_case.py");
  } finally {
    if (previous.key === undefined) delete process.env.OPENAI_API_KEY; else process.env.OPENAI_API_KEY = previous.key;
    if (previous.base === undefined) delete process.env.OPENAI_BASE_URL; else process.env.OPENAI_BASE_URL = previous.base;
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});
