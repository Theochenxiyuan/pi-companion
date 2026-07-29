# 阶段 8 进展：直接对话、Web Search 与本地 Git 写入

## 当前结论

阶段 8 的直接对话、Provider 原生 Web Search 与本地 Git 写入子阶段均已完成 MVP。新任务会显式提供“选择工作目录”和“直接对话”两个入口；用户选择直接对话后，无需工作目录即可发送首条消息。产品仍复用现有 Task、Run、Transcript、Pi Session、恢复和标题/摘要生成链路，不另建第二套聊天后端。

## 本地 Git 写入

- Git Inspector 参考 OpenChamber 的紧凑布局，提供“提交”“更新”“提交历史”三个页签；原 PR 位置用于本地提交历史，不提供远程同步入口。
- 提交页区分已暂存和未暂存变更，支持逐文件及全部暂存/取消暂存；Commit Message 可由用户手写，也可基于当前暂存区 Diff 通过共享 AI 元数据 Worker 生成并继续编辑。暂存内容变化后，旧生成结果会失效并要求重新生成。
- 创建提交前会确认暂存区非空；工作目录位于仓库子目录时，如果暂存区夹带目录外文件则拒绝提交，避免越过当前 Workspace 边界。
- 提交历史按当前工作目录的 pathspec 读取，展示提交主题、作者、时间和短 Hash；点击提交可打开该提交在当前工作目录范围内的 Diff。
- 仓库根目录可创建、切换本地分支，并在整个仓库干净且没有未完成 Git 操作时，将另一个本地分支合并或变基到当前分支。
- 合并或变基发生冲突时保留 Git 原生中间状态，侧栏提供中止操作；变基继续执行仍交给外部 Git 工具，避免在首版中隐藏复杂冲突处理。
- 所有 Git 参数通过 `ProcessStartInfo.ArgumentList` 传递，关闭终端提示和外部 Diff；读取命令禁用可选锁，写入命令恢复 Git 锁，并设置超时。
- 本阶段明确不执行 `push`、`pull`、`fetch`，不创建或修改 remote，也不处理 GitHub/GitLab、SSH Key 或凭据。

## Web Search

- 随应用提供 `pi-web-search` 1.3.1 的私有单文件 bundle，不执行 `pi install`，也不读取用户全局 Extension。
- OpenAI Responses、OpenAI Codex Responses、Google Generative AI 和 Anthropic Messages 的官方内置 Provider 均声明网络搜索能力；Provider 详情中未连接时显示“支持网络搜索”，连接后显示“自带网络搜索”，列表行不重复展示该标签。
- 自定义 Provider 即使复用相同 API 协议也不会自动启用；运行时仅为受支持的显式模型引用加载 Extension 和开放 `web_search` 工具。
- 任务中途切换到不支持的模型时，应用包装层会关闭搜索工具；`url_context` 本阶段不开放。
- `web_search` 投影为独立的 `WebSearch` Transcript 活动，不计入普通“工具调用”；摘要模式和 Monitor 分别统计思考、工具调用与网络搜索，零次项目不显示。
- RPC Adapter 从 `tool_execution_start.args.query` 提取搜索内容并保存到 `TranscriptBlock.Input`。Agent Chat 在活动摘要行显示截断后的 query，展开区以“搜索内容”显示完整 query，并继续显示结果和可点击来源。
- Monitor 将每次 Web Search 作为一条单行活动记录，显示 query 和完成/失败状态；搜索结果正文不进入 Monitor。升级前已持久化的历史搜索事件没有 query，不执行回填。
- 搜索结果保留结构化输入、输出和来源链接；真实 Provider 联网、引用点击、错误和取消保留为发布候选版本人工验收。
- 版本、许可证、网络能力和发布说明要求记录在 [随附 Extension 清单](included-pi-extensions.md) 与根目录 `THIRD-PARTY-NOTICES.md`。

## 托管隔离空间

- 每个 General Chat Task 在 `%LOCALAPPDATA%\PiCompanion\general-chat\<taskId>` 下拥有稳定的 `workspace` 和 `published` 目录。
- `workspace` 是 Pi Runtime 的真实 cwd，但不会通过 Bridge、任务历史、Monitor 或 Chat UI 暴露给用户。
- General Chat 的附件无论原位置如何都会复制到现有 Task/Run 隔离附件目录；Runtime 只读取快照。
- Task 进入回收站时保留托管文件；永久删除或清空回收站时同时删除托管目录和附件快照。
- Workspace Task 与 General Chat 的范围在任务创建后固定，现有 Session 恢复仍要求 cwd 一致。

## 工具边界

General Chat 开放 `read`、`grep`、`find`、`ls`、`edit`、`write`、`ask_user` 和 `publish_artifact`：

- 读取只允许托管工作区和只读附件根。
- 写入只允许托管工作区。
- Shell 在 Extension 权限层硬阻止，不依赖模型提示词。
- 工作目录外路径、附件写入、目录穿越和 junction 逃逸继续失败关闭。
- Runtime worker 的复用键包含 Scope 和 artifact 目录，防止 Workspace/General Chat 工具配置串用。

## 文件交付

Agent 必须在托管工作区完成文件后调用 `publish_artifact`：

1. Extension 验证源文件位于当前托管工作区且不超过 256 MB。
2. 文件以独立 ID 复制到 `published`，尽力设置为只读。
3. Desktop 再次验证发布路径位于当前 Task 的 artifact 根，重新计算 SHA-256。
4. artifact 元数据写入 SQLite `task_artifacts`，并随 Task/Run 恢复。
5. Chat 显示文件名、类型、大小以及“打开”“保存到…”操作；Bridge 不返回内部存储路径。

当前只发布单个文件。目录应先由后续受控文件工具打包；Office/PDF/图片等复杂二进制生成也留给后续 Artifact Worker，不通过 General Chat 开放任意 Shell。

## UI

- 新任务初始显示“尚未选择模式”，输入、附件与发送按钮保持禁用；点击“选择工作目录”或“直接对话”后才进入对应模式。
- 选择目录仍会创建原有 Workspace Task，并显示原有权限模式。
- General Chat 隐藏真实 cwd；Workspace/Git Inspector 收到空工作目录，因此不会浏览托管内部文件。
- 历史页将内部 `GeneralChat` 范围聚合到独立的“直接对话”分组。
- 输入区添加按钮使用固定 viewBox 的 SVG 加号，与相邻操作按钮保持几何居中。
- Monitor 结果正文直接显示总结内容，不再附加“总结：”标签。
- Monitor 的 Capsule 与 Expanded 复用同一个 Header，只切换下方 `ExpandedBody`；Picker、展开/收起和真实高度变化不使用过渡动画。
- Monitor 进行态最多显示最近 12 条严格单行摘要。普通工具和 Web Search 均按调用合并为一行，显示命令、路径、pattern 或 query，不把 start/progress/completed 流水和输出正文重复渲染为活动。

## AI 标题与总结

- “AI 生成任务标题”和“AI 生成任务总结”保留独立开关，但合并到同一个“AI 元数据”设置区，并共用一个模型。
- 旧配置首次规范化时优先迁移已有总结模型，缺失时再使用标题模型；保存后两个旧字段与新的共享模型字段保持一致。
- 应用启动后按需预热一个专用 Pi RPC 进程。标题、总结和超长总结改写进入同一串行队列，不再为每次请求重新启动 Runtime。
- 每个元数据作业开始前仍调用 `new_session`，随后应用共享模型与 `thinking=off`，因此只复用进程，不复用任务上下文或正式 Agent Session。
- 超时或取消时中止并回收 Worker；进程异常退出后，下一次请求会自动创建新 Worker。应用退出时由 `TaskCoordinator` 释放专用进程。
- `%LOCALAPPDATA%\PiCompanion\logs\metadata-worker.jsonl` 记录排队、Worker 就绪、Session 重置和 Provider 生成耗时，不记录用户提示正文。
- `summary` 仅保存 AI 生成的 Run 总结；Runtime 完成、失败或中断文案保存在独立状态字段中，不再写入 `summary`。AI 总结开启且终态 `summary` 仍为空时，Monitor 显示“正在生成 AI 总结”和加载指示，不回退到 Agent 回答；AI 总结关闭时，Monitor 显示截断后的最新 Agent 回答。Chat 的总结区域仍只在存在有效 `summary` 时显示。
- Monitor 结果卡片按内容自适应高度，最大高度为 360px；展开窗口跟随内容收缩，最大高度为 620px，并在自动贴边时随高度变化重新对齐。网络搜索正文只保留在聊天记录中，Monitor 保留搜索运行副标题和包含 query 的单行活动记录。
- 主输入框支持直接粘贴剪贴板中的 PNG/JPEG/GIF/WebP 图片作为附件；图片保存到应用管理目录后复用现有附件发送管线，单张上限 10 MB，普通文字粘贴不受影响。
- 永久删除直接对话任务或清空回收站前，会先释放占用其隔离工作区的 Pi 当前/预热 worker，再以短暂重试删除目录；持久化记录只在文件清理成功后删除，避免 Windows 文件占用导致半完成状态。
- 工具详情使用 Pi `result.content` 的真实文本，不再把“执行完成”等内部状态当作输出；写入 App transcript 的工具输出最多保留 24,000 字符，避免长搜索结果拖慢桥接、持久化和渲染。

## 验证

- .NET：General Chat 工作区创建、附件强制快照、artifact 结果校验与持久化、Scope 重启恢复。
- Extension：General Chat Shell 阻止、工作区文件发布、外部路径拒绝。
- Vue：无目录发送、隔离空间权限显示、artifact 文件卡片和打开/保存 Bridge 操作。
