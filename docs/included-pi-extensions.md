# Pi Companion 随附的 Pi Extension

本文是发布说明和第三方组件披露的事实来源。新增、删除或升级随应用分发的 Pi Extension 时，必须同步更新本文、锁文件及 `THIRD-PARTY-NOTICES.md`。

## 随附清单

| Extension | 版本 | 来源 | 许可证 | 归属 | 用途 |
|---|---:|---|---|---|---|
| Pi Companion Extension | 随应用版本 | 本仓库 `src/PiCompanion.Extension` | 第一方 | Pi Companion | 工作区权限、附件、恢复、交互和文件发布 |
| Pi 内置 MCP、Tool Search、Code Mode | 1.0.3 | `@earendil-works/pi-coding-agent` | MIT | Pi 贡献者 | MCP 连接、工具发现与批量调用 |
| Pi Companion Web Search Extension | 随应用版本 | 本仓库 `src/PiCompanion.WebSearchExtension` | 第一方与第三方组合 | Pi Companion、`pi-web-search` 贡献者 | 为受支持模型接入 Provider 原生网络搜索；内含 `pi-web-search` 1.6.0（MIT） |

## MCP 集成边界

- 设置中的 MCP 页面提供个人和工作区两种保存位置，分别使用 Pi 的全局 `mcp.json` 和工作区 `.pi/mcp.json`。工作区配置需要用户信任；同名工作区服务优先。个人服务可在工作区单独启停和调整调用方式，局部覆盖不复制连接配置或凭据。
- 支持 stdio 本地进程、Streamable HTTP 远程服务和 Pi 提供的 OAuth 登录；凭据使用 Pi 的 `mcp-auth.json`，按服务名称和 URL 隔离。导入先预览，整批验证通过后写入，遇到同名服务或工具命名冲突会提示用户处理。
- 新服务默认按需发现工具；高级设置可选直接调用或批量调用。嵌套调用逐项通过 Companion 权限检查，只读任务会拦住未声明只读的 MCP 工具。
- MCP Apps 的交互界面尚未接入。

## Web Search 集成边界

- `pi-web-search` 以精确版本锁定，并与第一方适配器一起由 `src/PiCompanion.WebSearchExtension` 构建成应用私有单文件；不会执行 `pi install`，也不会修改用户的全局 Pi Extension。
- Pi 内置的 OpenAI Responses、OpenAI Codex Responses、Azure OpenAI Responses、Google Generative AI、Anthropic Messages、xAI Responses、GitHub Copilot Responses 和 OpenCode Responses 模型通过 `pi-web-search` 执行搜索。
- Pi 内置 Xiaomi Provider 的 `mimo-v2.5`、`mimo-v2.5-pro`、`mimo-v2.5-pro-ultraspeed`、`mimo-v2.6-flash`、`mimo-v2.6-pro` 和 `mimo-v2.6-pro-ultraspeed` 通过第一方请求适配器启用 MiMo 联网搜索。使用前必须在 MiMo 控制台开通联网服务插件；搜索调用和新增输入 Token 按 MiMo 规则计费。
- MiMo 的搜索来源位于供应商响应的 `annotations` 中，Pi 1.0.3 的 OpenAI-compatible 响应解析不会保留该字段，因此当前对话可获得联网后的模型回答，但不会生成 Pi `web_search` 工具记录或可点击引用。
- 自定义 Provider 即使使用相同 API 协议也不会自动获得此能力。外部搜索服务（例如 Tavily、Brave 或 Serper）不在当前随附范围。
- 自动化测试不调用真实 Provider。发布候选版本需分别完成已配置 Provider 的人工联网搜索、可点击引用、错误与取消测试。

## 发布说明要求

每个正式版本的发行说明至少列出：

1. 随附 Extension 名称和版本。
2. 第一方或第三方归属及许可证。
3. Extension 获得的能力与网络访问范围。
4. 新增、移除、升级以及实验性状态变化。
