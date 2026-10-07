# Windows QTA Runtime

为生成的 QTA Python 脚本提供统一的 Windows UI 操作能力。

| 模块 | 职责 |
| --- | --- |
| `qta_windows_base/` | QTA 生命周期、连接与截图收尾 |
| `scripts/yuanbao_windows_kit/` | UIA 定位、交互、等待、条件和断言 |
| `settings.py` | QTAF 框架配置 |
| `tests/` | 公共执行层回归测试 |

从项目根目录准备环境：

```powershell
py -3.9 -m venv .\qta_runtime\.venv
.\qta_runtime\.venv\Scripts\python.exe -m pip install -r .\qta_runtime\requirements.txt
npm run test:python
```

管理平台自动导出业务脚本到 `qta_cases/windows_generated/`。执行前需人工审查，并准备目标客户端状态；操作步骤见 [使用指南](../docs/USER-GUIDE.md)。通过与否以 QTA 最终结果为准。

[返回项目首页](../README.md)
