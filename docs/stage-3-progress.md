# 阶段 3：真实 Pi RPC（实现进度）

> 历史交付快照：本文记录阶段 3 完成时的 Pi 合约、测试和限制，不代表当前 `main`。当前入口见 [`docs/README.md`](README.md)。

## 已完成的垂直切片

阶段 3 已从 `DemoAgentBackend` 切换到真实 `PiRpcBackend`。Explorer 或 Agent Chat 提交的任务会在工作目录内启动独立 Pi RPC 进程；当前只开放 `read`、`grep`、`find` 和 `ls`，因此这一切片只支持只读分析。

RPC 适配器按 Pi 0.81.1 合约处理 `prompt`、`steer`、`follow_up`、`abort`、`get_state`、`switch_session` 和流式 Event。stdout 只作为协议流，采用严格 LF JSONL 分帧；stderr 单独写入 `%LOCALAPPDATA%\PiCompanion\logs`。未知 Event 会转成 Warning 并忽略，不会使适配器失控。

每个 Run 都有独立进程和 Windows Job Object。Job 使用 `KILL_ON_JOB_CLOSE`，正常完成、Abort 超时、应用退出和协议失败都会清理进程树。Pi 0.81.1 的 `agent_settled` Event 是成功完成的首要终态信号，适配器收到后生成领域 `RunSettled`；为兼容旧版 Pi 或事件丢失，`agent_end` 后仍会延迟通过 `get_state` 检查 `isStreaming` 与 `pendingMessageCount`，仅在 Agent 空闲、队列为空且尚未收到 `agent_settled` 时回退完成。

Pi 0.81.1 新增的 `summarization_retry_scheduled`、`summarization_retry_attempt_start` 和 `summarization_retry_finished` 会映射为独立的“摘要重试”生命周期，不再作为未知协议警告展示；Companion 会保留重试次数、等待时间、摘要来源和触发原因。

## 对话投影与界面

阶段 3 收尾已把运行事件时间线替换为面向对话的结构化 Transcript。领域投影会把连续文本增量合并到同一条 Agent 消息，把 Thinking、Tool、Interaction 和 Notice 保持为独立语义块；工具开始、进度和结束事件会原位更新同一工具语义块，不再把每个词或每个事件渲染为一条步骤。

Agent Chat 以整个 Run 作为一条 Agent 回复，Run 内的多段正文、思考、工具调用和交互按事件顺序嵌入同一回复，并提供摘要、标准、详细三种显示密度。思考和工具默认只显示无边框的“图标 + 灰色说明 + 结果”状态行；连续工具会先折叠为工具组，重复工具用主题数字圆标计数，展开后显示缩进的子工具列表，再展开单个工具才出现带边框的完整输入输出面板。摘要模式隐藏逐项工作过程，但保留当前 Run 的思考次数和工具调用总数。Markdown 在渲染前由 DOMPurify 清理。Steer 与 Follow-up 也会写入 Transcript，SQLite 恢复时可重放出一致的对话视图。

## 任务目录与桌面壳层

Agent Chat 可直接为新任务选择工作目录，Explorer 激活仍可把目录和附件带入 Draft。Task 一旦创建便锁定工作目录，后续继续和新 Run 都沿用该目录；如需切换目录必须新建 Task。顶栏在所有响应式宽度下保留工作目录，空间不足时只做省略显示；近期任务项的 Tooltip 同样显示持久化的工作目录。

左侧任务栏支持收起和拖拽调宽，展开宽度限制为 220–420px，默认 232px，并在本地记忆。Agent Chat 的显示密度、模型和推理等级菜单改为页面内锚定的自定义下拉层，避免 WebView2 原生弹层先出现在旧坐标再跳转；WPF 下拉 Popup 同样关闭位移动画并绑定定位目标。托盘菜单只保留打开 Agent Chat、显示/隐藏 Monitor 和退出，不再提供绕过正常任务创建流程的快速演示入口。

## SQLite 与恢复

数据库位于 `%LOCALAPPDATA%\PiCompanion\pi-companion.db`，使用 WAL 和外键。当前写入：

- `tasks`
- `task_attachments`
- `runs`
- `run_events`
- `schema_migrations`

同时已创建产品计划中的其余证据表骨架。事件以 `(run_id, sequence)` 唯一约束保证幂等；协调器在事件写入 SQLite 后才更新 UI 投影。

启动时恢复最近任务并重放领域事件。Agent Chat 同时加载最近 20 条任务摘要及其工作目录，用户可以从侧栏按任务 ID 打开并重放其最新 Run；当前 Run 活跃时禁止切换，避免后台事件丢失。数据库中仍为活动状态的 Run 会追加一个 `RunInterrupted` 恢复事件，不会静默重启。用户明确继续该任务时会创建新 Run，并在提交 Prompt 前使用已保存路径执行 `switch_session`。

## Runtime 边界

Resolver 的默认目标是应用目录内的 `PiRuntime\pi.exe` 或私有 Node/Pi 文件。开发机可以显式设置：

```text
PI_COMPANION_PI_PATH
PI_COMPANION_NODE_PATH
```

未配置且应用私有 Runtime 缺失时，任务明确失败；不会搜索或调用用户全局 `pi` shim。

## 自动化覆盖

- JSONL：半个 UTF-8 字符、半条 JSON、多条 JSON、CRLF 和最大帧限制。
- Resolver：显式 JavaScript Runtime、应用私有 exe、禁止全局 Pi 回退。
- SQLite：事件重放、重复 Sequence 幂等、活动 Run 中断恢复、Session 路径保存。
- 任务边界：近期任务排序与目录恢复、已创建 Task 禁止切换工作目录。
- Pi RPC 进程：受控 Node fixture 验证 Response/Event 交错、文本流、工具事件、严格 Sequence、`agent_settled` 主终态、旧版状态检查回退和 Abort → Interrupted。
- Transcript：流式文本合并、工具生命周期原位更新、Steer 用户消息和 SQLite 恢复。

最新 Release 验证（2026-07-19）：.NET 与前端构建 0 警告、0 错误，xUnit 40/40 通过，Explorer Command COM 冒烟测试通过。

## 尚未完成

- 将锁定版本的 Pi Runtime 真正纳入 MSIX/发布构建；当前只实现 Resolver 和开发期显式路径。
- 独立完整任务历史页、重命名、回收站和证据详情 UI。
- 阶段 4 的 Pi Companion Extension、路径权限策略和完整授权/提问体验已在后续阶段完成，见 `stage-4-progress.md`。
- 真实供应商凭据下的人工端到端验收与长时间压力测试。
