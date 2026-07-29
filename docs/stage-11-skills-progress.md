# 阶段 11：技能管理阶段 1～3 进展

> 历史实现记录：本文包含后来已删除的技能库与受管安装方案，不再代表当前产品边界。当前基线以 `docs/pi-companion-product-technical-plan.md` 第 8.8、16.6 和阶段 9 为准。

状态：**阶段 1“真实的只读技能总览”、阶段 2“本地技能库与导入”和阶段 3“安装、卸载与 Pi 原生生效”已完成**

完成日期：2026-07-27

后续阶段 4 已完成，见 `docs/stage-12-skills-phase-4-progress.md`。

## 后续产品方向简化（2026-07-28）

- 删除“技能读取来源”设置及其持久化、Bridge 字段和来源裁剪逻辑；管理界面与补全始终扫描 Pi 和通用 Agent 的原生目录。
- Companion 不再用 `--no-skills` 禁用技能，也不再用逐项 `--skill` 参数接管加载列表；普通对话和元数据任务都交由 Pi 按原生规则发现技能。
- 技能扫描结果仍用于管理界面、工作区优先级展示和只读文件访问边界，不再决定 Pi 的启动参数。
- Bridge protocol version 提升到 `51`，移除设置中的技能来源字段。

## 参考基线

实施前已完整阅读：

- 当时的独立技能计划（现已归档为主计划合并说明）：`docs/skill-management-implementation-plan.md`
- 当前开发机 Pi 0.82 Runtime 随附的 `docs/skills.md`
- 当前开发机 Pi 0.82 Runtime 随附的 `dist/core/skills.js`
- 当前开发机 Pi 0.82 Runtime 随附的 `dist/core/package-manager.js`

另外核对了 Runtime 的 `dist/utils/frontmatter.js`，用于确认 frontmatter 的起止边界和解析失败行为。

## 阶段 1 已交付

### Application 只读发现

新增 `PiCompanion.Application.Skills.SkillDiscoveryService` 和只读领域快照，扫描：

- `~/.pi/agent/skills/`
- `~/.agents/skills/`
- 每个已登记工作区的 `<workspace>/.pi/skills/`
- 每个已登记工作区从自身到 Git 根目录（无 Git 根时到文件系统根）的 `.agents/skills/`

当前实现与 Pi 0.82 对齐的规则：

- 目录含 `SKILL.md` 时将其作为技能根，并停止向下递归。
- Pi 专属技能根允许直接 `.md` 文件；通用 `.agents/skills` 根忽略直接 `.md` 文件。
- 跳过隐藏目录和 `node_modules`。
- 读取 `.gitignore`、`.ignore`、`.fdignore`，支持常用 glob、目录规则、注释和否定规则。
- 跟随文件或目录链接进行只读发现，按规范化最终路径去重，并用已访问真实目录集合阻止链接循环。
- 解析 `name`、`description` 和 `disable-model-invocation`；支持普通、单双引号和 `|`/`>` 块字符串。
- 缺少 `description` 时保留为不可用条目并返回诊断；名称和描述规范问题作为警告，但继续作为可用技能展示。
- 同名优先级按 Pi 0.82 的项目 Pi、当前目录到 Git 根的项目 Agent、全局 Pi、全局 Agent 顺序计算；不同已登记工作区之间不互相制造冲突。
- 目录不存在、目录为空、已发现和无法访问分别返回类型化扫描位置状态。
- 阶段 1 尚无 Companion 管理的安装，因此发现到的安装全部标为“外部安装”。
- 设置页新增独立“技能”设置，持久化 `all`、`pi`、`agents` 三种读取来源；发现服务在扫描前按该值裁剪真实目录，而不是在 UI 中对完整结果做事后隐藏。

### Bridge

- Bridge protocol version 最终从 `39` 提升到 `41`；`40` 引入初版只读技能协议，`41` 增加本次扫描实际采用的 `sourceMode`。
- 新增类型化 `LoadSkillsRequestDto`。
- 新增类型化 `SkillsLoadedDto`、技能、安装、扫描位置和诊断 DTO。
- Desktop Host 从持久化的 `TaskCoordinator.Workspaces` 投影已登记工作区，并在后台执行只读扫描。
- 初始化能力列表新增 `skill-discovery-readonly`。

### Vue

- 新增真实 `SkillsView.vue`，替换 skills 分支的占位页。
- 支持名称、描述、路径和工作区搜索。
- “都读”时支持 Pi/通用 Agent 来源筛选与全局/工作区作用域筛选；选择单一读取来源时隐藏冗余的来源筛选，仅保留作用域筛选。
- 支持手动刷新、安装路径、工作区归属、继承状态、Pi 原生优先项和诊断展示。
- 显式展示空目录、缺失目录、无技能、无效技能和名称冲突。
- 阶段 1 交付时页面明确提示这只是只读发现，且当时的 Companion 对话仍保留 `--no-skills`；该运行时边界已由阶段 3 完整交付后解除。
- `presets` 和 `scheduled` 继续使用原占位页。
- 开发模式在无 WebView Bridge 时可加载预览快照；该 fallback 受 `import.meta.env.DEV` 保护，生产构建不会用假技能代替 Host 数据。

## 阶段 2 已交付

### Application 本地技能库

- 新增版本化 `SkillLibraryStore`，元数据以 `skill-library.v1` 键下的 version 2 结构保存在现有 SQLite settings 中，库文件位于 `%LOCALAPPDATA%\PiCompanion\skills\library`。
- 建立 `library`、`staging` 和预留 `cache` 三层目录；应用启动时只清理内部遗留 staging，不触碰导入来源或外部技能目录。
- 支持选择包含根级 `SKILL.md` 的目录，以及包含单个技能根的 ZIP；ZIP 可以有一层或多层包装目录。
- 导入采用“两步式”流程：先安全复制/解压到 staging，返回导入预览；用户确认后才将内容移动到库目录并提交元数据。
- 预览展示 `name`、`description`、版本、许可证、“本地导入”标记、内容哈希、完整文件清单、总大小、脚本、可执行文件和许可证文件。
- 目录导入拒绝 symlink、junction 和其他 reparse point；ZIP 导入拒绝绝对路径、`..`、盘符、链接、根外文件和多个 `SKILL.md`。
- 限制单包最多 2,000 个文件、单文件 25 MB、总大小 100 MB；逐文件计算 SHA-256，并基于相对路径和文件哈希计算稳定内容哈希。
- 相同内容明确拒绝；同名但内容不同会创建独立受控副本，并在确认页和库卡片中提示，不会静默覆盖。
- 不持久化用户选择的目录或 ZIP 路径；version 1 元数据中的旧路径字段会在兼容读取后通过 version 2 重写移除。
- 删除先检查发现快照中的受管安装标记；存在受管安装时拒绝删除。允许删除时只删除 Companion 库副本，不修改其他本地文件或外部安装。
- 元数据提交失败会回滚目录移动；删除的元数据提交失败会将库目录移回原位。

### Bridge 与 Desktop

- Bridge protocol version 从 `41` 提升到 `43`：`42` 引入本地技能库协议，`43` 从持久化模型和响应中移除不必要的原始导入路径；初始化能力新增 `skill-library-local-import`。
- `SkillsLoadedDto` 新增类型化 `LibraryPackages`；安装项新增 `ManagedPackageId`，用于区分外部安装和未来由 Companion 管理的副本。
- 新增目录/ZIP 选择、导入预览、确认导入、取消导入、从本地库删除和动作结果的类型化请求/响应。
- Desktop 文件选择后在后台完成 staging 校验；Vue 确认前不会写入库，取消时会清理 prepared import。

### Vue 与本次 UI 调整

- 技能页默认进入“已安装” Tab，并新增“技能库” Tab；`presets` 和 `scheduled` 仍保留占位页。
- “已安装”继续展示真实 Pi/通用 Agent 发现结果；“技能库”只展示 Companion 保存的本地技能和“未安装”状态。
- “扫描详情”改为扫描时间右侧的紧凑按钮和独立弹窗；弹窗只展示明确发现技能的目录，以及目录存在但没有发现技能的目录，不展示不存在或无法访问的目录。
- 顶部移除已登记工作区统计；问题数为 0 时不显示，存在问题时使用“有问题”而不是“有诊断”。
- 技能卡用“缺少描述”“名称冲突”“格式无法解析”等用户可理解标题展示问题，不再把内部诊断码作为主标题。
- 技能库卡片只保留名称、描述、“本地导入 / 未安装”和操作；版本、许可证、导入时间、文件数量、总大小、哈希、问题和文件清单统一放入“查看详情”弹窗。
- 详情与导入确认不再单列“内容与安全”、脚本数量或许可证文件提示；许可证统一显示具体值或“不包含许可证”，文件清单仅展示更易读的路径与大小。
- 技能库支持搜索、目录/ZIP 导入、导入前安全确认、详情查看和带二次确认的删除。
- 导入和安装保持为两个独立动作：导入只写入本地库，之后由用户另行选择安装目标。

## 阶段 3 已交付

### 受管安装与卸载

- 新增 `SkillInstallationService`，可把技能库中的不可变副本安装到：
  - `~/.pi/agent/skills/<skill>`
  - `~/.agents/skills/<skill>`
  - `<workspace>/.pi/skills/<skill>`
  - `<workspace>/.agents/skills/<skill>`
- 技能库副本始终保留在 Companion 数据目录；安装只向 Pi 原生加载目录创建独立受管副本。
- 每个受管副本写入 `.pi-companion-skill.json`，记录包 ID、内容哈希、版本、作用域、安装位置、工作区和时间；发现快照会把该标记投影为受管安装状态。
- 安装前重新验证库内容与逐文件哈希，拒绝链接、junction、路径逃逸、保留标记文件、缺失内容和不符合通用 Agent 目录要求的名称。
- 复制先写入同一目标根下的隐藏 staging，再通过目录移动原子生效；更新已有受管副本时先保留隐藏备份，失败会恢复，进程中断遗留的目标专属备份会在下次安装时优先恢复。
- 已存在但没有 Companion 标记的目录被视为外部冲突，绝不覆盖或删除；属于其他包的受管目录也不会覆盖。
- 如果受管副本内容与标记哈希不一致，显示“内容已被外部修改”，并禁止覆盖和卸载，避免丢失用户修改。
- 卸载只移除哈希仍匹配的受管副本，技能库副本和其他本地/外部目录不受影响。
- 删除技能库包前始终扫描 Pi 与通用 Agent 两类真实目录，不受页面来源设置裁剪；当前已登记工作区或全局目录中存在该包的受管副本时拒绝删除。

### Pi 项目信任与运行时生效

- 新增 `PiProjectTrustService`，直接兼容 Pi 0.82 的 `~/.pi/agent/trust.json`：使用规范化真实目录、最近祖先继承、`true`/`false`/`null` 值和同名 `.lock` 目录协议。
- 工作区安装页展示实际信任状态、决策路径和是否继承；未信任时点击安装会进入独立二次确认，Host 不接受仅由前端默认代填的静默授权。
- 二次确认以简洁文案说明 Pi 还会加载项目设置、扩展和其他项目资源，并明确项目扩展可能执行代码；确认按钮直接表达“信任项目并安装”，不再增加重复复选框。
- 若技能复制成功但信任写入失败，会回滚刚创建的受管副本；卸载不会撤销项目原有信任。
- 普通 Pi RPC 与 `PiTaskMetadataGenerator` 均不再传入 `--no-skills` 或逐项 `--skill`；所有 Pi 进程都使用原生技能发现。
- 全局技能变更在存在任一运行中任务时被拒绝；工作区技能变更在该工作区有运行中任务时被拒绝。
- 安装或卸载前会终止受影响工作区的空闲/预热 Pi worker；全局变更会终止全部空闲/预热 worker，确保下一次消息创建新的资源上下文，不会改变运行中的进程。
- General Chat 继续使用独立托管工作目录，因此能发现全局 Pi/Agent 技能，但不会把普通已登记工作区的项目技能带入 General Chat。

### Bridge、设置与 Vue

- Bridge protocol version 在阶段 3 从 `43` 提升到 `44`，并在移除重复的持久化默认安装位置后提升到 `45`；初始化能力新增 `skill-native-installation`。
- 新增类型化 `ApplySkill`、`UnapplySkill` 请求和完整安装目标响应；目标状态覆盖可安装、已安装、可更新、外部冲突、其他受管包冲突、内容漂移和不可访问。
- 设置页不再提供技能读取来源；技能总览始终扫描 Pi 与通用 Agent 原生目录。
- 技能库卡片继续保持精简，只增加“安装/管理”入口；完整的全局/工作区、Pi/Agent、工作区、目标路径、冲突和信任信息统一放在安装弹窗。安装目标选择器按作用域、安装位置、工作区从左到右排列。
- “已安装”卡片同样改为摘要层级，只显示名称、状态、描述、安装位置数量和“查看详情”；具体路径、问题与卸载入口统一放在详情弹窗。
- “已安装”中的 Companion 受管副本提供直接“卸载”，二次确认展示精确目标路径并说明技能库副本仍会保留。
- 安装结果、失败原因和刷新后的真实安装状态均通过 Bridge 返回；生产环境没有安装成功或安装状态的假数据。
- 用户界面统一使用“安装 / 卸载”术语；类型化 Bridge 仍保留 `ApplySkill` / `UnapplySkill` 内部 contract，避免为文案变化制造协议不兼容。
- 动作结果提醒提供直接关闭按钮；用户手动刷新技能时会立即清除旧结果，后续真实扫描状态仍通过 `SkillsLoaded` 更新。

## 实际边界与近似

- 发现页只扫描计划列出的 Pi 原生全局目录和已登记工作区目录；不扫描 Pi package、settings/CLI 显式路径或内置技能。技能库作为独立 Tab 和安装来源展示，不混入 Pi 原生发现优先级。
- 工作区只读发现仍会展示未信任目录中的文件；信任状态用于说明 Pi 是否会加载项目资源，并在执行工作区安装时强制确认。
- 为避免只读总览引入新的 YAML 运行时依赖，Application 仅解析当前展示需要的顶层字段和常用 YAML 字符串形式；Pi Runtime 仍使用完整 `yaml` 包。复杂 YAML 特性的完全等价不属于本阶段。
- ignore 匹配覆盖 Pi 技能目录中常用的规则，但 Runtime 使用 npm `ignore` 包；极少见的复杂 gitignore 组合仍可能有边缘差异。
- 冲突结果仍按每个已登记工作区分别计算；阶段 3 的安装目标也只包含全局目录和当前已登记工作区。取消登记的工作区不会出现在管理界面中，重新登记后可再次识别其中的受管标记。
- 技能发现始终覆盖 Pi/Agent 原生目录，不限制安装目标，也不削弱删除前的受管副本保护。
- Companion 只管理带有效标记且内容哈希匹配的副本；不会接管、迁移或“修复”已有外部技能目录。
- 项目信任是 Pi 的目录级安全决定，不是单技能授权。卸载不会自动写入 `false` 或删除信任记录，避免破坏用户对其他 Pi 项目资源的决定。
- ZIP 以“恰好一个 `SKILL.md` 技能根”为边界：技能根外存在普通文件会拒绝导入，避免把压缩包中的无关内容静默带入库。
- 库元数据使用现有 SQLite settings 的版本化 JSON 保存，而不是新增数据库表；文件内容与元数据仍分别采用安全移动和回滚来保持一致。

## 严格未做

- 未增加收藏、远端搜索、下载、更新、版本切换或迁移功能。
- 未接管或删除外部安装，也未提供强制覆盖内容漂移副本的操作。
- 未自动撤销 Pi 项目信任；信任撤销不与单个技能的卸载绑定。
- 未接管 Pi 的技能加载顺序；加载与覆盖规则仍由 Pi Runtime 决定。

## 验证结果

自动化验证：

```text
dotnet test
通过：195，失败：0，跳过：0

src/PiCompanion.Chat > npm test
Test Files：31 passed
Tests：156 passed

src/PiCompanion.Chat > npm run build
vue-tsc --noEmit：通过
Vite production build：通过（101 modules transformed）
```

额外编译检查：

```text
dotnet build src/PiCompanion.Desktop/PiCompanion.Desktop.csproj --no-restore
0 warnings, 0 errors

scripts/build.ps1 -Configuration Release
Release Desktop、Explorer Command、COM smoke、Chat build/test、Node tool tests 和 .NET tests 全部通过

scripts/install-explorer-integration.ps1 -Configuration Release -NoBuild
PiCompanion.Development 0.4.0.0：Status=Ok
安装位置的 Chat bundle 与 PiCompanion.Application.dll SHA-256 均与本次 Release 产物一致
```

锁定 Pi 0.82 Runtime 的真实 RPC 探针：

```text
临时 HOME + 已信任临时工作区 + .pi/skills/stage3-runtime-probe/SKILL.md
get_commands：success=true
找到：skill:stage3-runtime-probe
sourceInfo：source=auto, scope=project, origin=top-level
```

这确认没有 `--no-skills` 的普通 RPC 会按 Pi 0.82 原生规则加载受信任工作区中的安装副本；探针结束后已删除全部临时文件。

开发机真实目录探针结果：

```text
find-skills  True  True  global/agents/True
%USERPROFILE%\.agents\skills\find-skills\SKILL.md
```

这确认当前开发机的 `find-skills` 被识别为“可用 / 全局生效 / 通用 Agent / 外部安装”。

新增验收覆盖：

- .NET：全局与工作区扫描、Pi/Agent 根差异、递归停止、隐藏目录、`node_modules`、ignore、frontmatter、缺少描述、校验警告、Git 根继承、真实路径合并、按工作区冲突优先级，以及 Pi 启动参数不含 `--no-skills`/`--skill`。
- Bridge：协议版本一致、类型化 Skills contract、安装/卸载 contract、安装目标状态与技能管理能力。
- Vue：真实导航链路、`LoadSkills`/`SkillsLoaded`、安装与诊断展示、搜索、作用域筛选、刷新清除旧消息、动作消息关闭和技能管理弹窗。
- .NET 阶段 2：目录复制与重启持久化、两步式预览/取消/确认、导入路径不持久化、同内容拒绝、同名受控副本、ZIP traversal/link/缺失技能拒绝，以及受管安装删除保护。
- Bridge/Vue 阶段 2：协议 42、目录/ZIP picker、导入预览与确认、删除动作、技能库元数据/安全/文件清单、双 Tab、问题文案、扫描详情降级和生产无假数据。
- .NET 阶段 3：四类全局/工作区 Pi/Agent 目标、受管标记、真实发现回读、外部冲突、内容漂移保护、复制失败回滚、中断备份恢复、Pi 信任持久化/继承/覆盖、运行任务阻止变更、空闲 worker 定向失效，以及 fake Pi RPC 正常启动参数不含 `--no-skills`。
- Bridge/Vue 阶段 3：协议 45、安装时选择安装位置、安装管理弹窗、已安装详情弹窗、目标/冲突/信任展示、完整信任影响确认、安装/卸载消息、受管副本卸载确认和真实状态刷新。
