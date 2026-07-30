# Pi Companion

Pi Companion 是一个面向 Windows 11 的本地 AI Agent 桌面应用。它把任务对话、工作区文件、Git 状态和运行监视器放在同一个界面中，并提供 Windows 资源管理器右键菜单入口。

> [!WARNING]
> 项目仍处于早期开发阶段，目前没有经过代码签名的公开安装包。请仅在理解本地 Agent 权限边界的环境中构建和运行。

## 主要能力

- 从应用或 Windows 11 资源管理器为指定目录创建任务。
- 在多个工作区之间切换，并发运行不同目录中的任务。
- 在 Agent Chat 中查看思考、工具调用、授权请求、问题和文件变更。
- 通过桌面任务监视器跟踪运行状态，在任务需要操作时收到 Windows 通知。
- 浏览工作区文件和本地 Git 状态，暂存变更并创建本地提交。
- 管理模型 Provider、默认模型、推理等级、上下文压缩和重试策略。
- 支持只读、标准访问和按任务开启的完全访问权限。
- 支持简体中文和英语界面。

更完整的产品边界与技术设计见 [产品计划与技术规格](docs/pi-companion-product-technical-plan.md)。当前说明与历史阶段记录的区别见 [文档索引](docs/README.md)。

## 项目状态

当前代码覆盖桌面外壳、Agent Chat、任务持久化、多任务调度、工作区与 Git 浏览、Provider 配置、技能管理、Explorer Command 和应用私有 Web Search Extension。

尚未提供：

- 面向最终用户的正式安装包与自动更新；
- 代码签名和稳定版本兼容承诺；
- 对 Windows 11 x64 以外平台的支持。

## 环境要求

- Windows 11 x64，Build 22000 或更高；
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)；
- Node.js 24 和 npm 11；
- Visual Studio 2022 Build Tools，并安装 x64 C++ 工具链；
- Microsoft Edge WebView2 Runtime；
- 开发运行时需要 Pi RPC 0.83.0 Runtime，以及所选模型服务的有效账号或 API Key。

依赖版本由 `global.json`、npm lockfile 和 NuGet lockfile 固定。

## 从源码构建

在 PowerShell 中运行：

```powershell
git clone https://github.com/Theochenxiyuan/pi-companion.git
Set-Location pi-companion
.\scripts\build.ps1 -Configuration Release
```

完整构建会：

1. 恢复并测试 Web Search Extension；
2. 类型检查、构建并测试 Vue Agent Chat；
3. 运行 Pi Companion Extension 测试；
4. 恢复、构建并测试 .NET 解决方案；
5. 构建并冒烟测试 Explorer Command。

## 开发运行

开发构建会优先查找当前机器上全局安装的 Pi Runtime。也可以显式指定 Pi 和 Node 路径：

```powershell
.\scripts\build.ps1 -Configuration Debug
.\scripts\run.ps1 -NoBuild `
  -PiRuntimePath 'C:\path\to\pi-coding-agent\dist\cli.js' `
  -NodeRuntimePath 'C:\Program Files\nodejs\node.exe'
```

也可为当前进程设置：

```powershell
$env:PI_COMPANION_PI_PATH = 'C:\path\to\pi-coding-agent\dist\cli.js'
$env:PI_COMPANION_NODE_PATH = 'C:\Program Files\nodejs\node.exe'
```

这些路径和任何模型凭据都不应提交到仓库。

## Explorer 右键菜单

构建稀疏开发包：

```powershell
.\scripts\build-explorer-integration.ps1 -Configuration Release
```

开发包未签名。注册前需要在 Windows 开发者设置中允许开发人员模式或旁加载，然后运行：

```powershell
.\scripts\install-explorer-integration.ps1 -Configuration Release -NoBuild
```

移除开发注册：

```powershell
.\scripts\uninstall-explorer-integration.ps1
```

安装脚本不会替用户修改系统安全策略。

## 工程结构

```text
src/
  PiCompanion.Core/              领域模型、事件与任务投影
  PiCompanion.Application/       Pi RPC、调度、持久化与工作区服务
  PiCompanion.Extension/         工具权限、路径策略与交互工具
  PiCompanion.WebSearchExtension/ Provider 原生搜索扩展
  PiCompanion.Chat/              Vue 3 Agent Chat
  PiCompanion.Desktop/           WPF 外壳、Monitor、托盘与 WebView Bridge
  PiCompanion.ExplorerCommand/   Windows 11 Explorer Command
  PiCompanion.Packaging/         开发包清单
tests/
  PiCompanion.Core.Tests/
  PiCompanion.Extension.Tests/
  PiCompanion.ExplorerCommand.Smoke/
```

## 安全与隐私

Pi Companion 会在本机运行 Agent，并可能按所选权限读取或修改文件、执行命令。完全访问不会获得管理员权限，但会取消工作区边界和逐次授权，因此只能按任务显式开启。

- Provider 凭据由 Pi 自己的认证存储管理，Pi Companion 不回读或复制密钥。
- 本地任务、日志、缓存和会话数据不会被纳入版本控制。
- 导出的诊断包不包含 Provider 密钥。
- 发现安全问题时请遵循 [安全政策](SECURITY.md)，不要在公开 Issue 中披露漏洞细节或凭据。

## 参与贡献

提交 Issue 或 Pull Request 前请阅读 [贡献指南](CONTRIBUTING.md)。所有改动都应通过：

```powershell
.\scripts\build.ps1 -Configuration Release
```

## 第三方组件

应用私有 Web Search Extension 包含 MIT 许可的第三方组件。完整清单和保留声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 及 [`src/PiCompanion.WebSearchExtension/legal/`](src/PiCompanion.WebSearchExtension/legal/)。

## 许可证

Pi Companion 以 [MIT License](LICENSE) 开源。
