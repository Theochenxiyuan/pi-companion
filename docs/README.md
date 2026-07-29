# 文档索引

这里同时保留当前实现说明、产品规划和历史阶段记录。阅读时请先确认文档类型；当文字与代码不一致时，以当前 `main` 的源码、配置、自动化测试和构建脚本为准。

## 当前事实来源

- [`../README.md`](../README.md)：项目定位、当前能力、环境要求和源码构建入口。
- [`pi-companion-product-technical-plan.md`](pi-companion-product-technical-plan.md)：当前产品边界、实现架构和后续计划。标为计划或目标的内容不代表已经交付。
- [`skill-management-implementation-plan.md`](skill-management-implementation-plan.md)：Skill 管理最终交付边界。
- [`included-pi-extensions.md`](included-pi-extensions.md)：随应用分发的 Pi Extension 与第三方披露。
- [`ui-system.md`](ui-system.md)：当前 UI token、共享组件和维护方式。
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) 与 [`../SECURITY.md`](../SECURITY.md)：贡献和安全报告流程。

精确的 Bridge 协议版本、运行上下文 schema、依赖版本和测试数量不在索引中重复维护，分别以协议常量、项目配置、lockfile 和当前 CI 为准。

## 当前实现进展

- [`stage-8-progress.md`](stage-8-progress.md)：直接对话、Provider 原生 Web Search 与本地 Git 写入。
- [`stage-10-progress.md`](stage-10-progress.md)：多任务并发、工作区实体和 Monitor 任务切换。

## 历史交付快照

以下文档记录各阶段完成时的界面、协议、测试数量和已知限制，用于追溯设计演进，不代表当前版本：

- [`stage-1-delivery.md`](stage-1-delivery.md)
- [`stage-2-delivery.md`](stage-2-delivery.md)
- [`stage-3-progress.md`](stage-3-progress.md)
- [`stage-4-progress.md`](stage-4-progress.md)
- [`stage-5-delivery.md`](stage-5-delivery.md)
- [`stage-6-delivery.md`](stage-6-delivery.md)
- [`stage-7-delivery.md`](stage-7-delivery.md)
- [`stage-11-skills-progress.md`](stage-11-skills-progress.md)
- [`stage-12-skills-phase-4-progress.md`](stage-12-skills-phase-4-progress.md)

阶段 11/12 的 Skill 文件名沿用当时的开发顺序，与当前产品计划中的阶段编号不是同一套编号。
