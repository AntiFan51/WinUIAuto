# 配置指南

[返回项目首页](../README.md)

## 管理服务与 Agent

平台仅监听 IPv4 回环接口。默认端口为 `4173`，启动日志使用标准 `localhost` 地址显示访问入口；它表示当前执行机，不代表某台开发设备。无需填写电脑 IP，也不需要修改前端 URL。

| 配置 | 默认值 | 用途 |
| --- | --- | --- |
| `PORT` | `4173` | 管理平台监听端口；Agent 启动脚本也读取此值 |
| `CACHE_AGENT_SERVER_URL` | 根据 `PORT` 构造的本地服务地址 | 覆盖 Agent 连接地址 |
| Agent 参数 `-ServerUrl` | 未设置 | 优先于环境变量，显式指定服务地址 |

Agent 地址优先级：`-ServerUrl` → `CACHE_AGENT_SERVER_URL` → 基于 `PORT` 的默认地址。平台不读取 `CACHE_AGENT_SERVER_URL`。服务地址必须是 HTTP(S) origin，不能带账号密码、路径、查询参数或片段。

更换端口时，在两个 PowerShell 窗口分别设置相同的 `PORT`，然后启动对应进程：

```powershell
# 平台窗口，当前目录为项目根目录
$env:PORT = "5180"
npm start
```

```powershell
# Agent 窗口，当前目录为项目根目录
$env:PORT = "5180"
npm run start:agent
```

如果此前配置过 `CACHE_AGENT_SERVER_URL`，请同步更新或移除该环境变量，避免覆盖新的端口设置。需要显式传入地址时，可将平台启动日志中的地址保存为环境变量，再执行：

```powershell
npm run start:agent -- -ServerUrl $env:CACHE_AGENT_SERVER_URL
```

日常运行端口范围为 `1–65535`；平台测试可使用 `PORT=0` 由操作系统分配端口，Agent 使用日志显示的实际地址连接。端口被占用时，服务会输出启动错误并以非零状态退出。

## AI 代码生成

录制、复核和原生回放不要求配置 AI。代码生成支持本地配置文件及环境变量，环境变量优先。

### 配置文件

从项目根目录复制样例：

```powershell
Copy-Item .\platform\config.example.json .\platform\config.local.json
```

| 字段 | 含义 |
| --- | --- |
| `apiKey` | 当前服务的 API 密钥，样例留空 |
| `baseUrl` | 服务的 API 根地址，通常以 `/v1` 结尾 |
| `model` | 该服务支持的模型 ID |
| `apiStyle` | `responses` 或 `chat_completions` |
| `openaiApiKey` | 可选；使用官方服务时的独立密钥字段，存在时优先于文件内的 `apiKey` |

样例中的 `api.example.com` 是文档占位域名，不能用于实际生成；必须替换地址、模型和密钥。缺省地址与模型沿用生成器的内置默认值，团队环境应显式配置实际使用的服务。

### 环境变量

| 环境变量 | 对应字段 | 兼容变量 |
| --- | --- | --- |
| `AI_API_KEY` | `apiKey` | `OPENAI_API_KEY` |
| `AI_BASE_URL` | `baseUrl` | `OPENAI_BASE_URL` |
| `AI_MODEL` | `model` | `OPENAI_MODEL` |
| `AI_API_STYLE` | `apiStyle` | 无 |

优先级为：`AI_*` → 对应兼容变量 → `config.local.json` → 内置默认值；密钥无内置值。环境变量修改后，需要在同一终端重新启动平台进程。本项目不自动加载 `.env` 文件。

生成器按需读取本地配置。切换服务时请确认密钥与地址属于同一服务，不要复用其他服务的凭证。配置状态接口不返回密钥。

## 运行路径

项目以源码位置解析路径，不依赖终端的绝对目录或特定盘符。`platform/` 和 `qta_runtime/` 应保持相邻：

- 录制与复核：`platform/cases-native/`。
- 生成用例：`qta_runtime/qta_cases/windows_generated/`。
- 汇总与回收站：`platform/runtime/`。
- Agent 构建：`platform/native/YuanbaoRecorder.Agent/bin/`。

这些目录在相应操作时创建，不需要复制历史数据或手动创建占位文件。

## 安全边界

平台与 Agent 当前依赖同机共享目录，配置 URL 不意味着支持跨机器部署。没有身份认证的桌面控制服务不应通过端口转发、反向代理或监听配置暴露给其他机器。

静态文件服务仅开放页面资源和录制截图，不开放本地配置、源码或生成诊断文件。业务 API 仍面向可信本机访问，不能视作多用户安全隔离。

`config.local.json`、密钥、录制数据、截图和日志不得提交。AI 生成会向所配置的服务发送 Cache Case 中的步骤、文本及定位信息，使用前应完成数据授权与敏感信息审查。
