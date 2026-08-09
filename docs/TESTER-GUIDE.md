# Pi Companion 源码内测指南

这份指南面向直接克隆仓库参与内测的朋友。当前没有签名安装包或自动更新器；测试者运行的是本机编译的开发版本。

## 开始之前

- 仅支持 Windows 11 x64，建议先在不含重要资料的测试目录中使用。
- Pi Companion 会在所选权限范围内读取或修改文件、执行命令。第一次测试优先使用“只读”或“标准访问”，不要直接对重要工作区开启“完全访问”。
- 不要把 API Key、OAuth Token、Prompt、个人文件内容、Pi `auth.json` 或诊断 ZIP 提交到仓库或公开 Issue。
- 测试克隆最好保持在 `main`，自己的实验代码放在单独分支或另一份 clone 中。

## 首次安装

在 PowerShell 中运行：

```powershell
git clone https://github.com/Theochenxiyuan/pi-companion.git
Set-Location pi-companion
.\scripts\bootstrap.ps1 -InstallMissing
```

安装脚本只在显式提供 `-InstallMissing` 后才会改动系统。它会按需安装以下受支持基线：

- Git for Windows；
- Node.js 24 LTS 与 npm 11；
- `.NET SDK 10.0.302` 所在的兼容 feature band；
- Visual Studio 2022 Build Tools 的 C++ 桌面工作负载及推荐组件；
- Microsoft Edge WebView2 Runtime；
- 仓库当前锁定版本的 `@earendil-works/pi-coding-agent`。

WinGet 安装可能弹出安装界面或 UAC。脚本不会自动开启 Windows 开发人员模式，也不会配置 Provider 账号。如果提示 PATH 尚未刷新，请新开一个 PowerShell，回到仓库后重新运行：

```powershell
.\scripts\bootstrap.ps1 -InstallMissing
.\scripts\doctor.ps1
```

所有必需项显示 `OK` 后启动：

```powershell
.\scripts\run.ps1
```

第一次运行会完成 Debug 构建和自动化测试，因此会比之后启动更久。构建通过后，后续可使用 `run.ps1 -NoBuild`。

## 配置 Provider 与第一次测试

在应用设置的 Provider 页面登录或保存 API Key。凭据由 Pi 存放在用户目录的 `.pi\agent\auth.json` 中，Pi Companion 不在仓库或自己的数据库中复制一份。

第一次任务建议：

1. 新建一个只包含示例文件的临时 Git 仓库；
2. 使用“只读”确认浏览、对话和模型配置正常；
3. 再使用“标准访问”做一个容易检查和撤销的小改动；
4. 确认授权问题、工具调用、文件变化和 Git 状态都能正常显示；
5. 最后再测试并发任务、定时任务或 Web Search 等扩展功能。

“完全访问”会取消工作区边界和逐次授权，只应按明确任务临时开启。

## 拉取后续更新

关闭 Pi Companion，在仓库中运行：

```powershell
.\scripts\update-dev.ps1
.\scripts\run.ps1 -NoBuild
```

更新脚本会依次：

1. 检查工作区必须为干净状态；
2. 执行 `git pull --ff-only`；
3. 按新代码重新检查依赖；
4. 完成 Debug 构建和全部自动化测试。

它不会 reset、stash 或覆盖本地改动。如果诊断提示 Pi 或其他基线已变化，先运行 `bootstrap.ps1 -InstallMissing` 再重试。

## 可选：Explorer 右键菜单

核心应用不需要 Windows 开发人员模式。只有注册当前未签名的 Explorer 开发包时，才需要测试者在 Windows 设置中手动启用开发人员模式或旁加载，然后运行：

```powershell
.\scripts\install-explorer-integration.ps1 -Configuration Debug -NoBuild
```

拉取更新后，可以让更新脚本重新注册：

```powershell
.\scripts\update-dev.ps1 -InstallExplorerIntegration
```

移除右键菜单：

```powershell
.\scripts\uninstall-explorer-integration.ps1
```

## 本地数据与隐私

- Pi Companion 的任务数据库、缓存和日志位于 `%LOCALAPPDATA%\PiCompanion`。
- Pi 的 Provider 配置、凭据和 Session 位于用户目录的 `.pi\agent`。
- 源码目录中的 `node_modules`、`bin`、`obj`、`artifacts` 和生成的前端资源都被 Git 忽略。
- 应用的“设置 → 存储与诊断”可以打开数据/日志目录并导出诊断 ZIP。

诊断 ZIP 不包含 Provider Key，但可能包含本机路径、版本和运行错误。不要直接上传到公开 Issue；先检查内容，再通过双方认可的私下渠道发送。

## 报告问题

使用 GitHub 的 [内测问题模板](https://github.com/Theochenxiyuan/pi-companion/issues/new/choose)，至少提供：

```powershell
git rev-parse --short HEAD
.\scripts\doctor.ps1
```

同时写清复现步骤、预期结果、实际结果，以及问题是否每次出现。截图和日志应遮掉用户名、个人路径、Prompt、文件内容、仓库远端地址和任何凭据。

安全漏洞不要发公开 Issue；请遵循 [安全政策](../SECURITY.md)。

## 常见问题

### PowerShell 阻止脚本运行

不要修改整台机器的执行策略。可以只为这一次新进程绕过：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\doctor.ps1
```

确认仓库来源后，对 `bootstrap.ps1`、`run.ps1` 或 `update-dev.ps1` 也可以使用相同方式。

### 缺少 C++ Build Tools 或 Windows SDK

打开 Visual Studio Installer，确认已安装“使用 C++ 的桌面开发”工作负载和推荐组件。Explorer 开发包还需要 Windows SDK 中的 `makeappx.exe`。

### Pi Runtime 版本不匹配

优先运行：

```powershell
.\scripts\bootstrap.ps1 -InstallMissing
```

也可以从 `doctor.ps1` 的修复提示复制当前仓库要求的精确 npm 安装命令。

### 更新脚本提示工作区不干净

先运行 `git status`。保留的改动应提交到自己的分支或 stash；不需要的生成文件应确认路径后处理。更新脚本不会替测试者决定如何处置本地工作。

### npm 显示 deprecated、allow-scripts 或前端 chunk 大小提示

先看命令最终是否以成功状态结束，再运行 `scripts\audit-dependencies.ps1`。当前 lockfile 的已知构建提示不等同于仍存在安全漏洞；不要为了消除提示直接运行 `npm audit fix --force` 或随意批准新的安装脚本。如果审计失败，请把脱敏后的报告发给维护者。

### 应用启动失败

先检查 `%LOCALAPPDATA%\PiCompanion\logs\startup-error.log`，再运行 `doctor.ps1`。公开反馈只贴脱敏后的相关错误；完整诊断包请私下发送。
