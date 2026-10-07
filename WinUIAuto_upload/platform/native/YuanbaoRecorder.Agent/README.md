# Windows 原生 Agent

基于 C#、Win32、Windows Forms 和 UI Automation 实现的操作采集与回放组件。

从项目根目录执行 `npm run start:agent`；平台需先通过 `npm start` 启动。启动脚本会构建 `Current` 版本，设置连接地址并校验 Agent 状态。地址配置见 [配置指南](../../../docs/CONFIGURATION.md)。

在本目录也可直接调用脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\start-agent.ps1
```

| 脚本 | 用途 |
| --- | --- |
| `build.ps1` | 查找 Roslyn 并编译程序，不启动 GUI |
| `start-agent.ps1` | 关闭同名旧实例、重新构建、启动并校验连接 |
| `smoke-test.ps1` | 构建 Validation 版本并创建测试窗口验证 UIA |

启动前应结束已有录制，不要直接双击 `bin/` 中的 exe。录制、回放与冒烟验证需要未锁屏的交互桌面。构建产物和录制数据不纳入版本管理。

[返回项目首页](../../../README.md)
