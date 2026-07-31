<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { UiButton, UiDialog, UiSelect } from '@/components/ui'
import { connectBridge, postBridgeMessage } from '@/bridge'
import { useTaskStore } from '@/stores/task'
import type {
  BridgeEnvelope,
  ComposerAttachment,
  BeginSkillImportRequest,
  CancelSkillImportRequest,
  ConfirmSkillImportRequest,
  PrepareSkillImportRequest,
  FileChangeEvidence,
  LocalMessageAttachmentsSelected,
  LoadSkillsRequest,
  RemoveSkillInstallationRequest,
  SetWorkspaceTrustDecisionRequest,
  PiModelInfo,
  PiCustomProviderInfo,
  PiThinkingLevel,
  PiOAuthLoginProgress,
  PermissionMode,
  SettingsActionCompleted,
  SettingsSnapshot,
  SessionStatisticsSnapshot,
  SkillsLoaded,
  SkillImportCompleted,
  SkillImportPreparation,
  SkillImportReady,
  SkillImportSource,
  SkillImportSourceInspected,
  SkillImportSourceKind,
  SkillRemovalCompleted,
  SkillWorkspaceTrustCompleted,
  TaskHistoryEntry,
  TaskHistoryPage,
  TaskRunSnapshot,
  TrustSkillWorkspaceRequest,
  TranscriptBlock,
  WorkspaceColorKey,
  WorkspaceDirectoryListing,
  WorkspaceFileEntry,
  WorkspaceFileSearchResult,
  WorkspaceGitAction,
  WorkspaceGitActionCompleted,
  WorkspaceGitCommit,
  WorkspaceGitCommitMessageGenerated,
  WorkspaceGitEntry,
  WorkspaceGitHistorySnapshot,
  WorkspaceGitSnapshot,
  WorkspaceIconKey,
  WorkspaceHistoryEntry,
  WorkspaceTrustDecisionCompleted,
} from '@/types/bridge'
import CommitDiffDialog from '@/components/CommitDiffDialog.vue'
import ComposerPanel from '@/components/ComposerPanel.vue'
import ConversationRun from '@/components/ConversationRun.vue'
import FileDiffDialog from '@/components/FileDiffDialog.vue'
import FeaturePlaceholderView from '@/components/FeaturePlaceholderView.vue'
import LocalMessageEditorDialog from '@/components/LocalMessageEditorDialog.vue'
import SettingsModal from '@/components/SettingsModal.vue'
import SkillManagementModal from '@/components/SkillManagementModal.vue'
import SkillsView from '@/components/SkillsView.vue'
import TaskManagementOverlays from '@/components/TaskManagementOverlays.vue'
import TaskManagementView from '@/components/TaskManagementView.vue'
import WorkspaceLocationMenu from '@/components/WorkspaceLocationMenu.vue'
import WorkspaceSidebar from '@/components/WorkspaceSidebar.vue'
import WorkspaceInspector from '@/components/WorkspaceInspector.vue'
import WorkspacePresentationDialog from '@/components/WorkspacePresentationDialog.vue'
import {
  isComposerCommandName,
  literalComposerMessage,
  parseComposerInvocation,
  type ComposerCommandName,
  type ComposerSkillOption,
} from '@/composerCommands'
import { useAttachmentDrop } from '@/composables/useAttachmentDrop'
import { useAttachmentPaste } from '@/composables/useAttachmentPaste'
import { useSidebarResize } from '@/composables/useSidebarResize'
import { useTaskManagement } from '@/composables/useTaskManagement'
import { coerceThinkingLevel } from '@/utils/thinkingLevels'
import { useI18n } from '@/i18n'
import { applyTheme, clearTheme, resolveTheme, systemThemeQuery } from '@/theme'
import { loadTaskPromptDraft, saveTaskPromptDraft } from '@/utils/taskPromptDrafts'

const store = useTaskStore()
const { locale, setLocale, t } = useI18n()
const prompt = ref('')
const promptDraftTaskId = ref<string | null>(null)
const selectedModel = ref('')
const selectedThinkingLevel = ref<PiThinkingLevel>('high')
const selectedPermissionMode = ref<PermissionMode>('standard')
const fullAccessConfirmationOpen = ref(false)
const settingsOpen = ref(false)
const editingWorkspaceId = ref<string | null>(null)
const hidingWorkspaceId = ref<string | null>(null)
const inspectorCollapsed = ref(window.localStorage.getItem('pi-companion:inspector-collapsed') === 'true')
const inspectorTab = ref<'git' | 'files' | 'context'>('files')
const workspaceDirectoryUpdate = ref<WorkspaceDirectoryListing | null>(null)
const workspaceSearchUpdate = ref<WorkspaceFileSearchResult | null>(null)
const workspaceGitUpdate = ref<WorkspaceGitSnapshot | null>(null)
const workspaceGitHistoryUpdate = ref<WorkspaceGitHistorySnapshot | null>(null)
const workspaceGitHistoryLoading = ref(false)
const workspaceGitActionResult = ref<WorkspaceGitActionCompleted | null>(null)
const workspaceGitPendingAction = ref<WorkspaceGitAction | null>(null)
const workspaceGitCommitMessageResult = ref<WorkspaceGitCommitMessageGenerated | null>(null)
const workspaceGitCommitMessageLoading = ref(false)
const sessionStatisticsUpdate = ref<SessionStatisticsSnapshot | null>(null)
const sessionStatisticsLoading = ref(false)
const sessionStatisticsCache = new Map<string, { update: SessionStatisticsSnapshot; lastSequence: number }>()
const historyHasMore = ref(false)
const historyLoading = ref(false)
const historyLoadedCount = ref(0)
const settingsSnapshot = ref<SettingsSnapshot>(createPreviewSettingsSnapshot())
const appearancePreview = ref<{
  language: SettingsSnapshot['values']['general']['language']
  theme: SettingsSnapshot['values']['general']['theme']
} | null>(null)
const skillsSnapshot = ref<SkillsLoaded | null>(null)
const skillsLoading = ref(false)
const skillsError = ref<string | null>(null)
const skillRemovalResult = ref<SkillRemovalCompleted | null>(null)
const skillRemovalPendingId = ref<string | null>(null)
const skillTrustPendingWorkspaceId = ref<string | null>(null)
const skillTrustResult = ref<SkillWorkspaceTrustCompleted | null>(null)
const skillTrustConfirmationWorkspaceId = ref<string | null>(null)
const workspaceTrustDialogWorkspaceId = ref<string | null>(null)
const workspaceTrustDecisionPending = ref(false)
const pendingWorkspaceRun = ref<{
  type: 'SendPrompt' | 'StartDemo'
  payload: Record<string, unknown>
  clearDraftAfterPost: boolean
} | null>(null)
let workspaceTrustRequestSequence = 0
let workspaceTrustRequestId: string | null = null
const skillImportSource = ref<SkillImportSource | null>(null)
const skillImportPreparation = ref<SkillImportPreparation | null>(null)
const skillImportResult = ref<SkillImportCompleted | null>(null)
const skillImportPhase = ref<'source' | 'target' | 'commit' | null>(null)
const skillImportError = ref<string | null>(null)
const skillManagerContext = ref<{ workspaceId: string | null; directChat: boolean } | null>(null)
const transientNotice = ref<{
  id: string
  message: string
  succeeded: boolean
} | null>(null)
const startupTheme = new URLSearchParams(window.location.search).get('theme')
if (startupTheme === 'dark' || startupTheme === 'light' || startupTheme === 'system') {
  settingsSnapshot.value.values.general.theme = startupTheme
}
const settingsAction = ref<SettingsActionCompleted | null>(null)
const piOAuthLoginProgress = ref<PiOAuthLoginProgress | null>(null)
const systemThemeMedia = typeof window.matchMedia === 'function'
  ? window.matchMedia(systemThemeQuery)
  : null
const systemPrefersLight = ref(systemThemeMedia?.matches ?? false)
const resolvedTheme = computed(() => resolveTheme(
  appearancePreview.value?.theme ?? settingsSnapshot.value.values.general.theme,
  systemPrefersLight.value,
))
const activeLanguage = computed(() =>
  appearancePreview.value?.language ?? settingsSnapshot.value.values.general.language)
const editingLocalMessageId = ref<string | null>(null)
const selectedLocalMessageAttachments = ref<ComposerAttachment[] | null>(null)
let localMessageAttachmentRequestId: string | null = null
let localMessageAttachmentRequestSequence = 0
let workspaceGitHistoryRequestId: string | null = null
let workspaceGitHistoryRequestSequence = 0
let workspaceGitActionRequestId: string | null = null
let workspaceGitActionRequestSequence = 0
let workspaceGitCommitMessageRequestId: string | null = null
let workspaceGitCommitMessageRequestSequence = 0
let skillsRequestId: string | null = null
let skillsRequestSequence = 0
let skillRemovalRequestId: string | null = null
let skillRemovalRequestSequence = 0
let skillImportRequestId: string | null = null
let skillImportRequestSequence = 0
let skillTrustRequestId: string | null = null
let skillTrustRequestSequence = 0
const viewMode = computed(() => settingsSnapshot.value.values.general.conversationDetailLevel)
const visiblePiModels = computed(() => {
  const hidden = new Set(settingsSnapshot.value.values.modelVisibility.hiddenModelReferences)
  return settingsSnapshot.value.pi.models.filter(model => !hidden.has(`${model.provider}/${model.id}`))
})
const selectedModelInfo = computed<PiModelInfo | null>(() =>
  settingsSnapshot.value.pi.models.find(model => `${model.provider}/${model.id}` === selectedModel.value) ?? null)
const selectablePiModels = computed(() => {
  const models = visiblePiModels.value
  const current = selectedModelInfo.value
  if (!store.currentTask || !current || models.some(model => model.provider === current.provider && model.id === current.id)) {
    return models
  }
  return [current, ...models]
})
const visibleRecentTasks = computed(() => {
  const limit = Math.max(1, Math.min(20, settingsSnapshot.value.values.tasks.recentTaskCount))
  return store.recentTasks.slice(0, limit)
})
const selectedHistoryTask = computed(() => {
  const recent = visibleRecentTasks.value
  const current = currentTaskHistoryEntry()
  if (!current || recent.some(task => task.id === current.id)) return null
  return current
})
const editingWorkspace = computed(() =>
  store.workspaces.find(workspace => workspace.id === editingWorkspaceId.value) ?? null)
const hidingWorkspace = computed(() =>
  store.workspaces.find(workspace => workspace.id === hidingWorkspaceId.value) ?? null)
const skillManagerWorkspace = computed(() =>
  store.workspaces.find(workspace => workspace.id === skillManagerContext.value?.workspaceId) ?? null)
const skillTrustConfirmationWorkspace = computed(() => {
  const workspaceId = skillTrustConfirmationWorkspaceId.value
  if (!workspaceId) return null
  const workspace = store.workspaces.find(candidate => candidate.id === workspaceId)
  const trust = skillsSnapshot.value?.workspaceTrust?.find(candidate =>
    candidate.workspaceId === workspaceId)
  if (!workspace && !trust) return null
  return {
    id: workspaceId,
    name: workspace?.name ?? trust?.workspaceName ?? workspaceId,
    path: workspace?.workingDirectory ?? trust?.workspacePath ?? '',
  }
})
const composerWorkspaceId = computed(() => {
  if (isGeneralChat.value) return null
  const directory = store.currentTask?.workingDirectory ?? store.draft?.workingDirectory
  if (!directory) return null
  const normalized = normalizePathForComparison(directory)
  return store.workspaces.find(workspace =>
    normalizePathForComparison(workspace.workingDirectory) === normalized)?.id ?? null
})
const composerSkillOptions = computed<ComposerSkillOption[]>(() => {
  const snapshot = skillsSnapshot.value
  if (!snapshot) return []
  const workspaceId = composerWorkspaceId.value
  return snapshot.skills.flatMap(skill => {
    const effective = skill.variants.flatMap(variant =>
      variant.installations.map(installation => ({ variant, installation })))
      .find(({ variant, installation }) =>
        variant.isAvailable &&
        (isGeneralChat.value
          ? installation.isGloballyEffective
          : Boolean(workspaceId && installation.effectiveWorkspaceIds.includes(workspaceId))))
    if (!effective) return []
    const origin = isGeneralChat.value
      ? effective.installation.origins.find(candidate => candidate.scope === 'global')
      : effective.installation.origins.find(candidate => candidate.workspaceId === workspaceId)
        ?? effective.installation.origins.find(candidate => candidate.scope === 'global')
    return [{
      name: skill.name,
      description: effective.variant.description ?? '',
      location: origin?.scope === 'workspace'
        ? origin.workspaceName || t('工作区')
        : t('全局'),
      manualOnly: effective.variant.disableModelInvocation,
    }]
  })
    .sort((left, right) => left.name.localeCompare(right.name, locale.value))
})
const editingLocalMessage = computed(() =>
  store.currentTask?.localQueuedMessages?.find(message => message.id === editingLocalMessageId.value) ?? null)
const modelOptions = computed(() => {
  const options = selectablePiModels.value.map(model => ({
    value: `${model.provider}/${model.id}`,
    label: model.name,
    group: settingsSnapshot.value.pi.providers.find(provider => provider.id === model.provider)?.name ?? model.provider,
    tooltip: modelTooltip(model),
  }))
  return options
})

function modelTooltip(model: SettingsSnapshot['pi']['models'][number]) {
  return [
    t('上下文窗口：{count} tokens', { count: model.contextWindow.toLocaleString(locale.value) }),
    t('推理：{value}', { value: t(model.reasoning ? '支持' : '不支持') }),
    t('图像输入：{value}', { value: t(model.input.includes('image') ? '支持' : '不支持') }),
  ].join('\n')
}

function modelDisplayName(reference: string) {
  const model = settingsSnapshot.value.pi.models.find(candidate => `${candidate.provider}/${candidate.id}` === reference)
  if (model) return model.name
  return reference.split('/').at(-1) || reference || 'Agent'
}

function normalizePathForComparison(path: string) {
  return path.replace(/[\\/]+$/u, '').replaceAll('/', '\\').toLocaleLowerCase('en-US')
}

function currentTaskHistoryEntry(): TaskHistoryEntry | null {
  const current = store.currentTask
  if (!current) return null
  return store.historyTasks.find(task => task.id === current.id)
    ?? store.recentTasks.find(task => task.id === current.id)
    ?? {
      id: current.id,
      runId: current.runId,
      title: current.title,
      workingDirectory: current.workingDirectory,
      scopeKind: current.scopeKind,
      status: current.status,
      statusText: current.statusText,
      summary: current.summary,
      aiSummaryStatus: current.aiSummaryStatus,
      updatedAt: '',
      deletedAt: null,
    }
}
watch(visiblePiModels, models => {
  const references = models.map(model => `${model.provider}/${model.id}`)
  if (!references.length || references.includes(selectedModel.value)) return
  // Hiding a model controls future selection. Existing tasks keep their recorded
  // model until the user explicitly chooses another one.
  if (store.currentTask) return
  const preferred = settingsSnapshot.value.values.agent.defaultModel
  selectedModel.value = references.includes(preferred) ? preferred : references[0]!
}, { deep: true })
const thinkingLevelOptions = computed(() => {
  if (!selectablePiModels.value.length) return []
  const model = selectedModelInfo.value
  const levels = model?.thinkingLevels.length ? model.thinkingLevels : ['low', 'medium', 'high']
  return levels.map(value => ({ value, label: thinkingLevelLabel(value) }))
})
watch(thinkingLevelOptions, options => {
  const next = coerceThinkingLevel(selectedThinkingLevel.value, options.map(option => option.value))
  if (next && next !== selectedThinkingLevel.value) selectedThinkingLevel.value = next
}, { immediate: true })
const transcript = ref<HTMLElement | null>(null)
const composerPanel = ref<{ focus: () => void } | null>(null)
const recoveryTarget = ref<FileChangeEvidence | null>(null)
const stickTranscriptToBottom = ref(true)
const {
  mainView,
  historySearch,
  historyStatus,
  taskContextMenu,
  renameTaskTarget,
  renameTitle,
  confirmAction,
  confirmDialogTitle,
  confirmDialogDescription,
  openTaskContextMenu,
  closeTaskContextMenu,
  showMainView,
  openRenameTask,
  submitRenameTask,
  requestTaskAction,
  confirmTaskManagementAction,
  restoreTask,
  selectTask,
} = useTaskManagement()
const {
  collapsed: sidebarCollapsed,
  width: sidebarWidth,
  workspaceStyle: sidebarWorkspaceStyle,
  setWidth: setSidebarWidth,
  beginResize: beginSidebarResize,
} = useSidebarResize()
const {
  width: inspectorWidth,
  workspaceStyle: inspectorWorkspaceStyle,
  setWidth: setInspectorWidth,
  beginResize: beginInspectorResize,
} = useSidebarResize({
  collapsed: inspectorCollapsed,
  defaultWidth: 340,
  minimumWidth: 300,
  maximumWidth: 560,
  storageKey: 'pi-companion.inspector-width',
  cssVariable: '--inspector-width',
  edge: 'right',
})
const workspaceStyle = computed(() => ({
  ...sidebarWorkspaceStyle.value,
  ...inspectorWorkspaceStyle.value,
}))
let disconnect: () => void = () => {}
let transcriptScrollFrame = 0
let transientNoticeTimer = 0
let workspaceGitRefreshTimer = 0
let workspaceGitAutoRefreshTimer = 0
let workspaceGitRequestSequence = 0
let workspaceGitRequestId: string | null = null
let sessionStatisticsRefreshTimer = 0
let sessionStatisticsRequestSequence = 0
let sessionStatisticsRequestId: string | null = null
let taskHistoryRequestSequence = 0
let taskHistoryRequestId: string | null = null
let taskHistorySearchTimer = 0
let taskHistoryLoadAllPending = false
let executionDefaultsTimer = 0
let bridgeErrorTimer = 0
let composerReloadPending = false
const directChatSelected = ref(false)
const isGeneralChat = computed(() => store.currentTask
  ? store.currentTask.scopeKind === 'GeneralChat'
  : directChatSelected.value)
const isModeSelected = computed(() =>
  Boolean(store.currentTask || store.draft?.workingDirectory || directChatSelected.value))
const currentDirectory = computed(() => {
  if (store.currentTask) {
    return store.currentTask.scopeKind === 'GeneralChat'
      ? t('直接对话 · 隔离空间')
      : store.currentTask.workingDirectory
  }
  if (directChatSelected.value) return t('直接对话 · 隔离空间')
  return store.draft?.workingDirectory ?? t('尚未选择模式')
})
const workspaceDirectory = computed(() => store.currentTask
  ? (store.currentTask.scopeKind === 'Workspace' ? store.currentTask.workingDirectory : null)
  : store.draft?.workingDirectory || null)
const conversationSkillsWorkspace = computed(() => {
  const directory = workspaceDirectory.value
  if (!directory) return null
  const normalized = directory.trim().replace(/\//g, '\\').replace(/\\+$/, '').toLocaleLowerCase('en-US')
  return store.workspaces.find(workspace =>
    workspace.workingDirectory.trim().replace(/\//g, '\\').replace(/\\+$/, '').toLocaleLowerCase('en-US') === normalized) ?? null
})
const workspaceTrustDialogWorkspace = computed(() => {
  const workspaceId = workspaceTrustDialogWorkspaceId.value
  if (!workspaceId) return null
  return store.workspaces.find(workspace => workspace.id === workspaceId) ?? null
})
const conversationWorkspaceTrustStatus = computed(() =>
  conversationSkillsWorkspace.value?.trustStatus ?? 'trusted')
const conversationWorkspaceTrustLabel = computed(() => {
  const workspace = conversationSkillsWorkspace.value
  if (!workspace) return ''
  const status = workspace.trustStatus ?? 'trusted'
  if (workspace.trustInherited) {
    if (status === 'trusted') return t('继承信任')
    if (status === 'declined') return t('继承不信任')
  }
  if (status === 'trusted') return t('已信任')
  if (status === 'declined') return t('不信任')
  return t('尚未选择信任')
})
const canViewConversationSkills = computed(() =>
  isGeneralChat.value || Boolean(conversationSkillsWorkspace.value))
const hasWorkingDirectory = computed(() => Boolean(workspaceDirectory.value))
const workspaceGitChangeCount = computed(() =>
  workspaceGitUpdate.value?.workingDirectory === workspaceDirectory.value
    ? workspaceGitUpdate.value.entries.length
    : 0)
const thinkingLevelPayload = computed(() => selectedThinkingLevel.value)
const { isAttachmentDragActive } = useAttachmentDrop({
  isTaskActive: () => store.isActive,
  isChatView: () => mainView.value === 'chat' && isModeSelected.value,
  getPayload: () => ({
    workingDirectory: store.currentTask?.workingDirectory ?? store.draft?.workingDirectory,
    prompt: prompt.value,
    model: selectedModel.value,
    thinkingLevel: thinkingLevelPayload.value,
    permissionMode: selectedPermissionMode.value,
  }),
  reportError: (message) => { store.bridgeError = message },
})
useAttachmentPaste({
  isTaskActive: () => store.isActive,
  isChatView: () => mainView.value === 'chat' && isModeSelected.value,
  getPayload: () => ({
    workingDirectory: store.currentTask?.workingDirectory ?? store.draft?.workingDirectory,
    prompt: prompt.value,
    model: selectedModel.value,
    thinkingLevel: thinkingLevelPayload.value,
    permissionMode: selectedPermissionMode.value,
  }),
  reportError: (message) => { store.bridgeError = message },
})
const conversationRuns = computed<TaskRunSnapshot[]>(() => {
  const task = store.currentTask
  if (!task) return []
  if (task.runs.length) return task.runs

  return [{
    id: task.runId,
    prompt: task.prompt,
    model: task.model,
    thinkingLevel: task.thinkingLevel,
    messageAttachments: task.attachments,
    status: task.status,
    statusText: task.statusText,
    summary: task.summary,
    aiSummaryStatus: task.aiSummaryStatus,
    activityStatus: task.activityStatus,
    assistantText: task.assistantText,
    finalAnswer: task.finalAnswer,
    lastSequence: task.lastSequence,
    pendingSteering: task.pendingSteering,
    pendingFollowUps: task.pendingFollowUps,
    transcript: task.transcript,
    activities: task.activities,
    artifacts: task.artifacts ?? [],
  }]
})

function openFileDiff(file: FileChangeEvidence) {
  if (file.isBinary || !file.hasDiff) return
  store.clearCommitDiff()
  store.clearFileDiff()
  postBridgeMessage('GetFileDiff', { changeId: file.id })
}

function confirmRecovery() {
  if (!recoveryTarget.value) return
  postBridgeMessage('RestoreFile', { changeId: recoveryTarget.value.id })
  recoveryTarget.value = null
}

function normalizeDefaultPermissionMode(value?: PermissionMode | null): PermissionMode {
  return value === 'read-only' ? 'read-only' : 'standard'
}

function requestFullAccess() {
  if (store.currentTask) return
  fullAccessConfirmationOpen.value = true
}

function confirmFullAccess() {
  selectedPermissionMode.value = 'full-access'
  fullAccessConfirmationOpen.value = false
}

watch(
  activeLanguage,
  language => setLocale(language),
  { immediate: true },
)

watch(resolvedTheme, theme => applyTheme(theme), { immediate: true })

watch(
  () => store.draft,
  (draft) => {
    if (draft?.workingDirectory) directChatSelected.value = false
    if (draft) {
      prompt.value = draft.prompt
      selectedModel.value = draft.model
      selectedThinkingLevel.value = normalizeThinkingLevel(draft.thinkingLevel)
      selectedPermissionMode.value = normalizeDefaultPermissionMode(
        draft.permissionMode ?? settingsSnapshot.value.values.tasks.permissionMode,
      )
    }
  },
  { immediate: true },
)

watch(
  prompt,
  value => {
    if (promptDraftTaskId.value) saveTaskPromptDraft(promptDraftTaskId.value, value)
  },
  { flush: 'sync' },
)

watch(inspectorCollapsed, value => {
  window.localStorage.setItem('pi-companion:inspector-collapsed', String(value))
  if (!value && inspectorTab.value === 'context') scheduleSessionStatisticsRefresh(0)
})

watch(workspaceDirectory, () => {
  workspaceGitUpdate.value = null
  workspaceGitHistoryUpdate.value = null
  workspaceGitHistoryLoading.value = false
  workspaceGitHistoryRequestId = null
  workspaceGitCommitMessageResult.value = null
  workspaceGitCommitMessageLoading.value = false
  workspaceGitCommitMessageRequestId = null
  scheduleWorkspaceGitRefresh(0)
  scheduleWorkspaceGitAutoRefresh()
})

watch(
  () => settingsSnapshot.value.values.general.gitAutoRefreshSeconds,
  () => scheduleWorkspaceGitAutoRefresh(),
)

watch(
  () => store.currentTask,
  (task, previousTask) => {
    if (task?.id !== previousTask?.id) activateTaskPromptDraft(task?.id ?? null)
    if (task?.id !== previousTask?.id && executionDefaultsTimer) {
      window.clearTimeout(executionDefaultsTimer)
      executionDefaultsTimer = 0
    }
    if (task) directChatSelected.value = false
    if (sessionStatisticsRefreshTimer) window.clearTimeout(sessionStatisticsRefreshTimer)
    sessionStatisticsRefreshTimer = 0
    sessionStatisticsRequestId = null
    const cached = task ? sessionStatisticsCache.get(sessionStatisticsCacheKey(task.id)) : null
    sessionStatisticsUpdate.value = task && cached?.lastSequence === task.lastSequence ? cached.update : null
    sessionStatisticsLoading.value = false
    if (task) {
      selectedModel.value = task.model
      selectedThinkingLevel.value = normalizeThinkingLevel(task.thinkingLevel)
      selectedPermissionMode.value = task.permissionMode ?? 'standard'
      scheduleSessionStatisticsRefresh(0)
    } else if (!store.draft) {
      applyAgentDefaults()
    }
  },
  { immediate: true },
)

watch(
  () => store.bridgeError,
  (message) => {
    if (bridgeErrorTimer) window.clearTimeout(bridgeErrorTimer)
    bridgeErrorTimer = 0
    if (!message) return
    showTransientNotice(`bridge:${message}`, message, false, 5000)
    bridgeErrorTimer = window.setTimeout(() => {
      if (store.bridgeError === message) store.bridgeError = null
      bridgeErrorTimer = 0
    }, 5000)
  },
)

watch([selectedModel, selectedThinkingLevel], () => {
  scheduleSessionStatisticsRefresh(0)
  if (executionDefaultsTimer) window.clearTimeout(executionDefaultsTimer)
  const task = store.currentTask
  if (!task || (task.model === selectedModel.value && normalizeThinkingLevel(task.thinkingLevel) === selectedThinkingLevel.value)) return
  const taskId = task.id
  const model = selectedModel.value
  const thinkingLevel = selectedThinkingLevel.value
  executionDefaultsTimer = window.setTimeout(() => {
    executionDefaultsTimer = 0
    const current = store.currentTask
    if (!current ||
        current.id !== taskId ||
        (current.model === model && normalizeThinkingLevel(current.thinkingLevel) === thinkingLevel)) return
    postBridgeMessage('UpdateTaskExecutionDefaults', {
      taskId,
      model,
      thinkingLevel,
    })
  }, 120)
})

watch(mainView, view => {
  if (view === 'history' && historyHasMore.value) loadTaskHistory(true)
  if (view === 'skills' && !skillsSnapshot.value && !skillsLoading.value) loadSkills()
})

watch([historySearch, historyStatus], ([search, status]) => {
  const filterActive = mainView.value === 'history' && Boolean(search.trim() || status !== 'all')
  if (!filterActive) {
    taskHistoryLoadAllPending = false
    return
  }
  if (!historyHasMore.value) return
  if (taskHistorySearchTimer) window.clearTimeout(taskHistorySearchTimer)
  if (executionDefaultsTimer) window.clearTimeout(executionDefaultsTimer)
  taskHistorySearchTimer = window.setTimeout(() => {
    taskHistorySearchTimer = 0
    loadTaskHistory(true)
  }, search.trim() ? 250 : 0)
})

watch(
  () => store.currentTask?.lastSequence,
  () => {
    if (!stickTranscriptToBottom.value || transcriptScrollFrame) return
    transcriptScrollFrame = window.requestAnimationFrame(async () => {
      transcriptScrollFrame = 0
      await nextTick()
      if (transcript.value) transcript.value.scrollTop = transcript.value.scrollHeight
    })
  },
)

onMounted(async () => {
  systemThemeMedia?.addEventListener('change', handleSystemThemeChange)
  const searchParams = new URLSearchParams(window.location.search)
  const preview = searchParams.get('preview')
  if (import.meta.env.DEV && (preview === 'transcript' || preview === 'performance' || preview === 'settings' || preview === 'commit-diff')) {
    const {
      createCommitDiffPreview,
      createPerformancePreview,
      createTaskHistoryPreview,
      createTranscriptPreview,
    } = await import('@/preview')
    const taskHistoryPreview = createTaskHistoryPreview()
    store.connected = true
    store.currentTask = preview === 'performance' ? createPerformancePreview() : createTranscriptPreview()
    store.historyTasks = taskHistoryPreview.history
    store.recentTasks = taskHistoryPreview.history
    store.recycleBinTasks = taskHistoryPreview.recycleBin
    store.workspaces = [{
      id: 'preview-workspace',
      name: 'pi-companion',
      workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      taskCount: taskHistoryPreview.history.length,
      hasActiveTask: true,
    }]
    if (preview === 'settings') {
      settingsOpen.value = true
      settingsAction.value = { message: t('设置已保存。'), succeeded: true }
    }
    if (preview === 'commit-diff') {
      store.commitDiff = createCommitDiffPreview()
    }
  } else {
    disconnect = connectBridge(consumeBridgeMessage)
  }
  window.addEventListener('keydown', handleGlobalKeydown)
  window.addEventListener('click', closeTaskContextMenu)
  window.addEventListener('blur', closeTaskContextMenu)
  window.addEventListener('resize', closeTaskContextMenu)
})

onBeforeUnmount(() => {
  if (transcriptScrollFrame) window.cancelAnimationFrame(transcriptScrollFrame)
  if (transientNoticeTimer) window.clearTimeout(transientNoticeTimer)
  if (workspaceGitRefreshTimer) window.clearTimeout(workspaceGitRefreshTimer)
  if (workspaceGitAutoRefreshTimer) window.clearTimeout(workspaceGitAutoRefreshTimer)
  if (sessionStatisticsRefreshTimer) window.clearTimeout(sessionStatisticsRefreshTimer)
  if (taskHistorySearchTimer) window.clearTimeout(taskHistorySearchTimer)
  if (executionDefaultsTimer) window.clearTimeout(executionDefaultsTimer)
  if (bridgeErrorTimer) window.clearTimeout(bridgeErrorTimer)
  systemThemeMedia?.removeEventListener('change', handleSystemThemeChange)
  clearTheme()
  disconnect()
  window.removeEventListener('keydown', handleGlobalKeydown)
  window.removeEventListener('click', closeTaskContextMenu)
  window.removeEventListener('blur', closeTaskContextMenu)
  window.removeEventListener('resize', closeTaskContextMenu)
})

function handleSystemThemeChange(event: MediaQueryListEvent) {
  systemPrefersLight.value = event.matches
}

function activateTaskPromptDraft(taskId: string | null) {
  const previousTaskId = promptDraftTaskId.value
  if (previousTaskId) saveTaskPromptDraft(previousTaskId, prompt.value)
  promptDraftTaskId.value = taskId
  prompt.value = taskId ? loadTaskPromptDraft(taskId) : ''
}

async function beginNewTask() {
  activateTaskPromptDraft(null)
  applyAgentDefaults()
  directChatSelected.value = false
  store.clearDraft()
  mainView.value = 'chat'
  postBridgeMessage('NewTask')
  await nextTick()
  composerPanel.value?.focus()
}

async function beginNewTaskInWorkspace(workspaceId: string) {
  activateTaskPromptDraft(null)
  applyAgentDefaults()
  directChatSelected.value = false
  store.clearDraft()
  mainView.value = 'chat'
  postBridgeMessage('NewTaskInWorkspace', { workspaceId })
  await nextTick()
  composerPanel.value?.focus()
}

function saveWorkspacePresentation(payload: {
  workspaceId: string
  displayName: string | null
  iconKey: WorkspaceIconKey
  colorKey: WorkspaceColorKey
}) {
  const posted = postBridgeMessage('UpdateWorkspacePresentation', payload)
  if (!posted) {
    const workspace = store.workspaces.find(candidate => candidate.id === payload.workspaceId)
    if (workspace) {
      const segments = workspace.workingDirectory.split(/[\\/]/).filter(Boolean)
      workspace.displayName = payload.displayName
      workspace.name = payload.displayName ?? segments.at(-1) ?? workspace.workingDirectory
      workspace.iconKey = payload.iconKey
      workspace.colorKey = payload.colorKey
    }
  }
  editingWorkspaceId.value = null
}

function confirmHideWorkspace() {
  const workspaceId = hidingWorkspaceId.value
  if (!workspaceId) return
  const posted = postBridgeMessage('HideWorkspace', { workspaceId })
  if (!posted) {
    const index = store.workspaces.findIndex(workspace => workspace.id === workspaceId)
    if (index >= 0) store.workspaces.splice(index, 1)
  }
  hidingWorkspaceId.value = null
}

function handleGlobalKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && hidingWorkspaceId.value) {
    hidingWorkspaceId.value = null
    return
  }

  if (event.key === 'Escape' && editingWorkspaceId.value) {
    editingWorkspaceId.value = null
    return
  }

  if (event.key === 'Escape' && settingsOpen.value) {
    settingsOpen.value = false
    return
  }

  if (event.key === 'Escape' && (renameTaskTarget.value || confirmAction.value)) {
    renameTaskTarget.value = null
    confirmAction.value = null
    return
  }

  if (event.key === 'Escape' && taskContextMenu.value) {
    closeTaskContextMenu()
    return
  }

  if (event.key.toLowerCase() === 'n' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault()
    void beginNewTask()
  }
}

function requestWorkspaceRun(
  type: 'SendPrompt' | 'StartDemo',
  payload: Record<string, unknown>,
  clearDraftAfterPost = false,
) {
  const workspace = conversationSkillsWorkspace.value
  if (workspace && (workspace.trustStatus ?? 'trusted') === 'undecided') {
    pendingWorkspaceRun.value = { type, payload, clearDraftAfterPost }
    workspaceTrustDialogWorkspaceId.value = workspace.id
    return
  }

  postBridgeMessage(type, payload)
  if (clearDraftAfterPost) clearComposerDraft()
}

function openWorkspaceTrust(workspace: WorkspaceHistoryEntry) {
  pendingWorkspaceRun.value = null
  workspaceTrustDialogWorkspaceId.value = workspace.id
}

function cancelWorkspaceTrustDecision() {
  if (workspaceTrustDecisionPending.value) return
  workspaceTrustDialogWorkspaceId.value = null
  pendingWorkspaceRun.value = null
}

function setWorkspaceTrustDecision(trusted: boolean) {
  const workspace = workspaceTrustDialogWorkspace.value
  if (!workspace || workspaceTrustDecisionPending.value) return
  workspaceTrustRequestSequence += 1
  const requestId = `workspace-trust-${Date.now()}-${workspaceTrustRequestSequence}`
  workspaceTrustRequestId = requestId
  workspaceTrustDecisionPending.value = true
  const request: SetWorkspaceTrustDecisionRequest = {
    requestId,
    workspaceId: workspace.id,
    trusted,
  }
  postBridgeMessage('SetWorkspaceTrustDecision', { ...request })
}

function startDemo(mode: 'Success' | 'InteractiveSuccess' | 'Failure', text: string) {
  if (!hasWorkingDirectory.value) {
    selectWorkingDirectory()
    return
  }
  requestWorkspaceRun('StartDemo', {
    prompt: text,
    mode,
    workingDirectory: store.draft?.workingDirectory,
    model: selectedModel.value,
    thinkingLevel: thinkingLevelPayload.value,
    permissionMode: selectedPermissionMode.value,
  })
}

function ensureComposerSkills() {
  if (!skillsSnapshot.value && !skillsLoading.value) loadSkills()
}

function clearComposerDraft() {
  prompt.value = ''
  store.clearDraft()
}

function composerCommandError(message: string) {
  store.bridgeError = message
}

function executeComposerCommand(name: ComposerCommandName, args: string) {
  const task = store.currentTask
  switch (name) {
    case 'compact':
      if (!task) {
        composerCommandError(t('当前没有可压缩的任务上下文。'))
        return
      }
      if (store.isActive) {
        composerCommandError(t('任务运行中，完成或停止后才能压缩上下文。'))
        return
      }
      postBridgeMessage('CompactSession', {
        taskId: task.id,
        customInstructions: args || null,
      })
      clearComposerDraft()
      return
    case 'model': {
      if (store.isActive) {
        composerCommandError(t('任务运行中，不能切换模型。'))
        return
      }
      if (!args) {
        prompt.value = '/model '
        return
      }
      const normalized = args.toLocaleLowerCase()
      const option = modelOptions.value.find(candidate =>
        candidate.value.toLocaleLowerCase() === normalized ||
        candidate.label.toLocaleLowerCase() === normalized)
      if (!option) {
        composerCommandError(t('没有找到模型 {model}。', { model: args }))
        return
      }
      selectedModel.value = option.value
      clearComposerDraft()
      showTransientNotice(`model:${option.value}`, t('已选择模型 {model}。', { model: option.label }), true)
      return
    }
    case 'new':
      if (store.isActive) {
        composerCommandError(t('当前任务仍在运行，请先停止或等待它结束。'))
        return
      }
      clearComposerDraft()
      void beginNewTask()
      return
    case 'name':
      if (!task) {
        composerCommandError(t('当前没有可重命名的任务。'))
        return
      }
      if (!args) {
        prompt.value = '/name '
        return
      }
      postBridgeMessage('RenameTask', { taskId: task.id, title: args })
      clearComposerDraft()
      return
    case 'session':
      if (!task) {
        composerCommandError(t('当前没有可查看的 Session。'))
        return
      }
      inspectorCollapsed.value = false
      inspectorTab.value = 'context'
      refreshSessionStatistics(false)
      clearComposerDraft()
      return
    case 'settings':
      settingsOpen.value = true
      clearComposerDraft()
      return
    case 'reload':
      if (store.isActive) {
        composerCommandError(t('任务运行中，不能重新加载 Pi 本地状态。'))
        return
      }
      composerReloadPending = true
      postBridgeMessage('ReloadPiConfiguration')
      skillsSnapshot.value = null
      clearComposerDraft()
      return
    case 'stop':
      if (!store.isActive) {
        composerCommandError(t('当前没有正在运行的任务。'))
        return
      }
      postBridgeMessage('AbortRun')
      clearComposerDraft()
      return
    case 'help':
      prompt.value = '/'
      return
  }
}

function submit() {
  const message = prompt.value.trim()
  const hasAttachments = Boolean(store.draft?.attachments.length)
  if (!message && !hasAttachments) return
  if (!store.currentTask && !isModeSelected.value) {
    store.bridgeError = t('请先选择工作目录或直接对话。')
    return
  }

  let outgoingMessage = literalComposerMessage(message)
  if (message.startsWith('/') && !message.startsWith('//')) {
    if (message.startsWith('/skill:')) {
      const invocation = parseComposerInvocation(message)
      if (!invocation || invocation.kind !== 'skill') {
        composerCommandError(t('请选择一个有效技能，并在技能名后输入任务要求。'))
        ensureComposerSkills()
        return
      }
      if (!skillsSnapshot.value) {
        composerCommandError(t('正在读取可用技能，请稍后再试。'))
        ensureComposerSkills()
        return
      }
      if (!composerSkillOptions.value.some(skill => skill.name === invocation.name)) {
        composerCommandError(t('技能 {name} 在当前任务中不可用。', { name: invocation.name }))
        return
      }
    } else {
      const invocation = parseComposerInvocation(message)
      if (!invocation || invocation.kind !== 'command' || !isComposerCommandName(invocation.name)) {
        composerCommandError(t('未知指令。输入 /help 查看可用指令；使用 // 可发送以 / 开头的普通文本。'))
        return
      }
      if (hasAttachments) {
        composerCommandError(t('App 指令不能携带附件。'))
        return
      }
      executeComposerCommand(invocation.name, invocation.args)
      return
    }
  }

  if (store.currentTask && store.isActive) {
    postBridgeMessage('QueueLocalMessage', { message: outgoingMessage })
  } else {
    requestWorkspaceRun('SendPrompt', {
      prompt: outgoingMessage,
      mode: 'InteractiveSuccess',
      workingDirectory: store.draft?.workingDirectory,
      model: selectedModel.value,
      thinkingLevel: thinkingLevelPayload.value,
      permissionMode: selectedPermissionMode.value,
    }, true)
    return
  }

  clearComposerDraft()
}

function updateLocalMessage(messageId: string, message: string, attachments: string[]) {
  postBridgeMessage('UpdateLocalMessage', { messageId, message, attachments })
  closeLocalMessageEditor()
}

function removeLocalMessage(messageId: string) {
  postBridgeMessage('RemoveLocalMessage', { messageId })
}

function moveLocalMessage(messageId: string, newIndex: number) {
  postBridgeMessage('MoveLocalMessage', { messageId, newIndex })
}

function dispatchLocalMessage(messageId: string, delivery: 'steer' | 'follow-up' | 'new-run') {
  postBridgeMessage('DispatchLocalMessage', { messageId, delivery })
}

function openLocalMessageEditor(messageId: string) {
  editingLocalMessageId.value = messageId
  selectedLocalMessageAttachments.value = null
}

function closeLocalMessageEditor() {
  editingLocalMessageId.value = null
  selectedLocalMessageAttachments.value = null
  localMessageAttachmentRequestId = null
}

function confirmLocalMessageEditor(message: string, attachments: string[]) {
  if (!editingLocalMessage.value) return
  updateLocalMessage(editingLocalMessage.value.id, message, attachments)
}

function selectLocalMessageAttachments(attachments: string[]) {
  localMessageAttachmentRequestSequence += 1
  localMessageAttachmentRequestId = `local-message-attachments-${Date.now()}-${localMessageAttachmentRequestSequence}`
  postBridgeMessage('SelectLocalMessageAttachments', {
    requestId: localMessageAttachmentRequestId,
    initialDirectory: store.currentTask?.workingDirectory,
    attachments,
  })
}

function selectWorkingDirectory() {
  if (store.currentTask) return
  postBridgeMessage('SelectWorkingDirectory', {
    initialDirectory: store.draft?.workingDirectory,
    prompt: prompt.value,
    model: selectedModel.value,
    thinkingLevel: thinkingLevelPayload.value,
    permissionMode: selectedPermissionMode.value,
  })
}

function openWorkspaceLocation(action: 'terminal' | 'explorer' | 'copy') {
  if (!store.currentTask?.workingDirectory || store.currentTask.scopeKind !== 'Workspace') return
  postBridgeMessage('OpenWorkspaceLocation', {
    workingDirectory: store.currentTask.workingDirectory,
    action,
  })
}

async function selectDirectChat() {
  if (store.currentTask) return
  if (store.draft?.workingDirectory) {
    store.clearDraft()
    postBridgeMessage('NewTask')
  }
  directChatSelected.value = true
  await nextTick()
  composerPanel.value?.focus()
}

function selectAttachments() {
  if (!isModeSelected.value) return
  postBridgeMessage('SelectAttachments', {
    initialDirectory: store.currentTask?.workingDirectory ?? store.draft?.workingDirectory,
    workingDirectory: store.currentTask?.workingDirectory ?? store.draft?.workingDirectory,
    prompt: prompt.value,
    model: selectedModel.value,
    thinkingLevel: thinkingLevelPayload.value,
    permissionMode: selectedPermissionMode.value,
  })
}

function removeAttachment(path: string) {
  postBridgeMessage('RemoveAttachment', {
    path,
    workingDirectory: store.currentTask?.workingDirectory ?? store.draft?.workingDirectory,
    prompt: prompt.value,
    model: selectedModel.value,
    thinkingLevel: thinkingLevelPayload.value,
    permissionMode: selectedPermissionMode.value,
  })
}

function normalizeThinkingLevel(value: string): PiThinkingLevel {
  return ({
    关闭: 'off', off: 'off', 最小: 'minimal', minimal: 'minimal', 低: 'low', low: 'low',
    中: 'medium', medium: 'medium', 高: 'high', high: 'high', 超高: 'xhigh', xhigh: 'xhigh',
    最大: 'max', max: 'max',
  } as Record<string, PiThinkingLevel>)[value.trim().toLocaleLowerCase()] ?? 'high'
}

function thinkingLevelLabel(value: string) {
  return ({ off: 'None', minimal: 'Minimal', low: 'Low', medium: 'Medium', high: 'High', xhigh: 'Xhigh', max: 'Max' } as Record<string, string>)[value] ?? value
}

function applyAgentDefaults() {
  const preferred = settingsSnapshot.value.values.agent.defaultModel
  const available = visiblePiModels.value.map(model => `${model.provider}/${model.id}`)
  selectedModel.value = available.includes(preferred) ? preferred : available[0] ?? ''
  selectedThinkingLevel.value = settingsSnapshot.value.values.agent.defaultThinkingLevel
  selectedPermissionMode.value = normalizeDefaultPermissionMode(settingsSnapshot.value.values.tasks.permissionMode)
}

function consumeBridgeMessage(message: BridgeEnvelope) {
  const initializes = message.type === 'InitializeSnapshot'
  if (message.type === 'OpenCurrentTask') {
    const taskId = (message.payload as { taskId?: string | null }).taskId
    if (executionDefaultsTimer) window.clearTimeout(executionDefaultsTimer)
    executionDefaultsTimer = 0
    store.bridgeError = null
    settingsOpen.value = false
    renameTaskTarget.value = null
    confirmAction.value = null
    closeTaskContextMenu()
    mainView.value = 'chat'
    if (taskId && store.currentTask?.id !== taskId) postBridgeMessage('SelectTask', { taskId })
    return
  } else if (message.type === 'InitializeSnapshot') {
    const snapshot = message.payload as { settings?: SettingsSnapshot; historyHasMore?: boolean }
    if (snapshot.settings) {
      appearancePreview.value = null
      settingsSnapshot.value = snapshot.settings
    }
    historyHasMore.value = snapshot.historyHasMore ?? false
    historyLoading.value = false
    taskHistoryLoadAllPending = false
    historyLoadedCount.value = (message.payload as { historyTasks?: TaskHistoryEntry[] }).historyTasks?.length ?? 0
  } else if (message.type === 'SettingsUpdated') {
    const nextSettings = message.payload as SettingsSnapshot
    appearancePreview.value = null
    settingsSnapshot.value = nextSettings
    if (!store.currentTask && !store.draft) applyAgentDefaults()
    if (composerReloadPending) {
      composerReloadPending = false
      loadSkills()
    }
    return
  } else if (message.type === 'SettingsActionCompleted') {
    const action = message.payload as SettingsActionCompleted
    if (action.operation === 'companion-auto-save' && !action.succeeded) {
      appearancePreview.value = null
    }
    settingsAction.value = action
    return
  } else if (message.type === 'LocalMessageAttachmentsSelected') {
    const selection = message.payload as LocalMessageAttachmentsSelected
    if (selection.requestId === localMessageAttachmentRequestId) {
      selectedLocalMessageAttachments.value = selection.attachments
    }
    return
  } else if (message.type === 'PiOAuthLoginProgress') {
    const progress = message.payload as PiOAuthLoginProgress
    piOAuthLoginProgress.value = progress.phase === 'idle' ? null : progress
    return
  } else if (message.type === 'WorkspaceDirectoryLoaded') {
    workspaceDirectoryUpdate.value = message.payload as WorkspaceDirectoryListing
    return
  } else if (message.type === 'WorkspaceFileSearchResults') {
    workspaceSearchUpdate.value = message.payload as WorkspaceFileSearchResult
    return
  } else if (message.type === 'WorkspaceGitStatusLoaded') {
    const update = message.payload as WorkspaceGitSnapshot
    if (update.requestId === workspaceGitRequestId) workspaceGitUpdate.value = update
    return
  } else if (message.type === 'WorkspaceGitHistoryLoaded') {
    const update = message.payload as WorkspaceGitHistorySnapshot
    if (update.requestId === workspaceGitHistoryRequestId) {
      const current = workspaceGitHistoryUpdate.value
      if (update.offset > 0 &&
          current?.workingDirectory === update.workingDirectory) {
        workspaceGitHistoryUpdate.value = update.error
          ? { ...current, error: update.error }
          : {
              ...update,
              entries: Array.from(new Map(
                [...current.entries, ...update.entries].map(entry => [entry.hash, entry]),
              ).values()),
            }
      } else {
        workspaceGitHistoryUpdate.value = update
      }
      workspaceGitHistoryLoading.value = false
    }
    return
  } else if (message.type === 'WorkspaceGitActionCompleted') {
    const result = message.payload as WorkspaceGitActionCompleted
    if (result.requestId === workspaceGitActionRequestId) {
      workspaceGitPendingAction.value = null
      workspaceGitActionResult.value = result
      refreshWorkspaceGit()
      refreshWorkspaceGitHistory()
    }
    return
  } else if (message.type === 'WorkspaceGitCommitMessageGenerated') {
    const result = message.payload as WorkspaceGitCommitMessageGenerated
    if (result.requestId === workspaceGitCommitMessageRequestId) {
      workspaceGitCommitMessageLoading.value = false
      workspaceGitCommitMessageResult.value = result
    }
    return
  } else if (message.type === 'SessionStatisticsLoaded') {
    const update = message.payload as SessionStatisticsSnapshot
    if (update.requestId === sessionStatisticsRequestId) {
      acceptSessionStatistics(update)
    }
    return
  } else if (message.type === 'SkillsLoaded') {
    const update = message.payload as SkillsLoaded
    if (update.requestId === skillsRequestId) {
      skillsSnapshot.value = update
      skillsLoading.value = false
      skillsError.value = null
    }
    return
  } else if (message.type === 'SkillRemovalCompleted') {
    const result = message.payload as SkillRemovalCompleted
    if (result.requestId === skillRemovalRequestId) {
      skillsSnapshot.value = result.snapshot
      skillRemovalPendingId.value = null
      skillRemovalResult.value = result
    }
    return
  } else if (message.type === 'SkillWorkspaceTrustCompleted') {
    const result = message.payload as SkillWorkspaceTrustCompleted
    if (result.requestId === skillTrustRequestId) {
      skillsSnapshot.value = result.snapshot
      skillTrustPendingWorkspaceId.value = null
      skillTrustResult.value = result
    }
    return
  } else if (message.type === 'WorkspaceTrustDecisionCompleted') {
    const result = message.payload as WorkspaceTrustDecisionCompleted
    if (result.requestId === workspaceTrustRequestId) {
      workspaceTrustDecisionPending.value = false
      if (result.succeeded) {
        store.workspaces = store.workspaces.map(workspace =>
          workspace.id === result.workspaceId
            ? {
                ...workspace,
                trustStatus: result.status,
                trustDecisionPath: workspace.workingDirectory,
                trustInherited: false,
              }
            : workspace)
        skillsSnapshot.value = null
        const pendingRun = pendingWorkspaceRun.value
        workspaceTrustDialogWorkspaceId.value = null
        pendingWorkspaceRun.value = null
        transientNotice.value = {
          id: `workspace-trust-${Date.now()}`,
          message: result.message,
          succeeded: true,
        }
        if (pendingRun) {
          window.queueMicrotask(() => {
            postBridgeMessage(pendingRun.type, pendingRun.payload)
            if (pendingRun.clearDraftAfterPost) clearComposerDraft()
          })
        }
      } else {
        store.bridgeError = result.message
      }
    }
    return
  } else if (message.type === 'SkillImportSourceInspected') {
    const result = message.payload as SkillImportSourceInspected
    if (result.requestId === skillImportRequestId) {
      skillImportPhase.value = null
      if (result.succeeded && result.source) {
        skillImportSource.value = result.source
        skillImportPreparation.value = null
        skillImportError.value = null
      } else if (!result.cancelled) {
        skillImportError.value = result.message
      }
    }
    return
  } else if (message.type === 'SkillImportReady') {
    const result = message.payload as SkillImportReady
    if (result.requestId === skillImportRequestId) {
      skillImportPhase.value = null
      skillImportPreparation.value = result.succeeded ? result.preparation : null
      skillImportError.value = result.succeeded ? null : result.message
    }
    return
  } else if (message.type === 'SkillImportCompleted') {
    const result = message.payload as SkillImportCompleted
    if (result.requestId === skillImportRequestId) {
      skillsSnapshot.value = result.snapshot
      skillImportPhase.value = null
      skillImportPreparation.value = null
      skillImportError.value = result.succeeded || result.cancelled ? null : result.message
      skillImportResult.value = result.cancelled ? null : result
      if (result.succeeded || result.cancelled) {
        skillImportSource.value = null
      }
    }
    return
  } else if (message.type === 'TaskHistoryPageLoaded') {
    const page = message.payload as TaskHistoryPage
    if (page.requestId === taskHistoryRequestId) {
      const items = page.replaces ? page.items : [...store.historyTasks, ...page.items]
      store.historyTasks = Array.from(new Map(items.map(item => [item.id, item])).values())
      historyHasMore.value = page.hasMore
      historyLoading.value = false
      historyLoadedCount.value = page.replaces ? page.items.length : page.offset + page.items.length
      if (taskHistoryLoadAllPending && historyHasMore.value && mainView.value === 'history') {
        taskHistoryLoadAllPending = false
        window.queueMicrotask(() => loadTaskHistory(true))
      }
    }
    return
  } else if (message.type === 'TaskCollectionsUpdated') {
    const collections = message.payload as { historyHasMore?: boolean }
    historyHasMore.value = collections.historyHasMore ?? false
    historyLoading.value = false
    taskHistoryLoadAllPending = false
    historyLoadedCount.value = (message.payload as { historyTasks?: TaskHistoryEntry[] }).historyTasks?.length ?? 0
  } else if (message.type === 'BridgeError') {
    if (historyLoading.value) {
      historyLoading.value = false
      taskHistoryLoadAllPending = false
    }
    if (skillsLoading.value) {
      skillsLoading.value = false
      skillsError.value = (message.payload as { message?: string }).message ?? t('技能扫描失败')
    }
    if (skillRemovalPendingId.value) {
      skillRemovalPendingId.value = null
      skillsError.value = (message.payload as { message?: string }).message ?? t('技能卸载失败')
    }
    if (skillTrustPendingWorkspaceId.value) {
      skillTrustPendingWorkspaceId.value = null
      skillsError.value = (message.payload as { message?: string }).message ?? t('工作区信任失败')
    }
    if (workspaceTrustDecisionPending.value) {
      workspaceTrustDecisionPending.value = false
      store.bridgeError = (message.payload as { message?: string }).message ?? t('工作区信任失败')
    }
    if (skillImportPhase.value) {
      skillImportPhase.value = null
      skillImportPreparation.value = null
      skillImportError.value = (message.payload as { message?: string }).message ?? t('技能导入失败')
    }
  }

  store.consume(message)
  if (initializes && !store.currentTask && !store.draft) applyAgentDefaults()
  if (['InitializeSnapshot', 'TaskUpdated', 'TaskDelta', 'EvidenceUpdated', 'RecoveryCompleted'].includes(message.type)) {
    scheduleWorkspaceGitRefresh()
    scheduleSessionStatisticsRefresh()
  }
}

function loadSkills() {
  skillsRequestSequence += 1
  const requestId = `skills-${Date.now()}-${skillsRequestSequence}`
  skillsRequestId = requestId
  skillsLoading.value = true
  skillsError.value = null
  const payload: LoadSkillsRequest = { requestId }
  const posted = postBridgeMessage('LoadSkills', { ...payload })
  if (!posted && import.meta.env.DEV) {
    void import('@/preview').then(({ createSkillsPreview }) => {
      if (skillsRequestId !== requestId) return
      const previewTrust = new URLSearchParams(window.location.search).get('previewTrust')
      skillsSnapshot.value = createSkillsPreview(
        requestId,
        previewTrust === 'untrusted' ? 'undecided' : 'trusted',
      )
      skillsLoading.value = false
    })
  }
}

function refreshSkills() {
  skillRemovalResult.value = null
  skillImportResult.value = null
  skillTrustResult.value = null
  loadSkills()
}

function requestSkillWorkspaceTrust(workspaceId: string) {
  if (!store.workspaces.some(workspace => workspace.id === workspaceId) &&
      !skillsSnapshot.value?.workspaceTrust?.some(trust => trust.workspaceId === workspaceId)) return
  if (skillTrustPendingWorkspaceId.value) return
  skillTrustConfirmationWorkspaceId.value = workspaceId
}

function confirmSkillWorkspaceTrust() {
  const workspaceId = skillTrustConfirmationWorkspaceId.value
  if (!workspaceId) return
  skillTrustConfirmationWorkspaceId.value = null
  trustSkillWorkspace(workspaceId)
}

function trustSkillWorkspace(workspaceId: string) {
  skillTrustRequestSequence += 1
  const requestId = `skill-trust-${Date.now()}-${skillTrustRequestSequence}`
  skillTrustRequestId = requestId
  skillTrustPendingWorkspaceId.value = workspaceId
  skillTrustResult.value = null
  skillsError.value = null
  const request: TrustSkillWorkspaceRequest = { requestId, workspaceId }
  const posted = postBridgeMessage('TrustSkillWorkspace', { ...request })
  if (!posted && import.meta.env.DEV && skillsSnapshot.value) {
    const snapshot = structuredClone(skillsSnapshot.value)
    snapshot.requestId = requestId
    snapshot.scannedAt = new Date().toISOString()
    snapshot.workspaceTrust = snapshot.workspaceTrust.map(trust =>
      trust.workspaceId === workspaceId
        ? { ...trust, status: 'trusted', decisionPath: trust.workspacePath, inherited: false }
        : trust)
    for (const skill of snapshot.skills) {
      skill.diagnostics = skill.diagnostics.filter(diagnostic =>
        diagnostic.code !== 'workspace-untrusted' || diagnostic.workspaceId !== workspaceId)
      for (const variant of skill.variants) {
        for (const installation of variant.installations) {
          installation.diagnostics = installation.diagnostics.filter(diagnostic =>
            diagnostic.code !== 'workspace-untrusted' || diagnostic.workspaceId !== workspaceId)
          if (installation.origins.some(origin =>
            origin.scope === 'workspace' && origin.workspaceId === workspaceId) &&
            !installation.effectiveWorkspaceIds.includes(workspaceId)) {
            installation.effectiveWorkspaceIds.push(workspaceId)
          }
        }
      }
    }
    window.queueMicrotask(() => {
      if (skillTrustRequestId !== requestId) return
      skillsSnapshot.value = snapshot
      skillTrustPendingWorkspaceId.value = null
      skillTrustResult.value = {
        requestId,
        succeeded: true,
        message: t('工作区已受 Pi 信任。'),
        workspaceId,
        snapshot,
      }
    })
  }
}

function removeSkillInstallation(payload: {
  installationId: string
  expectedContentHash: string
  workspaceId?: string
}) {
  skillRemovalRequestSequence += 1
  const requestId = `skill-removal-${Date.now()}-${skillRemovalRequestSequence}`
  skillRemovalRequestId = requestId
  skillRemovalPendingId.value = payload.installationId
  skillRemovalResult.value = null
  skillImportResult.value = null
  skillsError.value = null
  const request: RemoveSkillInstallationRequest = {
    requestId,
    installationId: payload.installationId,
    expectedContentHash: payload.expectedContentHash,
    ...(payload.workspaceId ? { workspaceId: payload.workspaceId } : {}),
  }
  const posted = postBridgeMessage('RemoveSkillInstallation', { ...request })
  if (!posted && import.meta.env.DEV && skillsSnapshot.value) {
    const snapshot = structuredClone(skillsSnapshot.value)
    snapshot.requestId = requestId
    snapshot.scannedAt = new Date().toISOString()
    snapshot.skills = snapshot.skills
      .map(skill => ({
        ...skill,
        variants: skill.variants
          .map(variant => ({
            ...variant,
            installations: variant.installations.filter(
              installation => installation.id !== payload.installationId),
          }))
          .filter(variant => variant.installations.length > 0),
      }))
      .filter(skill => skill.variants.length > 0)
    window.queueMicrotask(() => {
      if (skillRemovalRequestId !== requestId) return
      skillsSnapshot.value = snapshot
      skillRemovalPendingId.value = null
      skillRemovalResult.value = {
        requestId,
        succeeded: true,
        message: t('技能已移入可恢复位置。'),
        removedInstallationId: payload.installationId,
        recoveryPath: 'preview://skill-trash',
        snapshot,
      }
    })
  }
}

function openSkillImport() {
  skillImportRequestSequence += 1
  const requestId = `skill-import-${Date.now()}-${skillImportRequestSequence}`
  skillImportRequestId = requestId
  skillImportSource.value = null
  skillImportPreparation.value = null
  skillImportResult.value = null
  skillImportPhase.value = null
  skillImportError.value = null
  skillRemovalResult.value = null
  skillsError.value = null
}

function beginSkillImport(sourceKind: SkillImportSourceKind) {
  if (!skillImportRequestId) openSkillImport()
  const requestId = skillImportRequestId!
  skillImportPhase.value = 'source'
  skillImportError.value = null
  const request: BeginSkillImportRequest = {
    requestId,
    sourceKind,
  }
  const posted = postBridgeMessage('BeginSkillImport', { ...request })
  if (!posted && import.meta.env.DEV) {
    window.queueMicrotask(() => {
      if (skillImportRequestId !== requestId) return
      skillImportPhase.value = null
      skillImportSource.value = {
        token: `preview-source-${requestId}`,
        name: 'local-preview-skill',
        description: t('本地导入预览技能。'),
        sourceKind,
        contentHash: 'preview-content-hash',
        fileCount: 4,
        totalBytes: 4096,
        files: [
          { relativePath: 'SKILL.md', size: 1024, kind: 'file' },
          { relativePath: 'references/usage.md', size: 1280, kind: 'file' },
          { relativePath: 'scripts/run.ps1', size: 1536, kind: 'script' },
          { relativePath: 'assets/icon.svg', size: 256, kind: 'file' },
        ],
        scriptFiles: ['scripts/run.ps1'],
        executableFiles: [],
      }
      skillImportPreparation.value = null
    })
  }
}

function prepareSkillImport(payload: {
  targetScope: 'global' | 'workspace'
  workspaceId?: string
}) {
  const source = skillImportSource.value
  const requestId = skillImportRequestId
  if (!source || !requestId) return
  skillImportPhase.value = 'target'
  skillImportPreparation.value = null
  skillImportError.value = null
  const request: PrepareSkillImportRequest = {
    requestId,
    sourceToken: source.token,
    targetScope: payload.targetScope,
    ...(payload.workspaceId ? { workspaceId: payload.workspaceId } : {}),
  }
  const posted = postBridgeMessage('PrepareSkillImport', { ...request })
  if (!posted && import.meta.env.DEV) {
    const workspace = payload.workspaceId
      ? store.workspaces.find(candidate => candidate.id === payload.workspaceId)
      : null
    window.queueMicrotask(() => {
      if (skillImportRequestId !== requestId || skillImportSource.value?.token !== source.token) return
      skillImportPhase.value = null
      skillImportPreparation.value = {
        token: `preview-preparation-${requestId}`,
        sourceToken: source.token,
        name: source.name,
        description: source.description,
        targetScope: payload.targetScope,
        workspaceId: workspace?.id ?? null,
        workspaceName: workspace?.name ?? null,
        targetPath: workspace
          ? `${workspace.workingDirectory}\\.pi\\skills\\${source.name}`
          : `C:\\Users\\you\\.pi\\agent\\skills\\${source.name}`,
        sourceKind: source.sourceKind,
        contentHash: source.contentHash,
        fileCount: source.fileCount,
        totalBytes: source.totalBytes,
        files: source.files,
        scriptFiles: source.scriptFiles,
        executableFiles: source.executableFiles,
        requiresProjectTrust: Boolean(workspace),
        trustStatus: workspace ? 'undecided' : 'not-required',
      }
    })
  }
}

function confirmSkillImport() {
  const preparation = skillImportPreparation.value
  const requestId = skillImportRequestId
  if (!preparation || !requestId) return
  const request: ConfirmSkillImportRequest = {
    requestId,
    token: preparation.token,
  }
  const posted = postBridgeMessage('ConfirmSkillImport', { ...request })
  skillImportPhase.value = 'commit'
  skillImportError.value = null
  if (!posted && import.meta.env.DEV && skillsSnapshot.value) {
    const snapshot = structuredClone(skillsSnapshot.value)
    snapshot.requestId = requestId
    snapshot.scannedAt = new Date().toISOString()
    window.queueMicrotask(() => {
      if (skillImportRequestId !== requestId) return
      skillsSnapshot.value = snapshot
      skillImportPhase.value = null
      skillImportPreparation.value = null
      skillImportSource.value = null
      skillImportResult.value = {
        requestId,
        succeeded: true,
        cancelled: false,
        message: t('技能已导入。'),
        skillName: preparation.name,
        targetPath: preparation.targetPath,
        snapshot,
      }
    })
  }
}

function cancelSkillImport() {
  const requestId = skillImportRequestId
  if (!requestId) return
  const request: CancelSkillImportRequest = {
    requestId,
    ...(skillImportSource.value ? { sourceToken: skillImportSource.value.token } : {}),
    ...(skillImportPreparation.value
      ? { preparationToken: skillImportPreparation.value.token }
      : {}),
  }
  postBridgeMessage('CancelSkillImport', { ...request })
  skillImportRequestId = null
  skillImportSource.value = null
  skillImportPreparation.value = null
  skillImportPhase.value = null
  skillImportError.value = null
}

function openWorkspaceSkills(workspaceId: string) {
  if (!store.workspaces.some(workspace => workspace.id === workspaceId)) return
  skillManagerContext.value = { workspaceId, directChat: false }
  skillRemovalResult.value = null
  loadSkills()
}

function openConversationSkills() {
  if (isGeneralChat.value) {
    skillManagerContext.value = { workspaceId: null, directChat: true }
    skillRemovalResult.value = null
    loadSkills()
    return
  }
  if (conversationSkillsWorkspace.value) {
    openWorkspaceSkills(conversationSkillsWorkspace.value.id)
  }
}

function showNavigationView(view: Parameters<typeof showMainView>[0]) {
  showMainView(view)
}

function loadTaskHistory(loadAll = false) {
  if (historyLoading.value) {
    if (loadAll) taskHistoryLoadAllPending = true
    return
  }
  if (!loadAll && !historyHasMore.value) return
  if (loadAll) taskHistoryLoadAllPending = false
  taskHistoryRequestSequence += 1
  const requestId = `history-${Date.now()}-${taskHistoryRequestSequence}`
  taskHistoryRequestId = requestId
  historyLoading.value = true
  const posted = postBridgeMessage(loadAll ? 'LoadAllTaskHistory' : 'LoadMoreTaskHistory', {
    requestId,
    offset: loadAll ? 0 : historyLoadedCount.value,
  })
  if (!posted && import.meta.env.DEV) {
    window.queueMicrotask(() => {
      historyHasMore.value = false
      historyLoading.value = false
    })
  }
}

function scheduleWorkspaceGitRefresh(delay = 450) {
  if (workspaceGitRefreshTimer) window.clearTimeout(workspaceGitRefreshTimer)
  workspaceGitRefreshTimer = 0
  if (!workspaceDirectory.value) return
  workspaceGitRefreshTimer = window.setTimeout(() => {
    workspaceGitRefreshTimer = 0
    refreshWorkspaceGit()
  }, delay)
}

function scheduleWorkspaceGitAutoRefresh() {
  if (workspaceGitAutoRefreshTimer) window.clearTimeout(workspaceGitAutoRefreshTimer)
  workspaceGitAutoRefreshTimer = 0
  const seconds = settingsSnapshot.value.values.general.gitAutoRefreshSeconds
  if (!workspaceDirectory.value || !seconds) return
  workspaceGitAutoRefreshTimer = window.setTimeout(() => {
    workspaceGitAutoRefreshTimer = 0
    if (!workspaceGitPendingAction.value) refreshWorkspaceGit()
    scheduleWorkspaceGitAutoRefresh()
  }, seconds * 1000)
}

function refreshWorkspaceGit() {
  if (!workspaceDirectory.value) return
  workspaceGitRequestSequence += 1
  const requestId = `git-${Date.now()}-${workspaceGitRequestSequence}`
  workspaceGitRequestId = requestId
  const posted = postBridgeMessage('RefreshWorkspaceGit', {
    requestId,
    workingDirectory: workspaceDirectory.value,
  })
  if (!posted && import.meta.env.DEV) {
    window.queueMicrotask(() => {
      workspaceGitUpdate.value = {
        requestId,
        workingDirectory: workspaceDirectory.value ?? '',
        isRepository: true,
        repositoryRoot: workspaceDirectory.value,
        branch: 'main',
        isDetached: false,
        entries: previewWorkspaceGitEntries,
        error: null,
      }
    })
  }
}

function manuallyRefreshWorkspaceGit() {
  workspaceGitActionResult.value = null
  workspaceGitUpdate.value = null
  refreshWorkspaceGit()
}

function refreshWorkspaceGitHistory(append = false) {
  if (!workspaceDirectory.value || workspaceGitHistoryLoading.value) return
  const current = workspaceGitHistoryUpdate.value?.workingDirectory === workspaceDirectory.value
    ? workspaceGitHistoryUpdate.value
    : null
  if (append && (!current || !current.hasMore)) return
  const offset = append ? current?.entries.length ?? 0 : 0
  if (!append) workspaceGitHistoryUpdate.value = null
  workspaceGitHistoryLoading.value = true
  workspaceGitHistoryRequestSequence += 1
  const requestId = `git-history-${Date.now()}-${workspaceGitHistoryRequestSequence}`
  workspaceGitHistoryRequestId = requestId
  const posted = postBridgeMessage('RefreshWorkspaceGitHistory', {
    requestId,
    workingDirectory: workspaceDirectory.value,
    offset,
  })
  if (!posted && import.meta.env.DEV) {
    window.queueMicrotask(() => {
      workspaceGitHistoryUpdate.value = {
        requestId,
        workingDirectory: workspaceDirectory.value ?? '',
        entries: [],
        offset,
        hasMore: false,
        error: null,
      }
      workspaceGitHistoryLoading.value = false
    })
  } else if (!posted) {
    workspaceGitHistoryLoading.value = false
  }
}

function runWorkspaceGitAction(action: WorkspaceGitAction, payload: Record<string, unknown> = {}) {
  if (!workspaceDirectory.value || workspaceGitPendingAction.value) return
  workspaceGitActionRequestSequence += 1
  const requestId = `git-action-${Date.now()}-${workspaceGitActionRequestSequence}`
  workspaceGitActionRequestId = requestId
  workspaceGitPendingAction.value = action
  workspaceGitActionResult.value = null
  const posted = postBridgeMessage('RunWorkspaceGitAction', {
    requestId,
    workingDirectory: workspaceDirectory.value,
    action,
    ...payload,
  })
  if (!posted && import.meta.env.DEV) {
    window.queueMicrotask(() => {
      workspaceGitPendingAction.value = null
      workspaceGitActionResult.value = {
        requestId,
        workingDirectory: workspaceDirectory.value ?? '',
        action,
        succeeded: true,
        message: t('Git 操作已完成。'),
        detail: action === 'commit' ? '0123456789abcdef' : null,
      }
    })
  }
}

function stageWorkspaceGit(paths: string[]) {
  runWorkspaceGitAction('stage', { relativePaths: paths })
}

function unstageWorkspaceGit(paths: string[]) {
  runWorkspaceGitAction('unstage', { relativePaths: paths })
}

function commitWorkspaceGit(message: string) {
  runWorkspaceGitAction('commit', { message })
}

function generateWorkspaceGitCommitMessage() {
  if (!workspaceDirectory.value || workspaceGitCommitMessageLoading.value) return
  workspaceGitCommitMessageRequestSequence += 1
  const requestId = `git-commit-message-${Date.now()}-${workspaceGitCommitMessageRequestSequence}`
  workspaceGitCommitMessageRequestId = requestId
  workspaceGitCommitMessageLoading.value = true
  workspaceGitCommitMessageResult.value = null
  const posted = postBridgeMessage('GenerateWorkspaceGitCommitMessage', {
    requestId,
    workingDirectory: workspaceDirectory.value,
  })
  if (!posted) {
    window.setTimeout(() => {
      if (workspaceGitCommitMessageRequestId !== requestId) return
      workspaceGitCommitMessageLoading.value = false
      workspaceGitCommitMessageResult.value = {
        requestId,
        workingDirectory: workspaceDirectory.value ?? '',
        succeeded: true,
        message: 'feat: improve workspace task experience',
        stagedFingerprint: workspaceGitUpdate.value?.stagedFingerprint ?? null,
        truncatedInput: false,
        error: null,
      }
    }, 350)
  }
}

function switchWorkspaceGitBranch(branch: string) {
  runWorkspaceGitAction('switch-branch', { branch })
}

function createWorkspaceGitBranch(branch: string) {
  runWorkspaceGitAction('create-branch', { branch })
}

function updateWorkspaceGitBranch(strategy: 'merge' | 'rebase', sourceBranch: string) {
  runWorkspaceGitAction('update-branch', { strategy, sourceBranch })
}

function abortWorkspaceGitOperation() {
  runWorkspaceGitAction('abort-operation')
}

function scheduleSessionStatisticsRefresh(delay = 450) {
  if (sessionStatisticsRefreshTimer) window.clearTimeout(sessionStatisticsRefreshTimer)
  sessionStatisticsRefreshTimer = 0
  if (!store.currentTask || inspectorCollapsed.value || inspectorTab.value !== 'context') return
  sessionStatisticsRefreshTimer = window.setTimeout(() => {
    sessionStatisticsRefreshTimer = 0
    refreshSessionStatistics(false)
  }, delay)
}

function sessionStatisticsCacheKey(taskId: string) {
  return taskId.toLocaleLowerCase()
}

function restoreCachedSessionStatistics() {
  const task = store.currentTask
  if (!task) return false
  const cached = sessionStatisticsCache.get(sessionStatisticsCacheKey(task.id))
  if (!cached || cached.lastSequence !== task.lastSequence) return false
  sessionStatisticsUpdate.value = cached.update
  sessionStatisticsLoading.value = false
  return true
}

function acceptSessionStatistics(update: SessionStatisticsSnapshot) {
  const task = store.currentTask
  if (!task || !update.taskId || sessionStatisticsCacheKey(task.id) !== sessionStatisticsCacheKey(update.taskId)) return
  sessionStatisticsCache.set(sessionStatisticsCacheKey(task.id), { update, lastSequence: task.lastSequence })
  sessionStatisticsUpdate.value = update
  sessionStatisticsLoading.value = false
}

function refreshSessionStatistics(loadHistoricalSession = true) {
  const task = store.currentTask
  if (!task) return
  if (!loadHistoricalSession && sessionStatisticsLoading.value) return
  if (!loadHistoricalSession && restoreCachedSessionStatistics()) return
  sessionStatisticsRequestSequence += 1
  const requestId = `session-statistics-${Date.now()}-${sessionStatisticsRequestSequence}`
  sessionStatisticsRequestId = requestId
  sessionStatisticsLoading.value = true
  const posted = postBridgeMessage('RefreshSessionStatistics', { requestId, taskId: task.id, loadHistoricalSession })
  if (!posted && import.meta.env.DEV) {
    const contextWindow = selectedModelInfo.value?.contextWindow ?? 272000
    window.queueMicrotask(() => {
      if (!loadHistoricalSession && !store.isActive) {
        acceptSessionStatistics({
          requestId,
          taskId: task.id,
          available: false,
          statistics: null,
          error: null,
        })
        return
      }
      acceptSessionStatistics({
        requestId,
        taskId: task.id,
        available: true,
        statistics: {
          sessionId: 'preview-session',
          sessionFile: null,
          userMessages: 18,
          assistantMessages: 132,
          toolCalls: 93,
          toolResults: 91,
          totalMessages: 150,
          inputTokens: 16125,
          outputTokens: 127,
          cacheReadTokens: 24576,
          cacheWriteTokens: 0,
          totalTokens: 40828,
          cost: 0,
          contextUsage: { tokens: 41242, contextWindow, percent: 41242 / contextWindow * 100 },
        },
        error: null,
      })
    })
  }
}

function selectInspectorTab(tab: 'git' | 'files' | 'context') {
  inspectorTab.value = tab
  if (tab === 'git') refreshWorkspaceGit()
  if (tab === 'context') refreshSessionStatistics(false)
}

function openGitInspector() {
  inspectorCollapsed.value = false
  inspectorTab.value = 'git'
  refreshWorkspaceGit()
}

function openWorkspaceGitDiff(entry: WorkspaceGitEntry) {
  if (!workspaceDirectory.value) return
  store.clearCommitDiff()
  store.clearFileDiff()
  const posted = postBridgeMessage('GetWorkspaceGitDiff', {
    workingDirectory: workspaceDirectory.value,
    relativePath: entry.relativePath,
  })
  if (!posted && import.meta.env.DEV) {
    store.fileDiff = {
      changeId: `workspace-git:${entry.relativePath}`,
      runId: '',
      path: entry.relativePath,
      diffText: `--- a/${entry.relativePath}\n+++ b/${entry.relativePath}\n@@ -1,2 +1,3 @@\n-old line\n+new line\n+another line\n`,
      isBinary: entry.isBinary,
      truncated: false,
      source: 'WorkspaceGit',
    }
  }
}

function openWorkspaceGitCommit(commit: WorkspaceGitCommit) {
  if (!workspaceDirectory.value) return
  store.clearFileDiff()
  store.clearCommitDiff()
  postBridgeMessage('GetWorkspaceGitCommitDiff', {
    workingDirectory: workspaceDirectory.value,
    commitHash: commit.hash,
  })
}

function loadWorkspaceDirectory(requestId: string, relativePath: string) {
  if (!workspaceDirectory.value) return
  const posted = postBridgeMessage(relativePath ? 'LoadWorkspaceDirectory' : 'RefreshWorkspaceFiles', {
    requestId,
    workingDirectory: workspaceDirectory.value,
    relativePath,
  })
  if (!posted && import.meta.env.DEV) {
    window.queueMicrotask(() => {
      workspaceDirectoryUpdate.value = {
        requestId,
        workingDirectory: workspaceDirectory.value ?? '',
        relativePath,
        entries: previewWorkspaceEntries(relativePath),
        inaccessibleEntries: 0,
        error: null,
      }
    })
  }
}

function searchWorkspaceFiles(requestId: string, query: string, includeIgnored: boolean) {
  if (!workspaceDirectory.value) return
  const posted = postBridgeMessage('SearchWorkspaceFiles', {
    requestId,
    workingDirectory: workspaceDirectory.value,
    query,
    includeIgnored,
  })
  if (!posted && import.meta.env.DEV) {
    const normalized = query.toLocaleLowerCase()
    window.queueMicrotask(() => {
      workspaceSearchUpdate.value = {
        requestId,
        workingDirectory: workspaceDirectory.value ?? '',
        query,
        entries: Object.values(previewWorkspaceTree).flat().filter(entry =>
          (includeIgnored || !entry.isIgnored) &&
          (entry.name.toLocaleLowerCase().includes(normalized) || entry.relativePath.toLocaleLowerCase().includes(normalized))),
        truncated: false,
        visitedEntries: Object.values(previewWorkspaceTree).flat().length,
        inaccessibleEntries: 0,
        error: null,
      }
    })
  }
}

function revealWorkspaceEntry(entry: WorkspaceFileEntry) {
  if (!workspaceDirectory.value) return
  postBridgeMessage('RevealWorkspaceEntry', {
    workingDirectory: workspaceDirectory.value,
    relativePath: entry.relativePath,
  })
}

function previewWorkspaceEntry(
  entry: Omit<WorkspaceFileEntry, 'isIgnored' | 'ignoreSource'> &
    Partial<Pick<WorkspaceFileEntry, 'isIgnored' | 'ignoreSource'>>,
): WorkspaceFileEntry {
  return { isIgnored: false, ignoreSource: null, ...entry }
}

const previewWorkspaceTree: Record<string, WorkspaceFileEntry[]> = {
  '': [
    previewWorkspaceEntry({ name: '.git', relativePath: '.git', isDirectory: true, hasChildren: true, isReparsePoint: false, isIgnored: true, ignoreSource: 'built-in' }),
    previewWorkspaceEntry({ name: 'docs', relativePath: 'docs', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'src', relativePath: 'src', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'tests', relativePath: 'tests', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: '.editorconfig', relativePath: '.editorconfig', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: '.gitignore', relativePath: '.gitignore', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'AGENTS.md', relativePath: 'AGENTS.md', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'Directory.Build.props', relativePath: 'Directory.Build.props', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'Directory.Packages.props', relativePath: 'Directory.Packages.props', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'global.json', relativePath: 'global.json', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'PiCompanion.sln', relativePath: 'PiCompanion.sln', isDirectory: false, hasChildren: false, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'README.md', relativePath: 'README.md', isDirectory: false, hasChildren: false, isReparsePoint: false }),
  ],
  src: [
    previewWorkspaceEntry({ name: 'PiCompanion.Application', relativePath: 'src/PiCompanion.Application', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'PiCompanion.Chat', relativePath: 'src/PiCompanion.Chat', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'PiCompanion.Core', relativePath: 'src/PiCompanion.Core', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'PiCompanion.Desktop', relativePath: 'src/PiCompanion.Desktop', isDirectory: true, hasChildren: true, isReparsePoint: false }),
  ],
  docs: [
    previewWorkspaceEntry({ name: 'stage-7-delivery.md', relativePath: 'docs/stage-7-delivery.md', isDirectory: false, hasChildren: false, isReparsePoint: false }),
  ],
  tests: [
    previewWorkspaceEntry({ name: 'PiCompanion.Core.Tests', relativePath: 'tests/PiCompanion.Core.Tests', isDirectory: true, hasChildren: true, isReparsePoint: false }),
    previewWorkspaceEntry({ name: 'PiCompanion.Extension.Tests', relativePath: 'tests/PiCompanion.Extension.Tests', isDirectory: true, hasChildren: true, isReparsePoint: false }),
  ],
}

const previewWorkspaceGitEntries: WorkspaceGitEntry[] = [
  { relativePath: 'README.md', originalRelativePath: null, status: ' M', indexStatus: ' ', workingTreeStatus: 'M', kind: 'Modified', isStaged: false, isUnstaged: true, isUntracked: false, isBinary: false, addedLines: 6, deletedLines: 2 },
  { relativePath: 'src/PiCompanion.Chat/src/App.vue', originalRelativePath: null, status: ' M', indexStatus: ' ', workingTreeStatus: 'M', kind: 'Modified', isStaged: false, isUnstaged: true, isUntracked: false, isBinary: false, addedLines: 18, deletedLines: 5 },
  { relativePath: 'src/PiCompanion.Chat/src/styles.css', originalRelativePath: null, status: ' M', indexStatus: ' ', workingTreeStatus: 'M', kind: 'Modified', isStaged: false, isUnstaged: true, isUntracked: false, isBinary: false, addedLines: 24, deletedLines: 8 },
  { relativePath: 'tests/PiCompanion.Core.Tests/WorkspaceGitBrowserTests.cs', originalRelativePath: null, status: '??', indexStatus: '?', workingTreeStatus: '?', kind: 'Added', isStaged: false, isUnstaged: true, isUntracked: true, isBinary: false, addedLines: 72, deletedLines: 0 },
]

function previewWorkspaceEntries(relativePath: string) {
  return previewWorkspaceTree[relativePath] ?? []
}

function saveCompanionSettings(settings: SettingsSnapshot['values']) {
  postBridgeMessage('SaveCompanionSettings', { settings })
}

function previewAppearance(appearance: {
  language: SettingsSnapshot['values']['general']['language']
  theme: SettingsSnapshot['values']['general']['theme']
}) {
  appearancePreview.value = appearance
}

function savePiAgentSettings(agent: SettingsSnapshot['values']['agent']) {
  postBridgeMessage('SavePiAgentSettings', { agent })
}

function addPiCustomProvider(provider: PiCustomProviderInfo, apiKey: string, modelsConfigRevision: string | null) {
  postBridgeMessage('AddPiCustomProvider', { provider, apiKey, modelsConfigRevision })
}

function updatePiCustomProvider(provider: PiCustomProviderInfo, apiKey: string, modelsConfigRevision: string | null) {
  postBridgeMessage('UpdatePiCustomProvider', { provider, apiKey, modelsConfigRevision })
}

function deletePiCustomProvider(providerId: string, modelsConfigRevision: string | null) {
  postBridgeMessage('DeletePiCustomProvider', { providerId, modelsConfigRevision })
}

function showTransientNotice(id: string, message: string, succeeded: boolean, duration = 4500) {
  if (transientNoticeTimer) window.clearTimeout(transientNoticeTimer)
  transientNotice.value = { id, message, succeeded }
  transientNoticeTimer = window.setTimeout(() => {
    if (transientNotice.value?.id === id) transientNotice.value = null
    transientNoticeTimer = 0
  }, duration)
}

function dismissTransientNotice() {
  transientNotice.value = null
  if (transientNoticeTimer) window.clearTimeout(transientNoticeTimer)
  transientNoticeTimer = 0
}

watch(settingsAction, action => {
  if (!action || action.silent) return
  showTransientNotice(`settings:${Date.now()}`, action.message, action.succeeded)
})

watch(workspaceGitActionResult, action => {
  if (!action || (action.succeeded && (action.action === 'stage' || action.action === 'unstage'))) return
  showTransientNotice(`git:${action.requestId}`, action.message, action.succeeded)
})

function createPreviewSettingsSnapshot(): SettingsSnapshot {
  const providers = [
    { id: 'openai-codex', name: 'OpenAI Codex', configured: true, authType: 'oauth' as const, authSource: 'stored', supportsApiKey: false, supportsOAuth: true },
    { id: 'amazon-bedrock', name: 'Amazon Bedrock', configured: false, authType: null, authSource: null, supportsApiKey: false, supportsOAuth: false },
    { id: 'ant-ling', name: 'Ant Ling', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'anthropic', name: 'Anthropic', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: true },
    { id: 'azure-openai-responses', name: 'Azure OpenAI', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'cerebras', name: 'Cerebras', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'cloudflare-ai-gateway', name: 'Cloudflare AI Gateway', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'deepseek', name: 'DeepSeek', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'github-copilot', name: 'GitHub Copilot', configured: false, authType: null, authSource: null, supportsApiKey: false, supportsOAuth: true },
    { id: 'openai', name: 'OpenAI', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'google', name: 'Google Gemini', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'google-vertex', name: 'Google Vertex AI', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'groq', name: 'Groq', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'huggingface', name: 'Hugging Face', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'kimi-coding', name: 'Kimi For Coding', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: true },
    { id: 'mistral', name: 'Mistral', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: false },
    { id: 'openrouter', name: 'OpenRouter', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: true },
  ]
  return {
    values: {
      general: { launchAtLogin: false, keepRunningInTray: true, language: 'zh-CN', theme: 'dark', logLevel: 'information', uiScalePercent: 100, gitAutoRefreshSeconds: 0, conversationDetailLevel: 'normal' },
      monitor: { position: 'top-right', showOnStartup: true, alwaysOnTop: true, autoCollapseSeconds: 8, animationsEnabled: true },
      tasks: { aiTitleEnabled: true, aiTitleModel: 'openai-codex/gpt-5.6-luna', aiSummaryEnabled: true, aiSummaryModel: 'openai-codex/gpt-5.6-luna', aiMetadataModel: 'openai-codex/gpt-5.6-luna', recentTaskCount: 5, recentTaskSubtitle: 'workspace', permissionMode: 'standard', fileChangesExpandedByDefault: false, completionBehavior: 'keep-expanded', autoStartLocalQueueEnabled: false, autoStartLocalQueueDelaySeconds: 15 },
      agent: { defaultModel: 'openai-codex/gpt-5.6-sol', defaultThinkingLevel: 'xhigh', autoCompact: true, autoRetry: true, compactionReserveTokens: 16384, compactionKeepRecentTokens: 20000, retryMaxRetries: 3, retryBaseDelayMilliseconds: 2000, retryMaxDelayMilliseconds: 60000, steeringMode: 'one-at-a-time', followUpMode: 'one-at-a-time' },
      notifications: { notifyOnCompletion: true, notifyOnFailure: true, notifyWhenAttentionRequired: true, playSound: true, onlyWhenAppIsInBackground: true },
      dataRetention: { taskHistoryDays: 0, recycleBinDays: 30, logDays: 30 },
      modelVisibility: { hiddenModelReferences: [], legacyPiScopeMigrationCompleted: true },
    },
    pi: {
      available: true,
      version: '0.83.0',
      runtimePath: 'C:\\PiRuntime\\dist\\cli.js',
      defaultModel: 'openai-codex/gpt-5.6-sol',
      defaultThinkingLevel: 'xhigh',
      autoCompact: true,
      autoRetry: true,
      compactionReserveTokens: 16384,
      compactionKeepRecentTokens: 20000,
      retryMaxRetries: 3,
      retryBaseDelayMilliseconds: 2000,
      retryMaxDelayMilliseconds: 60000,
      steeringMode: 'one-at-a-time',
      followUpMode: 'one-at-a-time',
      providers,
      models: [
        { provider: 'openai-codex', id: 'gpt-5.6-sol', name: 'GPT-5.6 Sol', reasoning: true, contextWindow: 272000, input: ['text', 'image'], thinkingLevels: ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] },
        { provider: 'openai-codex', id: 'gpt-5.6-luna', name: 'GPT-5.6 Luna', reasoning: true, contextWindow: 272000, input: ['text', 'image'], thinkingLevels: ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] },
      ],
      enabledModels: null,
      customProviders: [],
      modelsConfigRevision: null,
      error: null,
    },
    dataDirectory: 'C:\\Users\\you\\AppData\\Local\\PiCompanion',
    logDirectory: 'C:\\Users\\you\\AppData\\Local\\PiCompanion\\logs',
  }
}

function handleTranscriptScroll() {
  const element = transcript.value
  if (!element) return
  stickTranscriptToBottom.value = element.scrollHeight - element.scrollTop - element.clientHeight < 96
}

function resolveInteraction(block: TranscriptBlock, approved: boolean, response?: string) {
  postBridgeMessage('ResolveInteraction', { interactionId: block.interactionId, approved, response })
}
</script>

<template>
  <div
    class="workspace"
    :class="{
      'sidebar-collapsed': sidebarCollapsed,
      'inspector-collapsed': inspectorCollapsed || mainView !== 'chat',
    }"
    :style="workspaceStyle"
  >
    <div
      class="workspace-content"
      :inert="settingsOpen || editingLocalMessage || editingWorkspace || workspaceTrustDialogWorkspace ? true : undefined"
    >
    <WorkspaceSidebar
      :recent-tasks="visibleRecentTasks"
      :workspaces="store.workspaces"
      :recent-task-subtitle="settingsSnapshot.values.tasks.recentTaskSubtitle"
      :selected-history-task="selectedHistoryTask"
      :current-task-id="store.currentTask?.id"
      :view="mainView"
      :width="sidebarWidth"
      @new-task="beginNewTask"
      @show-view="showNavigationView"
      @select-task="selectTask"
      @open-context-menu="openTaskContextMenu"
      @begin-resize="beginSidebarResize"
      @set-width="setSidebarWidth"
      @open-settings="settingsOpen = true"
    />

    <main v-if="mainView === 'chat'" class="main">
      <header class="topbar">
        <div class="topbar-leading">
          <UiButton
            class="sidebar-toggle"
            type="button"
            :aria-label="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
            :title="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
            @click="sidebarCollapsed = !sidebarCollapsed"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <rect x="3.5" y="4" width="17" height="16" rx="2" />
              <path d="M9 4v16" />
            </svg>
          </UiButton>
          <div class="location">
            <strong>{{ store.currentTask?.title ?? t('新任务') }}</strong>
            <WorkspaceLocationMenu
              v-if="store.currentTask?.scopeKind === 'Workspace'"
              :path="store.currentTask.workingDirectory"
              @select="openWorkspaceLocation"
            />
            <UiButton
              v-else
              class="location-path"
              type="button"
              :title="store.currentTask ? currentDirectory : `${currentDirectory} · ${t('点击选择目录')}`"
              :disabled="Boolean(store.currentTask)"
              @click="selectWorkingDirectory"
            >{{ currentDirectory }}</UiButton>
          </div>
        </div>
        <div class="topbar-actions">
          <UiButton
            v-if="conversationSkillsWorkspace"
            class="workspace-trust-badge"
            :class="`trust-${conversationWorkspaceTrustStatus}`"
            type="button"
            :title="t('查看或更改工作区信任')"
            @click="openWorkspaceTrust(conversationSkillsWorkspace)"
          >
            <span aria-hidden="true">{{ conversationWorkspaceTrustStatus === 'trusted' ? '✓' : conversationWorkspaceTrustStatus === 'declined' ? '–' : '?' }}</span>
            {{ conversationWorkspaceTrustLabel }}
          </UiButton>
          <UiButton
            v-if="canViewConversationSkills"
            class="topbar-skill-button"
            type="button"
            :aria-label="t('查看技能')"
            :title="t(isGeneralChat ? '查看全局技能' : '查看工作区技能')"
            @click="openConversationSkills"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9.2 4.5a2.8 2.8 0 1 1 5.6 0V7H17v2.2a2.8 2.8 0 1 1 0 5.6V17h-2.2a2.8 2.8 0 1 1-5.6 0H7v-2.2a2.8 2.8 0 1 1 0-5.6V7h2.2z" /></svg>
            <span>{{ t('技能') }}</span>
          </UiButton>
          <UiButton
            class="sidebar-toggle inspector-toggle"
            type="button"
            :aria-label="t(inspectorCollapsed ? '展开右侧栏' : '收起右侧栏')"
            :title="t(inspectorCollapsed ? '展开右侧栏' : '收起右侧栏')"
            @click="inspectorCollapsed = !inspectorCollapsed"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <rect x="3.5" y="4" width="17" height="16" rx="2" />
              <path d="M15 4v16" />
            </svg>
          </UiButton>
        </div>
      </header>

      <section ref="transcript" class="transcript" @scroll.passive="handleTranscriptScroll">
        <div v-if="!store.currentTask" class="empty-state">
          <span class="empty-mark">π</span>
          <h1>{{ t('今天想完成什么？') }}</h1>
          <p>{{ t('选择工作目录处理项目，或进入直接对话。') }}</p>

          <div class="mode-picker">
            <UiButton
              class="workspace-picker"
              :class="{ selected: hasWorkingDirectory }"
              type="button"
              :aria-pressed="hasWorkingDirectory"
              @click="selectWorkingDirectory"
            >
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M3.5 7.5h6l2-2h9v13h-17z" />
              </svg>
              <span>{{ store.draft?.workingDirectory ?? t('选择工作目录') }}</span>
            </UiButton>
            <UiButton
              class="workspace-picker direct-chat-picker"
              :class="{ selected: directChatSelected }"
              type="button"
              :aria-pressed="directChatSelected"
              @click="selectDirectChat"
            >
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M5 5.5h14v10H10l-4 3v-3H5z" />
              </svg>
              <span>{{ t('直接对话') }}</span>
            </UiButton>
          </div>

          <div class="starters">
            <UiButton type="button" :disabled="!hasWorkingDirectory" @click="startDemo('Success', t('检查这个目录的工程结构并总结主要模块'))">
              <span>{{ t('分析工程') }}</span><small>{{ t('只读查看目录与文件') }}</small><b>›</b>
            </UiButton>
            <UiButton type="button" :disabled="!hasWorkingDirectory" @click="startDemo('InteractiveSuccess', t('查找这个工程中可能需要关注的 TODO 并给出摘要'))">
              <span>{{ t('检查 TODO') }}</span><small>{{ t('搜索并汇总待办项') }}</small><b>›</b>
            </UiButton>
            <UiButton type="button" :disabled="!hasWorkingDirectory" @click="startDemo('Failure', t('阅读 README 和项目文档，概括当前实现状态'))">
              <span>{{ t('阅读文档') }}</span><small>{{ t('生成项目状态摘要') }}</small><b>›</b>
            </UiButton>
          </div>
        </div>

        <div v-else class="conversation">
          <ConversationRun
            v-for="run in conversationRuns"
            :key="run.id"
            :run="run"
            :agent-name="modelDisplayName(run.model)"
            :view-mode="viewMode"
            :current-run-id="store.currentTask?.runId"
            :needs-interaction="store.needsInteraction"
            :task-active="store.isActive"
            :file-changes-expanded-by-default="settingsSnapshot.values.tasks.fileChangesExpandedByDefault"
            @open-diff="openFileDiff"
            @request-recovery="recoveryTarget = $event"
            @resolve-interaction="resolveInteraction"
            @abort-retry="postBridgeMessage('AbortRetry')"
            @open-external-link="postBridgeMessage('OpenExternalLink', { url: $event })"
            @open-artifact="postBridgeMessage('OpenArtifact', { artifactId: $event })"
            @save-artifact="postBridgeMessage('SaveArtifact', { artifactId: $event })"
          />
        </div>
      </section>

      <ComposerPanel
        ref="composerPanel"
        v-model:prompt="prompt"
        v-model:selected-model="selectedModel"
        v-model:selected-thinking-level="selectedThinkingLevel"
        v-model:selected-permission-mode="selectedPermissionMode"
        :task-active="store.isActive"
        :has-current-task="Boolean(store.currentTask)"
        :mode-selected="isModeSelected"
        :general-chat="isGeneralChat"
        :attachments="store.draft?.attachments ?? []"
        :local-queued-messages="store.currentTask?.localQueuedMessages ?? []"
        :local-queue-auto-start-message-id="store.currentTask?.localQueueAutoStartMessageId"
        :local-queue-auto-start-at="store.currentTask?.localQueueAutoStartAt"
        :model-options="modelOptions"
        :selected-model-supports-images="selectedModelInfo?.input.includes('image') ?? false"
        :thinking-level-options="thinkingLevelOptions"
        :skill-options="composerSkillOptions"
        :skills-loading="skillsLoading"
        :workspace-git-change-count="workspaceGitChangeCount"
        @select-attachments="selectAttachments"
        @remove-attachment="removeAttachment"
        @abort="postBridgeMessage('AbortRun')"
        @submit="submit"
        @edit-local-message="openLocalMessageEditor"
        @remove-local-message="removeLocalMessage"
        @move-local-message="moveLocalMessage"
        @dispatch-local-message="dispatchLocalMessage"
        @cancel-local-queue-auto-start="postBridgeMessage('CancelLocalQueueAutoStart')"
        @open-git="openGitInspector"
        @request-skills="ensureComposerSkills"
        @request-full-access="requestFullAccess"
      />
    </main>

    <TaskManagementView
      v-else-if="mainView === 'history'"
      v-model:search="historySearch"
      v-model:status="historyStatus"
      :tasks="store.historyTasks"
      :workspaces="store.workspaces"
      :sidebar-collapsed="sidebarCollapsed"
      @toggle-sidebar="sidebarCollapsed = !sidebarCollapsed"
      @select-task="selectTask"
      @open-context-menu="openTaskContextMenu"
      @create-workspace="postBridgeMessage('CreateWorkspace')"
      @new-task-in-workspace="beginNewTaskInWorkspace"
      @manage-workspace-skills="openWorkspaceSkills"
      @edit-workspace="editingWorkspaceId = $event"
      @hide-workspace="hidingWorkspaceId = $event"
    />

    <SkillsView
      v-else-if="mainView === 'skills'"
      :snapshot="skillsSnapshot"
      :loading="skillsLoading"
      :error="skillsError"
      :sidebar-collapsed="sidebarCollapsed"
      :workspaces="store.workspaces"
      :removing-installation-id="skillRemovalPendingId"
      :removal-result="skillRemovalResult"
      :trusting-workspace-id="skillTrustPendingWorkspaceId"
      :trust-result="skillTrustResult"
      :import-phase="skillImportPhase"
      :import-source="skillImportSource"
      :import-preparation="skillImportPreparation"
      :import-error="skillImportError"
      :import-result="skillImportResult"
      @toggle-sidebar="sidebarCollapsed = !sidebarCollapsed"
      @refresh="refreshSkills"
      @remove-installation="removeSkillInstallation"
      @trust-workspace="requestSkillWorkspaceTrust"
      @open-import="openSkillImport"
      @begin-import="beginSkillImport"
      @prepare-import="prepareSkillImport"
      @confirm-import="confirmSkillImport"
      @cancel-import="cancelSkillImport"
    />

    <FeaturePlaceholderView
      v-else
      :view="mainView"
      :sidebar-collapsed="sidebarCollapsed"
      @toggle-sidebar="sidebarCollapsed = !sidebarCollapsed"
    />

    <WorkspaceInspector
      v-if="mainView === 'chat' && !inspectorCollapsed"
      :working-directory="workspaceDirectory"
      :directory-update="workspaceDirectoryUpdate"
      :search-update="workspaceSearchUpdate"
      :git-update="workspaceGitUpdate"
      :git-history-update="workspaceGitHistoryUpdate"
      :git-history-loading="workspaceGitHistoryLoading"
      :git-action-result="workspaceGitActionResult"
      :git-pending-action="workspaceGitPendingAction"
      :git-commit-message-result="workspaceGitCommitMessageResult"
      :git-commit-message-loading="workspaceGitCommitMessageLoading"
      :task-active="store.isActive"
      :task-id="store.currentTask?.id ?? null"
      :task-title="store.currentTask?.title ?? null"
      :selected-model="selectedModelInfo"
      :selected-model-reference="selectedModel"
      :session-model-reference="store.currentTask?.model ?? null"
      :session-update="sessionStatisticsUpdate"
      :session-loading="sessionStatisticsLoading"
      :session-manual-load-available="Boolean(store.currentTask && !store.isActive)"
      :active-tab="inspectorTab"
      :width="inspectorWidth"
      @load-directory="loadWorkspaceDirectory"
      @search="searchWorkspaceFiles"
      @reveal="revealWorkspaceEntry"
      @select-tab="selectInspectorTab"
      @refresh-git="manuallyRefreshWorkspaceGit"
      @refresh-git-history="refreshWorkspaceGitHistory"
      @refresh-session="refreshSessionStatistics"
      @open-git-diff="openWorkspaceGitDiff"
      @open-git-commit="openWorkspaceGitCommit"
      @stage-git="stageWorkspaceGit"
      @unstage-git="unstageWorkspaceGit"
      @commit-git="commitWorkspaceGit"
      @generate-git-commit-message="generateWorkspaceGitCommitMessage"
      @switch-git-branch="switchWorkspaceGitBranch"
      @create-git-branch="createWorkspaceGitBranch"
      @update-git-branch="updateWorkspaceGitBranch"
      @abort-git-operation="abortWorkspaceGitOperation"
      @begin-resize="beginInspectorResize"
      @set-width="setInspectorWidth"
    />

    <SkillManagementModal
      v-if="skillManagerContext"
      :snapshot="skillsSnapshot"
      :loading="skillsLoading"
      :error="skillsError"
      :workspace="skillManagerWorkspace"
      :global-only="skillManagerContext.directChat"
      :removing-installation-id="skillRemovalPendingId"
      :removal-result="skillRemovalResult"
      :trusting-workspace-id="skillTrustPendingWorkspaceId"
      :trust-result="skillTrustResult"
      @close="skillManagerContext = null"
      @refresh="refreshSkills"
      @remove-installation="removeSkillInstallation"
      @trust-workspace="requestSkillWorkspaceTrust"
    />

    <UiDialog
      v-if="fullAccessConfirmationOpen"
      :title="t('启用完全访问？')"
      :description="t('完全访问允许此任务访问工作区外文件并直接执行命令。')"
      overlay-class="dialog-backdrop"
      content-class="task-dialog full-access-confirm-dialog"
      alert
      :close-on-backdrop="false"
      @close="fullAccessConfirmationOpen = false"
    >
      <h2>{{ t('启用完全访问？') }}</h2>
      <p>{{ t('此任务将能在当前 Windows 用户权限范围内访问任意本地路径并执行命令，不再逐次请求授权。') }}</p>
      <p>{{ t('这不会获得管理员权限，并且只对即将发送的这个任务生效。请仅在你信任任务内容时启用。') }}</p>
      <div class="dialog-actions">
        <UiButton type="button" @click="fullAccessConfirmationOpen = false">{{ t('取消') }}</UiButton>
        <UiButton class="danger-action" type="button" @click="confirmFullAccess">{{ t('启用完全访问') }}</UiButton>
      </div>
    </UiDialog>

    <UiDialog
      v-if="workspaceTrustDialogWorkspace"
      :title="t('工作区信任')"
      overlay-class="dialog-backdrop"
      content-class="task-dialog skill-trust-confirm-dialog workspace-trust-dialog"
      alert
      :close-on-backdrop="false"
      @close="cancelWorkspaceTrustDecision"
    >
        <h2>{{ t('是否信任“{name}”？', { name: workspaceTrustDialogWorkspace.name }) }}</h2>
        <p>{{ t('信任后，Pi 可以加载此工作区提供的项目设置、技能和系统提示。这些内容可能改变智能体的行为。') }}</p>
        <p>{{ t('工作区信任不会改变文件访问或命令执行权限；这些仍由任务权限模式控制。') }}</p>
        <p v-if="workspaceTrustDialogWorkspace.trustInherited && workspaceTrustDialogWorkspace.trustDecisionPath">
          {{ t('当前决定继承自：{path}', { path: workspaceTrustDialogWorkspace.trustDecisionPath }) }}
        </p>
        <code>{{ workspaceTrustDialogWorkspace.workingDirectory }}</code>
        <div class="dialog-actions">
          <UiButton type="button" :disabled="workspaceTrustDecisionPending" @click="cancelWorkspaceTrustDecision">
            {{ t('取消') }}
          </UiButton>
          <UiButton
            type="button"
            :disabled="workspaceTrustDecisionPending"
            @click="setWorkspaceTrustDecision(false)"
          >
            {{ t(pendingWorkspaceRun ? '不信任并开始' : '设为不信任') }}
          </UiButton>
          <UiButton
            class="primary"
            type="button"
            :disabled="workspaceTrustDecisionPending"
            @click="setWorkspaceTrustDecision(true)"
          >
            {{ t(workspaceTrustDecisionPending ? '正在保存…' : pendingWorkspaceRun ? '信任并开始' : '信任工作区') }}
          </UiButton>
        </div>
    </UiDialog>

    <UiDialog
      v-if="skillTrustConfirmationWorkspace"
      :title="t('信任此工作区？')"
      overlay-class="dialog-backdrop"
      content-class="task-dialog skill-trust-confirm-dialog"
      alert
      :close-on-backdrop="false"
      @close="skillTrustConfirmationWorkspaceId = null"
    >
        <h2>{{ t('信任“{name}”？', { name: skillTrustConfirmationWorkspace.name }) }}</h2>
        <p>{{ t('确认会将整个工作区标记为受 Pi 信任；该决定也会影响其他项目级 Pi 资源。') }}</p>
        <p>{{ t('Pi 将加载此目录中的项目技能。请仅在你信任该目录内容和来源时继续。') }}</p>
        <code>{{ skillTrustConfirmationWorkspace.path }}</code>
        <div class="dialog-actions">
          <UiButton type="button" @click="skillTrustConfirmationWorkspaceId = null">{{ t('取消') }}</UiButton>
          <UiButton class="primary" type="button" @click="confirmSkillWorkspaceTrust">{{ t('信任工作区') }}</UiButton>
        </div>
    </UiDialog>

    <FileDiffDialog v-if="store.fileDiff" :diff="store.fileDiff" @close="store.clearFileDiff()" />
    <CommitDiffDialog
      v-if="store.commitDiff"
      :diff="store.commitDiff"
      @close="store.clearCommitDiff()"
    />

    <UiDialog
      v-if="recoveryTarget"
      :title="t('恢复这个文件？')"
      overlay-class="dialog-backdrop"
      content-class="task-dialog recovery-dialog"
      @close="recoveryTarget = null"
    >
        <h2>{{ t('恢复这个文件？') }}</h2>
        <p>{{ t('将恢复 Agent 修改前的内容。恢复前会重新校验当前 Hash；如果文件后来又被修改，操作会停止并报告冲突。') }}</p>
        <code>{{ recoveryTarget.relativePath }}</code>
        <div class="dialog-actions">
          <UiButton type="button" @click="recoveryTarget = null">{{ t('取消') }}</UiButton>
          <UiButton class="primary" type="button" @click="confirmRecovery">{{ t('安全恢复') }}</UiButton>
        </div>
    </UiDialog>

    <div v-if="store.recoveryNotice" class="recovery-notice" :class="store.recoveryNotice.succeeded ? 'success' : 'danger'">
      <span>{{ store.recoveryNotice.message }}</span>
      <UiButton type="button" :aria-label="t('关闭恢复结果')" @click="store.clearRecoveryNotice()">×</UiButton>
    </div>

    <TaskManagementOverlays
      v-model:rename-title="renameTitle"
      :context-menu="taskContextMenu"
      :rename-target="renameTaskTarget"
      :confirmation="confirmAction"
      :confirm-title="confirmDialogTitle"
      :confirm-description="confirmDialogDescription"
      @open-rename="openRenameTask"
      @request-action="requestTaskAction"
      @restore-task="restoreTask"
      @dismiss-rename="renameTaskTarget = null"
      @submit-rename="submitRenameTask"
      @dismiss-confirmation="confirmAction = null"
      @confirm-action="confirmTaskManagementAction"
    />
    </div>

    <LocalMessageEditorDialog
      v-if="editingLocalMessage"
      :item="editingLocalMessage"
      :selected-attachments="selectedLocalMessageAttachments"
      @confirm="confirmLocalMessageEditor"
      @cancel="closeLocalMessageEditor"
      @select-attachments="selectLocalMessageAttachments"
    />

    <WorkspacePresentationDialog
      v-if="editingWorkspace"
      :workspace="editingWorkspace"
      @dismiss="editingWorkspaceId = null"
      @save="saveWorkspacePresentation"
    />

    <UiDialog
      v-if="hidingWorkspace"
      :title="t('隐藏工作区？')"
      overlay-class="dialog-backdrop"
      content-class="task-dialog"
      alert
      @close="hidingWorkspaceId = null"
    >
        <h2 :id="`hide-workspace-${hidingWorkspace.id}`">{{ t('隐藏工作区？') }}</h2>
        <p>{{ t('“{name}”及其任务会从 Pi Companion 中隐藏。不会删除任务数据或本地文件；重新添加这个目录后，任务会再次显示。', { name: hidingWorkspace.name }) }}</p>
        <div class="dialog-actions">
          <UiButton type="button" @click="hidingWorkspaceId = null">{{ t('取消') }}</UiButton>
          <UiButton class="primary" type="button" @click="confirmHideWorkspace">{{ t('隐藏工作区') }}</UiButton>
        </div>
    </UiDialog>

    <SettingsModal
      v-if="settingsOpen"
      :snapshot="settingsSnapshot"
      :action="settingsAction"
      :oauth-login-progress="piOAuthLoginProgress"
      :recycle-bin-tasks="store.recycleBinTasks"
      @close="settingsOpen = false"
      @save-companion="saveCompanionSettings"
      @preview-appearance="previewAppearance"
      @save-agent="savePiAgentSettings"
      @reload-pi="postBridgeMessage('ReloadPiConfiguration')"
      @refresh-model-catalog="postBridgeMessage('RefreshPiModelCatalog')"
      @save-pi-api-key="(providerId, apiKey) => postBridgeMessage('SavePiApiKey', { providerId, apiKey })"
      @logout-pi-provider="providerId => postBridgeMessage('LogoutPiProvider', { providerId })"
      @add-pi-custom-provider="addPiCustomProvider"
      @update-pi-custom-provider="updatePiCustomProvider"
      @delete-pi-custom-provider="deletePiCustomProvider"
      @open-pi-login="providerId => postBridgeMessage('OpenPiLogin', { providerId })"
      @cancel-pi-login="providerId => postBridgeMessage('CancelPiOAuthLogin', { providerId })"
      @open-data-directory="postBridgeMessage('OpenDataDirectory')"
      @open-log-directory="postBridgeMessage('OpenLogDirectory')"
      @export-diagnostics="postBridgeMessage('ExportDiagnostics')"
      @clear-cache="postBridgeMessage('ClearCache')"
      @empty-recycle-bin="postBridgeMessage('EmptyRecycleBin')"
      @restore-recycle-task="taskId => postBridgeMessage('RestoreTaskFromRecycleBin', { taskId })"
      @delete-recycle-task="taskId => postBridgeMessage('DeleteTaskPermanently', { taskId })"
    />

    <Transition name="app-toast">
      <div
        v-if="transientNotice"
        class="app-toast"
        :class="transientNotice.succeeded ? 'success' : 'danger'"
        :role="transientNotice.succeeded ? 'status' : 'alert'"
        aria-live="polite"
      >
        <span class="app-toast-icon" aria-hidden="true">{{ transientNotice.succeeded ? '✓' : '!' }}</span>
        <span>{{ transientNotice.message }}</span>
        <UiButton type="button" :aria-label="t('关闭提醒')" @click="dismissTransientNotice">×</UiButton>
      </div>
    </Transition>

    <div
      v-if="isAttachmentDragActive"
      class="attachment-drop-overlay"
      :class="{ blocked: store.isActive || mainView !== 'chat' }"
      aria-live="polite"
    >
      <div>
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V5m0 0L8 9m4-4 4 4M5 15v4h14v-4" /></svg>
        <strong>{{ t(store.isActive ? '任务运行中，暂不能添加附件' : mainView !== 'chat' ? '请回到智能体对话添加附件' : '松开以添加附件') }}</strong>
        <span>{{ t('支持从资源管理器拖入一个或多个文件') }}</span>
      </div>
    </div>
  </div>
</template>
