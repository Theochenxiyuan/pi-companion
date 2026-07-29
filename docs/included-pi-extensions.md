# Pi Companion 随附的 Pi Extension

本文是发布说明和第三方组件披露的事实来源。新增、删除或升级随应用分发的 Pi Extension 时，必须同步更新本文、锁文件及 `THIRD-PARTY-NOTICES.md`。

## 随附清单

| Extension | 版本 | 来源 | 许可证 | 归属 | 用途 |
|---|---:|---|---|---|---|
| Pi Companion Extension | 随应用版本 | 本仓库 `src/PiCompanion.Extension` | 第一方 | Pi Companion | 工作区权限、附件、恢复、交互和文件发布 |
| `pi-web-search` | 1.3.1 | [npm](https://www.npmjs.com/package/pi-web-search) / [GitHub](https://github.com/ttttmr/pi-web-search) | MIT | 第三方 | 使用受支持模型所属 Provider 的原生网络搜索 API |

## `pi-web-search` 集成边界

- 以精确版本锁定，并由 `src/PiCompanion.WebSearchExtension` 构建成应用私有单文件；不会执行 `pi install`，也不会修改用户的全局 Pi Extension。
- 当前只为 Pi 内置的 OpenAI Responses、OpenAI Codex Responses、Google Generative AI 和 Anthropic Messages 模型启用。
- 自定义 Provider 即使使用相同 API 协议也不会自动获得此能力。外部搜索服务（例如 Tavily、Brave 或 Serper）不在当前随附范围。
- 自动化测试不调用真实 Provider。发布候选版本需分别完成已配置 Provider 的人工联网搜索、可点击引用、错误与取消测试。

## 发布说明要求

每个正式版本的发行说明至少列出：

1. 随附 Extension 名称和版本。
2. 第一方或第三方归属及许可证。
3. Extension 获得的能力与网络访问范围。
4. 新增、移除、升级以及实验性状态变化。
