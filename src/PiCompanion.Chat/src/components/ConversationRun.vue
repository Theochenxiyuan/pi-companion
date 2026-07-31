<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { UiButton, UiTextarea } from '@/components/ui'
import { useMinuteClock } from '@/composables/useMinuteClock'
import RunEvidencePanel from '@/components/RunEvidencePanel.vue'
import ToolWorkItem from '@/components/ToolWorkItem.vue'
import VirtualList from '@/components/VirtualList.vue'
import WorkChevron from '@/components/WorkChevron.vue'
import { normalizeExternalUrl, renderSafeMarkdown } from '@/security/markdown'
import type { CommandExecutionEvidence, FileChangeEvidence, TaskRunSnapshot, TranscriptBlock } from '@/types/bridge'
import { formatConversationTimestamp } from '@/utils/dateTime'
import { activeTaskStatuses, taskStatusTone } from '@/utils/taskStatus'
import { thinkingLevelLabel } from '@/utils/thinkingLevels'
import { useI18n } from '@/i18n'

const { locale, t } = useI18n()
const timestampNow = useMinuteClock()

type RunRenderItem =
  | { type: 'block'; key: string; block: TranscriptBlock }
  | { type: 'tool-group'; key: string; blocks: TranscriptBlock[] }

const props = withDefaults(defineProps<{
  run: TaskRunSnapshot
  agentName?: string
  viewMode: 'summary' | 'normal' | 'verbose'
  currentRunId?: string
  needsInteraction: boolean
  taskActive: boolean
  fileChangesExpandedByDefault?: boolean
}>(), { fileChangesExpandedByDefault: false })

const emit = defineEmits<{
  openDiff: [file: FileChangeEvidence]
  requestRecovery: [file: FileChangeEvidence]
  resolveInteraction: [block: TranscriptBlock, approved: boolean, response?: string]
  abortRetry: []
  openExternalLink: [url: string]
  openArtifact: [artifactId: string]
  saveArtifact: [artifactId: string]
}>()

const interactionResponse = ref('')
const customChoiceInteractionId = ref<string | null>(null)
const expandedToolGroups = ref(new Set<string>())
const summaryExpanded = ref(false)
const summaryCanExpand = ref(props.run.summary.trim().length > 72)
const summaryTextElement = ref<HTMLElement | null>(null)
const summaryGenerating = computed(() => props.run.aiSummaryStatus === 'Generating')
const attachmentsExpanded = ref(false)
const copiedMessage = ref<'user' | 'agent' | null>(null)
const copiedInteractionId = ref<string | null>(null)
const expandedInteractionIds = ref(new Set<string>())
const markdownCache = new Map<string, { sequence: number; html: string }>()
const directAttachmentLimit = 5
let copiedMessageTimer = 0
let copiedInteractionTimer = 0
let summaryResizeObserver: ResizeObserver | null = null

const initialUserBlock = computed(() => props.run.transcript.find((block) => block.kind === 'UserMessage') ?? null)
const runBlocks = computed(() => initialUserBlock.value
  ? props.run.transcript.filter((block) => block.id !== initialUserBlock.value?.id)
  : props.run.transcript)
const visibleRunBlocks = computed(() => runBlocks.value.filter((block) => !isRedundantAskUserTool(block)))
const visibleMessageAttachments = computed(() => attachmentsExpanded.value
  ? props.run.messageAttachments
  : props.run.messageAttachments.slice(0, directAttachmentLimit))
const hiddenAttachmentCount = computed(() => Math.max(0, props.run.messageAttachments.length - directAttachmentLimit))
const agentMessageText = computed(() => {
  const transcriptText = props.run.transcript
    .filter(block => block.kind === 'AssistantMessage')
    .map(block => block.content.trim())
    .filter(Boolean)
    .join('\n\n')
  return transcriptText || props.run.finalAnswer?.trim() || props.run.assistantText?.trim() || ''
})

function isWebSearchBlock(block: TranscriptBlock) {
  return block.kind === 'WebSearch' || (
    block.kind === 'Tool' && block.title.trim().toLocaleLowerCase() === 'web_search'
  )
}

function isRedundantAskUserTool(block: TranscriptBlock) {
  if (block.kind !== 'Tool' || block.title.trim().toLocaleLowerCase() !== 'ask_user') return false

  return runBlocks.value.some((candidate) =>
    candidate.kind === 'Interaction'
    && candidate.interactionKind === 'Question'
    && candidate.firstSequence >= block.firstSequence
    && (block.status === 'Running' || candidate.lastSequence <= block.lastSequence),
  )
}

const renderItems = computed<RunRenderItem[]>(() => {
  const items: RunRenderItem[] = []
  const blocks = visibleRunBlocks.value

  for (let index = 0; index < blocks.length;) {
    const block = blocks[index]
    if (block.kind !== 'Tool' || isWebSearchBlock(block)) {
      items.push({ type: 'block', key: block.id, block })
      index += 1
      continue
    }

    const tools: TranscriptBlock[] = []
    while (index < blocks.length && blocks[index].kind === 'Tool' && !isWebSearchBlock(blocks[index])) {
      tools.push(blocks[index])
      index += 1
    }

    if (tools.length === 1) items.push({ type: 'block', key: tools[0].id, block: tools[0] })
    else items.push({ type: 'tool-group', key: `tool-group-${tools[0].id}`, blocks: tools })
  }

  return props.viewMode === 'summary'
    ? items.filter((item) => item.type === 'block' && shouldShowBlock(item.block))
    : items
})

const summaryStats = computed(() => ({
  thinking: visibleRunBlocks.value.filter((block) => block.kind === 'Thinking').length,
  tools: visibleRunBlocks.value.filter((block) => block.kind === 'Tool' && !isWebSearchBlock(block)).length,
  webSearches: visibleRunBlocks.value.filter(isWebSearchBlock).length,
}))
function attachmentDisplayName(path: string) {
  const trimmed = path.replace(/[\\/]+$/, '')
  return trimmed.split(/[\\/]/).at(-1) || path
}

function formatArtifactSize(size: number) {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(size < 10 * 1024 ? 1 : 0)} KB`
  return `${(size / (1024 * 1024)).toFixed(size < 10 * 1024 * 1024 ? 1 : 0)} MB`
}

function isRunActive() {
  return activeTaskStatuses.includes(props.run.status)
}

function shouldShowBlock(block: TranscriptBlock) {
  return props.viewMode !== 'summary' || !['Thinking', 'Tool', 'WebSearch'].includes(block.kind)
}

function renderMarkdown(block: TranscriptBlock) {
  const cached = markdownCache.get(block.id)
  if (cached?.sequence === block.lastSequence) return cached.html
  const html = renderSafeMarkdown(block.content)
  markdownCache.set(block.id, { sequence: block.lastSequence, html })
  return html
}

function handleMarkdownClick(event: MouseEvent) {
  const target = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a') : null
  if (!target) return
  event.preventDefault()
  event.stopPropagation()
  const url = normalizeExternalUrl(target.getAttribute('href') ?? '')
  if (url) emit('openExternalLink', url)
}

function handleToolGroupToggle(key: string, event: Event) {
  const details = event.currentTarget as HTMLDetailsElement
  const next = new Set(expandedToolGroups.value)
  if (details.open) next.add(key)
  else next.delete(key)
  expandedToolGroups.value = next
}

function formatTime(timestamp: string) {
  return formatConversationTimestamp(timestamp, locale.value, new Date(timestampNow.value))
}

function measureSummaryOverflow() {
  if (summaryExpanded.value) return
  const element = summaryTextElement.value
  summaryCanExpand.value = props.run.summary.trim().length > 72
    || Boolean(element && element.scrollWidth > element.clientWidth + 1)
}

function observeSummaryOverflow() {
  summaryResizeObserver?.disconnect()
  const element = summaryTextElement.value
  if (!element) {
    summaryCanExpand.value = props.run.summary.trim().length > 72
    return
  }
  measureSummaryOverflow()
  if (typeof ResizeObserver === 'undefined') return
  summaryResizeObserver = new ResizeObserver(measureSummaryOverflow)
  summaryResizeObserver.observe(element)
}

function toggleSummary() {
  summaryExpanded.value = !summaryExpanded.value
  if (!summaryExpanded.value) nextTick(measureSummaryOverflow)
}

onMounted(() => {
  nextTick(observeSummaryOverflow)
})

watch(() => props.run.summary, async () => {
  summaryExpanded.value = false
  summaryCanExpand.value = props.run.summary.trim().length > 72
  await nextTick()
  observeSummaryOverflow()
})

async function writeClipboard(content: string) {
  if (!content.trim()) return
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(content)
    } else {
      const textarea = document.createElement('textarea')
      textarea.value = content
      textarea.style.position = 'fixed'
      textarea.style.opacity = '0'
      document.body.appendChild(textarea)
      textarea.select()
      const copied = document.execCommand('copy')
      textarea.remove()
      if (!copied) return false
    }
    return true
  } catch {
    return false
  }
}

async function copyMessage(kind: 'user' | 'agent', content: string) {
  if (await writeClipboard(content)) {
    copiedMessage.value = kind
    if (copiedMessageTimer) window.clearTimeout(copiedMessageTimer)
    copiedMessageTimer = window.setTimeout(() => {
      copiedMessage.value = null
      copiedMessageTimer = 0
    }, 1600)
  } else {
    copiedMessage.value = null
  }
}

async function copyInteractionContent(block: TranscriptBlock) {
  if (!await writeClipboard(block.content)) {
    copiedInteractionId.value = null
    return
  }
  copiedInteractionId.value = block.id
  if (copiedInteractionTimer) window.clearTimeout(copiedInteractionTimer)
  copiedInteractionTimer = window.setTimeout(() => {
    copiedInteractionId.value = null
    copiedInteractionTimer = 0
  }, 1600)
}

function interactionContentExpanded(block: TranscriptBlock) {
  return expandedInteractionIds.value.has(block.id)
}

function interactionContentCanExpand(block: TranscriptBlock) {
  return block.content.length > 480 || block.content.split(/\r?\n/u).length > 8
}

function toggleInteractionContent(block: TranscriptBlock) {
  const next = new Set(expandedInteractionIds.value)
  if (next.has(block.id)) next.delete(block.id)
  else next.add(block.id)
  expandedInteractionIds.value = next
}

onUnmounted(() => {
  if (copiedMessageTimer) window.clearTimeout(copiedMessageTimer)
  if (copiedInteractionTimer) window.clearTimeout(copiedInteractionTimer)
  summaryResizeObserver?.disconnect()
})

function toolStatusLabel(block: TranscriptBlock) {
  const label = {
    Running: '运行中',
    Completed: '已完成',
    Failed: '失败',
    Cancelled: '已取消',
    Pending: '等待中',
  }[block.status]
  return label ? t(label) : block.status
}

function thinkingStatusLabel(block: TranscriptBlock) {
  const label = {
    Running: '思考中',
    Completed: '已完成',
    Failed: '失败',
    Cancelled: '已取消',
    Pending: '等待中',
  }[block.status]
  return label ? t(label) : block.status
}

function toolGroupKinds(blocks: TranscriptBlock[]) {
  const counts = new Map<string, number>()
  for (const block of blocks) counts.set(block.title, (counts.get(block.title) ?? 0) + 1)
  return [...counts.entries()].map(([name, count]) => ({ name, count }))
}

function toolGroupStatus(blocks: TranscriptBlock[]) {
  const failed = blocks.filter((block) => block.status === 'Failed').length
  const running = blocks.filter((block) => ['Running', 'Pending'].includes(block.status)).length
  if (failed) return t('{total} 项 · {count} 失败', { total: blocks.length, count: failed })
  if (running) return t('{total} 项 · {count} 运行中', { total: blocks.length, count: running })
  return t('{total} 项 · 已完成', { total: blocks.length })
}

function commandForTool(block: TranscriptBlock): CommandExecutionEvidence | null {
  return props.run.evidence?.commands.find(command => `tool-${command.toolCallId}` === block.id) ?? null
}

function isQuestion(block: TranscriptBlock) {
  return block.interactionKind === 'Question'
}

function isChoiceQuestion(block: TranscriptBlock) {
  return isQuestion(block) && block.interactionMethod === 'select' && block.interactionOptions.length > 0
}

function isOtherChoice(option: string) {
  return option === '其他…'
}

function isCustomChoiceOpen(block: TranscriptBlock) {
  return customChoiceInteractionId.value === block.interactionId
}

function chooseInteractionOption(block: TranscriptBlock, option: string) {
  if (isOtherChoice(option)) {
    customChoiceInteractionId.value = block.interactionId
    interactionResponse.value = ''
    return
  }

  resolveInteraction(block, true, option)
}

function interactionKindLabel(block: TranscriptBlock) {
  return t(isQuestion(block) ? '提问' : '授权')
}

function interactionStatusLabel(block: TranscriptBlock) {
  if (block.status === 'Completed') return t(isQuestion(block) ? '已回答' : '已允许')
  if (block.status === 'Cancelled') return t(isQuestion(block) ? '已取消' : '已拒绝')
  return toolStatusLabel(block)
}

function resolveInteraction(block: TranscriptBlock, approved: boolean, response?: string) {
  emit('resolveInteraction', block, approved, response)
  interactionResponse.value = ''
  customChoiceInteractionId.value = null
}
</script>

<template>
  <section class="conversation-run">
    <article v-if="initialUserBlock" class="message user transcript-message">
      <header><time>{{ formatTime(initialUserBlock.timestamp) }}</time><strong>{{ t(initialUserBlock.title) }}</strong></header>
      <p>{{ initialUserBlock.content }}</p>
      <div
        v-if="run.messageAttachments.length"
        class="message-attachments"
        :class="{ expanded: attachmentsExpanded, 'has-overflow': hiddenAttachmentCount > 0 }"
        :aria-label="t('本轮消息附件 {count} 个', { count: run.messageAttachments.length })"
      >
        <span v-for="attachment in visibleMessageAttachments" :key="attachment" class="message-attachment" :title="attachment">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M7.2 10.8 12 6a2.1 2.1 0 0 1 3 3l-6.1 6.1a3.5 3.5 0 0 1-5-5l6.4-6.4" /></svg>
          {{ attachmentDisplayName(attachment) }}
        </span>
        <UiButton
          v-if="hiddenAttachmentCount > 0"
          class="message-attachment attachment-overflow-toggle"
          type="button"
          :aria-expanded="attachmentsExpanded"
          @click="attachmentsExpanded = !attachmentsExpanded"
        >{{ t(attachmentsExpanded ? '收起附件' : '还有 {count} 个附件', { count: hiddenAttachmentCount }) }}</UiButton>
      </div>
      <div class="message-copy-actions user-copy-actions">
        <UiButton
          class="message-copy-button"
          type="button"
          :aria-label="t(copiedMessage === 'user' ? '已复制' : '复制消息')"
          :title="t(copiedMessage === 'user' ? '已复制' : '复制消息')"
          @click="copyMessage('user', initialUserBlock.content)"
        >
          <svg v-if="copiedMessage === 'user'" viewBox="0 0 20 20" aria-hidden="true"><path d="m4 10 3.5 3.5L16 5" /></svg>
          <svg v-else viewBox="0 0 20 20" aria-hidden="true"><rect x="6.5" y="6.5" width="9.5" height="10" rx="1.5" /><path d="M13.5 6.5V5A1.5 1.5 0 0 0 12 3.5H5A1.5 1.5 0 0 0 3.5 5v7A1.5 1.5 0 0 0 5 13.5h1.5" /></svg>
        </UiButton>
      </div>
    </article>

    <article class="message agent run-response">
      <header>
        <span class="agent-mark">π</span>
        <strong>{{ agentName ?? run.model }} ({{ thinkingLevelLabel(run.thinkingLevel) }})</strong>
        <time>{{ isRunActive() ? t('正在运行') : formatTime(run.transcript.at(-1)?.timestamp ?? initialUserBlock?.timestamp ?? new Date().toISOString()) }}</time>
      </header>

      <div class="run-response-body">
        <div v-if="viewMode === 'summary' && (summaryStats.thinking > 0 || summaryStats.tools > 0 || summaryStats.webSearches > 0)" class="run-summary-stats" :aria-label="t('Run 工作摘要')">
          <span v-if="summaryStats.thinking > 0">
            <span class="summary-stat-icon thinking-summary-icon" aria-hidden="true"><svg viewBox="0 0 20 20"><path d="M10 2.5v2M10 15.5v2M2.5 10h2M15.5 10h2M4.7 4.7l1.4 1.4M13.9 13.9l1.4 1.4M15.3 4.7l-1.4 1.4M6.1 13.9l-1.4 1.4" /><circle cx="10" cy="10" r="3.2" /></svg></span>
            <span class="summary-stat-label">{{ t('思考') }}</span><b class="count-badge">{{ summaryStats.thinking }}</b>
          </span>
          <span v-if="summaryStats.tools > 0">
            <span class="summary-stat-icon tool-summary-icon" aria-hidden="true"><svg viewBox="0 0 20 20"><rect x="3.5" y="2.5" width="13" height="11" rx="2" /><path d="M6 17h8M10 13.5V17" /></svg></span>
            <span class="summary-stat-label">{{ t('工具调用') }}</span><b class="count-badge">{{ summaryStats.tools }}</b>
          </span>
          <span v-if="summaryStats.webSearches > 0">
            <span class="summary-stat-icon web-search-summary-icon" aria-hidden="true"><svg viewBox="0 0 20 20"><circle cx="8.5" cy="8.5" r="5" /><path d="m12.2 12.2 4 4" /></svg></span>
            <span class="summary-stat-label">{{ t('网络搜索') }}</span><b class="count-badge">{{ summaryStats.webSearches }}</b>
          </span>
        </div>

        <VirtualList
          :items="renderItems"
          :item-key="(item: RunRenderItem) => item.key"
          :estimated-item-height="64"
          :overscan="10"
        >
          <template #default="{ item }">
            <details
              v-if="item.type === 'tool-group' && viewMode !== 'summary'"
              class="work-item tool-group"
              :open="viewMode === 'verbose' || expandedToolGroups.has(item.key)"
              @toggle="handleToolGroupToggle(item.key, $event)"
            >
              <summary>
                <span class="work-glyph group-glyph" aria-hidden="true">
                  <svg viewBox="0 0 20 20"><rect x="3.5" y="2.5" width="13" height="11" rx="2" /><path d="M6 17h8M10 13.5V17" /></svg>
                </span>
                <span class="work-label">{{ t('工具调用') }}</span>
                <span class="work-result tool-kind-list">
                  <template v-for="(kind, index) in toolGroupKinds(item.blocks)" :key="kind.name">
                    <span class="tool-kind">{{ kind.name }}<b v-if="kind.count > 1" class="count-badge">{{ kind.count }}</b></span>
                    <span v-if="index < toolGroupKinds(item.blocks).length - 1" class="tool-kind-separator">|</span>
                  </template>
                </span>
                <span class="work-status">{{ toolGroupStatus(item.blocks) }}</span>
                <WorkChevron />
              </summary>
              <VirtualList
                v-if="viewMode === 'verbose' || expandedToolGroups.has(item.key)"
                class="tool-group-children"
                :items="item.blocks"
                :item-key="(tool: TranscriptBlock) => tool.id"
                :estimated-item-height="36"
                :overscan="8"
              >
                <template #default="{ item: tool }"><ToolWorkItem :block="tool" :command="commandForTool(tool)" @open-external-link="emit('openExternalLink', $event)" /></template>
              </VirtualList>
            </details>

            <template v-else-if="item.type === 'block' && shouldShowBlock(item.block)">
              <div
                v-if="item.block.kind === 'AssistantMessage'"
                class="markdown-body assistant-segment"
                v-html="renderMarkdown(item.block)"
                @click="handleMarkdownClick"
              ></div>

              <div v-else-if="item.block.kind === 'UserMessage'" class="run-user-note">
                <span>↳</span><strong>{{ t(item.block.title) }}</strong><p>{{ item.block.content }}</p>
              </div>

              <details
                v-else-if="item.block.kind === 'Thinking'"
                class="work-item thinking-item"
                :class="item.block.status.toLowerCase()"
                :open="viewMode === 'verbose'"
              >
                <summary>
                  <span class="work-glyph" aria-hidden="true">
                    <svg viewBox="0 0 20 20"><path d="M10 2.5v2M10 15.5v2M2.5 10h2M15.5 10h2M4.7 4.7l1.4 1.4M13.9 13.9l1.4 1.4M15.3 4.7l-1.4 1.4M6.1 13.9l-1.4 1.4" /><circle cx="10" cy="10" r="3.2" /></svg>
                  </span>
                  <span class="work-label">{{ t(item.block.title) }}</span>
                  <span class="work-result"></span>
                  <span class="work-status">{{ thinkingStatusLabel(item.block) }}</span>
                  <WorkChevron />
                </summary>
                <div class="work-panel markdown-body" v-html="renderMarkdown(item.block)" @click="handleMarkdownClick"></div>
              </details>

              <ToolWorkItem v-else-if="item.block.kind === 'Tool' || item.block.kind === 'WebSearch'" :block="item.block" :command="commandForTool(item.block)" :open="viewMode === 'verbose'" @open-external-link="emit('openExternalLink', $event)" />

              <details
                v-else-if="item.block.kind === 'Interaction' && item.block.status !== 'Pending'"
                class="work-item interaction-item"
                :class="[item.block.status.toLowerCase(), { question: isQuestion(item.block) }]"
                :open="viewMode === 'verbose'"
              >
                <summary>
                  <span class="work-glyph" aria-hidden="true">
                    <svg v-if="isQuestion(item.block)" viewBox="0 0 20 20"><circle cx="10" cy="10" r="7" /><path d="M7.8 7.5a2.4 2.4 0 0 1 4.5 1.2c0 1.8-2.3 2-2.3 3.5M10 15h.01" /></svg>
                    <svg v-else viewBox="0 0 20 20"><rect x="3.5" y="8" width="13" height="8" rx="2" /><path d="M6.5 8V6a3.5 3.5 0 0 1 7 0v2" /></svg>
                  </span>
                  <span class="work-label">{{ interactionKindLabel(item.block) }}</span>
                  <span class="work-result">{{ t(item.block.output || item.block.content) }}</span>
                  <span class="work-status">{{ interactionStatusLabel(item.block) }}</span>
                  <WorkChevron />
                </summary>
                <div class="work-panel interaction-detail">
                  <p>{{ item.block.content }}</p>
                  <small v-if="item.block.output">{{ t('响应：{value}', { value: t(item.block.output) }) }}</small>
                </div>
              </details>

              <section
                v-else-if="item.block.kind === 'Interaction'"
                class="interaction-card"
                :class="[item.block.status.toLowerCase(), { question: isQuestion(item.block) }]"
              >
                <header><strong>{{ t(item.block.title) }}</strong><span>{{ item.block.status === 'Pending' ? t('等待响应') : toolStatusLabel(item.block) }}</span></header>
                <p
                  class="interaction-content"
                  :class="{ expanded: interactionContentExpanded(item.block) }"
                >{{ item.block.content }}</p>
                <div v-if="!isQuestion(item.block)" class="interaction-content-actions">
                  <UiButton
                    v-if="interactionContentCanExpand(item.block)"
                    class="interaction-content-button"
                    type="button"
                    :aria-expanded="interactionContentExpanded(item.block)"
                    @click="toggleInteractionContent(item.block)"
                  >{{ t(interactionContentExpanded(item.block) ? '收起完整内容' : '展开完整内容') }}</UiButton>
                  <UiButton
                    class="interaction-content-button"
                    type="button"
                    @click="copyInteractionContent(item.block)"
                  >{{ t(copiedInteractionId === item.block.id ? '已复制' : '复制授权内容') }}</UiButton>
                </div>
                <template v-if="item.block.status === 'Pending' && run.id === currentRunId && needsInteraction">
                  <UiTextarea
                    v-if="isQuestion(item.block) && !isChoiceQuestion(item.block)"
                    v-model="interactionResponse"
                    rows="3"
                    :placeholder="t('输入你的回答')"
                  ></UiTextarea>
                  <div v-if="isChoiceQuestion(item.block)" class="interaction-options">
                    <UiButton
                      v-for="option in item.block.interactionOptions"
                      :key="option"
                      class="secondary-button"
                      :class="{ selected: isOtherChoice(option) && isCustomChoiceOpen(item.block) }"
                      type="button"
                      @click="chooseInteractionOption(item.block, option)"
                    >{{ t(option) }}</UiButton>
                    <UiButton class="secondary-button" type="button" @click="resolveInteraction(item.block, false)">{{ t('取消') }}</UiButton>
                  </div>
                  <div v-if="isChoiceQuestion(item.block) && isCustomChoiceOpen(item.block)" class="interaction-custom-answer">
                    <UiTextarea
                      v-model="interactionResponse"
                      rows="3"
                      :placeholder="t('输入其他回答')"
                      autofocus
                    ></UiTextarea>
                    <div class="interaction-actions">
                      <UiButton
                        class="primary-button"
                        type="button"
                        :disabled="!interactionResponse.trim()"
                        @click="resolveInteraction(item.block, true, interactionResponse.trim())"
                      >{{ t('提交回答') }}</UiButton>
                    </div>
                  </div>
                  <div v-if="!isChoiceQuestion(item.block)" class="interaction-actions">
                    <UiButton class="secondary-button" type="button" @click="resolveInteraction(item.block, false)">{{ t(isQuestion(item.block) ? '取消' : '拒绝') }}</UiButton>
                    <UiButton
                      v-if="!isQuestion(item.block)"
                      class="secondary-button"
                      type="button"
                      @click="resolveInteraction(item.block, true, '本任务内允许同类操作')"
                    >{{ t('本任务内允许同类操作') }}</UiButton>
                    <UiButton
                      class="primary-button"
                      type="button"
                      :disabled="isQuestion(item.block) && !interactionResponse.trim()"
                      @click="resolveInteraction(item.block, true, interactionResponse.trim() || '允许一次')"
                    >{{ t(isQuestion(item.block) ? '提交回答' : '允许一次') }}</UiButton>
                  </div>
                </template>
                <small v-else-if="item.block.output">{{ t(item.block.output) }}</small>
              </section>

              <section v-else-if="item.block.kind === 'Notice'" class="notice-card" :class="item.block.status.toLowerCase()">
                <strong>{{ t(item.block.title) }}</strong><span>{{ t(item.block.content) }}</span>
                <UiButton
                  v-if="item.block.title === '自动重试' && item.block.status === 'Running' && run.id === currentRunId"
                  type="button"
                  @click="$emit('abortRetry')"
                >{{ t('取消重试') }}</UiButton>
              </section>
            </template>
          </template>
        </VirtualList>

        <section v-if="run.artifacts?.length" class="artifact-list" :aria-label="t('生成的文件')">
          <article v-for="artifact in run.artifacts" :key="artifact.id" class="artifact-card">
            <span class="artifact-icon" aria-hidden="true">
              <svg viewBox="0 0 20 20"><path d="M5 2.5h6l4 4v11H5z" /><path d="M11 2.5v4h4M7.5 11h5M7.5 14h4" /></svg>
            </span>
            <span class="artifact-copy">
              <strong>{{ artifact.displayName }}</strong>
              <small>{{ formatArtifactSize(artifact.size) }} · {{ artifact.contentType }}</small>
            </span>
            <UiButton type="button" @click="$emit('openArtifact', artifact.id)">{{ t('打开') }}</UiButton>
            <UiButton class="primary-button" type="button" @click="$emit('saveArtifact', artifact.id)">{{ t('保存到…') }}</UiButton>
          </article>
        </section>

        <div
          v-if="run.activityStatus"
          class="run-activity-status"
          role="status"
          aria-live="polite"
        >{{ t(run.activityStatus) }}</div>

        <div class="run-state-block">
          <div class="run-state-line" :class="taskStatusTone(run.status)">
            <span class="status-dot" :class="taskStatusTone(run.status)"></span>
            <strong>{{ t(run.statusText) }}</strong>
          </div>
          <div v-if="run.summary || summaryGenerating || agentMessageText" class="run-summary-row" :class="{ expanded: summaryExpanded }">
            <div v-if="run.summary" class="run-summary-content">
              <strong class="run-summary-label">{{ t('总结：') }}</strong>
              <span ref="summaryTextElement" class="run-summary-text" :title="run.summary">{{ run.summary }}</span>
              <UiButton
                v-if="summaryCanExpand"
                class="run-summary-toggle"
                type="button"
                :aria-expanded="summaryExpanded"
                @click="toggleSummary"
              >{{ t(summaryExpanded ? '收起' : '展开') }}</UiButton>
            </div>
            <div v-else-if="summaryGenerating" class="run-summary-content run-summary-loading" role="status">
              <span class="file-loading-spinner" aria-hidden="true"></span>
              <span>{{ t('正在生成 AI 总结') }}</span>
            </div>
            <UiButton
              v-if="agentMessageText"
              class="message-copy-button agent-copy-button"
              type="button"
              :aria-label="t(copiedMessage === 'agent' ? '已复制' : '复制消息')"
              :title="t(copiedMessage === 'agent' ? '已复制' : '复制消息')"
              @click="copyMessage('agent', agentMessageText)"
            >
              <svg v-if="copiedMessage === 'agent'" viewBox="0 0 20 20" aria-hidden="true"><path d="m4 10 3.5 3.5L16 5" /></svg>
              <svg v-else viewBox="0 0 20 20" aria-hidden="true"><rect x="6.5" y="6.5" width="9.5" height="10" rx="1.5" /><path d="M13.5 6.5V5A1.5 1.5 0 0 0 12 3.5H5A1.5 1.5 0 0 0 3.5 5v7A1.5 1.5 0 0 0 5 13.5h1.5" /></svg>
            </UiButton>
          </div>
        </div>

        <RunEvidencePanel
          :run="run"
          :task-active="taskActive"
          :expanded-by-default="fileChangesExpandedByDefault"
          @open-diff="$emit('openDiff', $event)"
          @request-recovery="$emit('requestRecovery', $event)"
        />
      </div>
    </article>
  </section>
</template>
