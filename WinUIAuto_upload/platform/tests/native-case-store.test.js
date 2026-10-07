const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const test = require("node:test");
const { NativeCaseStore } = require("../lib/native-case-store");

function writeJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, JSON.stringify(value), "utf8");
}

function createFixture() {
  const rootDirectory = fs.mkdtempSync(path.join(os.tmpdir(), "native-case-store-"));
  const caseId = "元宝测试-202608070001";
  const caseDirectory = path.join(rootDirectory, "cases-native", caseId);
  fs.mkdirSync(path.join(caseDirectory, "frames"), { recursive: true });
  fs.mkdirSync(path.join(caseDirectory, "hierarchy"), { recursive: true });
  fs.writeFileSync(path.join(caseDirectory, "frames", "action_0001.png"), "png");
  fs.writeFileSync(path.join(caseDirectory, "frames", "action_0002.png"), "png");
  const snapshot = {
    captured_at: "2026-08-07T00:00:01.000Z",
    window_title: "元宝",
    node_count: 2,
    nodes: [
      { index: 0, bounds: { x: 100, y: 50, width: 1200, height: 800 } },
      { index: 1, bounds: { x: 500, y: 700, width: 300, height: 60 } },
    ],
  };
  writeJson(path.join(caseDirectory, "hierarchy", "action_0001.json"), snapshot);
  writeJson(path.join(caseDirectory, "hierarchy", "action_0002.json"), snapshot);
  const rawTrace = {
    schema_version: "0.5-windows-native",
    session_id: caseId,
    case_name: "元宝测试",
    target_window: "腾讯元宝",
    started_at: "2026-08-07T00:00:00.000Z",
    finished_at: "2026-08-07T00:00:03.000Z",
    agent: {
      version: "0.5.0-event-correlated",
      build_configuration: "Current",
      git_commit: "abc1234",
      executable_path: path.join(rootDirectory, "agent", "YuanbaoRecorder.Agent.exe"),
    },
    actions: [
      {
        id: "action_0001",
        type: "click",
        timestamp: "2026-08-07T00:00:01.000Z",
        screen_point: { x: 650, y: 730 },
        window_point: { x: 450, y: 680 },
        target: {
          automation_id: "searchbar-editor",
          control_type: "Group",
          bounds: { x: 500, y: 700, width: 300, height: 60 },
        },
        locator: { automation_id: "searchbar-editor", fallback_window_point: { x: 450, y: 680 } },
        screenshot: "frames/action_0001.png",
        uia_snapshot: "hierarchy/action_0001.json",
      },
      {
        id: "action_0002_input",
        type: "input_text",
        text: "什么是VIP？",
        timestamp: "2026-08-07T00:00:02.000Z",
        confidence: 1,
        window_point: { x: 450, y: 680 },
        target: { automation_id: "searchbar-editor", control_type: "Group" },
        locator: { automation_id: "searchbar-editor", fallback_window_point: { x: 450, y: 680 } },
        screenshot: "frames/action_0002.png",
        uia_snapshot: "hierarchy/action_0002.json",
        evidence_before_snapshot: "hierarchy/action_0001.json",
        evidence_after_snapshot: "hierarchy/action_0002.json",
        capture_diagnostics: {
          selection_source: "immediate_uia_hit_test",
          transient_candidate_count: 0,
        },
      },
    ],
  };
  writeJson(path.join(caseDirectory, "raw-trace.json"), rawTrace);
  return { rootDirectory, caseId, caseDirectory, rawTrace };
}

test("loads native Raw Trace and builds management models", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const cases = store.listCases();
    assert.equal(cases.length, 1);
    assert.equal(cases[0].actionCount, 2);
    const loaded = store.loadCase(fixture.caseId);
    assert.equal(loaded.session.capture_adapter, "YuanbaoRecorder.Agent");
    assert.equal(loaded.session.agent.version, "0.5.0-event-correlated");
    assert.equal(loaded.session.actions[1].capture_diagnostics.selection_source, "immediate_uia_hit_test");
    assert.equal(loaded.session.actions[1].value, "什么是VIP？");
    assert.equal(loaded.session.frames.length, 2);
    assert.deepEqual(loaded.session.device.resolution, [1200, 800]);
    assert.equal(loaded.cacheCase.steps[1].input.text, "什么是VIP？");
    assert.equal(loaded.cacheCase.steps[0].target.locators[0].type, "automation_id");
    assert.equal(loaded.cacheCase.schema_version, "0.3-windows-native-cache");
    assert.deepEqual(loaded.cacheCase.applicability.recorded_window_size, { width: 1200, height: 800 });
    assert.equal(loaded.cacheCase.steps[0].target.locators.some((locator) =>
      locator.type === "control_type" && locator.value === "Group"), false);
    const coordinate = loaded.cacheCase.steps[0].target.locators.find((locator) =>
      locator.type === "coordinate_fallback");
    assert.deepEqual(coordinate.reference_window_size, { width: 1200, height: 800 });
    assert.deepEqual(coordinate.normalized_value, [0.375, 0.85]);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("builds deterministic stable locators without mutating Raw Trace", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions[0].target.automation_id = "view_12345";
    fixture.rawTrace.actions[0].target.class_name = "ql-editor ql-blank undefined";
    fixture.rawTrace.actions[0].locator.automation_id = "view_12345";
    fixture.rawTrace.actions[0].locator.class_name = "ql-editor ql-blank undefined";
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const rawPath = path.join(fixture.caseDirectory, "raw-trace.json");
    const originalBytes = fs.readFileSync(rawPath);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const first = store.loadCase(fixture.caseId).cacheCase;
    const second = store.loadCase(fixture.caseId).cacheCase;
    assert.deepEqual(second, first);
    assert.deepEqual(fs.readFileSync(rawPath), originalBytes);
    const locators = first.steps[0].target.locators;
    const dynamicId = locators.find((locator) => locator.type === "automation_id");
    assert.equal(dynamicId.priority, 55);
    assert.equal(dynamicId.stability, "session_only");
    const stableClass = locators.find((locator) => locator.type === "class_name_re");
    assert.equal(stableClass.stable_token, "ql-editor");
    assert.match("ql-editor ql-blank", new RegExp(stableClass.value));
    assert.match("ql-editor", new RegExp(stableClass.value));
    assert.equal(locators.some((locator) => locator.type === "control_type"), false);
    fixture.rawTrace.actions[0].target.class_name = "SendButton_sendButton__g3Lcb undefined";
    writeJson(rawPath, fixture.rawTrace);
    const hashedClass = store.loadCase(fixture.caseId).cacheCase.steps[0].target.locators
      .find((locator) => locator.type === "class_name_re");
    assert.equal(hashedClass.stable_token, "SendButton_sendButton");
    assert.match("SendButton_sendButton__nextHash", new RegExp(hashedClass.value));
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("preserves a stable UIA hit leaf for clicks instead of relying on coordinates", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions[0].capture_diagnostics = {
      selection_source: "semantic_immediate_hit_test",
      hit_leaf_target: {
        name: "",
        automation_id: "",
        class_name: "ql-editor ql-blank",
        control_type: "Group",
        bounds: { x: 520, y: 710, width: 260, height: 34 },
      },
    };
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);

    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const locators = store.loadCase(fixture.caseId).cacheCase.steps[0].target.locators;
    const hitLeaf = locators.find((locator) =>
      locator.source === "hit_leaf_target" && locator.type === "class_name_re");

    assert.ok(hitLeaf);
    assert.equal(hitLeaf.stable_token, "ql-editor");
    assert.equal(hitLeaf.control_type, "Group");
    assert.ok(locators.find((locator) => locator.type === "automation_id" &&
      locator.value === "searchbar-editor"));
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("allows safe business punctuation in case ids", () => {
  const fixture = createFixture();
  const punctuatedCaseId = "元宝测试-新建派&改名-202608070001";
  const punctuatedDirectory = path.join(fixture.rootDirectory, "cases-native", punctuatedCaseId);
  try {
    fs.renameSync(fixture.caseDirectory, punctuatedDirectory);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(punctuatedCaseId);
    assert.equal(loaded.session.case_id, punctuatedCaseId);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("keeps a case readable when one UIA snapshot is invalid JSON", () => {
  const fixture = createFixture();
  try {
    fs.writeFileSync(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), '{"bounds":{"x":INF}}', "utf8");
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    assert.equal(loaded.session.actions.length, 2);
    assert.equal(loaded.session.frames.length, 2);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("stores edits separately and keeps Raw Trace immutable", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    const editedActions = loaded.session.actions.slice(1);
    editedActions[0] = { ...editedActions[0], value: "什么是VPN？", label: "输入修改后的问题" };
    const result = store.saveReviewActions(fixture.caseId, editedActions);
    assert.equal(result.session.actions.length, 1);
    assert.equal(result.session.actions[0].value, "什么是VPN？");
    assert.equal(result.cacheCase.steps[0].input.text, "什么是VPN？");
    assert.ok(fs.existsSync(path.join(fixture.caseDirectory, "review-trace.json")));
    assert.ok(fs.existsSync(path.join(fixture.caseDirectory, "cache-case.json")));
    assert.deepEqual(readRawTrace(fixture.caseDirectory), fixture.rawTrace);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("builds a native replay task from reviewed actions", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.equal(spec.caseId, fixture.caseId);
    assert.equal(spec.target.process_name, "yuanbao");
    assert.deepEqual(spec.target.recorded_window_size, { width: 1200, height: 800 });
    assert.equal(spec.actions.length, 2);
    assert.equal(spec.actions[0].locator.automation_id, "searchbar-editor");
    assert.deepEqual(spec.actions[0].coordinate, { x: 450, y: 680 });
    assert.deepEqual(spec.actions[0].target_relative_point, { x: 0.5, y: 0.5 });
    assert.equal(spec.actions[1].value, "什么是VIP？");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("coalesces cumulative text commits for the same editable transaction", () => {
  const fixture = createFixture();
  try {
    const editableTarget = {
      runtime_id: [42, 100, 4, 5, 1, 9],
      name: "请输入分组名称",
      class_name: "t-input__inner",
      control_type: "Edit",
      bounds: { x: 500, y: 300, width: 300, height: 40 },
    };
    const editableLocator = {
      name: "请输入分组名称",
      class_name: "t-input",
      control_type: "Edit",
      ancestor_path: [{ class_name: "project-add-dialog_input", control_type: "Group" }],
      fallback_window_point: { x: 550, y: 320 },
    };
    fixture.rawTrace.actions = [
      {
        id: "action_0001_input",
        type: "input_text",
        text: "test",
        timestamp: "2026-08-07T00:00:01.000Z",
        target: editableTarget,
        locator: editableLocator,
      },
      {
        id: "action_0001",
        type: "key_press",
        key: "ENTER",
        timestamp: "2026-08-07T00:00:01.100Z",
        effects: [],
      },
      {
        id: "action_0002_input",
        type: "input_text",
        text: "test1",
        timestamp: "2026-08-07T00:00:02.000Z",
        target: editableTarget,
        locator: editableLocator,
      },
    ];
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    assert.equal(loaded.session.actions.length, 1);
    assert.equal(loaded.session.actions[0].type, "input_text");
    assert.equal(loaded.session.actions[0].value, "test1");
    assert.deepEqual(loaded.session.actions[0].input_transaction_repair.removed_action_ids, [
      "action_0001_input",
      "action_0001",
    ]);
    assert.equal(fixture.rawTrace.actions.length, 3);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("drops a redundant Enter before an explicit dialog confirmation click", () => {
  const fixture = createFixture();
  try {
    const runtimeId = [42, 100, 4, 5, 1, 12];
    fixture.rawTrace.actions = [
      {
        id: "action_0001_input",
        type: "input_text",
        text: "under",
        timestamp: "2026-08-07T00:00:01.000Z",
        target: { runtime_id: runtimeId, name: "请输入", control_type: "Edit" },
        locator: { name: "请输入", control_type: "Edit" },
      },
      {
        id: "action_0001",
        type: "key_press",
        key: "ENTER",
        timestamp: "2026-08-07T00:00:01.100Z",
        target: { runtime_id: runtimeId, name: "请输入", control_type: "Edit" },
        effects: [],
      },
      {
        id: "action_0002",
        type: "click",
        timestamp: "2026-08-07T00:00:02.000Z",
        target: { name: "确认", control_type: "Button" },
        locator: { name: "确认", control_type: "Button" },
      },
    ];
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    assert.deepEqual(loaded.session.actions.map((action) => action.type), ["input_text", "click"]);
    assert.deepEqual(loaded.session.actions[0].input_transaction_repair.removed_action_ids, ["action_0001"]);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("derives a semantic hover anchor for a dynamic row menu trigger", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions = [{
      id: "action_0001",
      type: "click",
      timestamp: "2026-08-07T00:00:01.000Z",
      target: { automation_id: "dynamic-row-id", class_name: "Item_dropdown-trigger__hash", control_type: "Group" },
      locator: {
        automation_id: "dynamic-row-id",
        class_name: "Item_dropdown-trigger",
        control_type: "Group",
        ancestor_path: [{ class_name: "Item_chatOrProjectItem", control_type: "Group" }],
      },
      uia_snapshot: "hierarchy/action_0001.json",
    }];
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), {
      nodes: [
        { index: 1, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group" },
        { index: 2, parent_index: 1, name: "test1", control_type: "Text" },
        { index: 3, parent_index: 1, automation_id: "dynamic-row-id", class_name: "Item_dropdown-trigger__hash", control_type: "Group" },
      ],
    });
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.deepEqual(spec.actions[0].hover_anchor_locator, {
      name: "test1",
      control_type: "Text",
      ancestor_path: [
        { control_type: "Group", class_name: "Item_chatOrProjectItem" },
        { name: "test1", control_type: "Text" },
      ],
    });
    assert.equal(spec.actions[0].locator.automation_id, null);
    assert.equal(spec.actions[0].locator.class_name, "Item_dropdown-trigger");
    assert.equal(spec.actions[0].locator.ancestor_path.at(-1).automation_id, null);
    assert.equal(spec.actions[0].hover_anchor_match_mode, "exact_row_text");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("promotes a shared sidebar row click to named text with a strict section scope", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions = [{
      id: "action_0001",
      type: "click",
      timestamp: "2026-08-07T00:00:01.000Z",
      screen_point: { x: 120, y: 165 },
      window_point: { x: 120, y: 165 },
      target: {
        class_name: "Item_chatOrProjectItem__hash",
        control_type: "Group",
        bounds: { x: 10, y: 145, width: 280, height: 50 },
      },
      locator: {
        class_name: "Item_chatOrProjectItem",
        control_type: "Group",
        ancestor_path: [{ name: "scrollable content", control_type: "Group" }],
      },
      capture_diagnostics: {
        hit_leaf_target: {
          name: "Showcase-\u79fb\u51fa\u5206\u7ec4",
          control_type: "Text",
          bounds: { x: 60, y: 155, width: 160, height: 25 },
        },
      },
      effects: [{
        type: "target_appeared",
        locator: { class_name: "Item_chatOrProjectItem", control_type: "Group" },
      }],
      uia_snapshot: "hierarchy/action_0001.json",
    }];
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), {
      nodes: [
        { index: 1, parent_index: -1, name: "\u5206\u7ec4", control_type: "Group", bounds: { x: 10, y: 100, width: 280, height: 30 } },
        { index: 2, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group", bounds: { x: 10, y: 145, width: 280, height: 50 } },
        { index: 3, parent_index: 2, name: "Showcase-\u79fb\u51fa\u5206\u7ec4", control_type: "Text", bounds: { x: 60, y: 155, width: 160, height: 25 } },
        { index: 4, parent_index: -1, name: "\u6700\u8fd1", control_type: "Group", bounds: { x: 10, y: 220, width: 280, height: 30 } },
        { index: 5, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group", bounds: { x: 10, y: 255, width: 280, height: 50 } },
        { index: 6, parent_index: 5, name: "Showcase-\u79fb\u51fa\u5206\u7ec4", control_type: "Text", bounds: { x: 60, y: 265, width: 160, height: 25 } },
      ],
    });
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);

    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    const replay = store.buildReplaySpec(fixture.caseId);
    assert.equal(loaded.session.actions[0].target.semantic_role, "Showcase-\u79fb\u51fa\u5206\u7ec4");
    assert.equal(replay.actions[0].locator.name, "Showcase-\u79fb\u51fa\u5206\u7ec4");
    assert.equal(replay.actions[0].locator.section_start_name, "\u5206\u7ec4");
    assert.equal(replay.actions[0].locator.section_end_name, "\u6700\u8fd1");
    assert.equal(replay.actions[0].effects.length, 0);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("binds an unnamed conversation container to its title text", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions = [{
      id: "action_0001",
      type: "right_click",
      timestamp: "2026-08-07T00:00:01.000Z",
      screen_point: { x: 800, y: 450 },
      window_point: { x: 800, y: 450 },
      target: {
        class_name: "item_itemContainer__hash",
        control_type: "Group",
        bounds: { x: 300, y: 400, width: 1000, height: 90 },
      },
      locator: {
        class_name: "item_itemContainer",
        control_type: "Group",
        ancestor_path: [{ automation_id: "chatContainer", control_type: "Group" }],
      },
      uia_snapshot: "hierarchy/action_0001.json",
    }];
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), {
      nodes: [
        { index: 1, parent_index: -1, class_name: "item_itemContainer__hash", control_type: "Group", depth: 20, bounds: { x: 300, y: 400, width: 1000, height: 90 } },
        { index: 2, parent_index: 1, class_name: "item_itemTitle__hash", control_type: "Group", depth: 21, bounds: { x: 320, y: 410, width: 800, height: 30 } },
        { index: 3, parent_index: 2, name: "SHOWCASE-\u5f85\u79fb\u51fa", control_type: "Text", depth: 22, bounds: { x: 340, y: 410, width: 180, height: 30 } },
        { index: 4, parent_index: 1, name: "long description", control_type: "Text", depth: 22, bounds: { x: 340, y: 450, width: 800, height: 25 } },
      ],
    });
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);

    const replay = new NativeCaseStore({ rootDirectory: fixture.rootDirectory }).buildReplaySpec(fixture.caseId);
    assert.equal(replay.actions[0].locator.class_name, "item_itemContainer");
    assert.equal(replay.actions[0].locator.descendant_name, "SHOWCASE-\u5f85\u79fb\u51fa");
    assert.equal(replay.actions[0].locator.descendant_control_type, "Text");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("recovers a submenu item click from the pre-dismissal scroll snapshot", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions = [
      {
        id: "action_0001",
        type: "scroll",
        timestamp: "2026-08-07T00:00:01.000Z",
        screen_point: { x: 620, y: 720 },
        window_point: { x: 620, y: 720 },
        wheel_delta: -120,
        target: { class_name: "yb-dropdown__submenu-wrapper", control_type: "Group" },
        locator: { class_name: "yb-dropdown__submenu-wrapper", control_type: "Group" },
        uia_snapshot: "hierarchy/action_0001.json",
      },
      {
        id: "action_0002",
        type: "click",
        timestamp: "2026-08-07T00:00:05.250Z",
        screen_point: { x: 650, y: 915 },
        window_point: { x: 650, y: 915 },
        target: { class_name: "chat_simplebar__hash", control_type: "Group", bounds: { x: 300, y: 100, width: 1800, height: 1000 } },
        locator: { class_name: "chat_simplebar", control_type: "Group" },
        uia_snapshot: "hierarchy/action_0002.json",
      },
    ];
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), {
      nodes: [
        { index: 1, parent_index: -1, class_name: "t-portal-wrapper", control_type: "Group", bounds: { x: 400, y: 350, width: 400, height: 620 } },
        { index: 2, parent_index: 1, class_name: "yb-dropdown__submenu-wrapper", control_type: "Group", bounds: { x: 500, y: 450, width: 230, height: 500 } },
        { index: 3, parent_index: 2, name: "\u79fb\u51fa\u672c\u7ec4", class_name: "yb-dropdown__item", control_type: "ListItem", bounds: { x: 515, y: 893, width: 211, height: 49 } },
        { index: 4, parent_index: 3, name: "\u79fb\u51fa\u672c\u7ec4", control_type: "Text", bounds: { x: 566, y: 903, width: 85, height: 28 } },
      ],
    });
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0002.json"), { nodes: [] });
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);

    const loaded = new NativeCaseStore({ rootDirectory: fixture.rootDirectory }).loadCase(fixture.caseId);
    const replay = new NativeCaseStore({ rootDirectory: fixture.rootDirectory }).buildReplaySpec(fixture.caseId);
    assert.equal(loaded.session.actions[1].target.semantic_role, "\u79fb\u51fa\u672c\u7ec4");
    assert.equal(replay.actions[1].locator.name, "\u79fb\u51fa\u672c\u7ec4");
    assert.equal(replay.actions[1].locator.control_type, "ListItem");
    assert.equal(replay.actions[1].target_relative_point.x > 0 && replay.actions[1].target_relative_point.x < 1, true);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("uses the first chat row structurally when a dynamic menu belongs to it", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions = [{
      id: "action_0001",
      type: "click",
      timestamp: "2026-08-07T00:00:01.000Z",
      target: { automation_id: "dynamic-row-id", class_name: "Item_dropdown-trigger__hash", control_type: "Group" },
      locator: {
        automation_id: "dynamic-row-id",
        class_name: "Item_dropdown-trigger",
        control_type: "Group",
        ancestor_path: [{ class_name: "Item_chatOrProjectItem", control_type: "Group" }],
      },
      uia_snapshot: "hierarchy/action_0001.json",
    }];
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), {
      nodes: [
        { index: 1, parent_index: -1, name: "\u804a\u5929", control_type: "Group", bounds: { x: 10, y: 100, width: 280, height: 30 } },
        { index: 2, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group", bounds: { x: 10, y: 130, width: 280, height: 50 }, offscreen: false },
        { index: 3, parent_index: 2, name: "dynamic title", control_type: "Text", bounds: { x: 20, y: 140, width: 160, height: 20 } },
        { index: 4, parent_index: 2, automation_id: "dynamic-row-id", class_name: "Item_dropdown-trigger__hash", control_type: "Group", bounds: { x: 250, y: 135, width: 30, height: 30 } },
        { index: 5, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group", bounds: { x: 10, y: 180, width: 280, height: 50 }, offscreen: false },
      ],
    });
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.deepEqual(spec.actions[0].hover_anchor_locator, {
      control_type: "Group",
      class_name: "Item_chatOrProjectItem",
    });
    assert.equal(spec.actions[0].hover_anchor_match_mode, "first_chat_row");
    const cacheStep = store.loadCase(fixture.caseId).cacheCase.steps[0];
    assert.equal(cacheStep.hover_reveal.match_mode, "first_chat_row");
    assert.equal(cacheStep.hover_reveal.target.locators.some((locator) =>
      locator.type === "class_name_re" && locator.stable_token === "Item_chatOrProjectItem"), true);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("keeps exact row text for a dynamic menu on a non-first chat row", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.actions = [{
      id: "action_0001",
      type: "click",
      timestamp: "2026-08-07T00:00:01.000Z",
      target: { automation_id: "second-row-menu", class_name: "Item_dropdown-trigger__hash", control_type: "Group" },
      locator: {
        automation_id: "second-row-menu",
        class_name: "Item_dropdown-trigger",
        control_type: "Group",
        ancestor_path: [{ class_name: "Item_chatOrProjectItem", control_type: "Group" }],
      },
      uia_snapshot: "hierarchy/action_0001.json",
    }];
    writeJson(path.join(fixture.caseDirectory, "hierarchy", "action_0001.json"), {
      nodes: [
        { index: 1, parent_index: -1, name: "\u804a\u5929", control_type: "Group", bounds: { x: 10, y: 100, width: 280, height: 30 } },
        { index: 2, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group", bounds: { x: 10, y: 130, width: 280, height: 50 }, offscreen: false },
        { index: 3, parent_index: 2, name: "first title", control_type: "Text", bounds: { x: 20, y: 140, width: 160, height: 20 } },
        { index: 4, parent_index: -1, class_name: "Item_chatOrProjectItem__hash", control_type: "Group", bounds: { x: 10, y: 180, width: 280, height: 50 }, offscreen: false },
        { index: 5, parent_index: 4, name: "selected history", control_type: "Text", bounds: { x: 20, y: 190, width: 160, height: 20 } },
        { index: 6, parent_index: 4, automation_id: "second-row-menu", class_name: "Item_dropdown-trigger__hash", control_type: "Group", bounds: { x: 250, y: 185, width: 30, height: 30 } },
      ],
    });
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const spec = new NativeCaseStore({ rootDirectory: fixture.rootDirectory }).buildReplaySpec(fixture.caseId);
    assert.equal(spec.actions[0].hover_anchor_match_mode, "exact_row_text");
    assert.equal(spec.actions[0].hover_anchor_locator.name, "selected history");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("repairs legacy post-state target promotion into trigger and effect", () => {
  const fixture = createFixture();
  try {
    const action = fixture.rawTrace.actions[0];
    action.target = {
      name: "请输入派名称",
      control_type: "Edit",
      class_name: "common_editInput__TGkXk",
      bounds: { x: 500, y: 700, width: 300, height: 60 },
    };
    action.locator = {
      name: "请输入派名称",
      control_type: "Edit",
      class_name: "common_editInput__TGkXk",
      fallback_window_point: { x: 450, y: 680 },
    };
    action.capture_diagnostics = {
      selection_source: "semantic_snapshot_promotion",
      immediate_target: {
        name: "",
        control_type: "Group",
        class_name: "common_infoWrapper__BP366",
        bounds: { x: 480, y: 680, width: 340, height: 80 },
      },
      after_snapshot_target: action.target,
    };
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);

    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    assert.equal(loaded.session.actions[0].target.node.class_name, "common_infoWrapper__BP366");
    assert.equal(loaded.session.actions[0].effects[0].type, "target_appeared");
    assert.equal(loaded.session.actions[0].effects[0].locator.name, "请输入派名称");

    const spec = store.buildReplaySpec(fixture.caseId);
    assert.equal(spec.actions[0].locator.class_name, "common_infoWrapper__BP366");
    assert.equal(spec.actions[0].effects[0].locator.name, "请输入派名称");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("repairs a generic Chromium host target from the captured hit leaf", () => {
  const fixture = createFixture();
  try {
    const action = fixture.rawTrace.actions[0];
    action.target = {
      name: "Chrome Legacy Window",
      automation_id: "10249",
      control_type: "Pane",
      class_name: "Chrome_RenderWidgetHostHWND",
      bounds: { x: 850, y: 365, width: 882, height: 960 },
    };
    action.locator = {
      name: "Chrome Legacy Window",
      automation_id: "10249",
      control_type: "Pane",
      class_name: "Chrome_RenderWidgetHostHWND",
      fallback_window_point: { x: 1232, y: 569 },
    };
    action.screen_point = { x: 1221, y: 558 };
    action.window_point = { x: 1232, y: 569 };
    action.capture_diagnostics = {
      hit_leaf_target: {
        name: "聊天记录",
        control_type: "Text",
        bounds: { x: 1211, y: 533, width: 84, height: 29 },
      },
    };
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.equal(loaded.session.actions[0].target.node.name, "聊天记录");
    assert.equal(loaded.session.actions[0].raw_action.target_repair, "generic_chrome_host_replaced_by_hit_leaf");
    assert.equal(spec.actions[0].locator.name, "聊天记录");
    assert.equal(spec.actions[0].locator.automation_id, null);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("persists replay reports and builds acceptance summary", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const report = store.saveReplayReport(fixture.caseId, {
      id: "task-001",
      caseId: fixture.caseId,
      caseName: "元宝测试",
      status: "succeeded",
      createdAt: "2026-08-12T10:00:00.000Z",
      startedAt: "2026-08-12T10:00:01.000Z",
      finishedAt: "2026-08-12T10:00:03.000Z",
      agentId: "agent-001",
      currentStep: 2,
      actionCount: 2,
      error: null,
      results: [{ actionId: "action_0001", status: "succeeded" }],
    });
    assert.equal(report.status, "succeeded");
    assert.ok(fs.existsSync(path.join(fixture.caseDirectory, "reports", "latest-replay-report.json")));
    const acceptance = store.buildAcceptanceReport({ limit: 10 });
    assert.equal(acceptance.summary.replayed, 1);
    assert.equal(acceptance.summary.replay_succeeded, 1);
    assert.equal(acceptance.cases[0].replay_status, "succeeded");
    assert.equal(acceptance.cases[0].generated_qta_status, "missing");
    assert.equal(acceptance.cases[0].ready_for_qta_run, false);

    fs.writeFileSync(path.join(fixture.caseDirectory, "generated-qta.json"), "{}\n", "utf8");
    const generatedAcceptance = store.buildAcceptanceReport({ limit: 10 });
    assert.equal(generatedAcceptance.cases[0].generated_qta_status, "fresh");
    assert.equal(generatedAcceptance.cases[0].ready_for_qta_run, true);
    assert.equal(generatedAcceptance.summary.generated_qta, 1);
    assert.equal(generatedAcceptance.summary.ready_for_qta_run, 1);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("keeps right-click actions through review and replay", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const action = store.loadCase(fixture.caseId).session.actions[0];
    const result = store.saveReviewActions(fixture.caseId, [{ ...action, type: "right_click", label: "右键输入框" }]);
    assert.equal(result.session.actions[0].type, "right_click");
    assert.equal(result.cacheCase.steps[0].action, "right_click");
    assert.equal(store.buildReplaySpec(fixture.caseId).actions[0].type, "right_click");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("loads assertions captured by the native recorder", () => {
  const fixture = createFixture();
  try {
    fixture.rawTrace.assertions = [
      {
        id: "assertion_0001",
        after_action_id: "action_0002_input",
        label: "回答区域应包含“VIP”",
        type: "text_contains",
        expected: "VIP",
        timeout_ms: 15000,
        source: "manual_during_recording",
        target: {
          semantic_role: "回答区域",
          locator_bundle: { automation_id: "answer-list", ancestor_path: [] },
        },
        evidence: {
          screenshot: "frames/assertion_0001.png",
          uia_snapshot: "hierarchy/assertion_0001.json",
        },
      },
    ];
    writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);

    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    assert.equal(loaded.session.assertions.length, 1);
    assert.equal(loaded.session.assertions[0].source, "manual_during_recording");
    assert.equal(loaded.cacheCase.assertions[0].expected, "VIP");
    assert.equal(store.buildReplaySpec(fixture.caseId).assertions[0].target.locator.automation_id, "answer-list");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("builds one ordered timeline for input, click, then its assertion", () => {
  const fixture = createFixture();
  fixture.rawTrace.actions.push({
    id: "action_0002", type: "click", timestamp: "2026-08-07T00:00:03.000Z",
    target: { automation_id: "create-button", control_type: "Button" },
    locator: { automation_id: "create-button" },
  });
  fixture.rawTrace.assertions = [{
    id: "assertion_0005", type: "target_not_exists", after_action_id: "action_0002",
    timeout_ms: 1000,
    evidence: { captured_at: "2026-08-07T00:00:10.000Z" },
    target: { locator: { automation_id: "create-dialog" } },
  }];
  writeJson(path.join(fixture.caseDirectory, "raw-trace.json"), fixture.rawTrace);
  const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
  try {
    const cacheCase = store.loadCase(fixture.caseId).cacheCase;
    assert.equal(cacheCase.assertions[0].after_step_id, "step_003");
    assert.equal(cacheCase.assertions[0].timeout_ms, 10000);
    assert.deepEqual(cacheCase.assertions[0].timeout_policy, {
      type: "poll_async_result",
      recorded_timeout_ms: 1000,
      wait_after_ms: 8000,
      observed_delay_ms: 7000,
      grace_ms: 2000,
      observed_delay_is_bounded_by_wait_after: true,
    });
    assert.deepEqual(cacheCase.timeline_events.map((event) => [event.kind, event.step_id || event.assertion_id]), [
      ["action", "step_001"], ["action", "step_002"], ["action", "step_003"], ["assertion", "assertion_0005"],
    ]);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("stores assertions and includes them in replay tasks", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const result = store.saveReviewAssertions(fixture.caseId, [
      {
        id: "assert_message",
        after_action_id: "action_0002_input",
        label: "用户消息显示正确",
        type: "text_contains",
        expected: "什么是VIP？",
        timeout_ms: 8000,
      },
    ]);
    assert.equal(result.session.assertions.length, 1);
    assert.equal(result.cacheCase.assertions[0].type, "text_contains");
    assert.equal(result.cacheCase.assertions[0].timeout_ms, 10000);
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.equal(spec.assertions[0].after_action_id, "action_0002_input");
    assert.equal(spec.assertions[0].timeout_ms, 8000);
    assert.equal(spec.assertions[0].expected, "什么是VIP？");
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("persists explicit component-title and input-value assertion modes without changing replay semantics", () => {
  const fixture = createFixture();
  const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
  try {
    const result = store.saveReviewAssertions(fixture.caseId, [
      { id: "exists", after_action_id: "action_0001", label: "输入框出现", type: "target_exists", assertion_mode: "component_exists", target: { semantic_role: "输入框", locator_bundle: { automation_id: "searchbar-editor" } } },
      { id: "named", after_action_id: "action_0002_input", label: "名称精确一致", type: "property_equals", property: "name", assertion_mode: "component_name_equals", expected: "测试分组", target: { semantic_role: "测试分组", locator_bundle: { name: "测试分组", control_type: "Text" } } },
      { id: "input", after_action_id: "action_0002_input", label: "输入内容精确一致", type: "property_equals", property: "value", assertion_mode: "input_value_equals", expected: "测试分组", target: { semantic_role: "分组名称输入框", locator_bundle: { automation_id: "group-name-editor", control_type: "Edit" } } },
    ]);
    assert.equal(result.session.assertions[0].assertion_mode, "component_exists");
    assert.equal(result.session.assertions[1].assertion_mode, "component_name_equals");
    assert.equal(result.session.assertions[2].assertion_mode, "input_value_equals");
    assert.equal(store.buildReplaySpec(fixture.caseId).assertions[1].type, "property_equals");
    assert.throws(() => store.saveReviewAssertions(fixture.caseId, [{
      id: "bad", after_action_id: "action_0001", type: "property_equals", property: "name", assertion_mode: "component_name_equals", expected: "测试分组",
      target: { semantic_role: "错误目标", locator_bundle: { class_name: "Item_chatOrProjectItem" } },
    }]), /component_name_equals/);
    assert.throws(() => store.saveReviewAssertions(fixture.caseId, [{
      id: "bad-value", after_action_id: "action_0001", type: "property_equals", property: "name", assertion_mode: "input_value_equals", expected: "测试分组",
      target: { semantic_role: "输入框", locator_bundle: { automation_id: "group-name-editor", control_type: "Edit" } },
    }]), /input_value_equals/);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("keeps aggregate table assertions in cache and replay specs", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const target = {
      semantic_role: "成绩表格",
      locator_bundle: { name: "姓名", control_type: "DataItem", ancestor_path: [] },
    };
    const result = store.saveReviewAssertions(fixture.caseId, [
      {
        id: "assert_keywords",
        after_action_id: "action_0002_input",
        label: "至少命中三个关键词",
        type: "keywords_match_count",
        target,
        scope: "nearest_ancestor",
        scope_control_type: "Table",
        keywords: ["姓名", "张三", "李四", "成绩"],
        minimum_matches: 3,
        timeout_ms: 8000,
      },
      {
        id: "assert_dimensions",
        after_action_id: "action_0002_input",
        label: "表格应为两行三列",
        type: "table_dimensions",
        target,
        scope: "nearest_ancestor",
        scope_control_type: "Table",
        expected_rows: 2,
        expected_columns: 3,
        timeout_ms: 8000,
      },
    ]);
    assert.equal(result.cacheCase.assertions[0].minimum_matches, 3);
    assert.equal(result.cacheCase.assertions[1].expected_columns, 3);
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.deepEqual(spec.assertions[0].keywords, ["姓名", "张三", "李四", "成绩"]);
    assert.equal(spec.assertions[0].scope_control_type, "Table");
    assert.equal(spec.assertions[1].expected_rows, 2);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("supports window scoped keyword assertions without a table target", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const result = store.saveReviewAssertions(fixture.caseId, [
      {
        id: "assert_math_keywords",
        after_action_id: "action_0002_input",
        label: "数学公式关键词至少命中三个",
        type: "keywords_match_count",
        target: null,
        scope: "window",
        keywords: ["a²", "b²", "c²", "a^2", "b^2", "c^2"],
        minimum_matches: 3,
        timeout_ms: 8000,
      },
    ]);
    assert.equal(result.cacheCase.assertions[0].scope, "window");
    assert.equal(result.cacheCase.assertions[0].target, null);
    const spec = store.buildReplaySpec(fixture.caseId);
    assert.equal(spec.assertions[0].scope, "window");
    assert.equal(spec.assertions[0].scope_control_type, null);
    assert.equal(spec.assertions[0].target, null);
    assert.deepEqual(spec.assertions[0].keywords, ["a²", "b²", "c²", "a^2", "b^2", "c^2"]);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("keeps scroll drag and replay lifecycle fields", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const loaded = store.loadCase(fixture.caseId);
    const base = loaded.session.actions[0];
    store.saveReviewActions(fixture.caseId, [
      { ...base, id: "scroll_1", type: "scroll", wheel_delta: -360, coordinate: [300, 400] },
      { ...base, id: "drag_1", type: "drag", coordinate: [300, 500], end_coordinate: [300, 200], duration_ms: 700 },
      { ...base, id: "condition_1", type: "condition", condition_type: "target_exists", skip_if_false: 2 },
      { ...base, id: "condition_body_1" },
      { ...base, id: "condition_body_2" },
    ]);
    const spec = store.buildReplaySpec(fixture.caseId, { restartBeforeReplay: true });
    assert.equal(spec.target.restart_before_replay, true);
    assert.equal(spec.actions[0].wheel_delta, -360);
    assert.deepEqual(spec.actions[1].end_coordinate, { x: 300, y: 200 });
    assert.equal(spec.actions[1].duration_ms, 700);
    assert.equal(spec.actions[2].condition_type, "target_exists");
    assert.equal(spec.actions[2].skip_if_false, 2);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("preserves independent waits and flat if-else branches", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const base = store.loadCase(fixture.caseId).session.actions[0];
    const result = store.saveReviewActions(fixture.caseId, [
      {
        ...base,
        id: "condition_1",
        type: "condition",
        condition_type: "same_kind_exists",
        condition_match_mode: "same_kind",
        condition_scope_type: "section",
        condition_scope_start_name: "分组",
        condition_scope_end_name: "聊天",
        true_step_count: 1,
        false_step_count: 1,
      },
      { ...base, id: "true_1", label: "成立分支" },
      { ...base, id: "false_1", label: "不成立分支" },
      {
        ...base,
        id: "wait_1",
        type: "wait_time",
        target: null,
        raw_action: null,
        timeout_ms: 15000,
      },
    ]);
    assert.equal(result.session.actions[0].true_step_count, 1);
    assert.equal(result.session.actions[0].false_step_count, 1);
    assert.equal(result.session.actions[3].timeout_ms, 15000);
    assert.deepEqual(result.cacheCase.steps[0].condition, {
      type: "same_kind_exists",
      true_step_count: 1,
      false_step_count: 1,
      match_mode: "same_kind",
      scope: { type: "section", start_name: "分组", end_name: "聊天" },
      fallback_policy: "strict",
    });
    assert.deepEqual(result.cacheCase.steps[3].synchronization, {
      type: "fixed_delay",
      duration_ms: 15000,
    });
    const replaySpec = store.buildReplaySpec(fixture.caseId);
    assert.equal(replaySpec.actions[0].condition_type, "same_kind_exists");
    assert.equal(replaySpec.actions[0].condition_match_mode, "same_kind");
    assert.equal(replaySpec.actions[0].condition_scope_type, "section");
    assert.equal(replaySpec.actions[0].condition_scope_start_name, "分组");
    assert.equal(replaySpec.actions[0].condition_scope_end_name, "聊天");
    assert.equal(replaySpec.actions[0].condition_fallback_policy, "strict");
    assert.equal(replaySpec.actions[0].true_step_count, 1);
    assert.equal(replaySpec.actions[0].false_step_count, 1);
    assert.equal(replaySpec.actions[3].timeout_ms, 15000);
    assert.equal(replaySpec.actions[3].locator, null);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("rejects invalid or nested control flow", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const base = store.loadCase(fixture.caseId).session.actions[0];
    assert.throws(() => store.saveReviewActions(fixture.caseId, [
      { ...base, type: "condition", true_step_count: 2, false_step_count: 0 },
      { ...base, id: "only_one_branch_step" },
    ]), /后续步骤数量不足/);
    assert.throws(() => store.saveReviewActions(fixture.caseId, [
      { ...base, type: "condition", true_step_count: 1, false_step_count: 0 },
      { ...base, id: "nested", type: "condition", true_step_count: 1, false_step_count: 0 },
      { ...base, id: "nested_body" },
    ]), /暂不支持嵌套条件分支/);
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

function readRawTrace(caseDirectory) {
  return JSON.parse(fs.readFileSync(path.join(caseDirectory, "raw-trace.json"), "utf8"));
}

test("renames a case through metadata without mutating Raw Trace", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const result = store.renameCase(fixture.caseId, "重命名后的真实用例");
    assert.equal(result.session.case_name, "重命名后的真实用例");
    assert.equal(result.cacheCase.case.name, "重命名后的真实用例");
    assert.equal(store.listCases()[0].name, "重命名后的真实用例");
    assert.deepEqual(readRawTrace(fixture.caseDirectory), fixture.rawTrace);
    assert.ok(fs.existsSync(path.join(fixture.caseDirectory, "case-metadata.json")));
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});

test("soft deletes a case into the recoverable runtime trash", () => {
  const fixture = createFixture();
  try {
    const store = new NativeCaseStore({ rootDirectory: fixture.rootDirectory });
    const result = store.deleteCase(fixture.caseId);
    assert.equal(result.deleted, true);
    assert.equal(store.listCases().length, 0);
    assert.equal(fs.existsSync(fixture.caseDirectory), false);
    assert.ok(fs.existsSync(path.join(fixture.rootDirectory, result.recoverablePath)));
  } finally {
    fs.rmSync(fixture.rootDirectory, { recursive: true, force: true });
  }
});
