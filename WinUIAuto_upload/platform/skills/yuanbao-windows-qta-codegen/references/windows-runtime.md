# 固定 Windows Runtime API

Runtime 已经统一提供 pywinauto/UIA、窗口激活与边界检查、Locator 回退、Unicode 输入、截图、诊断和 QTA 失败报告。生成代码只能使用下方列出的 API。

## Target 声明

```python
Locator(by, value, control_type=None, confidence=1.0)
Target(role, locators=(...), fallback_point=None, reference_window_size=None)
```

支持的 `by` 值包括：`automation_id`、`name`、`name_re`、`class_name`、`class_name_re`、`control_type`、`name_and_control_type` 和 `control_type_and_class`。

使用 `name_and_control_type` 时，优先采用 `Locator("name_and_control_type", {"name": "创建", "control_type": "Button"})`。为了兼容旧用法，Runtime 也接受 `(name, control_type)`，以及将 `value=name` 与 `control_type` 参数组合使用。

对于交互过程中会增加或移除状态 Token 的 CSS/WebView Class，应使用 `class_name_re`，匹配稳定 Token，而不是完整的瞬态 Class 字符串。

通过 `confidence` 保留 Cache Case 中的 Locator 顺序。只有 Cache Case 提供窗口相对坐标兜底时，才能设置 `fallback_point=(x, y)`；同时必须从该 Locator 设置 `reference_window_size=(width, height)`，使 Runtime 能在窗口尺寸变化时缩放坐标。

### 动作 Target 的语义边界

`Target` 的 Locator 表示要交互的那个控件，而不是包含它的区域。Runtime 优先命中 UIA Locator；因此一旦 `t-dialog` 容器可定位，`fallback_point` 不会把 `kit.click(DIALOG)` 自动修正为点击 `t-dialog__close`。关闭按钮、弹窗容器、标题文本和页面内容区域必须声明为不同的 Target。

生成代码必须保留 Cache Case 动作目标的稳定身份。特别是关闭类 Target 必须含 `close`、`关闭`、`dismiss`、`cancel` 对应的 Name、Automation ID 或稳定 class token；不得以 `dialog`、`modal`、`popup`、`portal` 等祖先容器 Locator 替代。

## 操作 API

```python
kit.step(label)
kit.screenshot(label)
kit.click(target, timeout=10)
kit.right_click(target, timeout=10)
kit.click_after_hover(target, hover_target, timeout=10)
kit.input_text(target, text, timeout=10, clear=True)
kit.press(keys)
kit.scroll(wheel_delta=-360, target=None)
kit.drag(start=(x, y), end=(x, y), duration_ms=500)
kit.wait_seconds(seconds)
kit.wait_exists(target, timeout=10)
kit.wait_not_exists(target, timeout=10)
kit.exists(target, timeout=0)  # boolean query for if/else; never fails the case
kit.exists_in_section(target, start_name, end_name, timeout=0)  # strict section-scoped boolean query
```

Use `click_after_hover` only when the Cache Case step contains `hover_reveal`. Runtime locates the live hover anchor through UIA, moves to its current bounds to reveal the transient control, then locates and clicks the actual target. It does not use the recorded pointer coordinate as the hover anchor.

当 Cache Case 包含 `condition.scope.type: section` 时，必须使用 `exists_in_section`。起止名称是 UIA 区域的语义标记，必须原样复制；不得用窗口级 `exists` 替代该调用。

## 断言 API

```python
kit.expect_exists(target, timeout=10, label=None)
kit.expect_not_exists(target, timeout=10, label=None)
kit.expect_text_contains(target, expected, timeout=10, label=None)
kit.expect_text_equals(target, expected, timeout=10, label=None)
kit.expect_property_equals(target, property_name, expected, timeout=10, label=None)  # property_name: name / value / automation_id / class_name / control_type / enabled
kit.expect_keywords(target, keywords, minimum=1, timeout=10, label=None)
```

对于 `scope` 为 `window` 的 Cache Case `keywords_match_count` 断言，必须将目标传为 `None`，使固定 Runtime 搜索完整元宝窗口：

```python
kit.expect_keywords(None, keywords=("a", "b", "c"), minimum=3, timeout=30)
```

不得为窗口级断言编造范围更窄的回答容器目标。
Runtime 会持续轮询目标是否出现及其后代文本，直至超时；必须始终将 `timeout_ms` 转换为秒，不得用固定值替代。

允许使用的属性名包括：`name`、`automation_id`、`class_name`、`control_type`、`enabled`。

将 Cache Case 中的每个预期结果映射为最接近的真实断言。如果现有断言无法验证，应在输出对象中增加 warning，不得编造 API 或虚假断言。
