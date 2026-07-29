# 阶段 6：Diff、测试证据与恢复

## 交付结论

阶段 6 已完成。Agent Chat 现在会在每个 Run 下展示文件变化、Diff、命令、测试状态和证据警告；可拦截的 `edit`/`write` 修改可以按单文件恢复，恢复前会重新计算当前文件 Hash，不会覆盖 Agent 结束后用户或其他程序产生的新内容。

本阶段复用了阶段 4 的 Extension 修改前备份和阶段 5 的 Task/Run、SQLite、Snapshot/Sequence 主干，没有建立第二套运行状态。Bridge 协议升级为 v12，重载 WebView 或打开历史任务时会从 SQLite 恢复同一份证据和 Run 附件快照。

## Pi 原生 Diff 支持与边界

项目当前锁定 Pi `0.82.0`，证据适配以该版本真实 RPC 结果为准：

- `edit` 的 `tool_execution_end.result.details.patch` 提供 unified patch；应用原样保存它，首个 edit 不再重复推导 Diff。
- `details.diff` 是 Pi 面向工具结果展示的 Diff 信息；阶段 6 优先使用可持久化、可直接展示的 `details.patch`。
- `write` 的结果没有原生 patch。应用使用 Extension 在执行前保存的内容寻址备份，与执行后的文件内容生成 Diff。
- 同一文件被多次 edit/write 时，后续证据使用“Run 首次修改前内容 → 当前内容”重新比较，避免只展示最后一次局部 edit。
- Pi 的 bash 结果没有独立的结构化退出码字段。成功结果按 `isError=false` 记录退出码 `0`；失败结果只解析锁定版本输出中的 `Command exited with code N`。错误存在但无法得到退出码、取消或超时时，测试状态为“未知”，不会猜测成功或失败。
- Adapter 保留工具开始时的参数和结束时的完整 result，再发布领域无关的 `AgentToolExecution`；原有 Transcript 仍只保存适合 UI 的摘要。

## 文件变化与 Diff

### Git 工作区

- Run 开始时保存仓库根、HEAD 和 `git status --porcelain=v1 -z --untracked-files=all`，结束时再次采集。
- tracked、staged、untracked、删除和重命名路径进入文件变化列表；Diff 使用 `git diff HEAD --no-ext-diff --no-textconv --binary`，不会调用用户的 external diff。
- Git 子进程禁用 fsmonitor 和交互提示，设置 8 秒上限，且不执行 `reset`、`checkout`、`clean` 或其他写操作。
- Run 开始前已有的 dirty 文件标为“运行前已有”；HEAD 在 Run 中变化会显示基线警告。

### 非 Git 与 Shell

- Extension 对每个 edit/write 在执行前追加 Run manifest。已有文件保存 SHA-256 内容寻址对象；新文件也保存 `existed=false` 的确定性基线。
- 内建工具产生的变化标为“已确认”。Shell 可能绕过内建工具，因此 FileSystemWatcher 只收集候选路径，相关变化标为“已观察”，并显示覆盖范围警告。
- Watcher 忽略 `.git` 内部事件，最多保留 512 个候选；监视不可用、缓冲区或候选溢出都会产生明确警告。
- 非 Git 文本 Diff 使用严格 UTF-8；任一侧超过 1 MiB、超过 1200 行或 Diff 超过 256 KiB 时标记截断。二进制文件不生成文本 Diff，但仍保留字节级 Hash 和可用备份。
- Pi 原生 patch 同样限制为 256 KiB 后持久化，避免单个证据无限增长。

文件证据区分四种可信度：

- `Confirmed`：来自 edit/write、Pi 原生 patch和修改前备份。
- `Observed`：Git/Watcher 证明路径在 Run 期间或 Run 后处于变化状态，但没有确定性修改前字节。
- `PreExisting`：Run 开始前 Git 工作区已经存在变化。
- `Unknown`：只能确认路径事件，无法可靠判断变化类型。

## 命令与测试证据

每个 bash 工具结束时保存 Command、WorkingDirectory、StartedAt、Duration、ExitCode、Cancelled、TimedOut 和 OutputSummary。当前识别：

- `dotnet test`
- `npm test`、`npm run test`、`pnpm test`、`yarn test`、`bun test`
- `node --test`、Vitest、Jest、Mocha
- `pytest`
- `cargo test`
- `go test`
- Maven 和 Gradle test

Run 测试状态聚合规则为：任一失败则“失败”；没有失败但存在未知则“未知”；全部已识别测试成功则“通过”；没有识别到测试命令则“未运行”。状态只来自实际工具结果，不从最终回答文本推断。

## 单文件恢复

恢复只对有确定性基线的 edit/write 文件开放，并且 Run 活跃时禁用：

1. 重新确认目标仍位于原工作目录内。
2. 拒绝目标路径中出现的符号链接、目录联接点或其他重解析点。
3. 重新计算当前文件 SHA-256；必须与 Agent 修改后的 Hash 完全一致。
4. 已有文件先校验备份对象 Hash，流式写入同目录临时文件，再次校验当前文件后原子替换。
5. Agent 新建的文件不会直接永久删除，而是移动到 `%LOCALAPPDATA%\PiCompanion\recovery-trash`。
6. 成功、冲突和不可恢复结果都会更新文件证据并追加 `recovery_actions` 审计记录。

恢复不会对 Shell/Git 的观察性变化提供确定性承诺，也不会自动运行 Git 回滚命令。

## UI 与持久化

- 每个 Run 默认突出“文件变更”；只有检测到测试命令时才显示测试状态，没有测试记录时不显示误导性的失败或警告状态。
- 文件行使用“Agent 修改”“检测到文件变化”等用户文案；`BackupComparison`、`PiEditPatch` 等内部来源不在主界面展示，命令记录默认折叠在“运行详情”中。
- 文本 Diff 使用逐行视图、旧/新行号、绿色新增、红色删除及 `+N/-N` 统计；二进制和截断边界有明确提示。
- Agent Chat 按侧栏、输入区、单轮会话、证据面板、任务管理和 Diff 对话框拆分组件；拖放、侧栏尺寸和任务管理状态由独立 composable 管理，根组件只保留桥接连接与页面级协调。
- 辅助文字使用统一的 12/13px 字号层级并提高弱文字对比度；Diff、证据来源、命令状态、附件和任务路径不再使用 10/11px 的正文说明。
- 恢复前显示 Hash 冲突保护说明；结果以成功或冲突通知返回。
- SQLite 按 Run 保存完整的有效上下文附件快照，包括显式的空快照；用户消息只显示相对上一 Run 新加入的附件文件名，不重复展示仍在上下文中的继承附件。
- Agent Chat 支持从资源管理器拖入一个或多个文件。前端只传递 WebView2 原生 `File` 对象，宿主从 `CoreWebView2File` 读取路径并复用最多 64 项、绝对路径、存在性和去重校验；运行中的任务拒绝变更附件。
- SQLite 使用 `run_evidence`、`file_changes`、`command_executions`、`test_results`、`warnings` 和 `recovery_actions`。永久删除任务或清空回收站时同步清理相关证据。
- Bridge v12 包含 `EvidenceUpdated`、`GetFileDiff`/`FileDiffLoaded`、`RestoreFile`/`RecoveryCompleted`、原生拖放附件，以及所有历史 Run 的证据与消息附件增量。

## 自动化验收

阶段 6 新增或扩展的自动化覆盖：

- Pi RPC 保留锁定版本 edit 的 args、`details.diff` 和 `details.patch`。
- 已有文件 edit 使用 Pi 原生 patch，且可恢复到修改前内容。
- Agent 新建文件恢复时移动到恢复暂存区。
- Agent 结束后文件被再次修改时报告冲突并保留当前内容。
- 超过 1 MiB 的文件只保存受限 Diff 元数据，仍可流式恢复。
- 临时 Git 仓库保存 HEAD、status、真实 Diff 和 Shell 覆盖警告。
- 测试失败状态来自 Pi bash 的真实退出码合约。
- Extension 对已有文件和新文件都生成正确的备份 manifest。
- Agent Chat 展示证据、请求 Diff，并通过确认对话框发起恢复。
- Agent Chat 展示每轮用户附件，并通过 WebView2 原生附加对象提交拖放文件。

最新 Release 验证（2026-07-20）：

- Vitest：16/16 通过。
- Extension：6/6 通过。
- xUnit：69/69 通过。
- Explorer Command COM 冒烟通过。
- Vue 生产构建与 .NET Release 构建通过，.NET 为 0 警告、0 错误。

## 人工验收建议

1. 在 Git 仓库让 Agent 修改 tracked 文件并新建文件，确认两者出现在同一 Run 的证据区，Diff 与 `git diff HEAD` 一致。
2. 在非 Git 目录分别执行 edit 和 write，确认 edit 首次使用 `PiEditPatch`，write 使用 `BackupComparison`。
3. 运行成功和失败的测试命令，确认状态、退出码、耗时和输出摘要来自实际执行。
4. 修改一个可恢复文件后直接恢复，确认内容回到 Run 前；对另一个文件先手工再改一次，确认恢复报告冲突且不覆盖手工内容。
5. 让 Shell 修改忽略文件或删除文件，确认界面显示观察性证据和 Shell 覆盖警告，不提供虚假的确定性恢复。
6. 重载 WebView、重启应用并从历史打开任务，确认文件、命令、测试状态和恢复结果仍存在。

## 已知边界

- FileSystemWatcher 是候选信号，不是审计日志；Shell 可修改工作目录外路径、忽略文件，或在短时间内修改后还原，应用明确提示但不承诺完整枚举。
- 只有 Extension 在 edit/write 前成功捕获的原始内容可确定性恢复；Git/Watcher 观察到的变化默认不可恢复。
- Pi `write` 没有原生 Diff，必须依赖备份比较；Pi bash 没有结构化退出码，无法解析的失败保持“未知”。
- 大文件、超长文本和二进制内容保留 Hash、大小和恢复能力，但不强行生成完整文本 Diff。
- 恢复是单文件操作，不提供整个 Run 一键回滚，也不会覆盖 Hash 已变化的当前文件。
- 单活动 Run 调度边界不变；阶段 7 先交付设置、诊断和开发版安装验证，正式安装与升级按当前计划后移。
