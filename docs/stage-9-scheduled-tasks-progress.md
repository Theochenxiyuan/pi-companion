# 阶段 9 进展：定时任务

本子阶段交付本地桌面进程内的定时任务。调度定义与任务模板保持独立；触发时创建普通 Task/Run，并复用现有并发队列、工作目录租约、交互请求、通知与历史能力。

## 产品边界

- 任务来源只有“自定义”和“关联模板”。自定义编辑器可以从任意模板快速填入，但复制后独立维护，不保存来源关系。
- 关联模式保存持久化模板 ID，每次触发读取模板最新版本。只有固定工作区或 Direct Chat 模板可以关联；系统模板和未确定上下文的模板只能用于填入。
- 支持单次、每天、工作日和每周指定日期；保存本地计划时间、时区和下一次 UTC 触发时间。
- 管理页支持新建、编辑、启用/暂停、立即运行、打开最近创建的任务和删除。
- Agent 可先读取当前作用域可见的模板和计划，再在用户逐次确认后创建自定义或关联模板的定时任务；完全访问不能跳过确认，且不得跨工作区读取或创建。
- 每次触发创建独立 Task，不续写上一次 Session，不改变用户当前选中的任务。已创建任务继续出现在普通历史和活动任务选择器中。

## 调度与恢复

- `ScheduledTaskService` 随桌面应用启动，等待最早的 `next_run_at`，配置变化会主动唤醒调度循环。
- 调度器依赖应用驻留托盘；完全退出后不使用 Windows Task Scheduler 唤醒。启动或系统恢复时，24 小时内错过的 occurrence 补跑一次，多个错过合并；超过 24 小时记为跳过。
- 同一计划已有排队、运行或等待交互的 Run 时跳过本次，避免计划积压。
- 每个 occurrence 先通过 `(scheduled_task_id, scheduled_for)` 唯一约束登记，再创建 Task；应用在派发中退出时，启动恢复将遗留 `Dispatching` 记录标记为失败。
- 工作区、模板或默认模型无法解析时，本次记录失败并暂停计划，不静默切换目标或模型。

## 数据与协议

- SQLite migration 18 新增 `scheduled_tasks` 和 `scheduled_task_occurrences`。
- occurrence 保存解析后的完整草稿 JSON、关联模板、计划时间、Task/Run ID 和派发错误，用于审计和去重。
- Bridge protocol 64 在初始化快照和 `ScheduledTasksUpdated` 增量消息中同步定时任务。
- Extension 通过当前 Windows 用户私有的双向 Agent 命令管道提供 `list_task_templates`、`get_task_template`、`list_scheduled_tasks` 和 `create_scheduled_task`；每个请求校验 Task、Run、Generation 与权限 Token，Extension 不直接访问 SQLite。
- `TaskCoordinator.StartBackgroundTaskAsync` 创建不抢占当前选择的后台 Task，并继续服从全局两个运行槽与同工作目录串行规则。

## 验证

- Core 测试覆盖自定义/关联归一化、日/工作日/周 recurrence、IANA 到 Windows 时区解析、SQLite 往返、occurrence 幂等和模板删除保护。
- Coordinator 测试验证后台任务不会追加或切换当前可见任务。
- Vue 验收覆盖“从模板填入”保存为自定义任务，以及关联模式只保存模板关系且过滤无固定目标模板。
