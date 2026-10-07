# 元宝 Windows UI 自动化平台

面向腾讯元宝 Windows 客户端的人工操作录制、测试复核、原生回放与 QTA 脚本生成平台。

项目将人工测试过程转化为包含操作、定位证据、断言和流程控制的结构化用例，支持通过原生 Agent 验证操作过程，并生成基于公共 Windows Runtime 的 Python QTA 脚本。适用于对人工测试经验的沉淀，以及对既有 GUI Agent 自动执行链路的补充。

## 核心能力

| 能力 | 说明 |
| --- | --- |
| 操作录制 | 采集点击、右键、输入、按键、滚动和拖动，关联 UI Automation 控件信息与截图 |
| 用例复核 | 查看原始轨迹，编辑步骤、定位、断言、等待和 IF/ELSE，保留原始证据 |
| 原生回放 | 由 Windows Agent 执行用例，回传逐步状态、断言结果和错误信息 |
| 脚本生成 | 根据 Cache Case 生成 QTA Python 代码，检查步骤覆盖、公共 API 和条件范围 |
| 公共执行层 | 提供统一的 UIA 定位、交互、等待与断言接口，减少生成脚本中的重复实现 |

## 系统架构

```text
人工操作元宝客户端
        │
        ▼
Windows Agent ──采集──▶ Raw Trace + UIA + 截图
        ▲                         │
        │                         ▼
        │                 Web 管理平台 · 人工复核
        │                         │
        │                         ▼
        └──── 原生回放 ────── Cache Case
                                  │
                                  ▼
                            AI 生成与校验
                                  │
                                  ▼
                         人工审查 → QTA 实跑
```

管理平台和原生 Agent 在同一 Windows 执行机上运行，通过回环 HTTP 通信并共享录制目录。浏览器使用同源 API；服务地址与 AI 服务配置不绑定开发者的电脑或内网环境。

## 环境要求

| 组件 | 要求 |
| --- | --- |
| Windows | x64，已登录且未锁屏的交互桌面 |
| 元宝客户端 | 已安装并登录；与 Agent 保持相同权限级别 |
| Node.js | 18 或更高版本，包含 npm；使用内置 HTTP、fetch 和测试模块 |
| C# 工具链 | Visual Studio / Build Tools，包含 MSBuild、Roslyn 和 .NET Framework 桌面开发组件 |
| Python | x64 Python 3.9，用于运行 QTA；依赖见 `qta_runtime/requirements.txt` |
| AI 服务 | 生成代码时按需配置；录制、复核和原生回放无需 AI 密钥 |

## 快速开始

以下命令在本项目根目录（包含 `package.json` 的目录）执行。

**1. 启动管理平台**

```powershell
npm start
```

浏览器打开终端输出的服务地址。Node.js 部分无第三方依赖，无需执行 `npm install`。首次启动为空用例库，可通过 Agent 新建录制。

**2. 启动原生 Agent**

在另一个位于项目根目录的 PowerShell 窗口执行：

```powershell
npm run start:agent
```

启动脚本自动构建并校验 Agent。确认终端显示 `Agent ready`、界面显示“管理平台已连接”后开始录制。请先结束已有录制；启动脚本会关闭同名旧 Agent。不要直接双击构建目录中的 exe。

**3. 按需配置脚本生成与执行环境**

```powershell
Copy-Item .\platform\config.example.json .\platform\config.local.json
py -3.9 -m venv .\qta_runtime\.venv
.\qta_runtime\.venv\Scripts\python.exe -m pip install -r .\qta_runtime\requirements.txt
```

将配置样例中的 API 地址、模型和空密钥替换为实际配置后再生成代码。详见 [配置指南](docs/CONFIGURATION.md) 与 [使用指南](docs/USER-GUIDE.md)。真实配置文件已被 Git 忽略。

## 项目结构

```text
windows/
├── README.md                     项目概览与快速开始
├── package.json                  统一启动、构建和测试命令
├── docs/                         配置、使用与开发文档
├── platform/                     Web 管理平台
│   ├── server.js                 HTTP 服务与 API
│   ├── lib/                      用例管理、回放调度、生成与校验
│   ├── native/YuanbaoRecorder.Agent/  原生 Agent 与构建脚本
│   ├── skills/                   AI 代码生成规则（运行依赖）
│   ├── tests/                    平台回归测试
│   └── config.example.json       无密钥配置样例
└── qta_runtime/                  Windows QTA 公共执行层
    ├── qta_windows_base/         QTA 生命周期适配
    ├── scripts/yuanbao_windows_kit/   UIA 交互与断言
    ├── tests/                    Runtime 回归测试
    └── requirements.txt          Python 依赖
```

## 文档

- [配置指南](docs/CONFIGURATION.md)：服务端口、Agent 地址、AI 配置及安全边界。
- [使用指南](docs/USER-GUIDE.md)：录制、复核、回放、QTA 生成与执行。
- [开发指南](docs/DEVELOPMENT.md)：工程命令、验证方法、源码与运行数据管理。
- [第三方声明](platform/docs/THIRD-PARTY-NOTICES.md)：所使用代码的来源与许可。

## 适用范围与限制

项目面向可信 Windows 桌面环境，不提供多用户认证、远程执行隔离或公网部署能力。录制与回放依赖交互桌面；IF/ELSE 暂不支持嵌套。

AI 生成代码必须经过人工审查。原生回放通过、生成成功、Python 静态检查通过和 QTA 实跑通过是独立的验证阶段，不互相替代。运行数据与生成用例默认不纳入版本管理。
