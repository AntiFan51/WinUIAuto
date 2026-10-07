# Web 管理平台

负责用例管理、人工复核、回放调度与 AI 代码生成；原生 Windows Agent 源码位于 `native/YuanbaoRecorder.Agent/`。

请从项目根目录运行 `npm start`，并通过终端输出的地址访问页面。平台只使用 Node.js 内置模块，无需安装额外 npm 依赖。

- [项目首页与快速开始](../README.md)
- [配置指南](../docs/CONFIGURATION.md)
- [使用指南](../docs/USER-GUIDE.md)
- [开发与测试](../docs/DEVELOPMENT.md)
- [第三方声明](docs/THIRD-PARTY-NOTICES.md)

`skills/` 为代码生成运行依赖。录制和生成结果按需创建，不纳入版本管理。`config.example.json` 为无密钥样例，真实配置使用被忽略的 `config.local.json`。
