# 开发指南

[返回项目首页](../README.md)

## 模块职责

| 模块 | 职责 |
| --- | --- |
| `platform/server.js` | HTTP API、静态资源及服务启动 |
| `platform/lib/server-config.js` | 服务端口校验及回环监听约定 |
| `platform/lib/native-case-store.js` | Raw/Review Trace、Cache Case、证据与报告管理 |
| `platform/lib/replay-task-store.js` | Agent 状态、回放任务与执行进度 |
| `platform/lib/qta-code-generator.js` | AI 请求、生成结果校验、诊断与导出 |
| `platform/native/YuanbaoRecorder.Agent/` | Win32 输入采集、UIA 证据、原生回放 |
| `qta_runtime/` | QTA 生命周期和公共 UIA 操作层 |

新增行为优先扩展公共 Runtime 与已有回归测试，避免把执行实现复制到生成的业务脚本。修改目录结构时须同步检查代码生成导出路径、Runtime 导入及文档链接。

## 工程命令

所有命令从项目根目录执行：

| 命令 | 用途 |
| --- | --- |
| `npm start` | 启动管理平台 |
| `npm run start:agent` | 构建、启动并校验原生 Agent |
| `npm run build:agent` | 仅编译原生 Agent 和冒烟测试程序 |
| `npm test` | 执行 Node.js 回归测试 |
| `npm run test:python` | 使用 `qta_runtime/.venv/` 执行 Python 回归测试 |
| `npm run test:agent` | 创建测试窗口，执行原生 UIA 冒烟测试 |
| `npm run report -- 10` | 生成最近 10 条用例的回放汇总 |

Node.js 部分没有外部 npm 依赖。Python 依赖通过 `requirements.txt` 固定直接依赖版本；安装步骤见首页。不要提交依赖目录或自行生成的运行结果。

## 验证分层

1. **平台回归**：用例转换、生成校验、回放调度、空库启动、动态端口和静态文件访问边界。
2. **Runtime 回归**：窗口选择、控件定位及交互逻辑，使用模拟对象避免操作实际客户端。
3. **原生构建与 UIA 冒烟**：编译检查与可交互桌面测试分开执行。
4. **业务端到端**：在真实元宝客户端完成录制、复核、原生回放、生成与 QTA 实跑，分别记录结果。

平台接口测试在临时目录中创建合成数据，并使用系统分配端口，不读取开发者用例库。Node.js 的 AI 测试使用模拟响应，不请求真实模型。工程测试或编译成功不能代替业务端到端验收。

Agent 编译脚本通过 `vswhere.exe` 查找 Roslyn，引用 .NET Framework 程序集；不使用 `dotnet build`。`Current` 是正式运行配置，`Validation` 用于冒烟验证。构建时记录源码版本；没有 Git 元数据时标记为 `unknown`。

## 版本管理

应提交源码、工程回归测试、运行规则、配置样例、依赖声明和文档。以下内容由 `.gitignore` 排除：

| 路径 / 类型 | 内容 |
| --- | --- |
| `platform/cases-native/` | 录制轨迹、复核稿、Cache Case、截图、UIA 树、生成结果及诊断 |
| `platform/runtime/` | 汇总报告与回收目录 |
| `qta_runtime/qta_cases/` | 生成、导出及人工业务用例 |
| `bin/`、`obj/` | 可执行程序、调试文件与自动构建信息 |
| `.venv/`、`node_modules/`、`__pycache__/` | 安装依赖及解释器缓存 |
| `config.local.json`、`.env*` | 真实凭证和执行机配置 |
| 日志、报告及临时文件 | 运行过程中产生的中间数据 |

QTA 截图、Agent 日志和客户端启动路径缓存还可能写入操作系统临时目录或用户应用数据目录，属于执行机数据。

提交前检查暂存区：

```powershell
git diff --cached --name-only
git diff --cached --check
```

`.gitignore` 不会取消已有跟踪记录；不要通过 `git add -f` 提交运行数据。保持 `.editorconfig` 和 `.gitattributes` 中的编码、缩进与换行约定。第三方来源及许可说明必须随对应代码保留。
