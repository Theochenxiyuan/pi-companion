# Contributing to Pi Companion

感谢你愿意参与 Pi Companion。

## 开始之前

Pi Companion 当前只支持 Windows 11 x64。完整构建需要：

- .NET 10 SDK；
- Node.js 24 和 npm 11；
- Visual Studio 2022 Build Tools 的 x64 C++ 工具链；
- Microsoft Edge WebView2 Runtime。

请先阅读 [README](README.md) 中的项目状态和权限说明。较大的功能或架构改动建议先创建 Issue，确认范围后再投入实现。

## 本地开发

Fork 并克隆仓库后，从 `main` 创建短生命周期分支：

```powershell
git switch -c feature/short-description
```

运行完整验证：

```powershell
.\scripts\build.ps1 -Configuration Release
```

如果只修改某一层，可以在开发过程中先运行更小范围的检查：

```powershell
Set-Location src\PiCompanion.Chat
npm ci
npm run build
npm test

Set-Location ..\..
dotnet test PiCompanion.sln -c Release
```

提交 Pull Request 前仍应运行完整构建，因为它还会验证 Web Search Extension、Explorer Command 和原生冒烟测试。

## 提交要求

- 保持改动聚焦，不要混入无关格式化或生成产物。
- 为行为变化补充或更新测试。
- 用户可见文字需要同时提供简体中文和英语翻译。
- 不要提交 API Key、OAuth Token、Pi `auth.json`、本地数据库、日志、运行时目录或真实用户路径。
- 不要降低权限检查、路径边界、诊断包脱敏或 Markdown 清理规则，除非改动本身经过明确的安全评审。
- 新增随应用分发的第三方代码或资产时，更新 `THIRD-PARTY-NOTICES.md` 并保留许可证文本。

## Pull Request

Pull Request 描述应说明：

1. 解决了什么问题；
2. 用户可见行为发生了什么变化；
3. 如何验证；
4. 是否涉及权限、凭据、文件写入、命令执行、持久化或打包。

维护者可能要求拆分范围、补充测试或更新文档后再合并。
