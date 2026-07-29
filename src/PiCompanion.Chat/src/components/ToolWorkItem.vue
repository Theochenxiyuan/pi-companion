<script setup lang="ts">
import { computed } from 'vue'
import WorkChevron from '@/components/WorkChevron.vue'
import type { CommandExecutionEvidence, TranscriptBlock } from '@/types/bridge'
import { t } from '@/i18n'
import { normalizeExternalUrl, renderSafeMarkdown } from '@/security/markdown'

const props = withDefaults(defineProps<{
  block: TranscriptBlock
  command?: CommandExecutionEvidence | null
  open?: boolean
}>(), {
  open: false,
})
const emit = defineEmits<{ openExternalLink: [url: string] }>()
const isWebSearch = computed(() =>
  props.block.kind === 'WebSearch' || props.block.title.trim().toLocaleLowerCase() === 'web_search',
)
const displayTitle = computed(() => {
  if (isWebSearch.value) return t('网络搜索')
  const toolName = props.block.title.trim().toLocaleLowerCase()
  if (toolName === 'ask_user') return t('向用户提问')
  if (toolName === 'list_available_skills') return t('列出可用技能')
  return props.block.title
})

const statusLabel = computed(() => t(({
  Running: '运行中',
  Completed: '已完成',
  Failed: '失败',
  Cancelled: '已取消',
  Pending: '等待中',
})[props.block.status] ?? props.block.status))

const compactResult = computed(() => {
  if (props.command?.command.trim()) return truncate(props.command.command.trim())
  if (props.block.input?.trim()) return truncate(props.block.input.trim())

  let value = props.block.content.replace(/\s+/g, ' ').trim()
  const toolName = props.block.title.trim()
  while (toolName && value.toLocaleLowerCase().startsWith(toolName.toLocaleLowerCase())) {
    value = value.slice(toolName.length).trim()
  }

  value = value.replace(/^(?:已?完成|正在运行|运行中|已取消|失败)\s*[:：·-]?\s*/u, '')
  return value ? truncate(value) : ''
})

const statusOnlyOutputs = new Set(['正在运行', '仍在运行', '执行完成', '执行失败'])
const detailOutput = computed(() => {
  const value = props.command?.outputSummary || props.block.output
  return value && !statusOnlyOutputs.has(value.trim()) ? value : ''
})
const hasDetails = computed(() => Boolean(props.command || props.block.input || detailOutput.value))
const webSearchOutput = computed(() => isWebSearch.value && detailOutput.value
  ? renderSafeMarkdown(detailOutput.value)
  : '')

const commandResult = computed(() => {
  if (!props.command) return ''
  if (props.command.cancelled) return t('已取消')
  if (props.command.timedOut) return t('已超时')
  if (props.command.exitCode === null) return t('退出码未知')
  return t('退出码 {code}', { code: props.command.exitCode })
})

function formatDuration(milliseconds: number) {
  if (milliseconds < 1000) return `${Math.max(0, Math.round(milliseconds))} ms`
  return `${(milliseconds / 1000).toFixed(1)} s`
}

function truncate(value: string) {
  return value.length > 88 ? `${value.slice(0, 88)}…` : value
}

function handleWebSearchLink(event: MouseEvent) {
  const target = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a') : null
  if (!target) return
  event.preventDefault()
  event.stopPropagation()
  const url = normalizeExternalUrl(target.getAttribute('href') ?? '')
  if (url) emit('openExternalLink', url)
}
</script>

<template>
  <details class="work-item tool-item" :class="[block.status.toLowerCase(), { 'web-search-item': isWebSearch }]" :open="open">
    <summary>
      <span class="work-glyph" aria-hidden="true">
        <svg v-if="isWebSearch" viewBox="0 0 20 20"><circle cx="8.5" cy="8.5" r="5" /><path d="m12.2 12.2 4 4" /></svg>
        <svg v-else viewBox="0 0 20 20"><rect x="2.5" y="3.5" width="15" height="13" rx="2" /><path d="m6 8 2 2-2 2M10.5 12h3.5" /></svg>
      </span>
      <span class="work-label">{{ displayTitle }}</span>
      <span v-if="compactResult" class="work-result">{{ compactResult }}</span>
      <span v-else class="work-result"></span>
      <span class="work-status">{{ statusLabel }}</span>
      <WorkChevron v-if="hasDetails" />
    </summary>
    <div v-if="hasDetails" class="work-panel tool-detail">
      <div v-if="command"><span>{{ t('命令') }}</span><pre>{{ command.command }}</pre></div>
      <div v-else-if="block.input"><span>{{ t(isWebSearch ? '搜索内容' : '输入') }}</span><pre>{{ block.input }}</pre></div>
      <div v-if="command" class="tool-command-meta">
        <span>{{ t('执行') }}</span>
        <p><b>{{ commandResult }}</b><small>{{ formatDuration(command.durationMilliseconds) }}<template v-if="command.detectedFramework"> · {{ command.detectedFramework }}</template></small></p>
      </div>
      <div v-if="detailOutput">
        <span>{{ t(isWebSearch ? '搜索结果与来源' : '输出') }}</span>
        <div
          v-if="isWebSearch"
          class="markdown-body web-search-result"
          v-html="webSearchOutput"
          @click="handleWebSearchLink"
        ></div>
        <pre v-else>{{ detailOutput }}</pre>
      </div>
      <div v-if="command?.workingDirectory"><span>{{ t('目录') }}</span><pre>{{ command.workingDirectory }}</pre></div>
    </div>
  </details>
</template>
