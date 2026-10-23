# Pi Companion

[![CI](https://github.com/Theochenxiyuan/pi-companion/actions/workflows/ci.yml/badge.svg)](https://github.com/Theochenxiyuan/pi-companion/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Theochenxiyuan/pi-companion?include_prereleases&label=Release)](https://github.com/Theochenxiyuan/pi-companion/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Pi Companion** 是一款专为 Windows 11 打造的本地 AI Agent 桌面应用。它将 Windows 资源管理器右键菜单、多工作区任务对话、文件与 Git 变更检视、后台任务监视器整合在同一套原生交互体验中。

---

## 核心特性

- **资源管理器即入口**：在 Windows 11 资源管理器任意文件夹或文件上右键即可呼出快捷提问框，自动携带所选上下文发起任务。
- **多工作区与并发执行**：支持在多个本地项目与通用对话之间自由切换，不同目录的任务可在后台并行推进。
- **透明可控的执行过程**：实时展示推理思考、工具调用、终端输出与文件变更，内置桌面悬浮监视器（Monitor）与系统通知提醒。
- **工作区文件与 Git 集成**：在对话侧栏直接浏览目录树、对比差异、暂存文件并创建本地 Git 提交。
- **可复用的任务模板与定时任务**：支持保存常用 Prompt 模板，或创建单次/周期定时计划自动执行例行任务。
- **生态扩展能力**：
  - **模型与 Provider 管理**：支持官方 Provider OAuth 登录、API Key 配置及 OpenAI 兼容自定义 Provider，支持主流模型原生联网搜索。
  - **MCP 服务管理**：可视化配置个人级或项目级 MCP Server（本地命令、远程服务、OAuth 认证与按需/批量工具暴露）。
  - **技能（Skills）管理**：发现并管理全局与工作区技能包，支持 `/` 与 `$` 快捷唤起。
- **清晰的权限与恢复边界**：提供**只读**、**标准访问**与单任务显式开启的**完全访问**三档权限；内置修改前文件备份与一键恢复能力。

---

## 下载与安装

系统要求：**Windows 11 x64（Build 22000 及以上）**，并已安装 Microsoft Edge WebView2 Runtime（Windows 11 默认自带）。Release 安装包已内置 .NET 运行时与私有的 Node.js + Pi 1.4.0 Runtime，无需额外部署开发环境。

前往 [**GitHub Releases**](https://github.com/Theochenxiyuan/pi-companion/releases) 下载最新版本：

### 方式一：MSIX 安装包（推荐，包含右键菜单与开始菜单入口）

下载以下三个文件到同一目录：
- `PiCompanion-v0.5.0-preview-win-x64.msix`
- `PiCompanion-v0.5.0-preview-win-x64.cer`
- `Install-PiCompanion.ps1`

在下载目录打开 PowerShell 运行安装脚本（首次安装会请求管理员权限以信任预览版自签名证书）：

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-PiCompanion.ps1 -Launch
```

安装完成后即可从 Windows 开始菜单启动 **Pi Companion**，或在文件资源管理器右键菜单中使用 **Ask Pi Companion**。

### 方式二：便携版 ZIP（免安装）

下载并解压 `PiCompanion-v0.5.0-preview-win-x64-portable.zip`，直接运行目录中的 `PiCompanion.Desktop.exe` 即可使用完整桌面与 Agent 功能（不包含资源管理器右键菜单扩展）。

---

## 安全与隐私边界

Pi Companion 在本机直接驱动 Agent 执行操作，设计上遵循最小权限与凭据隔离原则：

- **三档权限控制**：
  - **只读模式**：仅允许读取工作区内容，禁止修改文件或执行命令。
  - **标准模式（默认）**：工作区内读写受控，访问工作区外部路径、执行命令或敏感操作时逐次请求用户确认。
  - **完全访问**：仅限按单次任务显式开启并二次确认，跳过常规路径与命令确认（不会提权为系统管理员）。
- **凭据隔离**：模型 Provider 的 API Key 与 OAuth 令牌由底层 Pi 认证存储管理，应用日志与诊断导出包均经过脱敏处理，不包含任何密钥。
- **安全上报**：如发现安全隐患，请查阅 [安全政策 (SECURITY.md)](SECURITY.md)。

---

## 从源码构建与开发

如果你希望从源码体验最新开发分支或参与贡献，可以使用仓库内置的一键脚本。

### 1. 环境准备与快速启动

开发环境需要 Windows 11 x64、.NET 10 SDK、Node.js 24、Visual Studio 2022 Build Tools（含 x64 C++ 工具链）以及全局安装的 `@earendil-works/pi-coding-agent@1.4.0`。

```powershell
git clone https://github.com/Theochenxiyuan/pi-companion.git
Set-Location pi-companion

# 检查并自动补齐缺失的开发依赖
.\scripts\bootstrap.ps1 -InstallMissing
.\scripts\doctor.ps1

# 构建并启动开发版
.\scripts\run.ps1
```

日常拉取更新并运行：

```powershell
.\scripts\update-dev.ps1
.\scripts\run.ps1 -NoBuild
```

### 2. 完整构建、测试与打包

```powershell
# 运行全量 Release 构建与自动化测试（WebSearch 扩展 + Vue 前端 + .NET + C++ Explorer 冒烟测试）
.\scripts\build.ps1 -Configuration Release

# 依赖漏洞审计
.\scripts\audit-dependencies.ps1

# 生成自签名 MSIX 安装包与便携版 ZIP（输出至 artifacts/release/）
.\scripts\package-release.ps1 -Version 0.5.0-preview -Configuration Release
```

在开发环境中单独调试资源管理器右键菜单（需开启 Windows 开发人员模式）：

```powershell
.\scripts\install-explorer-integration.ps1 -Configuration Release -NoBuild
# 卸载开发版右键菜单注册
.\scripts\uninstall-explorer-integration.ps1
```

---

## 仓库结构与文档

```text
src/
  PiCompanion.Core/                 领域模型、事件与任务投影
  PiCompanion.Application/          Pi RPC 通信、调度、SQLite 持久化与工作区服务
  PiCompanion.Extension/            工具权限控制、文件备份、MCP 与模型配置适配器
  PiCompanion.WebSearchExtension/   内置官方 Provider 原生联网搜索扩展
  PiCompanion.Chat/                 Vue 3 + TypeScript Agent Chat 界面
  PiCompanion.Desktop/              WPF 桌面外壳、悬浮监视器、快速提问框与 WebView2 宿主
  PiCompanion.ExplorerCommand/      Windows 11 C++ IExplorerCommand 右键菜单扩展
  PiCompanion.Packaging/            MSIX 包清单模板
tests/
  PiCompanion.Core.Tests/           .NET 核心与应用层测试
  PiCompanion.ExplorerCommand.Smoke/ Explorer COM 扩展冒烟测试
```

更多文档：
- [产品计划与技术规格](docs/pi-companion-product-technical-plan.md)
- [源码内测与排障指南](docs/TESTER-GUIDE.md)
- [内置 Pi 扩展说明](docs/included-pi-extensions.md)
- [文档索引](docs/README.md)
- [贡献指南](CONTRIBUTING.md) · [第三方组件声明](THIRD-PARTY-NOTICES.md)

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
