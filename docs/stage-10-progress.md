# 阶段 10 进展：多任务并发执行

本阶段先交付“多任务运行、单任务聚焦”。Agent Chat 与 Monitor 仍只展示当前选中的一个任务，但其他任务可以在后台继续排队、运行、等待交互或结束。

## 当前边界

- 全局最多同时运行 2 个 Run；超过容量的 Run 保持 `Queued`，有槽位后自动启动。
- 同一个 Task 内的 Run 串行，继续对话仍复用该 Task 的 Pi Session。
- 同一规范化工作目录一次只允许一个 Run 执行，避免文件写入、Git 状态与 Evidence 归属互相污染。
- 不同工作目录和独立 General Chat 托管目录可以并发。
- 用户可以在 Run 活动时新建任务、切换任务，并停止仍在队列中的 Run。
- “全部任务”页中的工作区是独立持久化实体，不再只由现有任务临时派生；允许先添加空工作区，删除最后一个任务后工作区仍会保留。
- 每个普通工作区卡片提供 `+` 快捷入口，直接打开已预填该目录的新任务。
- Monitor 暂不常驻展示多任务列表，仍只呈现当前选中的任务；点击标题可从活动任务和最近任务中切换。

## 实现

- `TaskCoordinator` 将选中任务与活动任务分离，并按 Task/Run 保存投影、对话、本地待发送区计时器和调度状态。
- 调度器使用两个运行槽和工作目录租约；终态事件释放租约并继续派发可运行任务。
- `PiRpcBackend` 由单一活动上下文改为按 `RunId` 路由多个 `RunContext`，Steer、Follow-up、授权、提问、停止和统计读取均定向到对应 Run。
- `WorkspaceEvidenceService` 按 `RunId` 维护 watcher，允许不同工作目录的 Evidence 同时采集。
- SQLite 继续串行化事务写入；应用启动恢复会将数据库中所有遗留活动 Run 标记为 `Interrupted`。
- SQLite 新增独立 `workspaces` 记录和 Task 到 Workspace 的稳定关联；升级时会从现有 Workspace Task 自动回填。
- Desktop Shell 订阅全部任务变化用于通知和完成行为；Chat 与 Monitor 的详细投影仍只订阅选中任务。

## Monitor 展示收口（2026-07-26）

- Capsule 与 Expanded 继续复用同一个 WPF Window，并进一步共用同一个 Header；展开时只显示下方内容区，避免两套标题组件切换造成闪烁。
- Picker、展开/收起和内容真实高度变化不使用过渡动画。现有动画偏好只控制状态指示和“正在生成 AI 总结”加载指示，并遵循系统减少动态效果设置。
- 进行态最多显示最近 12 条严格单行摘要，换行与连续空白在 Monitor 展示层归一化，溢出内容省略并保留完整 Tooltip。
- 同一次工具调用只占一条活动记录，显示 `toolInput` 中的命令、路径、pattern 或 query，并以 `✓`、`✕` 或取消标记反映终态；工具输出和 start/progress/completed 流水不进入 Monitor 活动列表。
- Web Search 仍使用独立 `WebSearch` Transcript 类型和结果计数，但不再从活动列表完全隐藏：进行时保留“网络搜索进行中”副标题，列表显示 query，搜索结果正文仅在 Agent Chat 中展示。
- AI 总结开启且终态总结为空时，结果卡显示动态加载状态；AI 总结关闭时才使用截断后的最新 Agent 回答作为结果正文。

## 验证

- 两个真实 Pi RPC fixture 子进程并发运行并分别按 `RunId` 停止。
- 第三个不同工作目录任务排队，运行槽释放后自动启动。
- 同工作目录任务串行，排队任务可独立取消。
- 活动任务可在 Agent Chat 中切换，后台事件不会覆盖当前详情。
- 本地待发送区可在后台任务完成后继续按顺序启动后续 Run。
- 应用恢复会一次性中断所有遗留活动 Run。
- 空工作区可以跨重启保留；创建、删除或清理任务不会再决定工作区是否存在。
- 工作区卡片的 `+` 通过稳定 Workspace ID 解析目录并打开新任务，不接受前端直接注入目录。
- Monitor 标题选择器会按等待交互、运行中、排队中的优先级展示全部活动任务，并补充最近 5 个任务；选择后复用全局任务选中状态，Chat 与 Monitor 保持同步。
- 最新 Release 验证：Web Search Extension 2/2、Chat 145/145、Pi Companion Extension 22/22、Core 166/166 通过；.NET Release、x64 Explorer Command 与 COM 冒烟测试通过，`PiCompanion.Development 0.4.0.0` 覆盖安装后状态为 `Ok`。

Monitor 同时展示多个任务状态、同一仓库的隔离 worktree 并发，以及用户可配置的并发上限仍留作后续增强。
