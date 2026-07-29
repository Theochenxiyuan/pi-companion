# 阶段 4：权限、提问和方向调整（实现进度）

> 历史交付快照：本文记录阶段 4 完成时的权限模型、测试和限制，不代表当前 `main`。当前入口见 [`docs/README.md`](README.md)。

## 已完成的 MVP 垂直切片

阶段 4 已把 Pi RPC 从只读分析扩展为受应用策略保护的本地 Agent。桌面进程只通过显式 `--extension` 加载随应用发布的 `PiExtension\pi-companion.mjs`，同时继续用 `--no-extensions` 禁止发现用户或项目中的其他 Extension。Extension 缺失时 Run 明确失败，不会退回到无权限拦截的执行模式。

当前开放工具为：

```text
read, grep, find, ls, edit, write, bash, ask_user
```

## 权限与工作目录策略

Extension 在 Pi 的 `tool_call` 事件中、内建工具真正执行前作出决定：

| 操作 | 当前默认行为 |
|---|---|
| 工作目录内读取、搜索、列目录 | 允许 |
| 工作目录外读取或写入 | 阻止 |
| 工作目录内新建文件、普通 edit | 允许，已有文件先备份 |
| 覆盖已有文件、修改敏感路径 | 询问 |
| Shell 命令 | 询问 |
| 删除、提权和下载类 Shell 命令 | 单独的高风险权限类别，询问 |
| 未知自定义工具 | 询问 |

路径在判断前会规范化并解析最近存在祖先的真实路径，覆盖 `..` 穿越、大小写差异以及现有符号链接/Junction 指向目录外的情况。目录外访问直接返回被阻止的工具结果；未经授权的操作不会进入工具执行事件。

授权卡片支持：

- 允许一次。
- 本任务内允许同类操作。
- 拒绝。

“本任务内”授权按 Task ID 保存到 `%LOCALAPPDATA%\PiCompanion\permission-grants`，后续 Run 恢复同一 Task 时仍生效；不同 Task 不共享，产品没有永久全局允许。Shell 授权还绑定规范化命令指纹，覆盖/敏感写入绑定目标指纹，授权一个命令或文件不会放行其他命令或目标。

## 修改前备份

对可拦截的 `edit` 和 `write`，Extension 在工具执行前读取已有文件并计算 SHA-256。原内容保存到：

```text
%LOCALAPPDATA%\PiCompanion\backups\objects\<hash-prefix>\<sha256>
```

每个 Run 同时追加 manifest，记录原路径、Hash、大小、时间和 Tool Call ID。备份失败会阻止修改，不会在无恢复证据的情况下继续。

## 提问与 Extension UI RPC

Extension 注册 `ask_user` 工具。Agent 需要用户决策时可以提供最多 8 个单选项，也可以请求自由输入。Pi 的 `select`、`confirm`、`input` 和 `editor` RPC 请求会映射为带唯一交互 ID 的领域事件；权限类 `select` 使用内部标记与普通单选问题区分。

后端支持多个待处理交互，并拒绝重复提交或不在选项列表中的答案。Monitor 和 Agent Chat 都从同一 `TaskProjection` 渲染，因此标题、选项、处理状态和最终回答一致。每个新的待处理 Interaction 会让 Monitor 自动展开一次，但不会激活窗口或在用户手动收起后因同一请求重复展开。Agent Chat 中 Pending 授权/提问保持完整操作卡片；完成或取消后变为与工具调用、思考一致的单行折叠记录，可点击查看请求和响应详情。应用重启造成 Run 中断时，未完成交互会在证据表中转为 `Cancelled`。

## Steer、Follow-up 和队列

`steer` 与 `follow_up` 继续使用 Pi 原生 RPC 语义。`queue_update` 不再只保存数量，而是把实际 steering/follow-up 消息数组写入领域事件和 SQLite；Agent Chat 提供可展开的消息队列，Monitor 显示队列数量。Pi `agent_settled` 仍是主要完成信号，只有消息队列为空时任务才会进入完成状态。

Run 的终态直接保存为 `Completed`、`Failed` 或 `Interrupted`。Agent Chat 始终保留结果，不提供额外确认按钮；Monitor 保留“确认”按钮用于关闭当前结果卡片，点击后标题、目录和内容回到无进行中任务状态。这个操作只改变 Monitor 的本地展示，不追加领域事件，也不改变或删除 Task、Run、Transcript 与 Pi Session。

## SQLite 与恢复

`interaction_requests` 已从空表骨架升级为可查询的交互证据投影，保存：

- Task、Run 和 Interaction ID。
- Approval/Question 类型与 RPC method。
- 标题、单选项和创建时间。
- Pending、Approved、Rejected、Cancelled 状态。
- 用户响应与处理时间。

Run Event 仍是重放的权威来源；证据表与事件在同一个 SQLite 事务中更新。Bridge 协议升级到 v5，Snapshot 包含交互类型、选项以及两类消息队列。数据库迁移会把旧的 `CompletedUnacknowledged` / `FailedUnacknowledged` 转换为直接终态，并移除旧的 `Acknowledged` 展示事件，同时保留原始成功、失败或中断结果。

## 自动化与界面验收

- Extension：工作目录内路径、`..` 规范化、Junction 逃逸阻止。
- Extension：Shell 拒绝、允许一次、跨 Run 的 Task 级同类授权。
- Extension：edit 前内容寻址备份。
- Extension：`ask_user` 单选和自由输入。
- Pi RPC：Extension 显式加载参数和完整工具列表。
- Pi RPC：权限请求前不产生工具执行事件，拒绝后永不执行，批准后才执行。
- Pi RPC：问题选项/答案往返，Steer/Follow-up 队列内容。
- SQLite：交互请求物化、处理和 Transcript 重放。
- SQLite：旧确认事件迁移后恢复原始 `Completed` / `Failed` / `Interrupted` 终态。
- Vue：阶段 4 Transcript 预览通过真实浏览器 DOM/布局检查；已完成交互默认是 27px 单行并可展开，Pending 卡片仍可操作，无横向溢出和控制台错误。

最新 Release 验证（2026-07-19）：Extension 5/5、xUnit 49/49 通过，前端与 .NET 构建 0 警告、0 错误，Explorer Command COM 冒烟测试通过；0.4.0 开发 MSIX 创建成功并包含 `PiExtension\pi-companion.mjs`。

## 人工验收建议

1. 提交需要运行 Shell 命令的任务，确认命令在授权前没有执行。
2. 分别选择“拒绝”“允许一次”和“本任务内允许同类操作”，检查后续行为与 Transcript。
3. 要求 Agent 写入工作目录外路径，确认操作直接被阻止。
4. 要求 Agent 使用 `ask_user` 发起单选和自由输入问题，在 Monitor 与 Chat 分别作答。
5. 运行期间分别发送 Steer 和 Follow-up，展开 Chat 队列并等待 `agent_settled`。
6. 完成后确认 Agent Chat 没有结果确认按钮；在 Monitor 点击“确认”，检查标题、目录和内容回到无进行中任务状态，同时 Chat 与任务历史仍保留原始终态和结果。

## 已知边界

- 工作目录策略是应用层安全边界，不是 Windows 内核沙箱；Pi 与 Shell 仍使用当前用户权限运行。
- 路径在 `tool_call` 时解析真实祖先并立即判断，但内建工具随后打开目标，因此无法从应用层彻底消除验证后目标被替换的竞争条件。
- 确定性备份只覆盖被 Extension 拦截的 edit/write；Shell 产生的任意文件变化尚未提供完整恢复保证。
- 锁定版本 Pi Runtime 尚未正式纳入发布包；开发期仍需显式路径，发布构建缺少私有 Runtime 时会明确失败。
- 真实供应商凭据下的人工端到端验收和长时间压力测试仍需在发布候选版本上执行。
