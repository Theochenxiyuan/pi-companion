<script setup lang="ts">
import { computed } from 'vue'
import { UiButton } from '@/components/ui'
import type { PiModelInfo, SessionStatisticsSnapshot } from '@/types/bridge'
import { useI18n } from '@/i18n'

const { locale, t } = useI18n()

const props = defineProps<{
  taskId: string | null
  taskTitle: string | null
  selectedModel: PiModelInfo | null
  selectedModelReference: string
  sessionModelReference: string | null
  update: SessionStatisticsSnapshot | null
  loading: boolean
  manualLoadAvailable: boolean
}>()

const emit = defineEmits<{
  refresh: []
}>()

const matchingUpdate = computed(() => {
  if (!props.update || !props.taskId) return null
  return props.update.taskId?.toLocaleLowerCase() === props.taskId.toLocaleLowerCase()
    ? props.update
    : null
})
const statistics = computed(() => matchingUpdate.value?.statistics ?? null)
const contextTokens = computed(() => statistics.value?.contextUsage?.tokens ?? null)
const contextWindow = computed(() =>
  props.selectedModel?.contextWindow
    ?? statistics.value?.contextUsage?.contextWindow
    ?? 0)
const contextPercent = computed(() => {
  if (contextTokens.value === null || contextWindow.value <= 0) return null
  return Math.max(0, contextTokens.value / contextWindow.value * 100)
})
const progressPercent = computed(() => Math.min(100, contextPercent.value ?? 0))
const pressureTone = computed(() => {
  if (contextPercent.value === null) return 'unknown'
  if (contextPercent.value >= 90) return 'critical'
  if (contextPercent.value >= 70) return 'warning'
  return 'normal'
})
const providerName = computed(() => props.selectedModel?.provider ?? props.selectedModelReference.split('/')[0] ?? '')
const modelName = computed(() => props.selectedModel?.name ?? props.selectedModelReference.split('/').at(-1) ?? t('未选择模型'))
const usesSelectedModelCapacity = computed(() =>
  Boolean(props.sessionModelReference && props.selectedModelReference && props.sessionModelReference !== props.selectedModelReference))
const cacheHitRate = computed(() => {
  const value = statistics.value
  if (!value) return null
  const cacheableInput = value.inputTokens + value.cacheReadTokens + value.cacheWriteTokens
  return cacheableInput > 0 ? value.cacheReadTokens / cacheableInput * 100 : null
})

function formatNumber(value: number | null) {
  return value === null ? '—' : Math.round(value).toLocaleString(locale.value)
}

function formatPercent(value: number | null) {
  if (value === null) return '—'
  return `${value.toFixed(value >= 10 ? 1 : 2)}%`
}

function formatCost(value: number) {
  return `US$${value.toFixed(value > 0 && value < 0.01 ? 4 : 2)}`
}
</script>

<template>
  <section class="context-session-panel" :aria-label="t('Session 统计与上下文压力')" :aria-busy="loading">
    <header class="context-session-heading">
      <div>
        <strong :title="taskTitle ?? undefined">{{ taskTitle ?? t('Session 统计') }}</strong>
        <span v-if="taskId">{{ providerName }} / {{ modelName }}</span>
      </div>
      <UiButton
        type="button"
        :title="t(loading ? '正在刷新 Session 统计' : '刷新 Session 统计')"
        :aria-label="t('刷新 Session 统计')"
        :disabled="!taskId || loading"
        :class="{ loading }"
        @click="emit('refresh')"
      >
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M16 7a6 6 0 1 0 .2 5M16 3v4h-4" /></svg>
      </UiButton>
    </header>

    <div v-if="!taskId" class="context-session-empty">
      <strong>{{ t('还没有可统计的 Session') }}</strong>
      <span>{{ t('运行一次任务后，这里会显示真实的 Pi 会话数据。') }}</span>
    </div>

    <div v-else class="context-session-content">
      <section class="context-pressure-card" :class="pressureTone">
        <header>
          <span>{{ t('上下文压力') }}</span>
          <strong>{{ formatNumber(contextTokens) }} / {{ formatNumber(contextWindow || null) }}</strong>
        </header>
        <div class="context-pressure-track" role="progressbar" :aria-label="t('上下文使用率')" :aria-valuenow="contextPercent ?? undefined" aria-valuemin="0" aria-valuemax="100">
          <span :style="{ width: `${progressPercent}%` }"></span>
        </div>
        <div class="context-pressure-caption">
          <strong v-if="contextPercent !== null">{{ t('已使用 {value}', { value: formatPercent(contextPercent) }) }}</strong>
          <strong v-else>{{ t('等待下一条助手回复后更新占用') }}</strong>
          <span v-if="usesSelectedModelCapacity">{{ t('按当前选中模型容量计算') }}</span>
        </div>
      </section>

      <div v-if="!matchingUpdate" class="context-session-status">
        <span class="context-session-spinner"></span>
        {{ t('正在读取 Session 统计…') }}
      </div>

      <div v-else-if="!matchingUpdate.available || !statistics" class="context-session-status" :class="{ error: matchingUpdate.error }">
        <strong>{{ t(matchingUpdate.error ? '暂时无法读取 Session 统计' : '当前没有可读取的 Pi Session') }}</strong>
        <span>{{ matchingUpdate.error ?? t(manualLoadAvailable ? '点击右上角刷新，读取历史 Pi Session。' : '完成一次运行后可在这里刷新。') }}</span>
      </div>

      <template v-else>
        <section class="context-stat-section" :aria-label="t('消息统计')">
          <h3>{{ t('消息') }}</h3>
          <div class="context-stat-grid">
            <article><span>{{ t('总计') }}</span><strong>{{ formatNumber(statistics.totalMessages) }}</strong></article>
            <article><span>{{ t('用户') }}</span><strong>{{ formatNumber(statistics.userMessages) }}</strong></article>
            <article><span>{{ t('助手') }}</span><strong>{{ formatNumber(statistics.assistantMessages) }}</strong></article>
            <article><span>{{ t('工具调用') }}</span><strong>{{ formatNumber(statistics.toolCalls) }}</strong></article>
          </div>
        </section>

        <section class="context-stat-section context-token-section" :aria-label="t('累计 Token 统计')">
          <h3>{{ t('Session 累计') }}</h3>
          <div class="context-token-card">
            <article><span>{{ t('输入') }}</span><strong>{{ formatNumber(statistics.inputTokens) }}</strong></article>
            <article><span>{{ t('输出') }}</span><strong>{{ formatNumber(statistics.outputTokens) }}</strong></article>
            <article><span>{{ t('缓存读取') }}</span><strong>{{ formatNumber(statistics.cacheReadTokens) }}</strong></article>
            <article><span>{{ t('缓存写入') }}</span><strong>{{ formatNumber(statistics.cacheWriteTokens) }}</strong></article>
            <article><span>{{ t('缓存命中') }}</span><strong>{{ formatPercent(cacheHitRate) }}</strong></article>
            <article><span>{{ t('成本') }}</span><strong>{{ formatCost(statistics.cost) }}</strong></article>
          </div>
        </section>

        <footer class="context-session-footer" :title="statistics.sessionId">
          Session {{ statistics.sessionId.slice(0, 8) }}
        </footer>
      </template>
    </div>
  </section>
</template>

<style scoped>
.context-session-panel { min-width: 0; min-height: 0; overflow: auto; padding: 0 10px 18px; color: var(--color-text-primary); scrollbar-color: var(--color-scrollbar-thumb) transparent; }
.context-session-heading { position: sticky; z-index: 2; top: 0; display: flex; align-items: center; justify-content: space-between; gap: 8px; min-height: 57px; margin: 0 -2px; padding: 8px 2px; border-bottom: 1px solid var(--color-border-subtle); background: var(--color-bg-sidebar); }
.context-session-heading > div { display: grid; min-width: 0; gap: 3px; }
.context-session-heading strong { overflow: hidden; font-size: var(--font-size-body-sm); font-weight: var(--font-weight-semibold); text-overflow: ellipsis; white-space: nowrap; }
.context-session-heading span { overflow: hidden; color: var(--color-text-tertiary); font-size: var(--font-size-caption); text-overflow: ellipsis; white-space: nowrap; }
.context-session-heading button { display: grid; width: 29px; height: 29px; flex: none; place-items: center; padding: 0; border: 1px solid transparent; border-radius: 6px; background: transparent; color: var(--color-text-secondary); cursor: pointer; }
.context-session-heading button:hover:not(:disabled) { border-color: var(--color-border-default); background: var(--color-bg-hover); color: var(--color-text-primary); }
.context-session-heading button:disabled { opacity: .35; cursor: default; }
.context-session-heading svg { width: 15px; height: 15px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.6; }
.context-session-heading button.loading svg { animation: context-spinner 700ms linear infinite; }
.context-session-content { display: grid; gap: 14px; padding-top: 12px; }
.context-pressure-card { padding: 12px; border: 1px solid var(--color-border-subtle); border-radius: 9px; background: var(--color-bg-surface); }
.context-pressure-card header, .context-pressure-caption { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.context-pressure-card header span { color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.context-pressure-card header strong { color: var(--color-text-secondary); font: var(--font-size-caption) var(--font-family-mono); font-weight: var(--font-weight-medium); white-space: nowrap; }
.context-pressure-track { height: 4px; margin: 10px 0 8px; overflow: hidden; border-radius: 999px; background: var(--color-tone-6); }
.context-pressure-track > span { display: block; height: 100%; border-radius: inherit; background: var(--color-tone-12); transition: width 180ms ease, background 180ms ease; }
.context-pressure-card.warning .context-pressure-track > span { background: var(--color-warning); }
.context-pressure-card.critical .context-pressure-track > span { background: var(--color-danger); }
.context-pressure-caption { align-items: baseline; }
.context-pressure-caption strong { color: var(--color-tone-14); font-size: var(--font-size-caption); font-weight: var(--font-weight-semibold); }
.context-pressure-caption span { color: var(--color-text-tertiary); font-size: var(--font-size-micro); text-align: right; }
.context-stat-section { display: grid; gap: 7px; }
.context-stat-section h3 { margin: 0; color: var(--color-text-secondary); font-size: var(--font-size-caption); font-weight: var(--font-weight-medium); }
.context-stat-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 7px; }
.context-stat-grid article, .context-token-card article { display: grid; gap: 4px; min-width: 0; padding: 10px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-bg-surface); }
.context-stat-grid span, .context-token-card span { color: var(--color-text-tertiary); font-size: var(--font-size-caption); }
.context-stat-grid strong, .context-token-card strong { overflow: hidden; color: var(--color-tone-15); font: var(--font-size-body) var(--font-family-mono); font-weight: var(--font-weight-semibold); text-overflow: ellipsis; white-space: nowrap; }
.context-token-card { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); overflow: hidden; border: 1px solid var(--color-border-subtle); border-radius: 9px; background: var(--color-bg-surface); }
.context-token-card article { border: 0; border-radius: 0; background: transparent; }
.context-token-card article:nth-child(odd) { border-right: 1px solid var(--color-border-subtle); }
.context-token-card article:nth-child(n + 3) { border-top: 1px solid var(--color-border-subtle); }
.context-session-status, .context-session-empty { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px; min-height: 130px; padding: 18px; color: var(--color-text-tertiary); font-size: var(--font-size-caption); text-align: center; }
.context-session-status strong, .context-session-empty strong { color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.context-session-status.error strong { color: var(--color-danger); }
.context-session-spinner { width: 14px; height: 14px; border: 2px solid var(--color-border-default); border-top-color: var(--color-text-secondary); border-radius: 50%; animation: context-spinner 700ms linear infinite; }
.context-session-footer { overflow: hidden; padding: 1px 2px; color: var(--color-tone-9); font: var(--font-size-micro) var(--font-family-mono); text-overflow: ellipsis; white-space: nowrap; }
@keyframes context-spinner { to { transform: rotate(360deg); } }
</style>
