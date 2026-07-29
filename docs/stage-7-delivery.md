# 阶段 7：设置中心、运行配置与开发版安装验证

## 交付结论

阶段 7 的设置与开发版安装子阶段已完成。Agent Chat 现在提供覆盖主界面的 modal 设置中心，采用左侧分类导航，包含常规、通知、Monitor、任务、数据、回收站、Agent 和 Provider 八页。Companion 自身设置保存在现有 SQLite Event Store 的 `settings` 表中；Pi 的 Agent 默认值、Provider、模型目录和凭据继续由 Pi 自己的配置存储管理。

截至 2026-07-23，本交付还包含自定义 Provider 创建与编辑、模型启用范围、AI 任务元数据配置、任务历史交互收口、工作目录外附件安全暂存、共享排版与颜色 token、深色/浅色/跟随系统主题、WebView 界面缩放、Monitor 图标化展开/收起，以及 Explorer 菜单 Pi 图标。本文是当前实现的交付事实基线；阶段 1–6 文档保留各阶段完成时的协议和能力快照。

本阶段按当前开发计划继续使用 `PiCompanion.Development` 稀疏包做安装与 Explorer 集成验证；正式版 MSIX、签名、升级和卸载体验暂不处理。

开发构建和开发包通过 `PiCompanion.Development` 标记优先使用本机全局安装的 Pi Runtime。未来正式发布流水线必须传入 `PiCompanionFormalRelease=true`，移除开发标记并恢复只使用应用私有 Runtime 的发布边界。

## 中英文界面

- 常规设置可在简体中文与英语（美国）之间即时切换，并将语言偏好写入现有应用设置。
- Agent Chat、设置中心、任务历史、工作区、证据与 Diff、Provider 管理、Prompt Composer、Monitor 和托盘均使用同一语言偏好。
- 日期、数字和模型能力提示按当前语言格式化；Windows Explorer 命令按系统 UI 语言显示中文或英文标题。
- Pi/Agent 生成的回答、任务标题和总结保留其原始语言，不对用户内容做二次机器翻译。

## 设置生效边界

- 常规、通知、Monitor、任务与数据保留属于 Companion 自身偏好，切换或输入后会防抖自动保存并立即应用；关闭设置窗口前会刷新尚未发送的修改。
- 自动保存成功只在设置页标题区显示轻量状态，不重复弹出全局 Toast；失败时恢复宿主已保存值并显示错误。
- Agent 页的默认模型、推理等级、上下文压缩策略、重试策略与消息队列发送方式会写入 Pi 配置，因此保留“应用 Pi 设置”按钮。Provider、OAuth、API Key 与模型范围继续使用各自的明确操作。

## Pi Provider 与模型复用

Companion 只为 Pi 的能力提供 UI 外壳，不维护自己的 Provider 或模型系统：

- 启动设置页时，宿主解析与运行任务相同的 Pi Runtime，并从 Pi `0.82.0` 的 `ModelRuntime` 枚举 Provider、可用模型、上下文窗口、输入类型和 Provider 已验证的推理等级。
- Provider 的已配置状态来自 Pi `AuthStorage` 与环境凭据检测。
- 保存 API Key 时通过 Pi 的 `AuthStorage.modify` 写入 `~/.pi/agent/auth.json`；退出 Provider 使用 `AuthStorage.delete`。
- Provider 退出期间按钮显示旋转进度和“退出中”，并禁止重复提交；成功或失败结果返回后恢复。
- API Key 只在 WebView → WPF → Node 辅助进程的本次请求中传递，不写日志、不返回 UI，Companion 不保存副本。
- OAuth/订阅登录优先复用 Pi 的浏览器授权事件，在 Companion 内显示等待和取消状态；Pi `0.82.0` 新增的 OpenRouter 与 Kimi Code 登录可直接沿用这条链路。仍需要额外终端输入的 Provider 才回退到 `/login <provider>`。
- 新任务的模型选择项完全来自 Pi 返回的可用模型；设置页只展示具体模型，不再提供“跟随 Pi 默认模型”的中间选项。
- Agent 页通过 Pi `SettingsManager` 读取和写入全局默认模型、默认推理等级、auto compaction 与 auto retry，并在写入后等待 `flush()` 完成。
- 可在 Provider 搜索栏右侧直接创建兼容 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 或 Google Generative AI 的自定义 Provider；配置写入 Pi `models.json`，凭据仍写入 `auth.json`。
- 已创建的自定义 Provider 可在右侧详情区重新编辑；写入时使用配置修订号避免覆盖外部并发修改，并保留 JSONC 注释、尾逗号和无关配置。
- Provider 的模型列表可以直接调整启用范围；Agent、任务元数据和 Chat 模型选择只使用启用后的具体模型。

这条路径与 Agent Run 共用 `PiRuntimeResolver`。带 `PiCompanion.Development` 标记的开发构建会优先自动查找本机全局安装的 Pi Runtime，也可用 `PI_COMPANION_PI_PATH`/`PI_COMPANION_NODE_PATH` 显式覆盖；正式产物继续只接受应用私有 Runtime，不会回退到用户全局 `pi`。

## 设置页面

左侧导航按配置归属分成两组：`PI COMPANION` 包含常规、通知、Monitor、任务、数据和回收站，`PI` 包含 Agent 与 Provider。搜索仍可跨组过滤，空组不会占据版面。

表单标题和说明文字不再充当输入控件的点击区域；Toggle 只有开关本体可以切换，避免点击整行时误操作。

### 常规

- Windows 登录后启动：开发版使用当前用户 `HKCU\\...\\Run` 启动项，指向当前开发版可执行文件并带 `--background`。
- 关闭主窗口后是否继续驻留托盘。
- 语言、深色/浅色/跟随系统主题偏好和日志级别；跟随系统时，WebView 与 WPF 窗口会响应 Windows 深浅色偏好变化。
- Agent Chat 界面缩放支持 50%–200%，以 10% 为步长增减，中间百分比按钮恢复 100%。保存后直接设置 WebView2 `ZoomFactor`；`Ctrl + 滚轮`仍可使用，并会把实际比例反向持久化。

正式版 Startup Task 不在当前范围；UI 中明确标注这是开发版启动方式。

### Monitor

- 左上、右上、左下、右下四个初始位置。
- 启动时显示、始终置顶、自动收起时间和动画偏好。
- Capsule 与 Expanded 使用图标按钮显式展开和收起；鼠标进入只暂停收起计时，不会自动展开，鼠标离开已展开窗口后启动自动收起计时。
- 保存后对现有 Monitor 立即应用位置、计时器和 Topmost 状态。

### Agent

- Pi 的实际全局默认模型和模型支持的推理等级。
- 推理等级沿用 Pi/OpenChamber 的英文显示：`None`、`Minimal`、`Low`、`Medium`、`High`、`Xhigh`、`Max`；内部仍传递 Pi 原始值。
- Pi auto compaction 与 auto retry，以及压缩预留/最近 Token、最大重试次数、基础与最大等待时间。
- Steer 与 Follow-up 队列可分别选择逐条发送或一次全部发送。
- 只读、标准访问、完全访问三种按授权范围递增的 Extension 权限模式；只读仅允许工作区读取和信息搜索，标准访问会直接允许普通工作区文件修改但仍询问 Shell、敏感操作和工作区外访问，完全访问则允许当前 Windows 用户权限范围内的工作区外读写和命令执行且不再逐次询问。默认权限只能设为只读或标准访问；完全访问只能在发送新任务前单独选择并经过事前确认。完全访问不会提升为管理员权限，也不会解除 General Chat 的隔离空间限制。
- 当前 Pi 版本、Runtime 路径和连接状态。
- 切换模型时，如果原推理等级不在新模型能力范围内，自动选择该模型支持的最近可用等级，避免提交无效组合。

默认模型、推理等级、auto compaction 和 auto retry 以 Pi `~/.pi/agent/settings.json` 为唯一事实来源；Agent Chat 与 Explorer Prompt Composer 都显示 Pi 解析后的具体模型。权限模式仍是 Companion 自己的 Extension 设置。

### Provider

- 已配置 Provider 优先排序，展示 Pi 返回的 OAuth、API Key 或环境凭据状态。
- Provider 没有真实图标时直接显示名称，不用首字母方块占位。
- Provider 区域不再套用额外卡片边框；搜索框固定，列表有独立高度与滚轮滚动区，适配 Pi 返回的完整 Provider 目录。
- 支持按 Provider 能力保存 API Key、打开 Pi 原生 OAuth 登录和退出登录。
- API Key 保存、OAuth 打开、自定义 Provider 保存和 Provider 退出都有明确加载态，进行中操作不可重复触发。
- 自定义 Provider 支持创建后编辑；名称、Provider ID、Base URL、API 类型、认证方式和模型能力都在同一右侧详情区维护。
- 模型上下文窗口和最大输出 Token 使用普通数字输入，不显示对大数值无意义的浏览器微调箭头。
- 页面明确说明凭据存储位置和“不复制、不回读”的边界。

### 任务与回收站

- 可分别启用 AI 任务标题和 AI Run 总结，并从当前启用模型中选择各自模型；最近任务显示数量限制为 1–20。
- 文件变更面板可设置默认收起或展开；任务完成、失败或停止后可保持 Monitor 展开、收起 Monitor 或唤起 Agent Chat。
- 可选择在前一轮成功完成后自动开始本地待发送区的第一项，并将等待时间设为不等待、15 秒、30 秒或 1 分钟；失败或停止不会触发。
- 最近列表把状态点放在状态文字左侧，并在同一行右侧显示最近更新时间。
- 从完整历史打开不属于“最近”范围的任务时，会在“最近”区上方显示独立的临时入口，不计入配置的最近任务数量；切换回普通最近任务后消失，不修改持久化排序。
- 打开完整任务历史默认不选择当前任务；历史与回收站的搜索、状态筛选和操作按钮使用统一紧凑排版。
- 回收站条目只保留有意义的恢复和永久删除操作，不显示装饰性垃圾桶图标。

### 数据

- 任务历史、回收站任务和诊断日志可分别选择保留 7、30、90 天或永久保留；过期关系数据在事务内完整清理，最近任务不会被历史保留策略误删。
- 打开应用数据目录和日志目录。
- 导出包含应用版本、Pi Runtime 状态和日志的 ZIP 诊断包；不包含 Provider 密钥。
- 清理 WebView 磁盘缓存和任务附件暂存缓存，不影响任务、Pi Session 和 Provider 凭据。
- 清理缓存和清空任务回收站使用带取消与明确操作按钮的 `alertdialog`，不使用“再次点击确认”。
- 保存、刷新、维护成功或失败结果使用右上角悬浮 Toast，不占用设置页内容流。

## 附件安全与只读上下文

- 工作目录内的附件继续直接使用规范化后的真实路径。
- 工作目录外的附件在创建任务时复制到任务独立、只读可信的本地暂存根目录；Pi 只获得该暂存路径，不需要扩大整个文件系统的信任范围。
- 暂存目录按任务隔离，路径策略只允许读取当前任务的暂存后代；符号链接或 Junction 逃逸仍会被拒绝。
- 清理缓存会删除可重建的附件暂存内容；任务记录和原始文件不受影响。

## 视觉系统

- Web 端集中定义 `Segoe UI Variable Text`、等宽和品牌字体族，以及 11–28px 的文字阶梯、300–700 字重和四档行高。
- Vue 全局样式、设置、任务历史、回收站、聊天与证据界面全部改用 typography token；WPF 主窗口、Monitor 和 Prompt Composer 使用对应的共享资源。
- Web 端通过 `color-tokens.css` 集中定义中性色阶、状态色和语义色；WPF 使用对应的 Color、Brush 与运行时主题资源，相近颜色已收敛到共享 token。
- 深色、浅色和跟随系统三种偏好会统一应用到 Agent Chat、主窗口标题栏、Monitor 和 Prompt Composer；系统偏好变化时无需重启应用。
- 根节点明确设置 14px 正文字号，所有 Select 触发器明确设置控件字号，避免浏览器默认 16px 在筛选框等遗漏位置放大。
- 实际预览检查覆盖设置、任务历史、回收站和界面缩放控件，没有发现目标文字横向溢出。
- 托盘、窗口和 Explorer 菜单共用 Pi 品牌图标；Explorer 扩展从与 DLL 同目录的 `PiCompanion.ico` 返回菜单图标，安装暂存与原生 COM 冒烟会验证该文件。

## Bridge 与持久化

- Bridge 当前协议为 v31，`InitializeSnapshot` 包含设置、Pi 配置快照、任务集合、Pi 解析后的 Agent 默认值和新增能力标记。
- Bridge 覆盖 `SaveSettings`、Pi 配置刷新、Provider 凭据与 OAuth、自定义 Provider 创建/编辑、模型启用范围、任务管理、目录/诊断/缓存维护等消息。
- Web 与桌面宿主之间新增跨项目协议一致性验收；任一端单独升级版本都会直接导致测试失败，避免不兼容包进入安装流程。
- `AppSettingsService` 对所有枚举值和数值范围做归一化；损坏或未知 JSON 回退到安全默认值。
- SQLite `settings` 表通过单键 `app.settings.v1` 保存 Companion 设置、最近任务数量和界面缩放等值；Pi Agent 默认值的权威副本仍是 Pi 自己的 `settings.json`。

### 本地待发送区

- 任务运行中，发送按钮显示“加入”；点击按钮或按 `Ctrl+Enter` 只加入 Companion 本地待发送区，`Enter` 仍用于换行。
- 每条本地消息可“立即调整”（Pi `steer`）、“定为后续”（Pi `follow_up`）、编辑或取消；RPC 成功后才从本地列表移除，失败时保留。
- 队列项可随时拖动或使用上移/下移按钮调整顺序。编辑使用独立弹窗，可修改任务内容并增减附件，不重复展示模型、推理等级或权限设置。
- 含附件的项目只可作为新一轮发送，不能直接用作 Pi `steer` 或 `follow_up`。
- 运行结束时未处理的消息继续保留，并可作为新一轮发送。队列及其顺序、附件按任务写入 SQLite，切换任务或重启应用后仍可恢复。
- 启用自动开始后，成功完成会进入可取消倒计时并启动当前第一项；取消只作用于本次倒计时。切换任务或退出应用会取消倒计时但保留队列。

## 自动化与视觉验收

- Vitest 覆盖 modal、分组左侧导航、精确 Toggle 点击区域、设置保存、界面缩放、深浅色解析与颜色 token、Monitor 设置契约、Provider 加载态、自定义 Provider、Pi 模型范围和 Bridge 协议一致性。
- Extension 测试覆盖 JSONC Provider 配置、路径策略、外部附件暂存信任边界、Shell 授权、修改前备份和 `ask_user`。
- xUnit 覆盖设置默认值、缩放范围、持久化、任务元数据、附件暂存、证据与 Pi RPC 行为。
- Vue 类型检查、生产构建、Extension 语法检查以及完整 .NET 构建均纳入现有构建脚本。
- 本地浏览器验收确认 1180 × 850 modal、220px 分组左侧导航、背景遮罩、无横向溢出；Provider 列表可用滚轮滚动，Toggle 行文字不会触发切换，确认框会接管焦点且 Escape 只关闭确认框，悬浮 Toast 位于页面右上角。本地待发送区的倒计时、附件限制、换序按钮和独立编辑弹窗也完成实际布局检查，控制台无错误。
- 开发安装通过 `scripts/install-explorer-integration.ps1` 覆盖注册 `PiCompanion.Development`，不触碰正式版包。

最新 Release 验证（2026-07-24，Pi 0.82.0）：

- Vitest：95/95 通过（25 个测试文件）。
- Extension：17/17 通过。
- xUnit：126/126 通过。
- Explorer Command COM 冒烟通过。
- Vue 生产构建、.NET Release 与 x64 原生构建通过。
- `PiCompanion.Development 0.4.0.0` 覆盖注册成功，包状态为 `Ok`；暂存目录包含设置适配器、最新 ChatAssets、开发 Runtime 标记和 `PiCompanion.ico`。
- 使用当前 Pi `0.82.0` 实际读取 39 个 Provider、可用模型和已配置 Provider；OpenRouter 与 Kimi Code 均正确报告 API Key 和 OAuth 能力，输出不含凭据内容。

## 已知边界

- 正式版 MSIX、签名证书、Startup Task、升级迁移与正式卸载仍未实现。
- Companion 复用 Pi 的 OAuth Provider 和事件协议，不复制认证实现；需要 GUI 尚未支持的额外输入时仍回退到 Pi 原生 `/login` 终端。
- 单文件 Pi Runtime 暂不能被设置适配器动态导入；Provider/模型目录读取要求 Node 版 `dist/cli.js`。任务执行本身仍可使用受支持的私有 `pi.exe`。
- 日志级别已持久化，但运行中日志过滤仍属于后续增强。
- 界面缩放只作用于 Agent Chat WebView；原生 Monitor 和 Prompt Composer 继续遵循 Windows DPI，不跟随该百分比。
