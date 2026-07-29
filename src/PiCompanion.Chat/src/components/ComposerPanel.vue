<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { UiButton, UiSelect, UiTextarea } from '@/components/ui'
import ComposerActionMenu from '@/components/ComposerActionMenu.vue'
import type { ComposerCommandName, ComposerSkillOption } from '@/composerCommands'
import type { ComposerAttachment, LocalQueuedMessage, PermissionMode, PiThinkingLevel } from '@/types/bridge'
import { useI18n } from '@/i18n'

const { t } = useI18n()

interface SelectOption {
  value: string
  label: string
  group?: string
  tooltip?: string
}

interface ComposerSuggestion {
  id: string
  value: string
  label: string
  syntax?: string
  description: string
  meta?: string
}

const props = defineProps<{
  taskActive: boolean
  hasCurrentTask: boolean
  modeSelected: boolean
  generalChat?: boolean
  attachments: ComposerAttachment[]
  localQueuedMessages: LocalQueuedMessage[]
  localQueueAutoStartMessageId?: string | null
  localQueueAutoStartAt?: string | null
  modelOptions: SelectOption[]
  selectedModelSupportsImages?: boolean
  thinkingLevelOptions: SelectOption[]
  skillOptions: ComposerSkillOption[]
  skillsLoading?: boolean
  workspaceGitChangeCount: number
}>()

const emit = defineEmits<{
  selectAttachments: []
  removeAttachment: [path: string]
  abort: []
  submit: []
  editLocalMessage: [messageId: string]
  removeLocalMessage: [messageId: string]
  moveLocalMessage: [messageId: string, newIndex: number]
  dispatchLocalMessage: [messageId: string, delivery: 'steer' | 'follow-up' | 'new-run']
  cancelLocalQueueAutoStart: []
  openGit: []
  requestSkills: []
  requestFullAccess: []
}>()

const prompt = defineModel<string>('prompt', { required: true })
const selectedModel = defineModel<string>('selectedModel', { required: true })
const selectedThinkingLevel = defineModel<PiThinkingLevel>('selectedThinkingLevel', { required: true })
const selectedPermissionMode = defineModel<PermissionMode>('selectedPermissionMode', { required: true })
const permissionModeModel = computed({
  get: () => selectedPermissionMode.value,
  set: (value: string) => {
    if (value === 'full-access' && selectedPermissionMode.value !== 'full-access') {
      emit('requestFullAccess')
      return
    }
    selectedPermissionMode.value = value as PermissionMode
  },
})
const thinkingLevelModel = computed({
  get: () => selectedThinkingLevel.value,
  set: (value: string) => { selectedThinkingLevel.value = value as typeof selectedThinkingLevel.value },
})
const input = ref<InstanceType<typeof UiTextarea> | null>(null)
const isExpanded = ref(false)
const suggestionsDismissedFor = ref<string | null>(null)
const activeSuggestionIndex = ref(0)
const skillRequestPending = ref(false)
const pendingSkillArgs = ref('')
const draggedMessageId = ref<string | null>(null)
const currentTime = ref(Date.now())
let countdownTimer = 0
const permissionModeOptions = computed(() => [
  { value: 'read-only', label: t('只读'), tooltip: t('仅允许读取、网络搜索和向用户提问') },
  { value: 'standard', label: t('标准访问'), tooltip: t('允许工作区内普通文件修改；Shell、敏感操作和工作区外访问会请求授权') },
  { value: 'full-access', label: t('完全访问'), tooltip: t('允许在当前 Windows 用户权限范围内访问任意本地路径并执行命令，不再请求授权；选择前需要确认'), tone: 'danger' as const },
])
const commandSuggestions = computed<Array<ComposerSuggestion & { name: ComposerCommandName }>>(() => [
  { name: 'compact', id: 'command:compact', value: '/compact ', label: '/compact', syntax: t('[压缩要求]'), description: t('压缩当前任务上下文；压缩要求可选') },
  { name: 'model', id: 'command:model', value: '/model ', label: '/model', syntax: t('<模型>'), description: t('选择后续运行使用的模型') },
  { name: 'new', id: 'command:new', value: '/new', label: '/new', description: t('新建任务') },
  { name: 'name', id: 'command:name', value: '/name ', label: '/name', syntax: t('<新名称>'), description: t('重命名当前任务；新名称必填') },
  { name: 'session', id: 'command:session', value: '/session', label: '/session', description: t('查看当前 Session 信息') },
  { name: 'settings', id: 'command:settings', value: '/settings', label: '/settings', description: t('打开设置') },
  { name: 'reload', id: 'command:reload', value: '/reload', label: '/reload', description: t('重新加载 Pi 本地状态') },
  { name: 'stop', id: 'command:stop', value: '/stop', label: '/stop', description: t('停止当前任务') },
  { name: 'help', id: 'command:help', value: '/help', label: '/help', description: t('查看可用指令') },
])
const slashSuggestions = computed<ComposerSuggestion[]>(() => {
  if (!props.modeSelected || suggestionsDismissedFor.value === prompt.value) return []
  const value = prompt.value
  if (!value.startsWith('/') || value.startsWith('//') || value.includes('\n')) return []

  if (value.startsWith('/skill:')) {
    const query = value.slice('/skill:'.length)
    if (/\s/u.test(query)) return []
    const normalized = query.toLocaleLowerCase()
    return props.skillOptions
      .filter(skill =>
        !normalized ||
        skill.name.toLocaleLowerCase().includes(normalized) ||
        skill.description.toLocaleLowerCase().includes(normalized))
      .map(skill => ({
        id: `skill:${skill.name}`,
        value: `/skill:${skill.name} `,
        label: skill.name,
        description: skill.description || t('没有提供技能描述'),
        meta: [skill.location, skill.manualOnly ? t('仅手动调用') : ''].filter(Boolean).join(' · '),
      }))
  }

  const model = /^\/model(?:\s+(.*))?$/u.exec(value)
  if (model?.[1] !== undefined) {
    const query = model[1].trim().toLocaleLowerCase()
    return props.modelOptions
      .filter(option =>
        !query ||
        option.label.toLocaleLowerCase().includes(query) ||
        option.value.toLocaleLowerCase().includes(query) ||
        option.group?.toLocaleLowerCase().includes(query))
      .map(option => ({
        id: `model:${option.value}`,
        value: `/model ${option.value}`,
        label: option.label,
        description: option.value,
        meta: option.group,
      }))
  }

  if (value.includes(' ')) return []
  const query = value.slice(1).toLocaleLowerCase()
  const commands: ComposerSuggestion[] = commandSuggestions.value
    .filter(command => !query || command.name.includes(query))
  if (!query || 'skill:'.startsWith(query)) {
    commands.push({
      id: 'command:skill',
      value: '/skill:',
      label: '/skill:',
      syntax: t('<技能名> <任务要求>'),
      description: t('手动调用一个当前可用的技能'),
    })
  }
  return commands
})
const commandArgumentHint = computed(() => {
  if (prompt.value === '/name ') {
    return {
      syntax: `/name ${t('<新名称>')}`,
      description: t('输入任务的新名称，然后发送。'),
    }
  }
  if (prompt.value === '/compact ') {
    return {
      syntax: `/compact ${t('[压缩要求]')}`,
      description: t('可以直接发送，或补充压缩时需要保留的内容。'),
    }
  }
  if (/^\/skill:[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\s$/u.test(prompt.value)) {
    return {
      syntax: `/skill:${prompt.value.slice('/skill:'.length).trim()} ${t('<任务要求>')}`,
      description: t('输入希望这个技能完成的任务，然后发送。'),
    }
  }
  return null
})
const activeSuggestion = computed(() =>
  slashSuggestions.value[Math.min(activeSuggestionIndex.value, Math.max(0, slashSuggestions.value.length - 1))] ?? null)

const placeholder = computed(() => {
  if (!props.modeSelected) return t('请先选择工作目录或直接对话')
  if (!props.hasCurrentTask) return t('描述要完成的任务')
  if (!props.taskActive) return t('继续这项任务')
  return t('添加到本地待发送区')
})

const primaryAction = computed(() =>
  t(props.hasCurrentTask && props.taskActive ? '加入' : '发送'))
const canSubmit = computed(() =>
  props.modeSelected && (prompt.value.trim().length > 0 || props.attachments.length > 0))
const autoStartItem = computed(() =>
  props.localQueuedMessages.find(message => message.id === props.localQueueAutoStartMessageId) ?? null)
const autoStartRemainingSeconds = computed(() => {
  if (!props.localQueueAutoStartAt) return 0
  return Math.max(0, Math.ceil((Date.parse(props.localQueueAutoStartAt) - currentTime.value) / 1000))
})

function handleKeydown(event: KeyboardEvent) {
  if (props.modeSelected && event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault()
    emit('submit')
    return
  }
  if (slashSuggestions.value.length) {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      activeSuggestionIndex.value = (activeSuggestionIndex.value + 1) % slashSuggestions.value.length
      return
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault()
      activeSuggestionIndex.value = (activeSuggestionIndex.value - 1 + slashSuggestions.value.length) % slashSuggestions.value.length
      return
    }
    if (event.key === 'Tab' || (event.key === 'Enter' && !event.shiftKey)) {
      event.preventDefault()
      if (activeSuggestion.value) applySuggestion(activeSuggestion.value)
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      suggestionsDismissedFor.value = prompt.value
      return
    }
  }
}

async function applySuggestion(suggestion: ComposerSuggestion) {
  const suffix = suggestion.id.startsWith('skill:') && pendingSkillArgs.value
    ? pendingSkillArgs.value
    : ''
  prompt.value = `${suggestion.value}${suffix}`
  pendingSkillArgs.value = ''
  suggestionsDismissedFor.value =
    suggestion.value === '/skill:' || suggestion.value === '/model '
      ? null
      : prompt.value
  activeSuggestionIndex.value = 0
  await nextTick()
  input.value?.focus()
  input.value?.setSelectionRange(prompt.value.length, prompt.value.length)
}

async function beginSkillInvocation() {
  const existing = /^\/skill:([^\s]*)(?:\s+([\s\S]*))?$/u.exec(prompt.value.trim())
  if (existing) {
    prompt.value = `/skill:${existing[1] ?? ''}`
    pendingSkillArgs.value = existing[2]?.trim() ?? ''
  } else {
    pendingSkillArgs.value = prompt.value.trim()
    prompt.value = '/skill:'
  }
  suggestionsDismissedFor.value = null
  skillRequestPending.value = true
  emit('requestSkills')
  await nextTick()
  input.value?.focus()
  input.value?.setSelectionRange(prompt.value.length, prompt.value.length)
}

async function toggleExpanded() {
  isExpanded.value = !isExpanded.value
  await nextTick()
  input.value?.focus()
}

function attachmentCount(item: LocalQueuedMessage) {
  return item.attachments?.length ?? 0
}

function beginDrag(messageId: string, event: DragEvent) {
  draggedMessageId.value = messageId
  event.dataTransfer?.setData('text/plain', messageId)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
}

function dropAt(newIndex: number, event: DragEvent) {
  event.preventDefault()
  const messageId = draggedMessageId.value ?? event.dataTransfer?.getData('text/plain')
  draggedMessageId.value = null
  if (messageId) emit('moveLocalMessage', messageId, newIndex)
}

onMounted(() => {
  countdownTimer = window.setInterval(() => { currentTime.value = Date.now() }, 500)
})

onBeforeUnmount(() => {
  if (countdownTimer) window.clearInterval(countdownTimer)
})

watch(prompt, value => {
  if (suggestionsDismissedFor.value !== value) suggestionsDismissedFor.value = null
  activeSuggestionIndex.value = 0
  if (value.startsWith('/skill:') && !skillRequestPending.value) {
    skillRequestPending.value = true
    emit('requestSkills')
  }
  if (!value.startsWith('/skill:')) {
    skillRequestPending.value = false
    pendingSkillArgs.value = ''
  }
})

watch(() => props.skillsLoading, loading => {
  if (!loading) skillRequestPending.value = false
})

defineExpose({ focus: () => input.value?.focus() })
</script>

<template>
  <footer class="composer-area" :class="{ 'composer-expanded': isExpanded }">
    <section v-if="localQueuedMessages.length" class="local-queue-panel" :aria-label="t('本地待发送区')">
      <header>
        <strong>{{ t('本地待发送区') }}</strong>
        <span>{{ localQueuedMessages.length }}</span>
      </header>
      <div v-if="autoStartItem && localQueueAutoStartAt" class="local-queue-countdown">
        <div>
          <strong>{{ t('{seconds} 秒后自动开始', { seconds: autoStartRemainingSeconds }) }}</strong>
          <span>{{ autoStartItem.message }}</span>
        </div>
        <UiButton type="button" @click="$emit('cancelLocalQueueAutoStart')">{{ t('取消本次自动开始') }}</UiButton>
      </div>
      <ol>
        <li
          v-for="(item, index) in localQueuedMessages"
          :key="item.id"
          class="local-queue-item"
          :class="{ dragging: draggedMessageId === item.id, scheduled: item.id === localQueueAutoStartMessageId }"
          @dragover.prevent
          @drop="dropAt(index, $event)"
        >
          <UiButton
            class="local-queue-drag-handle"
            type="button"
            draggable="true"
            :aria-label="t('拖动调整顺序')"
            :title="t('拖动调整顺序')"
            @dragstart="beginDrag(item.id, $event)"
            @dragend="draggedMessageId = null"
          >⠿</UiButton>
          <div class="local-queue-copy">
            <p>{{ item.message }}</p>
            <span v-if="attachmentCount(item)" class="local-queue-attachment-count">📎 {{ t('{count} 个附件', { count: attachmentCount(item) }) }}</span>
          </div>
          <div class="local-queue-actions">
            <template v-if="taskActive">
              <UiButton
                type="button"
                class="primary"
                :disabled="attachmentCount(item) > 0"
                :title="attachmentCount(item) ? t('附件只能随新一轮发送') : undefined"
                @click="$emit('dispatchLocalMessage', item.id, 'steer')"
              >{{ t('立即调整') }}</UiButton>
              <UiButton
                type="button"
                :disabled="attachmentCount(item) > 0"
                :title="attachmentCount(item) ? t('附件只能随新一轮发送') : undefined"
                @click="$emit('dispatchLocalMessage', item.id, 'follow-up')"
              >{{ t('定为后续') }}</UiButton>
            </template>
            <UiButton v-else type="button" class="primary" @click="$emit('dispatchLocalMessage', item.id, 'new-run')">{{ t('发送新一轮') }}</UiButton>
            <UiButton
              type="button"
              class="local-queue-icon-action"
              :aria-label="t('编辑')"
              :title="t('编辑')"
              @click="$emit('editLocalMessage', item.id)"
            >
              <svg viewBox="0 0 20 20" aria-hidden="true"><path d="m4 13.8-.7 3 3-.7L15.8 6.6a1.5 1.5 0 0 0 0-2.1l-.3-.3a1.5 1.5 0 0 0-2.1 0L4 13.8Z" /><path d="m12.2 5.4 2.4 2.4" /></svg>
            </UiButton>
            <UiButton
              type="button"
              class="local-queue-icon-action danger"
              :aria-label="t('取消')"
              :title="t('取消')"
              @click="$emit('removeLocalMessage', item.id)"
            >
              <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4.5 5.5h11M8 5.5V4h4v1.5M6.2 5.5l.6 10h6.4l.6-10M8.5 8.5v4.5M11.5 8.5v4.5" /></svg>
            </UiButton>
          </div>
        </li>
      </ol>
    </section>
    <div class="composer">
      <div v-if="slashSuggestions.length || commandArgumentHint || (prompt.startsWith('/skill:') && skillsLoading)" class="composer-suggestions" role="listbox" :aria-label="t('输入建议')">
        <div v-if="prompt.startsWith('/skill:')" class="composer-suggestions-heading">
          <strong>{{ t('选择要手动调用的技能') }}</strong>
          <span v-if="skillsLoading">{{ t('正在扫描技能…') }}</span>
        </div>
        <UiButton
          v-for="(suggestion, index) in slashSuggestions"
          :key="suggestion.id"
          type="button"
          role="option"
          :aria-selected="index === activeSuggestionIndex"
          :class="{ active: index === activeSuggestionIndex }"
          @mouseenter="activeSuggestionIndex = index"
          @mousedown.prevent="applySuggestion(suggestion)"
        >
          <span>
            <strong>
              {{ suggestion.label }}
              <code v-if="suggestion.syntax">{{ suggestion.syntax }}</code>
            </strong>
            <small>{{ suggestion.description }}</small>
          </span>
          <em v-if="suggestion.meta">{{ suggestion.meta }}</em>
        </UiButton>
        <div v-if="commandArgumentHint" class="composer-command-hint">
          <code>{{ commandArgumentHint.syntax }}</code>
          <span>{{ commandArgumentHint.description }}</span>
        </div>
        <p v-if="prompt.startsWith('/skill:') && !commandArgumentHint && !skillsLoading && !slashSuggestions.length">
          {{ t('没有匹配的可用技能') }}
        </p>
      </div>
      <div v-if="attachments.length" class="draft-attachments">
        <div
          v-for="attachment in attachments"
          :key="attachment.path"
          class="draft-attachment"
          :class="{ 'draft-image-attachment': selectedModelSupportsImages && attachment.previewDataUrl }"
          :title="attachment.path"
        >
          <img
            v-if="selectedModelSupportsImages && attachment.previewDataUrl"
            :src="attachment.previewDataUrl"
            :alt="attachment.displayName"
          />
          <template v-else>
            <b>{{ attachment.kind }}</b><span>{{ attachment.displayName }}</span>
          </template>
          <UiButton type="button" :aria-label="t('移除附件 {name}', { name: attachment.displayName })" @click="$emit('removeAttachment', attachment.path)">×</UiButton>
        </div>
      </div>
      <UiTextarea ref="input" v-model="prompt" rows="2" :placeholder="placeholder" :disabled="!modeSelected" @keydown="handleKeydown"></UiTextarea>
      <div class="composer-footer">
        <div class="composer-options">
          <ComposerActionMenu
            :attachments-disabled="taskActive || !modeSelected"
            :skills-disabled="!modeSelected"
            @select-attachments="$emit('selectAttachments')"
            @invoke-skill="beginSkillInvocation"
          />
          <UiButton
            class="composer-expand-button"
            type="button"
            :aria-label="t(isExpanded ? '还原输入框' : '展开输入框')"
            :title="t(isExpanded ? '还原输入框' : '展开输入框')"
            :aria-pressed="isExpanded"
            @click="toggleExpanded"
          >
            <svg v-if="isExpanded" viewBox="0 0 20 20" aria-hidden="true">
              <path d="M3 8h5V3M17 8h-5V3M12 17v-5h5M8 17v-5H3" />
            </svg>
            <svg v-else viewBox="0 0 20 20" aria-hidden="true">
              <path d="M7 3H3v4M13 3h4v4M17 13v4h-4M7 17H3v-4" />
            </svg>
          </UiButton>
          <label class="composer-model-option">
            <UiSelect v-model="selectedModel" :ariaLabelText="t('模型')" :options="modelOptions" :disabled="taskActive || !modelOptions.length" :emptyLabel="t('正在获取模型…')" placement="top" searchable :searchPlaceholder="t('搜索模型或 Provider')" />
          </label>
          <label class="composer-thinking-option">
            <UiSelect v-model="thinkingLevelModel" :ariaLabelText="t('推理等级')" :options="thinkingLevelOptions" :disabled="taskActive || !thinkingLevelOptions.length" :emptyLabel="t('正在获取推理等级…')" placement="top" />
          </label>
          <span v-if="!modeSelected" class="composer-scope-badge composer-scope-unselected">
            {{ t('请选择模式') }}
          </span>
          <span v-else-if="generalChat" class="composer-scope-badge" :title="t('文件操作仅限 Pi Companion 管理的隔离空间')">
            {{ t('隔离空间') }}
          </span>
          <label v-else class="composer-permission-option" :title="t(hasCurrentTask ? '权限在任务创建后固定' : 'Companion Extension 权限')">
            <UiSelect v-model="permissionModeModel" :ariaLabelText="t('Companion Extension 权限')" :options="permissionModeOptions" :disabled="hasCurrentTask" placement="top" />
          </label>
        </div>
        <div class="composer-actions">
          <UiButton v-if="taskActive" class="abort-button" type="button" @click="$emit('abort')">{{ t('停止') }}</UiButton>
          <UiButton class="send-button" type="button" :disabled="!canSubmit" @click="$emit('submit')">{{ primaryAction }} <b>↑</b></UiButton>
        </div>
      </div>
    </div>
    <div class="composer-meta">
      <UiButton v-if="workspaceGitChangeCount > 0" class="workspace-git-indicator" type="button" @click="$emit('openGit')">
        <span class="workspace-git-indicator-icon" aria-hidden="true">!</span>
        <strong>{{ t('Git工作区中未提交更改') }}</strong>
        <span class="workspace-git-indicator-count">{{ workspaceGitChangeCount }}</span>
      </UiButton>
      <small class="hint">{{ t(taskActive ? 'Ctrl + Enter 加入待发送区' : 'Ctrl + Enter 发送') }}</small>
    </div>
  </footer>
</template>
