const assert = require("assert");
const fs = require("fs");
const path = require("path");
const test = require("node:test");

const projectRoot = path.resolve(__dirname, "..", "..", "qta_runtime");
const kitPath = path.join(projectRoot, "scripts", "yuanbao_windows_kit", "kit.py");
const basePath = path.join(projectRoot, "qta_windows_base", "base.py");

test("fixed Windows runtime exposes the codegen API contract", () => {
  const kit = fs.readFileSync(kitPath, "utf8");
  const base = fs.readFileSync(basePath, "utf8");
  for (const method of [
    "click", "right_click", "click_after_hover", "input_text", "press", "scroll", "drag", "wait_exists", "exists", "exists_in_section",
    "wait_not_exists", "expect_exists", "expect_not_exists", "expect_text_contains",
    "expect_text_equals", "expect_property_equals", "expect_keywords",
  ]) {
    assert.match(kit, new RegExp(`def ${method}\\(`), `missing kit.${method}`);
  }
  assert.match(kit, /Desktop\(backend="uia"\)/);
  assert.match(kit, /def _matches_process\(/);
  assert.match(kit, /def _candidate_hwnds\(/);
  assert.match(kit, /win32gui\.EnumWindows\(/);
  assert.match(kit, /"wechatappex\.exe"/);
  assert.match(kit, /"ENTER": "\{ENTER\}"/);
  assert.match(kit, /def fallback_scope_candidates\(/);
  assert.match(kit, /def _locator_criteria_variants\(/);
  assert.match(kit, /stable_token = re\.sub\(/);
  assert.match(kit, /texts\.extend\(child\.window_text\(\) for child in candidate\.descendants\(\)\)/);
  assert.match(kit, /Desktop\(backend="uia"\)\.from_point\(x, y\)/);
  assert.match(kit, /wrappers = root\.descendants\(\)/);
  assert.match(kit, /def _is_visible_wrapper\(/);
  assert.match(kit, /getattr\(info, "offscreen", False\)/);
  assert.doesNotMatch(kit, /\.is_offscreen\(\)/);
  assert.match(kit, /self\._matches_wrapper\(wrapper, criteria\)/);
  const assertionRuntime = kit.slice(kit.indexOf("def expect_exists("), kit.indexOf("def expect_property_equals("));
  assert.doesNotMatch(assertionRuntime, /fallback_scope_candidates/);
  const clickRuntime = kit.slice(kit.indexOf("def click(self, target, timeout=10, button="), kit.indexOf("def input_text("));
  assert.doesNotMatch(clickRuntime, /fallback_point|mouse\.click/);
  assert.match(kit, /window\(handle=handle\)\.wrapper_object\(\)/);
  assert.match(kit, /reference_window_size/);
  assert.match(kit, /while time\.time\(\) < deadline:/);
  assert.match(kit, /self\.driver\._root\(\) if target is None/);
  assert.match(kit, /rect\.width\(\) \/ int\(reference_width\)/);
  assert.match(base, /class YBWindowsQtaBase\(TestCase\)/);
  assert.match(base, /self\.kit = YBWindowsKit\(/);
});

test("native replay restart readiness does not depend on the first action locator", () => {
  const replayExecutorPath = path.join(
    __dirname,
    "..",
    "native",
    "YuanbaoRecorder.Agent",
    "ReplayExecutor.cs",
  );
  const source = fs.readFileSync(replayExecutorPath, "utf8");

  assert.match(source, /HasUsableApplicationContent\(candidate\)/);
  assert.match(source, /FrameworkIdProperty, "Chrome"/);
  assert.match(source, /FindVisibleWindowsByProcessName\(processName\)/);
  assert.doesNotMatch(source, /var readinessLocator = restarted/);
  assert.doesNotMatch(source, /未进入用例起始页/);
});
