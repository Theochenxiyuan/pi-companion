# Security Policy

## Supported versions

Pi Companion 尚未发布稳定版本。安全修复目前只针对 `main` 分支的最新代码，不为历史提交或本地开发包提供长期支持。

## Reporting a vulnerability

如果 GitHub 仓库 **Security** 页面提供 **Report a vulnerability**，请通过该入口私下提交报告。当前没有该入口时，请先通过维护者的 GitHub 个人资料请求一个私下沟通渠道，并只提供不敏感的概要。

不要在公开 Issue、Discussion 或 Pull Request 中披露漏洞细节、利用代码、凭据或个人数据。

报告中请尽量包括：

- 受影响的提交或版本；
- 问题的影响和前置条件；
- 可重复的最小步骤；
- 建议的缓解方式；
- 是否可能涉及凭据、工作区外文件、命令执行或权限绕过。

收到报告后，维护者会先确认问题和影响范围，再协调修复与披露时间。请在修复公开前避免对外发布细节。

## Security-sensitive areas

以下改动应视为安全敏感：

- Agent 权限模式、授权流程和路径边界；
- Shell 与工具调用；
- Provider 凭据和 OAuth；
- 诊断包、日志与本地持久化；
- Markdown、链接和 WebView 内容；
- Explorer Command 激活协议；
- 应用私有 Runtime 与 Extension 打包。
