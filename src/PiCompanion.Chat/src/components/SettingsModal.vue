<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { UiButton, UiDialog, UiInput, UiSelect, UiSwitch } from '@/components/ui'
import type { UiSelectOption } from '@/components/ui/UiSelect.vue'
import { coerceThinkingLevel } from '@/utils/thinkingLevels'
import { useI18n } from '@/i18n'
import type {
  PiCompanionSettings,
  PiCustomProviderInfo,
  PiOAuthLoginProgress,
  PiProviderInfo,
  SettingsActionCompleted,
  SettingsSnapshot,
  TaskHistoryEntry,
} from '@/types/bridge'

type SettingsTab = 'general' | 'notifications' | 'monitor' | 'tasks' | 'workspace' | 'skills' | 'agent' | 'providers' | 'data' | 'recycle-bin'
type CustomProviderDraft = PiCustomProviderInfo & { apiKey: string }

const props = withDefaults(defineProps<{
  snapshot: SettingsSnapshot
  action?: SettingsActionCompleted | null
  oauthLoginProgress?: PiOAuthLoginProgress | null
  recycleBinTasks?: TaskHistoryEntry[]
}>(), {
  action: null,
  oauthLoginProgress: null,
  recycleBinTasks: () => [],
})

const emit = defineEmits<{
  close: []
  saveCompanion: [settings: PiCompanionSettings]
  saveAgent: [agent: PiCompanionSettings['agent']]
  reloadPi: []
  refreshModelCatalog: []
  savePiApiKey: [providerId: string, apiKey: string]
  logoutPiProvider: [providerId: string]
  addPiCustomProvider: [provider: PiCustomProviderInfo, apiKey: string, modelsConfigRevision: string | null]
  updatePiCustomProvider: [provider: PiCustomProviderInfo, apiKey: string, modelsConfigRevision: string | null]
  deletePiCustomProvider: [providerId: string, modelsConfigRevision: string | null]
  openPiLogin: [providerId: string]
  cancelPiLogin: [providerId: string]
  openDataDirectory: []
  openLogDirectory: []
  exportDiagnostics: []
  clearCache: []
  emptyRecycleBin: []
  restoreRecycleTask: [taskId: string]
  deleteRecycleTask: [taskId: string]
  previewAppearance: [appearance: {
    language: PiCompanionSettings['general']['language']
    theme: PiCompanionSettings['general']['theme']
  }]
}>()

const { locale, setLocale, t } = useI18n()

const tabGroups = computed<Array<{
  label: string
  tabs: Array<{ id: SettingsTab; label: string; hint: string }>
}>>(() => [
  {
    label: t('应用'),
    tabs: [
      { id: 'general', label: t('常规'), hint: t('启动 语言 主题 托盘 缩放') },
      { id: 'notifications', label: t('通知'), hint: t('完成 失败 等待操作 声音 后台') },
      { id: 'monitor', label: t('任务监视器'), hint: t('位置 置顶 收起 动画') },
    ],
  },
  {
    label: t('工作流'),
    tabs: [
      { id: 'tasks', label: t('任务'), hint: t('最近任务 数量 AI 标题 总结 模型') },
      { id: 'workspace', label: t('工作区'), hint: t('权限 Git 文件变更 自动刷新') },
    ],
  },
  {
    label: t('数据'),
    tabs: [
      { id: 'data', label: t('存储与诊断'), hint: t('日志 清理 目录 缓存 诊断') },
      { id: 'recycle-bin', label: t('回收站'), hint: t('删除 恢复 永久删除 清空') },
    ],
  },
  {
    label: 'PI',
    tabs: [
      { id: 'agent', label: 'Agent', hint: t('模型 推理 压缩 重试 权限 Runtime') },
      { id: 'providers', label: 'Provider', hint: t('账号 API Key OAuth 登录 Pi') },
    ],
  },
])
const tabs = computed(() => tabGroups.value.flatMap(group => group.tabs))

const activeTab = ref<SettingsTab>('general')
const search = ref('')
const draft = ref<PiCompanionSettings>(cloneSettings(props.snapshot.values))
const providerSearch = ref('')
const recycleSearch = ref('')
const recycleStatus = ref('all')
const selectedProviderId = ref('')
const apiKey = ref('')
const providerModelSearch = ref('')
const savingApiKeyProviderId = ref('')
const loggingOutProviderId = ref('')
const deletingCustomProviderId = ref('')
const creatingCustomProvider = ref(false)
const editingCustomProviderId = ref('')
const addingCustomProvider = ref(false)
const pendingCustomProviderId = ref('')
const customProviderIdTouched = ref(false)
const customProviderError = ref('')
const customProviderDraft = ref<CustomProviderDraft>(createCustomProviderDraft())
const loggingInProviderId = ref('')
const pendingHeaderAction = ref<'save' | 'reload' | null>(null)
const modelCatalogRefreshing = ref(false)
const companionSaveState = ref<'idle' | 'saving' | 'saved' | 'error'>('idle')
const companionSaveMessage = ref('')
let companionSaveTimer = 0
let companionSaveInFlight = false
let companionSaveDirty = false
let suppressCompanionSave = false
const maintenanceConfirmation = ref<'cache' | 'recycle-bin' | null>(null)
const recycleDeleteTarget = ref<TaskHistoryEntry | null>(null)
const customProviderDeleteTarget = ref<PiCustomProviderInfo | null>(null)
const firstInput = ref<InstanceType<typeof UiInput> | null>(null)
const confirmationCancelButton = ref<InstanceType<typeof UiButton> | null>(null)

watch(() => props.snapshot.values, async value => {
  suppressCompanionSave = true
  if (!companionSaveDirty && !companionSaveInFlight) {
    draft.value.general = cloneSettings(value).general
    draft.value.monitor = cloneSettings(value).monitor
    draft.value.tasks = cloneSettings(value).tasks
    draft.value.notifications = cloneSettings(value).notifications
    draft.value.dataRetention = cloneSettings(value).dataRetention
    draft.value.modelVisibility = cloneSettings(value).modelVisibility
  }
  if (pendingHeaderAction.value !== 'save') {
    draft.value.agent = cloneSettings(value).agent
  }
  ensureConcreteModel()
  await nextTick()
  suppressCompanionSave = false
}, { deep: true })

watch(() => draft.value.general.language, language => setLocale(language), { immediate: true })
watch(
  [() => draft.value.general.language, () => draft.value.general.theme],
  ([language, theme]) => emit('previewAppearance', { language, theme }),
  { immediate: true },
)

watch(
  [() => draft.value.general, () => draft.value.monitor, () => draft.value.tasks, () => draft.value.notifications, () => draft.value.dataRetention, () => draft.value.modelVisibility],
  () => {
    if (suppressCompanionSave) return
    companionSaveDirty = true
    companionSaveState.value = 'saving'
    companionSaveMessage.value = ''
    scheduleCompanionSave()
  },
  { deep: true },
)

watch(() => props.snapshot.pi.providers, providers => {
  if (creatingCustomProvider.value) return
  if (!providers.some(provider => provider.id === selectedProviderId.value)) {
    selectedProviderId.value = providers.find(provider => provider.configured)?.id ?? providers[0]?.id ?? ''
  }
}, { immediate: true })

watch([() => props.snapshot.pi.providers, () => props.snapshot.pi.customProviders], () => {
  if (!pendingCustomProviderId.value) return
  if (!props.snapshot.pi.providers.some(provider => provider.id === pendingCustomProviderId.value)) return
  selectedProviderId.value = pendingCustomProviderId.value
  pendingCustomProviderId.value = ''
  creatingCustomProvider.value = false
  editingCustomProviderId.value = ''
  addingCustomProvider.value = false
  customProviderDraft.value.apiKey = ''
}, { deep: true })

const visibleGroups = computed(() => {
  const query = search.value.trim().toLocaleLowerCase()
  if (!query) return tabGroups.value
  return tabGroups.value
    .map(group => ({
      ...group,
      tabs: group.label.toLocaleLowerCase().includes(query)
        ? group.tabs
        : group.tabs.filter(tab => `${tab.label} ${tab.hint}`.toLocaleLowerCase().includes(query)),
    }))
    .filter(group => group.tabs.length > 0)
})

const hiddenModelReferences = computed(() => new Set(draft.value.modelVisibility.hiddenModelReferences))
const visibleModelReferences = computed(() => new Set(props.snapshot.pi.models
  .map(model => `${model.provider}/${model.id}`)
  .filter(reference => !hiddenModelReferences.value.has(reference))))
const visibleModels = computed(() => props.snapshot.pi.models.filter(
  model => visibleModelReferences.value.has(`${model.provider}/${model.id}`),
))
const modelOptions = computed<UiSelectOption[]>(() => visibleModels.value.map(model => ({
  value: `${model.provider}/${model.id}`,
  label: model.name,
  group: providerName(model.provider),
  tooltip: modelTooltip(model),
})))

const languageOptions = computed<UiSelectOption[]>(() => [
  { value: 'zh-CN', label: t('简体中文') },
  { value: 'en-US', label: t('英语') },
])
const themeOptions = computed<UiSelectOption[]>(() => [
  { value: 'system', label: t('跟随系统') },
  { value: 'light', label: t('浅色') },
  { value: 'dark', label: t('深色') },
])
const conversationDetailOptions = computed<UiSelectOption[]>(() => [
  { value: 'summary', label: t('摘要') },
  { value: 'normal', label: t('标准') },
  { value: 'verbose', label: t('详细') },
])
const logLevelOptions = computed<UiSelectOption[]>(() => [
  { value: 'error', label: t('错误') },
  { value: 'warning', label: t('警告') },
  { value: 'information', label: t('信息') },
  { value: 'debug', label: t('调试') },
])
const monitorPositionOptions = computed<UiSelectOption[]>(() => [
  { value: 'last-position', label: t('上次关闭时位置') },
  { value: 'top-left', label: t('左上角') },
  { value: 'top-right', label: t('右上角') },
  { value: 'bottom-left', label: t('左下角') },
  { value: 'bottom-right', label: t('右下角') },
])
const completionBehaviorOptions = computed<UiSelectOption[]>(() => [
  { value: 'keep-expanded', label: t('保持任务监视器展开') },
  { value: 'collapse-monitor', label: t('收起任务监视器') },
  { value: 'show-chat', label: t('打开智能体对话') },
])
const recentTaskSubtitleOptions = computed<UiSelectOption[]>(() => [
  { value: 'workspace', label: t('工作区名称') },
  { value: 'latest-run', label: t('最近一轮状态') },
])
const localQueueDelayOptions = computed<UiSelectOption[]>(() => [
  { value: '0', label: t('不等待') },
  { value: '15', label: t('15 秒') },
  { value: '30', label: t('30 秒') },
  { value: '60', label: t('1 分钟') },
])
const gitAutoRefreshOptions = computed<UiSelectOption[]>(() => [
  { value: '0', label: t('不自动刷新') },
  { value: '5', label: t('每 5 秒') },
  { value: '10', label: t('每 10 秒') },
  { value: '30', label: t('每 30 秒') },
  { value: '60', label: t('每 60 秒') },
])
const retentionOptions = computed<UiSelectOption[]>(() => [
  { value: '0', label: t('永久保留') },
  { value: '7', label: t('7 天') },
  { value: '30', label: t('30 天') },
  { value: '90', label: t('90 天') },
])
const permissionModeOptions = computed<UiSelectOption[]>(() => [
  { value: 'read-only', label: t('只读') },
  { value: 'standard', label: t('标准访问'), tooltip: t('可以直接修改工作区内的普通文件；执行命令、敏感操作或访问其他位置时会先征求你的同意。') },
])
function updateLocalQueueDelay(value: string) {
  draft.value.tasks.autoStartLocalQueueDelaySeconds = Number(value) as 0 | 15 | 30 | 60
}

function updateGitAutoRefresh(value: string) {
  draft.value.general.gitAutoRefreshSeconds = Number(value) as 0 | 5 | 10 | 30 | 60
}
const customProviderApiOptions: UiSelectOption[] = [
  { value: 'openai-completions', label: 'OpenAI Chat Completions' },
  { value: 'openai-responses', label: 'OpenAI Responses' },
  { value: 'anthropic-messages', label: 'Anthropic Messages' },
  { value: 'google-generative-ai', label: 'Google Generative AI' },
]
const customProviderCredentialOptions = computed<UiSelectOption[]>(() => [
  { value: 'api-key', label: 'API Key' },
  { value: 'local', label: t('无需认证（本地服务）') },
])
const recycleStatusOptions = computed<UiSelectOption[]>(() => [
  { value: 'all', label: t('全部状态') },
  { value: 'completed', label: t('已完成') },
  { value: 'stopped', label: t('已停止') },
  { value: 'failed', label: t('失败') },
])

const selectedModel = computed(() => props.snapshot.pi.models.find(
  model => `${model.provider}/${model.id}` === draft.value.agent.defaultModel,
))

const thinkingLevels = computed(() => selectedModel.value?.thinkingLevels.length
  ? selectedModel.value.thinkingLevels
  : ['low', 'medium', 'high'])
const thinkingOptions = computed<UiSelectOption[]>(() => thinkingLevels.value.map(level => ({
  value: level,
  label: thinkingLabel(level),
})))

const filteredProviders = computed(() => {
  const query = providerSearch.value.trim().toLocaleLowerCase()
  const providers = [...props.snapshot.pi.providers]
  const customProviderIds = new Set(props.snapshot.pi.customProviders.map(provider => provider.id))
  const providerGroup = (provider: PiProviderInfo) => provider.configured ? 0 : customProviderIds.has(provider.id) ? 1 : 2
  providers.sort((left, right) => providerGroup(left) - providerGroup(right) || left.name.localeCompare(right.name))
  if (!query) return providers
  return providers.filter(provider => `${provider.name} ${provider.id}`.toLocaleLowerCase().includes(query))
})
const filteredRecycleBinTasks = computed(() => {
  const query = recycleSearch.value.trim().toLocaleLowerCase('zh-CN')
  return props.recycleBinTasks.filter((task) => {
    if (query && ![task.title, task.workingDirectory, task.summary, task.scopeKind === 'GeneralChat' ? t('直接对话') : '']
      .some(value => value.toLocaleLowerCase('zh-CN').includes(query))) return false
    if (recycleStatus.value === 'all') return true
    if (recycleStatus.value === 'completed') return task.status === 'Completed'
    if (recycleStatus.value === 'stopped') return task.status === 'Interrupted'
    return task.status === 'Failed'
  })
})

const selectedProvider = computed(() => props.snapshot.pi.providers.find(
  provider => provider.id === selectedProviderId.value,
) ?? null)
const selectedCustomProvider = computed(() => props.snapshot.pi.customProviders.find(
  provider => provider.id === selectedProviderId.value,
) ?? null)
const selectedProviderModels = computed(() => props.snapshot.pi.models.filter(
  model => model.provider === selectedProviderId.value,
))
const selectedProviderSupportsWebSearch = computed(() =>
  selectedProvider.value ? providerSupportsWebSearch(selectedProvider.value) : false,
)
const filteredProviderModels = computed(() => {
  const query = providerModelSearch.value.trim().toLocaleLowerCase()
  if (!query) return selectedProviderModels.value
  return selectedProviderModels.value.filter(model => `${model.name} ${model.id}`.toLocaleLowerCase().includes(query))
})
const selectedProviderVisibleCount = computed(() => selectedProviderModels.value.filter(
  model => visibleModelReferences.value.has(`${model.provider}/${model.id}`),
).length)

watch([() => props.snapshot.pi.models, () => draft.value.modelVisibility.hiddenModelReferences], () => {
  ensureConcreteModel()
}, { deep: true, immediate: true })
const oauthLoginPhase = computed(() => {
  if (!selectedProvider.value) return 'idle'
  if (props.oauthLoginProgress?.providerId === selectedProvider.value.id) return props.oauthLoginProgress.phase
  return loggingInProviderId.value === selectedProvider.value.id ? 'opening' : 'idle'
})

watch(() => draft.value.agent.defaultModel, () => {
  const next = coerceThinkingLevel(draft.value.agent.defaultThinkingLevel, thinkingLevels.value)
  if (next) draft.value.agent.defaultThinkingLevel = next
})

watch(activeTab, async tab => {
  if (tab !== 'providers') return
  await nextTick()
  firstInput.value?.focus()
})

watch(() => props.action, action => {
  if (!action) return
  if (action.operation === 'companion-auto-save') {
    companionSaveInFlight = false
    if (action.succeeded) {
      companionSaveState.value = companionSaveDirty ? 'saving' : 'saved'
      companionSaveMessage.value = action.message
      if (companionSaveDirty) scheduleCompanionSave(0)
    } else {
      companionSaveDirty = false
      companionSaveState.value = 'error'
      companionSaveMessage.value = action.message
      restoreCompanionSnapshot()
    }
    return
  }
  if (savingApiKeyProviderId.value) {
    if (action.succeeded) apiKey.value = ''
    savingApiKeyProviderId.value = ''
  }
  loggingOutProviderId.value = ''
  deletingCustomProviderId.value = ''
  if (addingCustomProvider.value && !action.succeeded) {
    addingCustomProvider.value = false
    pendingCustomProviderId.value = ''
    customProviderError.value = action.message
  }
  if (!props.oauthLoginProgress) loggingInProviderId.value = ''
  pendingHeaderAction.value = null
  modelCatalogRefreshing.value = false
})

watch(() => props.oauthLoginProgress, progress => {
  loggingInProviderId.value = progress?.providerId ?? ''
}, { deep: true })

watch(() => selectedProvider.value?.configured, configured => {
  if (configured && selectedProvider.value?.id === savingApiKeyProviderId.value) {
    apiKey.value = ''
    savingApiKeyProviderId.value = ''
  }
})

function providerName(providerId: string) {
  return props.snapshot.pi.providers.find(provider => provider.id === providerId)?.name ?? providerId
}

function providerModelCount(providerId: string) {
  return props.snapshot.pi.customProviders.find(provider => provider.id === providerId)?.models.length
    ?? props.snapshot.pi.models.filter(model => model.provider === providerId).length
}

function cloneSettings(settings: PiCompanionSettings): PiCompanionSettings {
  // Vue Test Utils and WebView2 both expose props through proxies. A settings
  // snapshot is plain JSON data, so a JSON round-trip is the safest clone here.
  return JSON.parse(JSON.stringify(settings)) as PiCompanionSettings
}

function scheduleCompanionSave(delay = 350) {
  if (companionSaveTimer) window.clearTimeout(companionSaveTimer)
  if (companionSaveInFlight) return
  companionSaveTimer = window.setTimeout(() => flushCompanionSave(), delay)
}

function flushCompanionSave(force = false) {
  if (companionSaveTimer) window.clearTimeout(companionSaveTimer)
  companionSaveTimer = 0
  if (!companionSaveDirty || (companionSaveInFlight && !force)) return
  companionSaveDirty = false
  companionSaveInFlight = true
  companionSaveState.value = 'saving'
  emit('saveCompanion', cloneSettings(draft.value))
}

async function restoreCompanionSnapshot() {
  suppressCompanionSave = true
  const value = cloneSettings(props.snapshot.values)
  draft.value.general = value.general
  draft.value.monitor = value.monitor
  draft.value.tasks = value.tasks
  draft.value.notifications = value.notifications
  draft.value.dataRetention = value.dataRetention
  draft.value.modelVisibility = value.modelVisibility
  setLocale(value.general.language)
  await nextTick()
  suppressCompanionSave = false
}

function ensureConcreteModel() {
  const references = visibleModels.value.map(model => `${model.provider}/${model.id}`)
  const fallback = references[0] ?? ''
  if (!references.includes(draft.value.agent.defaultModel)) draft.value.agent.defaultModel = fallback
  const currentMetadataModel = draft.value.tasks.aiMetadataModel
    || draft.value.tasks.aiSummaryModel
    || draft.value.tasks.aiTitleModel
  const metadataModel = references.includes(currentMetadataModel)
    ? currentMetadataModel
    : draft.value.agent.defaultModel
  draft.value.tasks.aiMetadataModel = metadataModel
  draft.value.tasks.aiTitleModel = metadataModel
  draft.value.tasks.aiSummaryModel = metadataModel
}

function selectTab(tab: SettingsTab) {
  activeTab.value = tab
  search.value = ''
}

function selectProvider(provider: PiProviderInfo) {
  creatingCustomProvider.value = false
  editingCustomProviderId.value = ''
  customProviderError.value = ''
  selectedProviderId.value = provider.id
  apiKey.value = ''
  providerModelSearch.value = ''
}

function providerSupportsWebSearch(provider: PiProviderInfo) {
  return provider.capabilities?.includes('web-search') === true
}

function createCustomProviderDraft(): CustomProviderDraft {
  return {
    id: '',
    name: '',
    baseUrl: '',
    api: 'openai-completions',
    credentialMode: 'api-key',
    apiKey: '',
    models: [{
      id: '',
      name: '',
      reasoning: false,
      imageInput: false,
      contextWindow: 128000,
      maxTokens: 16384,
    }],
  }
}

function beginCustomProviderCreation() {
  customProviderDraft.value = createCustomProviderDraft()
  customProviderIdTouched.value = false
  customProviderError.value = ''
  providerModelSearch.value = ''
  selectedProviderId.value = ''
  editingCustomProviderId.value = ''
  creatingCustomProvider.value = true
}

function beginCustomProviderEdit() {
  if (!selectedCustomProvider.value || addingCustomProvider.value) return
  customProviderDraft.value = {
    ...JSON.parse(JSON.stringify(selectedCustomProvider.value)) as PiCustomProviderInfo,
    apiKey: '',
  }
  customProviderIdTouched.value = true
  customProviderError.value = ''
  providerModelSearch.value = ''
  editingCustomProviderId.value = selectedCustomProvider.value.id
  creatingCustomProvider.value = true
}

function cancelCustomProviderCreation() {
  if (addingCustomProvider.value) return
  const editedProviderId = editingCustomProviderId.value
  creatingCustomProvider.value = false
  editingCustomProviderId.value = ''
  customProviderError.value = ''
  selectedProviderId.value = editedProviderId
    || props.snapshot.pi.providers.find(provider => provider.configured)?.id
    || props.snapshot.pi.providers[0]?.id
    || ''
}

function updateCustomProviderName(event: Event) {
  const value = (event.target as HTMLInputElement).value
  customProviderDraft.value.name = value
  if (!customProviderIdTouched.value) customProviderDraft.value.id = slugifyProviderId(value)
  customProviderError.value = ''
}

function updateCustomProviderId(event: Event) {
  customProviderIdTouched.value = true
  customProviderDraft.value.id = (event.target as HTMLInputElement).value.toLocaleLowerCase().replace(/\s+/gu, '-')
  customProviderError.value = ''
}

function slugifyProviderId(value: string) {
  return value.trim().toLocaleLowerCase()
    .normalize('NFKD')
    .replace(/[^a-z0-9._-]+/gu, '-')
    .replace(/^-+|-+$/gu, '')
    .slice(0, 64)
}

function addCustomProviderModel() {
  customProviderDraft.value.models.push({
    id: '',
    name: '',
    reasoning: false,
    imageInput: false,
    contextWindow: 128000,
    maxTokens: 16384,
  })
}

function removeCustomProviderModel(index: number) {
  if (customProviderDraft.value.models.length === 1) return
  customProviderDraft.value.models.splice(index, 1)
}

function normalizeIntegerDown(value: number, minimum: number, maximum: number, step = 1) {
  if (value === null || value === undefined || String(value).trim() === '') return Number.NaN
  const numericValue = Number(value)
  if (!Number.isFinite(numericValue)) return numericValue
  const clampedValue = Math.min(Math.max(Math.floor(numericValue), minimum), maximum)
  return minimum + Math.floor((clampedValue - minimum) / step) * step
}

function normalizeCustomProviderModelLimits(provider: CustomProviderDraft) {
  for (const model of provider.models) {
    model.contextWindow = normalizeIntegerDown(model.contextWindow, 1024, 10_000_000, 1024)
    model.maxTokens = normalizeIntegerDown(model.maxTokens, 1, Number(model.contextWindow))
  }
}

function submitCustomProvider() {
  if (addingCustomProvider.value) return
  const provider = customProviderDraft.value
  normalizeCustomProviderModelLimits(provider)
  const error = validateCustomProvider(provider)
  if (error) {
    customProviderError.value = error
    return
  }

  const normalized: PiCustomProviderInfo = {
    id: provider.id.trim().toLocaleLowerCase(),
    name: provider.name.trim(),
    baseUrl: provider.baseUrl.trim().replace(/\/+$/u, ''),
    api: provider.api,
    credentialMode: provider.credentialMode,
    models: provider.models.map(model => ({
      id: model.id.trim(),
      name: model.name.trim() || model.id.trim(),
      reasoning: model.reasoning,
      imageInput: model.imageInput,
      contextWindow: Number(model.contextWindow),
      maxTokens: Number(model.maxTokens),
      supportsDeveloperRole: model.supportsDeveloperRole,
    })),
  }
  addingCustomProvider.value = true
  pendingCustomProviderId.value = normalized.id
  customProviderError.value = ''
  if (editingCustomProviderId.value) {
    emit('updatePiCustomProvider', normalized, provider.apiKey.trim(), props.snapshot.pi.modelsConfigRevision)
  } else {
    emit('addPiCustomProvider', normalized, provider.apiKey.trim(), props.snapshot.pi.modelsConfigRevision)
  }
}

function validateCustomProvider(provider: CustomProviderDraft) {
  if (!provider.name.trim()) return t('请输入 Provider 名称。')
  const id = provider.id.trim().toLocaleLowerCase()
  if (!/^[a-z0-9][a-z0-9._-]{0,63}$/u.test(id)) return t('Provider ID 只能使用小写字母、数字、点、短横线和下划线。')
  if (id !== editingCustomProviderId.value && props.snapshot.pi.providers.some(item => item.id === id)) return t('这个 Provider ID 已经存在。')
  try {
    const url = new URL(provider.baseUrl.trim())
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return t('Base URL 只支持 http:// 或 https://。')
  } catch {
    return t('请输入有效的 Base URL。')
  }
  const original = props.snapshot.pi.customProviders.find(item => item.id === editingCustomProviderId.value)
  const canPreserveApiKey = original?.credentialMode === 'api-key'
  if (provider.credentialMode === 'api-key' && !provider.apiKey.trim() && !canPreserveApiKey) return t('请输入 API Key。')
  if (!provider.models.length) return t('至少添加一个模型。')
  const ids = new Set<string>()
  for (const [index, model] of provider.models.entries()) {
    const modelId = model.id.trim()
    if (!modelId) return t('请输入模型 {index} 的 ID。', { index: index + 1 })
    if (/\s/u.test(modelId)) return t('模型 ID “{id}”不能包含空格。', { id: modelId })
    if (ids.has(modelId)) return t('模型 ID “{id}”重复。', { id: modelId })
    ids.add(modelId)
    if (!Number.isInteger(Number(model.contextWindow)) || Number(model.contextWindow) < 1024) return t('模型 {id} 的上下文窗口无效。', { id: modelId })
    if (!Number.isInteger(Number(model.maxTokens)) || Number(model.maxTokens) < 1 || Number(model.maxTokens) > Number(model.contextWindow)) return t('模型 {id} 的最大输出 Token 无效。', { id: modelId })
  }
  return ''
}

function saveEnabledModelSet(references: Set<string>) {
  if (!references.size) return
  draft.value.modelVisibility.hiddenModelReferences = props.snapshot.pi.models
    .map(model => `${model.provider}/${model.id}`)
    .filter(reference => !references.has(reference))
}

function toggleProviderModel(reference: string) {
  const references = new Set(visibleModelReferences.value)
  if (references.has(reference)) references.delete(reference)
  else references.add(reference)
  saveEnabledModelSet(references)
}

function showAllProviderModels() {
  const references = new Set(visibleModelReferences.value)
  selectedProviderModels.value.forEach(model => references.add(`${model.provider}/${model.id}`))
  saveEnabledModelSet(references)
}

function hideAllProviderModels() {
  const references = new Set(visibleModelReferences.value)
  selectedProviderModels.value.forEach(model => references.delete(`${model.provider}/${model.id}`))
  saveEnabledModelSet(references)
}

function formatContextWindow(tokens: number) {
  if (tokens >= 1_000_000) return `${Number((tokens / 1_000_000).toFixed(1))}M`
  if (tokens >= 1_000) return `${Number((tokens / 1_000).toFixed(1))}K`
  return tokens.toLocaleString(locale.value)
}

function closeSettings() {
  flushCompanionSave(true)
  emit('close')
}

onBeforeUnmount(() => {
  if (companionSaveTimer) window.clearTimeout(companionSaveTimer)
  flushCompanionSave(true)
})

function saveApiKey() {
  const value = apiKey.value.trim()
  if (!selectedProvider.value || !value) return
  savingApiKeyProviderId.value = selectedProvider.value.id
  emit('savePiApiKey', selectedProvider.value.id, value)
}

function beginOauthLogin() {
  if (!selectedProvider.value || loggingInProviderId.value) return
  loggingInProviderId.value = selectedProvider.value.id
  emit('openPiLogin', selectedProvider.value.id)
}

function cancelOauthLogin() {
  if (!selectedProvider.value || !loggingInProviderId.value) return
  emit('cancelPiLogin', selectedProvider.value.id)
}

function logoutProvider() {
  if (!selectedProvider.value || loggingOutProviderId.value) return
  loggingOutProviderId.value = selectedProvider.value.id
  emit('logoutPiProvider', selectedProvider.value.id)
}

async function requestCustomProviderDelete() {
  if (!selectedCustomProvider.value || deletingCustomProviderId.value) return
  customProviderDeleteTarget.value = selectedCustomProvider.value
  await nextTick()
  confirmationCancelButton.value?.focus()
}

function saveAgentSettings() {
  if (pendingHeaderAction.value) return
  const agent = cloneSettings(draft.value).agent
  agent.steeringMode = 'one-at-a-time'
  agent.followUpMode = 'one-at-a-time'
  agent.compactionReserveTokens = clampInteger(agent.compactionReserveTokens, 1024, 262144, 16384)
  agent.compactionKeepRecentTokens = clampInteger(agent.compactionKeepRecentTokens, 1024, 262144, 20000)
  agent.retryMaxRetries = clampInteger(agent.retryMaxRetries, 0, 20, 3)
  agent.retryBaseDelayMilliseconds = clampInteger(agent.retryBaseDelayMilliseconds, 100, 300000, 2000)
  agent.retryMaxDelayMilliseconds = clampInteger(agent.retryMaxDelayMilliseconds, 0, 3600000, 60000)
  draft.value.agent = agent
  pendingHeaderAction.value = 'save'
  emit('saveAgent', agent)
}

function clampInteger(value: number, minimum: number, maximum: number, fallback: number) {
  return Number.isFinite(value) ? Math.max(minimum, Math.min(maximum, Math.round(value))) : fallback
}

function adjustUiScale(delta: number) {
  draft.value.general.uiScalePercent = Math.max(50, Math.min(200, draft.value.general.uiScalePercent + delta))
}

function resetUiScale() {
  draft.value.general.uiScalePercent = 100
}

function reloadPi() {
  if (pendingHeaderAction.value || modelCatalogRefreshing.value) return
  pendingHeaderAction.value = 'reload'
  emit('reloadPi')
}

function refreshModelCatalog() {
  if (pendingHeaderAction.value || modelCatalogRefreshing.value) return
  modelCatalogRefreshing.value = true
  emit('refreshModelCatalog')
}

function confirmMaintenance() {
  if (customProviderDeleteTarget.value) {
    deletingCustomProviderId.value = customProviderDeleteTarget.value.id
    emit('deletePiCustomProvider', customProviderDeleteTarget.value.id, props.snapshot.pi.modelsConfigRevision)
    customProviderDeleteTarget.value = null
    return
  }
  if (recycleDeleteTarget.value) {
    emit('deleteRecycleTask', recycleDeleteTarget.value.id)
    recycleDeleteTarget.value = null
    return
  }
  if (maintenanceConfirmation.value === 'cache') emit('clearCache')
  if (maintenanceConfirmation.value === 'recycle-bin') emit('emptyRecycleBin')
  maintenanceConfirmation.value = null
}

async function requestRecycleDelete(task: TaskHistoryEntry) {
  recycleDeleteTarget.value = task
  await nextTick()
  confirmationCancelButton.value?.focus()
}

function closeMaintenanceConfirmation() {
  maintenanceConfirmation.value = null
  recycleDeleteTarget.value = null
  customProviderDeleteTarget.value = null
}

async function requestMaintenanceConfirmation(action: 'cache' | 'recycle-bin') {
  maintenanceConfirmation.value = action
  await nextTick()
  confirmationCancelButton.value?.focus()
}

function handleEscape() {
  if (maintenanceConfirmation.value || recycleDeleteTarget.value || customProviderDeleteTarget.value) {
    closeMaintenanceConfirmation()
    return
  }
  closeSettings()
}

function formatRecycleDate(task: TaskHistoryEntry) {
  const timestamp = task.deletedAt ?? task.updatedAt
  return new Intl.DateTimeFormat(locale.value, { year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(timestamp))
}

function thinkingLabel(level: string) {
  return ({ off: 'None', minimal: 'Minimal', low: 'Low', medium: 'Medium', high: 'High', xhigh: 'Xhigh', max: 'Max' } as Record<string, string>)[level] ?? level
}

function modelTooltip(model: SettingsSnapshot['pi']['models'][number]) {
  return [
    t('上下文窗口：{count} tokens', { count: model.contextWindow.toLocaleString(locale.value) }),
    t('推理：{value}', { value: t(model.reasoning ? '支持' : '不支持') }),
    t('图像输入：{value}', { value: t(model.input.includes('image') ? '支持' : '不支持') }),
  ].join('\n')
}

function authLabel(provider: PiProviderInfo) {
  const custom = props.snapshot.pi.customProviders.find(item => item.id === provider.id)
  if (custom?.credentialMode === 'local') return t('无需认证')
  if (!provider.configured) return t('未配置')
  if (provider.authType === 'oauth') return t('OAuth 已登录')
  if (provider.authType === 'api_key') return t('API Key 已配置')
  if (provider.authType === 'configuration') return t('配置凭据')
  return provider.authSource ? t('环境凭据 · {source}', { source: provider.authSource }) : t('环境凭据')
}
</script>

<template>
  <UiDialog
    :title="tabs.find(tab => tab.id === activeTab)?.label ?? t('设置')"
    overlay-class="settings-backdrop"
    content-class="settings-modal"
    @close="closeSettings"
    @keydown.esc.stop.prevent="handleEscape"
  >
      <UiButton class="settings-close" type="button" :aria-label="t('关闭设置')" :title="t('关闭设置')" @click="closeSettings">×</UiButton>

      <aside class="settings-sidebar">
        <label class="settings-search">
          <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5" /><path d="m16 16 4 4" /></svg>
          <UiInput v-model="search" type="search" :placeholder="t('搜索设置')" :aria-label="t('搜索设置')" />
        </label>

        <nav class="settings-nav" :aria-label="t('设置分类')">
          <section v-for="group in visibleGroups" :key="group.label" class="settings-nav-group">
            <p class="settings-group-title">{{ group.label }}</p>
            <UiButton
              v-for="tab in group.tabs"
              :key="tab.id"
              type="button"
              :class="{ active: activeTab === tab.id }"
              @click="selectTab(tab.id)"
            >
              <span class="settings-nav-icon" aria-hidden="true">
                <svg v-if="tab.id === 'general'" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3" /><path d="M19 12a7 7 0 0 0-.1-1l2-1.5-2-3.4-2.4 1A7 7 0 0 0 15 6l-.4-2.5h-4L10 6a7 7 0 0 0-1.5 1.1l-2.4-1-2 3.4 2 1.5a7 7 0 0 0 0 2l-2 1.5 2 3.4 2.4-1A7 7 0 0 0 10 18l.5 2.5h4L15 18a7 7 0 0 0 1.5-1.1l2.4 1 2-3.4-2-1.5a7 7 0 0 0 .1-1Z" /></svg>
                <svg v-else-if="tab.id === 'notifications'" viewBox="0 0 24 24"><path d="M6 17h12l-1.5-2.5V10a4.5 4.5 0 0 0-9 0v4.5zM10 20h4" /></svg>
                <svg v-else-if="tab.id === 'monitor'" viewBox="0 0 24 24"><path d="M3 12h4l2.2-5 4.2 10 2.2-5H21" /></svg>
                <svg v-else-if="tab.id === 'tasks'" viewBox="0 0 24 24"><path d="M8 4h8M9 3v3h6V3M6 5h12v16H6z" /><path d="M9 11h6M9 15h6" /></svg>
                <svg v-else-if="tab.id === 'workspace'" viewBox="0 0 24 24"><path d="M3 7h7l2 2h9v10H3z" /><path d="M8 14h8M13 11l3 3-3 3" /></svg>
                <svg v-else-if="tab.id === 'skills'" viewBox="0 0 24 24"><path d="M8 4h8v5h4v7h-4v4H8v-4H4V9h4z" /><path d="M10 9h4M9 12h6" /></svg>
                <svg v-else-if="tab.id === 'agent'" viewBox="0 0 24 24"><rect x="5" y="7" width="14" height="11" rx="3" /><path d="M9 11h.01M15 11h.01M9 15h6M12 7V4" /></svg>
                <svg v-else-if="tab.id === 'providers'" viewBox="0 0 24 24"><path d="M7 10V7a5 5 0 0 1 10 0v3M5 10h14v10H5z" /><path d="M12 14v2" /></svg>
                <svg v-else-if="tab.id === 'recycle-bin'" viewBox="0 0 24 24"><path d="M5 7h14M9 7V4h6v3M7 7l1 13h8l1-13M10 11v5M14 11v5" /></svg>
                <svg v-else viewBox="0 0 24 24"><ellipse cx="12" cy="6" rx="8" ry="3" /><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></svg>
              </span>
              {{ tab.label }}
            </UiButton>
          </section>
          <p v-if="visibleGroups.length === 0" class="settings-no-results">{{ t('没有匹配的设置') }}</p>
        </nav>

        <div class="settings-runtime-mini" :class="snapshot.pi.available ? 'ready' : 'unavailable'">
          <span></span>
          <div><strong>Pi Runtime</strong><small>{{ snapshot.pi.available ? `v${snapshot.pi.version}` : t('不可用') }}</small></div>
          <UiButton
            class="runtime-refresh"
            type="button"
            :disabled="pendingHeaderAction !== null || modelCatalogRefreshing"
            :aria-busy="pendingHeaderAction === 'reload'"
            :aria-label="t(pendingHeaderAction === 'reload' ? '正在重新加载 Pi 本地状态' : '重新加载 Pi 本地状态')"
            :title="t(pendingHeaderAction === 'reload' ? '正在重新加载' : '只重新读取本地 Runtime、配置与缓存模型')"
            @click="reloadPi"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 8a7 7 0 1 0 1 6" /><path d="M19 4v4h-4" /></svg>
            <span>{{ t(pendingHeaderAction === 'reload' ? '加载中' : '重新加载') }}</span>
          </UiButton>
        </div>
      </aside>

        <div class="settings-content">
          <header class="settings-heading">
            <div>
              <h1 id="settings-title">{{ tabs.find(tab => tab.id === activeTab)?.label }}</h1>
            </div>
          <div
            v-if="activeTab === 'general' || activeTab === 'notifications' || activeTab === 'monitor' || activeTab === 'tasks' || activeTab === 'workspace' || activeTab === 'data'"
            class="settings-auto-save-status"
            :class="companionSaveState"
            :role="companionSaveState === 'error' ? 'alert' : 'status'"
          >
            <i v-if="companionSaveState === 'saving'" aria-hidden="true"></i>
            <span>{{ companionSaveState === 'saving' ? t('保存中') : companionSaveState === 'saved' ? t('已自动保存') : companionSaveState === 'error' ? companionSaveMessage : t('更改会自动保存') }}</span>
          </div>
          <UiButton
            v-if="activeTab === 'agent'"
            class="settings-primary header-action"
            type="button"
            :disabled="pendingHeaderAction !== null || modelCatalogRefreshing"
            :aria-busy="pendingHeaderAction === 'save'"
            @click="saveAgentSettings"
          >
            <i v-if="pendingHeaderAction === 'save'" aria-hidden="true"></i>
            {{ t(pendingHeaderAction === 'save' ? '应用中' : '应用 Pi 设置') }}
          </UiButton>
          <UiButton
            v-else-if="activeTab === 'recycle-bin'"
            class="danger-button header-action"
            type="button"
            :disabled="recycleBinTasks.length === 0"
            @click="requestMaintenanceConfirmation('recycle-bin')"
          >{{ t('清空回收站') }}</UiButton>
        </header>

        <div
          class="settings-scroll"
          :class="{
            'provider-scroll': activeTab === 'providers',
            'recycle-empty-scroll': activeTab === 'recycle-bin' && recycleBinTasks.length === 0,
          }"
        >
          <template v-if="activeTab === 'general'">
            <section class="settings-section">
              <h2>{{ t('启动与窗口') }}</h2>
              <div class="settings-row toggle-row">
                <span><strong>{{ t('登录 Windows 后启动') }}</strong></span>
                <UiSwitch v-model="draft.general.launchAtLogin" :aria-label="t('登录 Windows 后启动')" />
              </div>
              <div class="settings-row toggle-row">
                <span><strong>{{ t('关闭主窗口后保持托盘运行') }}</strong></span>
                <UiSwitch v-model="draft.general.keepRunningInTray" :aria-label="t('关闭主窗口后保持托盘运行')" />
              </div>
            </section>
            <section class="settings-section">
              <h2>{{ t('外观') }}</h2>
              <div class="settings-row">
                <span><strong>{{ t('界面缩放') }}</strong></span>
                <div class="scale-stepper" role="group" :aria-label="t('界面缩放')">
                  <UiButton type="button" :aria-label="t('缩小界面')" :disabled="draft.general.uiScalePercent <= 50" @click="adjustUiScale(-10)">−</UiButton>
                  <UiButton class="scale-value" type="button" :aria-label="t('恢复默认界面缩放')" :title="t('恢复 100%')" @click="resetUiScale">{{ draft.general.uiScalePercent }}%</UiButton>
                  <UiButton type="button" :aria-label="t('放大界面')" :disabled="draft.general.uiScalePercent >= 200" @click="adjustUiScale(10)">+</UiButton>
                </div>
              </div>
              <div class="settings-row"><span><strong>{{ t('语言') }}</strong></span><UiSelect v-model="draft.general.language" :ariaLabelText="t('语言')" :options="languageOptions" /></div>
              <div class="settings-row"><span><strong>{{ t('主题') }}</strong></span><UiSelect v-model="draft.general.theme" :ariaLabelText="t('主题')" :options="themeOptions" /></div>
              <div class="settings-row">
                <span><strong>{{ t('对话显示风格') }}</strong><small>{{ t('控制思考过程与工具调用在对话中的展开程度。') }}</small></span>
                <UiSelect v-model="draft.general.conversationDetailLevel" :ariaLabelText="t('对话详情级别')" :options="conversationDetailOptions" />
              </div>
            </section>
          </template>

          <template v-else-if="activeTab === 'notifications'">
            <section class="settings-section">
              <h2>{{ t('任务通知') }}</h2>
              <div class="settings-row toggle-row"><span><strong>{{ t('任务完成') }}</strong></span><UiSwitch v-model="draft.notifications.notifyOnCompletion" :aria-label="t('任务完成通知')" /></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('任务失败或停止') }}</strong></span><UiSwitch v-model="draft.notifications.notifyOnFailure" :aria-label="t('任务失败通知')" /></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('等待操作') }}</strong><small>{{ t('任务需要你授权或回答问题时提醒你。') }}</small></span><UiSwitch v-model="draft.notifications.notifyWhenAttentionRequired" :aria-label="t('等待操作通知')" /></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('提醒方式') }}</h2>
              <div class="settings-row toggle-row"><span><strong>{{ t('播放提示音') }}</strong></span><UiSwitch v-model="draft.notifications.playSound" :aria-label="t('播放提示音')" /></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('仅在后台提醒') }}</strong><small>{{ t('你正在查看应用时不发送通知，避免重复打扰。') }}</small></span><UiSwitch v-model="draft.notifications.onlyWhenAppIsInBackground" :aria-label="t('仅在后台提醒')" /></div>
            </section>
          </template>

          <template v-else-if="activeTab === 'monitor'">
            <section class="settings-section">
              <h2>{{ t('显示') }}</h2>
              <div class="settings-row"><span><strong>{{ t('应用启动时出现位置') }}</strong></span><UiSelect v-model="draft.monitor.position" :ariaLabelText="t('任务监视器应用启动时出现位置')" :options="monitorPositionOptions" /></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('应用启动时显示任务监视器') }}</strong></span><UiSwitch v-model="draft.monitor.showOnStartup" :aria-label="t('应用启动时显示任务监视器')" /></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('始终置顶') }}</strong></span><UiSwitch v-model="draft.monitor.alwaysOnTop" :aria-label="t('任务监视器始终置顶')" /></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('展开与收起') }}</h2>
              <div class="settings-row"><span><strong>{{ t('自动收起时间') }}</strong><small>{{ t('鼠标移开后等待多久收起任务监视器；设为 0 时保持展开。') }}</small></span><span class="number-field"><UiInput v-model.number="draft.monitor.autoCollapseSeconds" type="number" min="0" max="300" :aria-label="t('自动收起时间（秒）')" /> {{ t('秒') }}</span></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('启用动画') }}</strong></span><UiSwitch v-model="draft.monitor.animationsEnabled" :aria-label="t('启用任务监视器动画')" /></div>
            </section>
          </template>

          <template v-else-if="activeTab === 'tasks'">
            <section class="settings-section">
              <h2>{{ t('最近任务') }}</h2>
              <div class="settings-row">
                <span><strong>{{ t('显示数量') }}</strong></span>
                <span class="number-field"><UiInput v-model.number="draft.tasks.recentTaskCount" type="number" min="1" max="20" step="1" :aria-label="t('最近任务显示数量')" /> {{ t('项') }}</span>
              </div>
              <div class="settings-row">
                <span><strong>{{ t('标题下方显示') }}</strong></span>
                <UiSelect v-model="draft.tasks.recentTaskSubtitle" :ariaLabelText="t('最近任务副信息')" :options="recentTaskSubtitleOptions" />
              </div>
            </section>
            <section class="settings-section">
              <h2>{{ t('AI 标题与总结') }}</h2>
              <div class="settings-row toggle-row">
                <span><strong>{{ t('AI 生成任务标题') }}</strong><small>{{ t('首次运行开始后，自动生成一个便于查找的简短任务标题。') }}</small></span>
                <UiSwitch v-model="draft.tasks.aiTitleEnabled" :aria-label="t('AI 生成任务标题')" />
              </div>
              <div class="settings-row toggle-row">
                <span><strong>{{ t('AI 生成任务总结') }}</strong><small>{{ t('每轮运行结束后自动提炼结果；关闭后不再生成，已有总结仍会保留并显示。') }}</small></span>
                <UiSwitch v-model="draft.tasks.aiSummaryEnabled" :aria-label="t('AI 生成任务总结')" />
              </div>
              <div class="settings-row">
                <span><strong>{{ t('生成模型') }}</strong><small>{{ t('只用于生成标题和总结，不会改变任务本身使用的模型。') }}</small></span>
                <UiSelect v-model="draft.tasks.aiMetadataModel" :ariaLabelText="t('标题与总结生成模型')" :options="modelOptions" :disabled="!draft.tasks.aiTitleEnabled && !draft.tasks.aiSummaryEnabled" searchable :searchPlaceholder="t('搜索模型或 Provider')" />
              </div>
            </section>
            <section class="settings-section">
              <h2>{{ t('任务完成') }}</h2>
              <div class="settings-row">
                <span><strong>{{ t('任务结束后的行为') }}</strong></span>
                <UiSelect v-model="draft.tasks.completionBehavior" :ariaLabelText="t('任务结束后的行为')" :options="completionBehaviorOptions" />
              </div>
            </section>
            <section class="settings-section">
              <h2>{{ t('本地待发送区') }}</h2>
              <div class="settings-row toggle-row">
                <span><strong>{{ t('自动开始下一个待发送任务') }}</strong><small>{{ t('当前任务成功完成后，自动开始待发送区中的第一项。') }}</small></span>
                <UiSwitch v-model="draft.tasks.autoStartLocalQueueEnabled" :aria-label="t('自动开始下一个待发送任务')" />
              </div>
              <div class="settings-row">
                <span><strong>{{ t('开始前等待') }}</strong><small>{{ t('倒计时期间可以在本地待发送区取消本次自动开始。') }}</small></span>
                <UiSelect
                  :model-value="String(draft.tasks.autoStartLocalQueueDelaySeconds)"
                  :ariaLabelText="t('自动开始等待时间')"
                  :options="localQueueDelayOptions"
                  :disabled="!draft.tasks.autoStartLocalQueueEnabled"
                  @update:model-value="updateLocalQueueDelay"
                />
              </div>
            </section>
          </template>

          <template v-else-if="activeTab === 'workspace'">
            <section class="settings-section">
              <h2>{{ t('工作区默认值') }}</h2>
              <div class="settings-row">
                <span><strong>{{ t('新任务默认权限') }}</strong><small>{{ t('决定新任务能否直接修改工作区文件。发送前仍可单独调整；完全访问只会按任务开启。') }}</small></span>
                <UiSelect v-model="draft.tasks.permissionMode" :ariaLabelText="t('新任务默认权限模式')" :options="permissionModeOptions" />
              </div>
              <div class="settings-row toggle-row">
                <span><strong>{{ t('默认展开文件变更') }}</strong><small>{{ t('有文件改动时自动展开变更列表，方便立即查看改了什么。') }}</small></span>
                <UiSwitch v-model="draft.tasks.fileChangesExpandedByDefault" :aria-label="t('默认展开文件变更')" />
              </div>
            </section>
            <section class="settings-section">
              <h2>Git</h2>
              <div class="settings-row">
                <span><strong>{{ t('自动刷新 Git 状态') }}</strong><small>{{ t('让侧栏及时显示其他工具或窗口产生的分支与文件变更。') }}</small></span>
                <UiSelect
                  :model-value="String(draft.general.gitAutoRefreshSeconds)"
                  :ariaLabelText="t('Git 自动刷新间隔')"
                  :options="gitAutoRefreshOptions"
                  @update:model-value="updateGitAutoRefresh"
                />
              </div>
            </section>
          </template>

          <template v-else-if="activeTab === 'agent'">
            <section class="settings-section">
              <h2>{{ t('配置') }}</h2>
              <div v-if="!snapshot.pi.available" class="pi-unavailable"><strong>{{ t('无法读取 Pi Runtime') }}</strong><span>{{ snapshot.pi.error }}</span></div>
              <div class="settings-row agent-model-row">
                <span><strong>{{ t('默认模型与推理等级') }}</strong><small>{{ t('作为新任务的默认选择，发送前仍可调整；推理等级越高通常耗时越长。') }}</small></span>
                <div class="agent-model-inputs">
                  <UiSelect v-model="draft.agent.defaultModel" :ariaLabelText="t('默认模型')" :options="modelOptions" searchable :searchPlaceholder="t('搜索模型或 Provider')" />
                  <UiSelect v-model="draft.agent.defaultThinkingLevel" :ariaLabelText="t('默认推理等级')" :options="thinkingOptions" />
                </div>
              </div>
              <div class="settings-row toggle-row"><span><strong>{{ t('自动压缩上下文') }}</strong><small>{{ t('对话内容接近上限时自动整理较早内容，减少任务因内容过长而中断。') }}</small></span><UiSwitch v-model="draft.agent.autoCompact" :aria-label="t('自动压缩上下文')" /></div>
              <div class="settings-row toggle-row"><span><strong>{{ t('自动重试') }}</strong><small>{{ t('遇到临时网络或服务问题时自动再试，减少手动重发。') }}</small></span><UiSwitch v-model="draft.agent.autoRetry" :aria-label="t('自动重试')" /></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('上下文压缩策略') }}</h2>
              <div class="settings-row"><span><strong>{{ t('预留 Token') }}</strong><small>{{ t('上下文剩余到此数量时开始整理；数值越大，越早触发。') }}</small></span><span class="number-field"><UiInput v-model.number="draft.agent.compactionReserveTokens" type="number" min="1024" max="262144" step="1024" :disabled="!draft.agent.autoCompact" :aria-label="t('压缩预留 Token')" /></span></div>
              <div class="settings-row"><span><strong>{{ t('保留最近 Token') }}</strong><small>{{ t('决定整理后原样保留多少最近对话；数值越大，保留越多。') }}</small></span><span class="number-field"><UiInput v-model.number="draft.agent.compactionKeepRecentTokens" type="number" min="1024" max="262144" step="1024" :disabled="!draft.agent.autoCompact" :aria-label="t('压缩保留最近 Token')" /></span></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('重试策略') }}</h2>
              <div class="settings-row"><span><strong>{{ t('最大重试次数') }}</strong><small>{{ t('一次请求失败后最多自动重试的次数。') }}</small></span><span class="number-field"><UiInput v-model.number="draft.agent.retryMaxRetries" type="number" min="0" max="20" step="1" :disabled="!draft.agent.autoRetry" :aria-label="t('最大重试次数')" /></span></div>
              <div class="settings-row"><span><strong>{{ t('基础等待时间') }}</strong><small>{{ t('首次重试前的等待时间，后续会指数增加。') }}</small></span><span class="number-field"><UiInput v-model.number="draft.agent.retryBaseDelayMilliseconds" type="number" min="100" max="300000" step="100" :disabled="!draft.agent.autoRetry" :aria-label="t('重试基础等待时间')" /> ms</span></div>
              <div class="settings-row"><span><strong>{{ t('最大等待时间') }}</strong><small>{{ t('两次重试之间最多等待多久；设为 0 表示不限制。') }}</small></span><span class="number-field"><UiInput v-model.number="draft.agent.retryMaxDelayMilliseconds" type="number" min="0" max="3600000" step="1000" :disabled="!draft.agent.autoRetry" :aria-label="t('重试最大等待时间')" /> ms</span></div>
            </section>
            <section class="settings-section">
              <h2>Pi Runtime</h2>
              <div class="runtime-card" :class="snapshot.pi.available ? 'ready' : 'unavailable'">
                <span class="runtime-mark">π</span>
                <div><strong>{{ snapshot.pi.available ? `Pi ${snapshot.pi.version}` : t('Pi Runtime 不可用') }}</strong><small :title="snapshot.pi.runtimePath ?? snapshot.pi.error ?? ''">{{ snapshot.pi.runtimePath ?? snapshot.pi.error }}</small></div>
                <em>{{ t(snapshot.pi.available ? '已连接' : '需检查') }}</em>
              </div>
            </section>
          </template>

          <template v-else-if="activeTab === 'providers'">
            <section class="provider-layout">
              <div class="provider-list">
                <div class="provider-toolbar">
                  <label class="provider-search"><UiInput ref="firstInput" v-model="providerSearch" type="search" :placeholder="t('搜索 Pi Provider')" :aria-label="t('搜索 Pi Provider')" /></label>
                  <UiButton class="provider-add" type="button" :aria-label="t('添加自定义 Provider')" :title="t('添加自定义 Provider')" @click="beginCustomProviderCreation">
                    <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14" /></svg>
                  </UiButton>
                </div>
                <div class="provider-items">
                  <UiButton v-for="provider in filteredProviders" :key="provider.id" type="button" :class="{ active: provider.id === selectedProviderId }" @click="selectProvider(provider)">
                    <span><strong>{{ provider.name }}</strong><small>{{ provider.id }}</small></span>
                    <span class="provider-list-meta">
                      <small v-if="!provider.configured && snapshot.pi.customProviders.some(item => item.id === provider.id)">{{ t('自定义') }}</small>
                      <small v-else-if="providerModelCount(provider.id)">{{ providerModelCount(provider.id) }}</small>
                      <i v-if="provider.configured"></i>
                    </span>
                  </UiButton>
                </div>
              </div>
              <div v-if="creatingCustomProvider" class="provider-detail custom-provider-editor">
                <div class="custom-provider-heading">
                  <h2>{{ t(editingCustomProviderId ? '编辑自定义 Provider' : '添加自定义 Provider') }}</h2>
                </div>
                <form class="custom-provider-form" novalidate @submit.prevent="submitCustomProvider">
                  <section class="custom-provider-section">
                    <h3>{{ t('基本信息') }}</h3>
                    <div class="custom-provider-grid">
                      <label><span>{{ t('名称') }}</span><UiInput :value="customProviderDraft.name" maxlength="80" :placeholder="t('例如：公司模型网关')" @input="updateCustomProviderName" /></label>
                      <label><span>Provider ID</span><UiInput :value="customProviderDraft.id" :disabled="Boolean(editingCustomProviderId)" maxlength="64" spellcheck="false" placeholder="company-gateway" @input="updateCustomProviderId" /></label>
                      <label class="wide"><span>Base URL</span><UiInput v-model="customProviderDraft.baseUrl" type="url" spellcheck="false" placeholder="https://api.example.com/v1" @input="customProviderError = ''" /></label>
                      <label><span>{{ t('API 类型') }}</span><UiSelect v-model="customProviderDraft.api" :ariaLabelText="t('自定义 Provider API 类型')" :options="customProviderApiOptions" /></label>
                      <label><span>{{ t('认证方式') }}</span><UiSelect v-model="customProviderDraft.credentialMode" :ariaLabelText="t('自定义 Provider 认证方式')" :options="customProviderCredentialOptions" /></label>
                      <label v-if="customProviderDraft.credentialMode === 'api-key'" class="wide"><span>API Key</span><UiInput v-model="customProviderDraft.apiKey" type="password" autocomplete="off" :placeholder="t(editingCustomProviderId ? '留空则保留现有 API Key' : '保存到 Pi auth.json')" @input="customProviderError = ''" /></label>
                      <div v-else class="custom-local-note wide"><strong>{{ t('本地免认证') }}</strong><small>{{ t('用于 Ollama、LM Studio 或局域网中的兼容服务，不需要 API Key。') }}</small></div>
                    </div>
                  </section>

                  <section class="custom-provider-section custom-model-editor">
                    <header><h3>{{ t('模型') }}</h3><UiButton type="button" @click="addCustomProviderModel">{{ t('添加模型') }}</UiButton></header>
                    <article v-for="(model, index) in customProviderDraft.models" :key="index" class="custom-model-card">
                      <header><strong>{{ t('模型 {index}', { index: index + 1 }) }}</strong><UiButton type="button" :disabled="customProviderDraft.models.length === 1" :aria-label="t('移除模型 {index}', { index: index + 1 })" @click="removeCustomProviderModel(index)">{{ t('移除') }}</UiButton></header>
                      <div class="custom-model-grid">
                        <label><span>{{ t('模型 ID') }}</span><UiInput v-model="model.id" maxlength="200" spellcheck="false" placeholder="model-id" @input="customProviderError = ''" /></label>
                        <label><span>{{ t('显示名称') }}</span><UiInput v-model="model.name" maxlength="120" :placeholder="t('留空则使用模型 ID')" /></label>
                        <label><span>{{ t('上下文窗口') }}</span><UiInput v-model.number="model.contextWindow" type="number" min="1024" max="10000000" step="1024" /></label>
                        <label><span>{{ t('最大输出 Token') }}</span><UiInput v-model.number="model.maxTokens" type="number" min="1" :max="model.contextWindow" step="1" /></label>
                      </div>
                      <div class="custom-model-capabilities">
                        <UiSwitch v-model="model.reasoning" class="custom-model-capability" size="sm"><span>{{ t('支持推理') }}</span></UiSwitch>
                        <UiSwitch v-model="model.imageInput" class="custom-model-capability" size="sm"><span>{{ t('支持图像输入') }}</span></UiSwitch>
                      </div>
                    </article>
                  </section>

                  <p v-if="customProviderError" class="custom-provider-error" role="alert">{{ customProviderError }}</p>
                  <footer class="custom-provider-actions">
                    <UiButton class="settings-secondary" type="button" :disabled="addingCustomProvider" @click="cancelCustomProviderCreation">{{ t('取消') }}</UiButton>
                    <UiButton class="settings-primary provider-save-key" type="submit" :disabled="addingCustomProvider">
                      <i v-if="addingCustomProvider" aria-hidden="true"></i>
                      {{ t(addingCustomProvider ? '正在验证并保存' : editingCustomProviderId ? '保存更改' : '添加 Provider') }}
                    </UiButton>
                  </footer>
                </form>
              </div>
              <div v-else-if="selectedProvider" class="provider-detail">
                <div class="provider-title">
                  <div>
                    <h2>{{ selectedProvider.name }}</h2>
                    <code>{{ selectedProvider.id }}</code>
                    <span
                      v-if="selectedProviderSupportsWebSearch"
                      class="provider-web-search-badge"
                      :class="{ available: selectedProvider.configured }"
                      :title="t('此 Provider 的部分模型支持原生网络搜索，具体可用性取决于所选模型、API 版本和账号权限；Pi Companion 不保证所有模型均可使用。')"
                    >{{ t(selectedProvider.configured ? '自带网络搜索' : '支持网络搜索') }}</span>
                  </div>
                  <div class="provider-title-actions">
                    <UiButton v-if="selectedCustomProvider" class="provider-edit" type="button" @click="beginCustomProviderEdit">{{ t('编辑') }}</UiButton>
                    <UiButton
                      v-if="selectedCustomProvider"
                      class="provider-delete"
                      type="button"
                      :disabled="Boolean(deletingCustomProviderId)"
                      @click="requestCustomProviderDelete"
                    >{{ t(deletingCustomProviderId === selectedProvider.id ? '正在删除' : '删除操作') }}</UiButton>
                    <span v-if="selectedCustomProvider" class="custom-provider-badge">{{ t('自定义') }}</span>
                    <span class="provider-status" :class="{ configured: selectedProvider.configured }">{{ authLabel(selectedProvider) }}</span>
                    <UiButton
                      v-if="selectedProvider.configured"
                      class="provider-logout"
                      type="button"
                      :disabled="Boolean(loggingOutProviderId)"
                      :aria-busy="loggingOutProviderId === selectedProvider.id"
                      @click="logoutProvider"
                    >
                      <i v-if="loggingOutProviderId === selectedProvider.id" aria-hidden="true"></i>
                      {{ t(loggingOutProviderId === selectedProvider.id ? '退出中' : '退出') }}
                    </UiButton>
                  </div>
                </div>
                <form v-if="selectedProvider.supportsApiKey && !selectedProvider.configured" class="provider-key-form" @submit.prevent="saveApiKey">
                  <label>API Key<UiInput v-model="apiKey" type="password" autocomplete="off" :placeholder="t('输入后保存到 Pi')" /></label>
                  <UiButton class="settings-primary provider-save-key" type="submit" :disabled="!apiKey.trim() || savingApiKeyProviderId === selectedProvider.id">
                    <i v-if="savingApiKeyProviderId === selectedProvider.id" aria-hidden="true"></i>
                    {{ t(savingApiKeyProviderId === selectedProvider.id ? '等待 Pi' : '保存到 Pi') }}
                  </UiButton>
                </form>
                <div v-if="selectedProvider.supportsOAuth && !selectedProvider.configured" class="provider-oauth">
                  <div><strong>{{ t('订阅 / OAuth') }}</strong><small>{{ t('在浏览器中完成账号授权，返回后这里会自动显示登录状态。') }}</small></div>
                  <div class="provider-login-actions">
                    <UiButton class="settings-secondary provider-login" type="button" :disabled="Boolean(loggingInProviderId)" :aria-busy="oauthLoginPhase === 'opening'" @click="beginOauthLogin">
                      <i v-if="oauthLoginPhase === 'opening'" aria-hidden="true"></i>
                      {{ t(oauthLoginPhase === 'opening' ? '正在打开浏览器' : oauthLoginPhase === 'waiting' ? '等待授权' : '在浏览器中登录') }}
                    </UiButton>
                    <template v-if="oauthLoginPhase === 'waiting'">
                      <UiButton class="settings-secondary" type="button" @click="reloadPi">{{ t('检查状态') }}</UiButton>
                      <UiButton class="text-danger-button" type="button" @click="cancelOauthLogin">{{ t('取消') }}</UiButton>
                    </template>
                  </div>
                </div>
                <section v-if="selectedProvider.configured" class="provider-models-section">
                  <header>
                    <div>
                      <div class="provider-model-title">
                        <h3>{{ t('可用模型') }} <span>({{ selectedProviderModels.length }})</span></h3>
                        <UiButton
                          class="provider-model-refresh"
                          type="button"
                          :disabled="pendingHeaderAction !== null || modelCatalogRefreshing"
                          :aria-busy="modelCatalogRefreshing"
                          :aria-label="t(modelCatalogRefreshing ? '正在联网刷新模型目录' : '联网刷新模型目录')"
                          :title="t('强制联网获取最新模型目录')"
                          @click="refreshModelCatalog"
                        ><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 8a7 7 0 1 0 1 6" /><path d="M19 4v4h-4" /></svg></UiButton>
                      </div>
                      <small class="provider-model-explainer">{{ t('控制模型是否出现在任务的模型选择器中。') }}</small>
                      <small class="provider-model-status">{{ companionSaveState === 'saving' ? t('正在保存到 Companion…') : t('已显示 {count} 个', { count: selectedProviderVisibleCount }) }}</small>
                    </div>
                    <div>
                      <UiButton type="button" :disabled="selectedProviderVisibleCount === 0 || selectedProviderVisibleCount === visibleModelReferences.size" :title="selectedProviderVisibleCount === visibleModelReferences.size ? t('至少需要保留一个显示模型') : undefined" @click="hideAllProviderModels">{{ t('全部隐藏') }}</UiButton>
                      <UiButton type="button" :disabled="selectedProviderVisibleCount === selectedProviderModels.length" @click="showAllProviderModels">{{ t('全部显示') }}</UiButton>
                    </div>
                  </header>
                  <label class="provider-model-search">
                    <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5" /><path d="m16 16 4 4" /></svg>
                    <UiInput v-model="providerModelSearch" type="search" :placeholder="t('筛选模型…')" :aria-label="t('筛选 {provider} 模型', { provider: selectedProvider.name })" />
                  </label>
                  <div v-if="filteredProviderModels.length" class="provider-model-items">
                    <article
                      v-for="model in filteredProviderModels"
                      :key="`${model.provider}/${model.id}`"
                      :class="{ hidden: !visibleModelReferences.has(`${model.provider}/${model.id}`) }"
                      :title="modelTooltip(model)"
                    >
                      <span><strong>{{ model.name }}</strong><small>{{ model.id }}</small></span>
                      <span class="provider-model-meta">
                        <em>{{ formatContextWindow(model.contextWindow) }}</em>
                        <em v-if="model.reasoning">{{ t('推理') }}</em>
                        <em v-if="model.input.includes('image')">{{ t('图像') }}</em>
                      </span>
                      <UiButton
                        type="button"
                        :disabled="visibleModelReferences.has(`${model.provider}/${model.id}`) && visibleModelReferences.size === 1"
                        :aria-label="t('{action}模型 {name}', { action: t(visibleModelReferences.has(`${model.provider}/${model.id}`) ? '隐藏' : '显示模型操作'), name: model.name })"
                        @click="toggleProviderModel(`${model.provider}/${model.id}`)"
                      >
                        <svg v-if="visibleModelReferences.has(`${model.provider}/${model.id}`)" viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z" /><circle cx="12" cy="12" r="2.5" /></svg>
                        <svg v-else viewBox="0 0 24 24" aria-hidden="true"><path d="m4 4 16 16M10.7 6.1A9 9 0 0 1 12 6c6 0 9.5 6 9.5 6a16 16 0 0 1-2.4 3.1M6.2 7.4A16.6 16.6 0 0 0 2.5 12s3.5 6 9.5 6a9 9 0 0 0 3-.5" /></svg>
                      </UiButton>
                    </article>
                  </div>
                  <div v-else class="provider-model-empty">{{ t('没有匹配的模型') }}</div>
                </section>
              </div>
              <div v-else class="provider-empty">{{ t('Pi 暂未返回 Provider 目录。') }}<br />{{ snapshot.pi.error }}</div>
            </section>
          </template>

          <template v-else-if="activeTab === 'data'">
            <section class="settings-section">
              <h2>{{ t('自动清理') }}</h2>
              <div class="settings-row"><span><strong>{{ t('任务历史') }}</strong><small>{{ t('自动永久删除超过期限的已完成、失败或停止任务；当前最近任务会保留。') }}</small></span><UiSelect :model-value="String(draft.dataRetention.taskHistoryDays)" :ariaLabelText="t('任务历史保留期限')" :options="retentionOptions" @update:model-value="draft.dataRetention.taskHistoryDays = Number($event)" /></div>
              <div class="settings-row"><span><strong>{{ t('回收站') }}</strong><small>{{ t('自动永久删除超过期限的回收站任务。') }}</small></span><UiSelect :model-value="String(draft.dataRetention.recycleBinDays)" :ariaLabelText="t('回收站保留期限')" :options="retentionOptions" @update:model-value="draft.dataRetention.recycleBinDays = Number($event)" /></div>
              <div class="settings-row"><span><strong>{{ t('诊断日志') }}</strong><small>{{ t('自动删除超过期限的本地 .log 文件。') }}</small></span><UiSelect :model-value="String(draft.dataRetention.logDays)" :ariaLabelText="t('诊断日志保留期限')" :options="retentionOptions" @update:model-value="draft.dataRetention.logDays = Number($event)" /></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('本地目录') }}</h2>
              <div class="path-row"><span><strong>{{ t('数据目录') }}</strong><code>{{ snapshot.dataDirectory }}</code></span><UiButton class="settings-secondary" type="button" @click="$emit('openDataDirectory')">{{ t('打开') }}</UiButton></div>
              <div class="path-row"><span><strong>{{ t('日志目录') }}</strong><code>{{ snapshot.logDirectory }}</code></span><UiButton class="settings-secondary" type="button" @click="$emit('openLogDirectory')">{{ t('打开') }}</UiButton></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('诊断') }}</h2>
              <div class="settings-row"><span><strong>{{ t('日志级别') }}</strong><small>{{ t('遇到问题时可切换为“调试”来记录更多信息；下次启动后生效。') }}</small></span><UiSelect v-model="draft.general.logLevel" :ariaLabelText="t('日志级别')" :options="logLevelOptions" /></div>
              <div class="action-row"><span><strong>{{ t('导出诊断包') }}</strong><small>{{ t('打包排查问题所需的版本与日志信息，不包含账号密钥。') }}</small></span><UiButton class="settings-secondary" type="button" @click="$emit('exportDiagnostics')">{{ t('导出 ZIP') }}</UiButton></div>
            </section>
            <section class="settings-section">
              <h2>{{ t('维护') }}</h2>
              <div class="action-row"><span><strong>{{ t('清理界面缓存') }}</strong><small>{{ t('界面显示或加载异常时可以尝试；不会删除任务、对话或账号信息。') }}</small></span><UiButton class="settings-secondary" type="button" @click="requestMaintenanceConfirmation('cache')">{{ t('清理缓存') }}</UiButton></div>
            </section>
          </template>

          <template v-else>
            <div v-if="recycleBinTasks.length" class="recycle-content">
              <div class="recycle-controls">
                <label class="recycle-search">
                  <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" /><path d="m13 13 4 4" /></svg>
                  <UiInput v-model="recycleSearch" type="search" :placeholder="t('搜索名称、目录或摘要')" :aria-label="t('搜索回收站任务')" />
                </label>
                <UiSelect v-model="recycleStatus" :ariaLabelText="t('筛选回收站任务状态')" :options="recycleStatusOptions" />
              </div>
              <div v-if="filteredRecycleBinTasks.length" class="settings-recycle-list">
                <article v-for="task in filteredRecycleBinTasks" :key="task.id" class="settings-recycle-item">
                  <span class="recycle-item-copy">
                    <strong>{{ task.title }}</strong>
                    <small>{{ task.summary || (task.scopeKind === 'GeneralChat' ? t('直接对话 · 隔离空间') : task.workingDirectory) }}</small>
                    <time>{{ t('删除于 {date}', { date: formatRecycleDate(task) }) }}</time>
                  </span>
                  <span class="recycle-item-actions">
                    <UiButton class="settings-secondary" type="button" @click="$emit('restoreRecycleTask', task.id)">{{ t('恢复') }}</UiButton>
                    <UiButton class="text-danger-button" type="button" @click="requestRecycleDelete(task)">{{ t('永久删除') }}</UiButton>
                  </span>
                </article>
              </div>
              <div v-else class="settings-recycle-empty compact">
                <span aria-hidden="true">⌕</span>
                <div><strong>{{ t('没有匹配的任务') }}</strong><small>{{ t('尝试修改搜索词或状态筛选。') }}</small></div>
              </div>
            </div>
            <div v-else class="settings-recycle-empty">
              <span aria-hidden="true">✓</span>
              <div><strong>{{ t('回收站为空') }}</strong></div>
            </div>
          </template>
        </div>
      </div>

      <UiDialog
        v-if="maintenanceConfirmation || recycleDeleteTarget || customProviderDeleteTarget"
        :title="t(customProviderDeleteTarget ? '确认删除自定义 Provider' : maintenanceConfirmation === 'cache' ? '确认清理缓存' : recycleDeleteTarget ? '确认永久删除任务' : '确认清空回收站')"
        overlay-class="settings-confirm-backdrop"
        content-class="settings-confirm-dialog"
        alert
        @close="closeMaintenanceConfirmation"
        @keydown.esc.stop.prevent="closeMaintenanceConfirmation"
      >
          <span class="settings-confirm-icon" :class="{ danger: maintenanceConfirmation === 'recycle-bin' || recycleDeleteTarget || customProviderDeleteTarget }" aria-hidden="true">!</span>
          <div>
            <h2>{{ t(customProviderDeleteTarget ? '删除自定义 Provider？' : maintenanceConfirmation === 'cache' ? '清理界面缓存？' : recycleDeleteTarget ? '永久删除这项任务？' : '永久清空回收站？') }}</h2>
            <p v-if="customProviderDeleteTarget">{{ t('“{name}”的模型配置和保存在 Pi auth.json 中的凭据将被删除。此操作无法撤销。', { name: customProviderDeleteTarget.name }) }}</p>
            <p v-else-if="maintenanceConfirmation === 'cache'">{{ t('将清除本机上的界面缓存。任务、对话和账号信息不会受到影响。') }}</p>
            <p v-else-if="recycleDeleteTarget">{{ t('“{title}”的任务、会话和运行记录将被永久删除，此操作无法撤销。', { title: recycleDeleteTarget.title }) }}</p>
            <p v-else>{{ t('回收站中的全部任务、会话和运行记录将被永久删除，此操作无法撤销。') }}</p>
          </div>
          <div class="settings-confirm-actions">
            <UiButton ref="confirmationCancelButton" type="button" @click="closeMaintenanceConfirmation">{{ t('取消') }}</UiButton>
            <UiButton
              type="button"
              :class="maintenanceConfirmation === 'cache' ? 'confirm' : 'danger'"
              @click="confirmMaintenance"
            >{{ t(customProviderDeleteTarget ? '删除操作' : maintenanceConfirmation === 'cache' ? '清理缓存' : recycleDeleteTarget ? '永久删除' : '清空回收站') }}</UiButton>
          </div>
      </UiDialog>
  </UiDialog>
</template>

<style scoped>
:global(.settings-backdrop) {
  position: fixed;
  z-index: 1400;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 32px;
  background: var(--color-overlay-strong);
  backdrop-filter: blur(2px);
}

:global(.settings-modal) {
  position: relative;
  display: grid;
  grid-template-columns: 220px minmax(0, 1fr);
  width: min(1180px, 100%);
  height: min(850px, 100%);
  min-height: 520px;
  overflow: hidden;
  border: 1px solid var(--color-tone-7);
  border-radius: 12px;
  background: var(--color-tone-2);
  box-shadow: 0 30px 90px var(--color-overlay-strong);
}

.settings-close {
  position: absolute;
  z-index: 5;
  top: 8px;
  right: 10px;
  width: 34px;
  height: 34px;
  justify-content: center;
  padding: 0 0 3px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--color-tone-10);
  cursor: pointer;
  font-size: var(--font-size-glyph-lg);
  font-weight: var(--font-weight-light);
  line-height: 1;
  text-align: center;
}
.settings-close:hover { background: var(--color-tone-4); color: var(--color-tone-14); }

.settings-sidebar {
  display: flex;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
  padding: 14px 12px 12px;
  border-right: 1px solid var(--color-tone-7);
  background: var(--color-tone-3);
}

.settings-search, .provider-search {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 38px;
  padding: 0 10px;
  border: 1px solid var(--color-tone-8);
  border-radius: 7px;
  background: var(--color-tone-2);
  color: var(--color-tone-11);
}
.settings-search:focus-within, .provider-search:focus-within { border-color: var(--color-tone-10); color: var(--color-tone-13); }
.settings-search svg { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.7; }
.settings-search input, .provider-search input { min-width: 0; width: 100%; border: 0; outline: 0; background: transparent; color: var(--color-text-primary); font-size: var(--font-size-body-sm); }
.settings-search input::placeholder, .provider-search input::placeholder { color: var(--color-tone-10); }

.settings-nav { display: block; min-height: 0; margin: 16px 0 0; flex: 1; overflow-y: auto; scrollbar-color: var(--color-tone-8) transparent; scrollbar-width: thin; }
.settings-nav-group { display: grid; gap: 2px; }
.settings-nav-group + .settings-nav-group { margin-top: 18px; }
.settings-group-title { margin: 0 9px 7px; color: var(--color-tone-11); font-size: var(--font-size-caption); font-weight: var(--font-weight-semibold); letter-spacing: .065em; }
.settings-nav button {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 9px 10px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--color-tone-13);
  cursor: pointer;
  font-size: var(--font-size-body);
  font-weight: var(--font-weight-medium);
  text-align: left;
}
.settings-nav button:hover { background: var(--color-tone-5); color: var(--color-tone-15); }
.settings-nav button.active { background: var(--color-tone-7); color: var(--color-tone-15); }
.settings-nav-icon { display: grid; width: 18px; height: 18px; flex: none; place-items: center; }
.settings-nav-icon svg { width: 17px; height: 17px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.55; }
.settings-no-results { margin: 12px 10px; color: var(--color-tone-10); font-size: var(--font-size-caption); }

.settings-runtime-mini {
  display: flex;
  align-items: center;
  gap: 9px;
  margin-top: auto;
  padding: 11px 9px 5px;
  border-top: 1px solid var(--color-tone-6);
}
.settings-runtime-mini > span { width: 7px; height: 7px; border-radius: 50%; background: var(--color-danger); }
.settings-runtime-mini.ready > span { background: var(--color-success); }
.settings-runtime-mini div { display: grid; min-width: 0; gap: 2px; }
.settings-runtime-mini strong { color: var(--color-tone-13); font-size: var(--font-size-caption); font-weight: var(--font-weight-semibold); }
.settings-runtime-mini small { color: var(--color-tone-10); font-size: var(--font-size-micro); }
.runtime-refresh {
  display: inline-flex;
  min-height: 29px;
  margin-left: auto;
  padding: 0 8px;
  flex: none;
  align-items: center;
  justify-content: center;
  gap: 5px;
  border: 1px solid var(--color-tone-7);
  border-radius: 6px;
  background: var(--color-tone-4);
  color: var(--color-tone-11);
  cursor: pointer;
  font-size: var(--font-size-micro);
}
.runtime-refresh:hover { border-color: var(--color-tone-9); background: var(--color-tone-5); color: var(--color-tone-14); }
.runtime-refresh:disabled { cursor: default; opacity: .48; }
.runtime-refresh svg { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.7; }
.runtime-refresh[aria-busy="true"] svg { animation: provider-key-spin .7s linear infinite; }

.settings-content { display: grid; min-width: 0; min-height: 0; grid-template-rows: auto minmax(0, 1fr); }
.settings-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 24px;
  min-height: 76px;
  padding: 18px clamp(44px, 5vw, 60px) 16px clamp(20px, 3.5vw, 44px);
  border-bottom: 1px solid var(--color-tone-4);
}
.settings-heading h1 { margin: 0; color: var(--color-tone-15); font-size: var(--font-size-title-lg); font-weight: var(--font-weight-semibold); }
.settings-auto-save-status { display: inline-flex; align-items: center; gap: 7px; color: var(--color-tone-10); font-size: var(--font-size-caption); white-space: nowrap; }
.settings-auto-save-status.saved { color: var(--color-success-emphasis); }
.settings-auto-save-status.error { color: var(--color-danger-text); }
.settings-auto-save-status > i { width: 12px; height: 12px; border: 2px solid var(--color-tone-8); border-top-color: var(--color-tone-12); border-radius: 50%; animation: provider-key-spin .7s linear infinite; }
.settings-scroll { min-height: 0; padding: 26px clamp(20px, 3.5vw, 44px) 70px; overflow-y: auto; scrollbar-color: var(--color-tone-8) transparent; }
.settings-scroll.provider-scroll { overflow: hidden; padding-bottom: 26px; }
.settings-scroll.recycle-empty-scroll { display: grid; padding-block: 26px; place-items: center; }

.settings-section { max-width: 760px; }
.settings-section + .settings-section { margin-top: 34px; padding-top: 30px; border-top: 1px solid var(--color-tone-6); }
.settings-section h2 { margin: 0 0 13px; color: var(--color-tone-14); font-size: var(--font-size-body); font-weight: var(--font-weight-semibold); }
.settings-row, .path-row, .action-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 34px;
  min-height: 58px;
  padding: 10px 0;
}
.settings-row > span:first-child, .path-row > span, .action-row > span { display: grid; min-width: 0; gap: 4px; }
.settings-row strong, .path-row strong, .action-row strong { color: var(--color-tone-14); font-size: var(--font-size-body); font-weight: var(--font-weight-medium); }
.settings-row small, .path-row small, .action-row small { color: var(--color-tone-10); font-size: var(--font-size-caption); line-height: var(--line-height-control); }
.settings-row > .app-select {
  width: 210px;
}
.settings-row > .app-select :deep(.app-select-trigger) { min-height: 35px; padding: 6px 13px 6px 10px; border-color: var(--color-tone-8); border-radius: 7px; background: var(--color-tone-3); color: var(--color-tone-14); font-size: var(--font-size-body-sm); }
.settings-row > .app-select :deep(.app-select-menu) { max-height: 310px; overflow-y: auto; scrollbar-color: var(--color-tone-8) transparent; }
.agent-model-inputs { display: grid; width: 360px; grid-template-columns: minmax(0, 1fr) 128px; gap: 8px; }
.agent-model-inputs :deep(.app-select-trigger) { min-height: 35px; padding: 6px 13px 6px 10px; border-color: var(--color-tone-8); border-radius: 7px; background: var(--color-tone-3); color: var(--color-tone-14); font-size: var(--font-size-body-sm); }
.agent-model-inputs :deep(.app-select-menu) { max-height: 310px; overflow-y: auto; scrollbar-color: var(--color-tone-8) transparent; }

.toggle-row { cursor: default; }

.number-field { display: flex !important; grid-auto-flow: column; align-items: center; gap: 7px !important; color: var(--color-tone-11); font-size: var(--font-size-caption); }
.number-field input { width: 86px; padding: 7px 8px; border: 1px solid var(--color-tone-8); border-radius: 7px; outline: 0; background: var(--color-tone-3); color: var(--color-tone-14); text-align: right; }
.number-field input:focus { border-color: var(--color-tone-10); }
.scale-stepper { display: grid; grid-template-columns: 32px 62px 32px; overflow: hidden; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-3); }
.scale-stepper button { min-height: 33px; justify-content: center; padding: 0; border: 0; background: transparent; color: var(--color-tone-12); cursor: pointer; font-size: var(--font-size-body); text-align: center; }
.scale-stepper button + button { border-left: 1px solid var(--color-tone-7); }
.scale-stepper button:hover:not(:disabled) { background: var(--color-tone-5); color: var(--color-tone-15); }
.scale-stepper button:disabled { cursor: default; opacity: .34; }
.scale-stepper .scale-value { color: var(--color-tone-14); font-size: var(--font-size-body-sm); font-weight: var(--font-weight-medium); }

.settings-primary, .settings-secondary, .danger-button {
  min-height: 33px;
  padding: 6px 12px;
  border: 1px solid var(--color-tone-8);
  border-radius: 7px;
  cursor: pointer;
  font-size: var(--font-size-body-sm);
  white-space: nowrap;
}
.settings-primary { border-color: var(--color-tone-14); background: var(--color-tone-15); color: var(--color-tone-3); font-weight: var(--font-weight-semibold); }
.settings-primary:hover { background: var(--color-tone-16); }
.settings-primary:disabled { cursor: default; opacity: .42; }
.settings-secondary { background: var(--color-tone-4); color: var(--color-tone-13); }
.settings-secondary:hover { border-color: var(--color-tone-10); background: var(--color-tone-5); color: var(--color-tone-15); }
.settings-secondary:disabled { cursor: default; opacity: .48; }
.header-action { display: inline-flex; align-items: center; justify-content: center; gap: 7px; }
.header-action > i { width: 12px; height: 12px; border: 2px solid var(--color-control-ink-muted); border-top-color: var(--color-tone-3); border-radius: 50%; animation: provider-key-spin .7s linear infinite; }
.danger-button { border-color: var(--color-danger-border); background: var(--color-danger-surface); color: var(--color-danger); }
.danger-button:hover { border-color: var(--color-danger-border-strong); background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }
.danger-button:disabled { cursor: default; opacity: .42; }

.pi-unavailable { display: grid; gap: 5px; margin: 0 0 12px; padding: 12px; border: 1px solid var(--color-danger-border); border-radius: 8px; background: var(--color-danger-surface); }
.pi-unavailable strong { color: var(--color-danger-text); font-size: var(--font-size-body-sm); }
.pi-unavailable span { color: var(--color-danger-muted); font-size: var(--font-size-caption); }
.runtime-card { display: grid; grid-template-columns: 42px minmax(0, 1fr) auto; align-items: center; gap: 12px; padding: 14px; border: 1px solid var(--color-tone-8); border-radius: 9px; background: var(--color-tone-3); }
.runtime-card.ready { border-color: var(--color-success-border); }
.runtime-mark { display: grid; width: 40px; height: 40px; place-items: center; border-radius: 9px; background: var(--color-tone-15); color: var(--color-tone-2); font-family: var(--font-family-brand); font-size: var(--font-size-glyph-lg); font-weight: var(--font-weight-bold); }
.runtime-card div { display: grid; min-width: 0; gap: 5px; }
.runtime-card strong { color: var(--color-tone-14); font-size: var(--font-size-body); }
.runtime-card small { overflow: hidden; color: var(--color-tone-10); font-size: var(--font-size-caption); text-overflow: ellipsis; white-space: nowrap; }
.runtime-card em { padding: 4px 7px; border-radius: 99px; background: var(--color-success-surface-emphasis); color: var(--color-success-emphasis); font-size: var(--font-size-micro); font-style: normal; }
.runtime-card.unavailable em { background: var(--color-danger-surface-emphasis); color: var(--color-danger); }

.provider-layout { display: grid; grid-template-columns: 238px minmax(0, 1fr); height: 100%; min-height: 0; overflow: hidden; }
.provider-list { display: flex; min-width: 0; min-height: 0; flex-direction: column; padding-right: 12px; border-right: 1px solid var(--color-tone-7); }
.provider-toolbar { display: grid; grid-template-columns: minmax(0, 1fr) 38px; gap: 7px; margin-bottom: 12px; }
.provider-search { box-sizing: border-box; min-width: 0; min-height: 38px; padding: 6px 10px; }
.provider-add { display: grid; width: 38px; height: 38px; padding: 0; place-items: center; border: 0; border-radius: 7px; background: transparent; color: var(--color-tone-11); cursor: pointer; }
.provider-add:hover { color: var(--color-tone-15); }
.provider-add:focus-visible { outline: 2px solid var(--color-tone-10); outline-offset: -2px; }
.provider-add svg { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-width: 1.8; }
.provider-items { display: grid; min-height: 0; padding-right: 4px; gap: 3px; overflow-y: auto; overscroll-behavior: contain; scrollbar-color: var(--color-tone-8) transparent; scrollbar-width: thin; }
.provider-items > button { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 9px; width: 100%; min-height: 45px; padding: 7px 8px; border: 0; border-radius: 7px; background: transparent; color: var(--color-tone-12); cursor: pointer; text-align: left; }
.provider-items > button:hover, .provider-items > button.active { background: var(--color-tone-5); color: var(--color-tone-15); }
.provider-items > button > span:first-child { display: grid; min-width: 0; gap: 2px; }
.provider-list strong, .provider-list small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.provider-list strong { font-size: var(--font-size-body); font-weight: var(--font-weight-medium); }
.provider-list small { color: var(--color-tone-10); font-size: var(--font-size-caption); }
.provider-list-meta { display: flex; align-items: center; gap: 7px; }
.provider-list-meta small { color: var(--color-tone-10); font-size: var(--font-size-caption); }
.provider-list-meta i { width: 6px; height: 6px; border-radius: 50%; background: var(--color-success); box-shadow: 0 0 0 3px var(--color-success-halo); }
.provider-detail { min-width: 0; padding: 30px 8px 24px 28px; overflow-y: auto; scrollbar-color: var(--color-tone-8) transparent; }
.provider-title { display: flex; align-items: center; justify-content: space-between; gap: 12px 16px; flex-wrap: wrap; }
.provider-title > div:first-child { min-width: min(180px, 100%); flex: 1 1 180px; }
.provider-title h2 { margin: 0 0 4px; color: var(--color-tone-15); font-size: var(--font-size-title-md); }
.provider-title code { color: var(--color-tone-11); font: var(--font-size-body-sm) var(--font-family-mono); }
.provider-web-search-badge { display: inline-flex; margin-left: 9px; padding: 3px 6px; border: 1px solid var(--color-tone-7); border-radius: 99px; background: var(--color-tone-4); color: var(--color-tone-11); font-size: var(--font-size-caption); vertical-align: 1px; }
.provider-web-search-badge.available { border-color: var(--color-success-border); background: var(--color-success-surface-emphasis); color: var(--color-success-emphasis); }
.provider-title-actions { display: flex; max-width: 100%; align-items: center; justify-content: flex-end; gap: 7px; flex-wrap: wrap; margin-left: auto; }
.provider-title-actions > * { white-space: nowrap; }
.provider-edit { padding: 4px 7px; border: 0; border-radius: 5px; background: transparent; color: var(--color-tone-12); cursor: pointer; font-size: var(--font-size-caption); }
.provider-edit:hover { background: var(--color-tone-5); color: var(--color-tone-15); }
.provider-delete { padding: 4px 7px; border: 0; border-radius: 5px; background: transparent; color: var(--color-danger); cursor: pointer; font-size: var(--font-size-caption); }
.provider-delete:hover:not(:disabled) { background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }
.provider-delete:disabled { cursor: default; opacity: .7; }
.custom-provider-badge { padding: 4px 7px; border: 1px solid var(--color-success-border); border-radius: 99px; color: var(--color-success-emphasis); font-size: var(--font-size-caption); }
.provider-status { padding: 5px 8px; border-radius: 99px; background: var(--color-tone-6); color: var(--color-tone-11); font-size: var(--font-size-caption); }
.provider-status.configured { background: var(--color-success-surface-emphasis); color: var(--color-success-emphasis); }
.provider-logout { display: inline-flex; align-items: center; gap: 5px; padding: 4px 7px; border: 0; border-radius: 5px; background: transparent; color: var(--color-danger); cursor: pointer; font-size: var(--font-size-caption); }
.provider-logout:hover { background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }
.provider-logout:disabled { cursor: default; opacity: .7; }
.provider-logout > i { width: 10px; height: 10px; border: 1.5px solid var(--color-danger-border); border-top-color: var(--color-danger-text); border-radius: 50%; animation: provider-key-spin .7s linear infinite; }
.provider-key-form { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: end; gap: 10px; margin-top: 24px; }
.provider-key-form label { display: grid; gap: 7px; color: var(--color-tone-13); font-size: var(--font-size-body-sm); }
.provider-key-form input { min-width: 0; height: 36px; padding: 0 10px; border: 1px solid var(--color-tone-8); border-radius: 7px; outline: 0; background: var(--color-tone-2); color: var(--color-tone-14); }
.provider-key-form input:focus { border-color: var(--color-tone-10); }
.provider-save-key { display: inline-flex; align-items: center; justify-content: center; gap: 7px; }
.provider-save-key > i { width: 12px; height: 12px; border: 2px solid var(--color-control-ink-muted); border-top-color: var(--color-tone-3); border-radius: 50%; animation: provider-key-spin .7s linear infinite; }
.provider-login { display: inline-flex; align-items: center; justify-content: center; gap: 7px; white-space: nowrap; }
.provider-login:disabled { opacity: 1; }
.provider-login > i { width: 12px; height: 12px; border: 2px solid var(--color-tone-9); border-top-color: var(--color-tone-13); border-radius: 50%; animation: provider-key-spin .7s linear infinite; }
.provider-oauth .provider-login-actions { display: flex; align-items: center; justify-content: flex-end; gap: 7px; }
.text-danger-button { padding: 6px 8px; border: 0; border-radius: 5px; background: transparent; color: var(--color-danger); cursor: pointer; font-size: var(--font-size-body-sm); }
.text-danger-button:hover { background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }
@keyframes provider-key-spin { to { transform: rotate(360deg); } }
.provider-oauth { display: flex; align-items: center; justify-content: space-between; gap: 20px; margin-top: 22px; padding: 15px; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-3); }
.provider-oauth div { display: grid; gap: 4px; }
.provider-oauth strong { color: var(--color-tone-13); font-size: var(--font-size-body); }
.provider-oauth small { color: var(--color-tone-10); font-size: var(--font-size-body-sm); line-height: var(--line-height-control); }
.provider-empty { align-self: center; padding: 30px; color: var(--color-tone-10); font-size: var(--font-size-body); line-height: var(--line-height-reading); text-align: center; }
.provider-models-section { margin-top: 28px; padding-top: 25px; border-top: 1px solid var(--color-tone-6); }
.provider-models-section > header { display: flex; align-items: center; justify-content: space-between; gap: 18px; margin-bottom: 12px; }
.provider-models-section > header > div:first-child { display: grid; gap: 4px; }
.provider-model-title { display: flex; align-items: center; gap: 6px; }
.provider-models-section h3 { margin: 0; color: var(--color-tone-14); font-size: var(--font-size-body-lg); font-weight: var(--font-weight-semibold); }
.provider-models-section h3 span { color: var(--color-tone-10); font-weight: var(--font-weight-medium); }
.provider-models-section header small { color: var(--color-tone-10); font-size: var(--font-size-caption); }
.provider-models-section header .provider-model-explainer { max-width: 500px; line-height: var(--line-height-control); }
.provider-models-section header .provider-model-status { color: var(--color-tone-9); }
.provider-models-section > header > div:last-child { display: flex; gap: 5px; }
.provider-models-section > header button { padding: 4px 7px; border: 1px solid var(--color-tone-8); border-radius: 6px; background: var(--color-tone-4); color: var(--color-tone-12); cursor: pointer; font-size: var(--font-size-caption); }
.provider-models-section > header button:hover:not(:disabled) { border-color: var(--color-tone-9); color: var(--color-tone-14); }
.provider-models-section > header .provider-model-refresh { display: grid; width: 24px; height: 24px; padding: 0; place-items: center; border-color: transparent; background: transparent; }
.provider-model-refresh svg { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.7; }
.provider-model-refresh[aria-busy="true"] svg { animation: provider-key-spin .7s linear infinite; }
.provider-models-section button:disabled { cursor: default; opacity: .38; }
.provider-model-search { display: flex; height: 34px; align-items: center; gap: 7px; margin-bottom: 8px; padding: 0 9px; border: 1px solid var(--color-tone-7); border-radius: 7px; background: var(--color-tone-3); color: var(--color-tone-10); }
.provider-model-search:focus-within { border-color: var(--color-tone-9); }
.provider-model-search svg { width: 14px; height: 14px; flex: none; fill: none; stroke: currentColor; stroke-linecap: round; stroke-width: 1.6; }
.provider-model-search input { min-width: 0; width: 100%; padding: 0; border: 0; outline: 0; background: transparent; color: var(--color-tone-14); font-size: var(--font-size-body-sm); }
.provider-model-items { border-top: 1px solid var(--color-tone-6); }
.provider-model-items article { display: grid; grid-template-columns: minmax(0, 1fr) auto 28px; align-items: center; gap: 10px; min-height: 48px; padding: 6px 2px 6px 0; border-bottom: 1px solid var(--color-tone-6); }
.provider-model-items article.hidden { opacity: .44; }
.provider-model-items article > span:first-child { display: grid; min-width: 0; gap: 2px; }
.provider-model-items strong, .provider-model-items small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.provider-model-items strong { color: var(--color-tone-14); font-size: var(--font-size-body); font-weight: var(--font-weight-medium); }
.provider-model-items small { color: var(--color-tone-10); font: var(--font-size-caption) var(--font-family-mono); }
.provider-model-meta { display: flex; align-items: center; justify-content: flex-end; gap: 4px; }
.provider-model-meta em { padding: 3px 5px; border-radius: 4px; background: var(--color-tone-4); color: var(--color-tone-10); font-size: var(--font-size-caption); font-style: normal; white-space: nowrap; }
.provider-model-items article > button { display: grid; width: 28px; height: 28px; padding: 0; place-items: center; border: 0; border-radius: 5px; background: transparent; color: var(--color-tone-11); cursor: pointer; }
.provider-model-items article > button:hover:not(:disabled) { background: var(--color-tone-5); color: var(--color-tone-14); }
.provider-model-items article > button svg { width: 15px; height: 15px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.55; }
.provider-model-empty { padding: 24px 8px; border-top: 1px solid var(--color-tone-6); color: var(--color-tone-10); font-size: var(--font-size-body-sm); text-align: center; }

.custom-provider-editor { padding-top: 25px; }
.custom-provider-heading h2 { margin: 0; color: var(--color-tone-15); font-size: var(--font-size-title-md); }
.custom-provider-form { display: grid; gap: 24px; margin-top: 24px; }
.custom-provider-section { display: grid; gap: 13px; }
.custom-provider-section + .custom-provider-section { padding-top: 22px; border-top: 1px solid var(--color-tone-6); }
.custom-provider-section h3 { margin: 0; color: var(--color-tone-14); font-size: var(--font-size-body); font-weight: var(--font-weight-semibold); }
.custom-provider-grid, .custom-model-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 13px 12px; }
.custom-provider-grid label, .custom-model-grid label { display: grid; min-width: 0; gap: 6px; color: var(--color-tone-12); font-size: var(--font-size-body-sm); }
.custom-provider-grid > .wide { grid-column: 1 / -1; }
.custom-provider-grid label > span, .custom-model-grid label > span { color: var(--color-tone-13); }
.custom-provider-grid input, .custom-model-grid input { box-sizing: border-box; min-width: 0; width: 100%; height: 36px; padding: 0 10px; border: 1px solid var(--color-tone-8); border-radius: 7px; outline: 0; background: var(--color-tone-2); color: var(--color-tone-14); font-size: var(--font-size-body-sm); }
.custom-provider-grid input:focus, .custom-model-grid input:focus { border-color: var(--color-tone-9); }
.custom-provider-grid input:disabled { border-color: var(--color-tone-7); background: var(--color-tone-3); color: var(--color-tone-10); cursor: not-allowed; }
.custom-provider-grid small { color: var(--color-tone-9); font-size: var(--font-size-caption); line-height: var(--line-height-control); }
.custom-provider-grid :deep(.app-select) { width: 100%; }
.custom-local-note { display: grid; gap: 4px; padding: 12px; border: 1px solid var(--color-success-border); border-radius: 7px; background: var(--color-success-surface); }
.custom-local-note strong { color: var(--color-success-emphasis); font-size: var(--font-size-caption); }
.custom-local-note small { color: var(--color-success-muted); font-size: var(--font-size-micro); line-height: var(--line-height-control); }
.custom-model-editor > header { display: flex; align-items: center; justify-content: space-between; gap: 15px; }
.custom-model-editor > header button, .custom-model-card > header button { padding: 4px 8px; border: 1px solid var(--color-tone-8); border-radius: 6px; background: var(--color-tone-4); color: var(--color-tone-12); cursor: pointer; font-size: var(--font-size-caption); }
.custom-model-editor > header button:hover, .custom-model-card > header button:hover:not(:disabled) { border-color: var(--color-tone-9); color: var(--color-tone-15); }
.custom-model-card { display: grid; gap: 13px; padding: 14px; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-3); }
.custom-model-card > header { display: flex; align-items: center; justify-content: space-between; }
.custom-model-card > header strong { color: var(--color-tone-13); font-size: var(--font-size-body-sm); }
.custom-model-card > header button:disabled { cursor: default; opacity: .35; }
.custom-model-capabilities { display: flex; flex-wrap: wrap; gap: 18px; }
.custom-model-capability { display: inline-flex; width: auto; align-items: center; gap: 7px; color: var(--color-tone-12); font-size: var(--font-size-body-sm); line-height: 1; }
.custom-model-capability:has(:deep(.ui-switch[data-state="checked"])) :deep(.ui-switch) { border-color: var(--color-success-emphasis); background: var(--color-success-solid); }
.custom-model-capability:has(:deep(.ui-switch[data-state="checked"])) :deep(.ui-switch-thumb) { background: var(--color-success-text-strong); }
.custom-model-capability:has(:deep(.ui-switch[data-state="checked"])) :deep(.ui-switch-label) { color: var(--color-success-text-strong); }
.custom-model-grid input[type="number"] { appearance: textfield; }
.custom-model-grid input[type="number"]::-webkit-inner-spin-button,
.custom-model-grid input[type="number"]::-webkit-outer-spin-button { margin: 0; appearance: none; }
.custom-provider-error { margin: -8px 0 0; padding: 9px 11px; border: 1px solid var(--color-danger-border); border-radius: 7px; background: var(--color-danger-surface); color: var(--color-danger-text); font-size: var(--font-size-caption); line-height: var(--line-height-control); }
.custom-provider-actions { display: flex; justify-content: flex-end; gap: 8px; padding-top: 18px; border-top: 1px solid var(--color-tone-6); }

.path-row, .action-row { padding: 14px; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-3); }
.path-row + .path-row, .action-row + .action-row { margin-top: 9px; }
.path-row code { overflow: hidden; max-width: 560px; color: var(--color-tone-11); font: var(--font-size-caption) var(--font-family-mono); text-overflow: ellipsis; white-space: nowrap; }
.danger-zone { border-color: var(--color-danger-border); background: var(--color-danger-surface); }

.recycle-content { display: grid; gap: 12px; }
.recycle-controls { display: grid; grid-template-columns: minmax(0, 1fr) 150px; gap: 10px; }
.recycle-search { display: flex; min-width: 0; height: 36px; align-items: center; gap: 8px; padding: 0 10px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-3); color: var(--color-tone-10); }
.recycle-search:focus-within { border-color: var(--color-tone-9); }
.recycle-search svg { width: 15px; height: 15px; flex: none; fill: none; stroke: currentColor; stroke-linecap: round; stroke-width: 1.5; }
.recycle-search input { min-width: 0; width: 100%; padding: 0; border: 0; outline: 0; background: transparent; color: var(--color-tone-14); font-size: var(--font-size-caption); }
.recycle-controls :deep(.app-select) { width: 150px; font-size: var(--font-size-caption); }
.recycle-controls :deep(.app-select-trigger) { min-height: 36px; font-size: var(--font-size-caption); font-weight: var(--font-weight-regular); }
.settings-recycle-item { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 11px; min-height: 72px; padding: 10px 5px; }
.settings-recycle-item + .settings-recycle-item { border-top: 1px solid var(--color-tone-7); }
.settings-recycle-item:hover { background: var(--color-tone-3); }
.recycle-item-copy { display: grid; min-width: 0; gap: 3px; }
.recycle-item-copy strong, .recycle-item-copy small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.recycle-item-copy strong { color: var(--color-tone-14); font-size: var(--font-size-body-sm); font-weight: var(--font-weight-medium); }
.recycle-item-copy small { color: var(--color-tone-10); font-size: var(--font-size-caption); }
.recycle-item-copy time { color: var(--color-tone-9); font-size: var(--font-size-micro); }
.recycle-item-actions { display: flex; align-items: center; gap: 3px; }
.recycle-item-actions .settings-secondary { min-height: 30px; padding: 4px 10px; }
.settings-recycle-empty { display: flex; min-height: 96px; align-items: center; justify-content: center; gap: 11px; color: var(--color-tone-10); }
.recycle-empty-scroll > .settings-recycle-empty { min-height: 0; }
.settings-recycle-empty.compact { min-height: 120px; }
.settings-recycle-empty.compact > span { border-color: var(--color-tone-8); color: var(--color-tone-11); }
.settings-recycle-empty > span { display: grid; width: 27px; height: 27px; place-items: center; border: 1px solid var(--color-success-border); border-radius: 50%; color: var(--color-success-emphasis); font-size: var(--font-size-caption); }
.settings-recycle-empty div { display: grid; gap: 3px; }
.settings-recycle-empty strong { color: var(--color-tone-12); font-size: var(--font-size-body-sm); }
.settings-recycle-empty small { color: var(--color-tone-10); font-size: var(--font-size-caption); }

:global(.settings-confirm-backdrop) { position: absolute; z-index: 20; inset: 0; display: grid; place-items: center; padding: 24px; background: var(--color-overlay); backdrop-filter: blur(2px); }
:global(.settings-confirm-dialog) { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 14px; width: min(440px, 100%); padding: 22px; border: 1px solid var(--color-tone-8); border-radius: 10px; background: var(--color-tone-4); box-shadow: 0 24px 70px var(--color-overlay-strong); }
.settings-confirm-icon { display: grid; width: 34px; height: 34px; place-items: center; border: 1px solid var(--color-warning-border); border-radius: 50%; background: var(--color-warning-surface-emphasis); color: var(--color-warning-text); font-size: var(--font-size-title-md); font-weight: var(--font-weight-bold); }
.settings-confirm-icon.danger { border-color: var(--color-danger-border); background: var(--color-danger-surface-emphasis); color: var(--color-danger-text); }
:global(.settings-confirm-dialog) h2 { margin: 1px 0 7px; color: var(--color-tone-15); font-size: var(--font-size-body-lg); font-weight: var(--font-weight-semibold); }
:global(.settings-confirm-dialog) p { margin: 0; color: var(--color-tone-11); font-size: var(--font-size-body-sm); line-height: var(--line-height-reading); }
.settings-confirm-actions { display: flex; grid-column: 1 / -1; justify-content: flex-end; gap: 8px; margin-top: 8px; }
.settings-confirm-actions button { min-height: 33px; padding: 6px 13px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-13); cursor: pointer; font-size: var(--font-size-body-sm); }
.settings-confirm-actions button:hover { border-color: var(--color-tone-9); color: var(--color-tone-15); }
.settings-confirm-actions button.confirm { border-color: var(--color-tone-14); background: var(--color-tone-15); color: var(--color-tone-3); font-weight: var(--font-weight-semibold); }
.settings-confirm-actions button.danger { border-color: var(--color-danger-border-strong); background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }

@media (max-width: 800px) {
  :global(.settings-backdrop) { padding: 14px; }
  :global(.settings-modal) { grid-template-columns: 176px minmax(0, 1fr); }
  .settings-heading { padding: 24px 48px 18px 24px; }
  .settings-scroll { padding: 22px 24px 50px; }
  .provider-layout { grid-template-columns: 190px minmax(0, 1fr); }
  .provider-detail { padding: 24px 0 20px 22px; }
}

@media (max-width: 700px) {
  :global(.settings-modal) { grid-template-columns: 160px minmax(0, 1fr); }
  .settings-heading { padding: 22px 44px 17px 16px; }
  .settings-scroll { padding: 20px 16px 45px; }
  .provider-layout { display: block; height: auto; overflow: visible; }
  .settings-scroll.provider-scroll { overflow-y: auto; }
  .provider-list { height: 220px; padding-right: 0; padding-bottom: 10px; border-right: 0; border-bottom: 1px solid var(--color-tone-7); }
  .provider-detail { padding: 22px 0 20px; }
  .custom-provider-grid, .custom-model-grid { grid-template-columns: 1fr; }
  .custom-provider-grid > .wide { grid-column: auto; }
}

@media (max-width: 620px) {
  :global(.settings-modal) { grid-template-columns: 142px minmax(0, 1fr); }
  .settings-sidebar { padding-inline: 8px; }
  .settings-nav button { padding-inline: 8px; }
  .settings-group-title { font-size: var(--font-size-micro); }
  .settings-heading { align-items: flex-start; flex-direction: column; gap: 12px; }
  .settings-row, .path-row, .action-row { align-items: flex-start; flex-direction: column; gap: 10px; }
  .settings-row > .app-select { width: 100%; }
  .agent-model-inputs { width: 100%; grid-template-columns: 1fr; }
  .toggle-row { flex-direction: row; }
  .provider-list { height: 190px; }
}
</style>
