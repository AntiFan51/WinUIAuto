# QTA 代码风格

生成用例必须继承 `YBWindowsQtaBase`，不得直接导入 `testbase`。必须包含以下元数据：`owner`、`timeout`、`priority`、`status`、`cache_case_id` 和 `case_text`。

生成文件必须满足以下要求：

1. 只导入固定公共 Runtime，以及用例真正需要的 Python 标准库内容；
2. 实现 `run_test(self)`；
3. 在适用场景中记录三个层级：来源步骤、操作、断言；
4. 使用 Kit 完成交互和失败截图；
5. 使用 Kit 断言，禁止使用原生 `assert`、`self.fail` 或通过打印伪造 PASS；
6. 不得包含自定义 Driver、Kit、Locator 搜索、截图、剪贴板或 UIA 实现。

推荐的报告步骤标签：

```python
self.start_step("步骤1：输入问题 [cache:step-001]")
self.start_step("步骤1·操作：在聊天输入框输入问题")
self.start_step("步骤1·断言：输入内容正确显示")
```

标准文件结构：

```python
# -*- coding: utf-8 -*-
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[2]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from qta_windows_base.base import YBWindowsQtaBase
from yuanbao_windows_kit import Locator, Target

CHAT_INPUT = Target(
    role="聊天输入框",
    locators=(Locator("automation_id", "searchbar-editor", control_type="Edit"),),
)

class YBQtaWindowsTestExample(YBWindowsQtaBase):
    """验证元宝 Windows 客户端可以输入并发送问题。"""

    owner = "yuanbao-ai"
    timeout = 180
    priority = YBWindowsQtaBase.EnumPriority.High
    status = YBWindowsQtaBase.EnumStatus.Ready
    cache_case_id = "case-id"
    case_text = """原始用例语义"""

    def run_test(self):
        kit = self.kit
        kit.step("步骤1：输入问题 [cache:step-001]")
        kit.input_text(CHAT_INPUT, "什么是VPN？")
        kit.expect_text_equals(CHAT_INPUT, "什么是VPN？", label="输入内容正确")
```

完整生成文件不得超过 220 行，大多数用例应控制在 120 行以内。
