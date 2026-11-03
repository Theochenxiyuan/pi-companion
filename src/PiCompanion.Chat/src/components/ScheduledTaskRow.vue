<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton, UiMenu, UiMenuItem, UiSwitch } from '@/components/ui'
import type { ScheduledTask, TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { formatNextRun } from '@/utils/scheduleFormat'
import { useI18n } from '@/i18n'

const props = defineProps<{
  scheduledTask: ScheduledTask
  templates: TaskTemplate[]
  workspaces: WorkspaceHistoryEntry[]
  pending: boolean
}>()

const emit = defineEmits<{
  toggle: [scheduledTask: ScheduledTask, enabled: boolean]
  runNow: [scheduledTask: ScheduledTask]
  edit: [scheduledTask: ScheduledTask]
  delete: [scheduledTask: ScheduledTask]
  openTask: [taskId: string]
}>()

const { t, locale } = useI18n()
const menuOpen = ref(false)
const localTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone

const source = computed(() => {
  const scheduledTask = props.scheduledTask
  if (scheduledTask.templateId) {
    const template = props.templates.find(candidate => candidate.id === scheduledTask.templateId)
    return t('关联模板：{name}', { name: template?.name ?? t('模板不可用') })
  }
  if (scheduledTask.targetKind === 'GeneralChat') return t('自定义 · 直接对话')
  const workspace = props.workspaces.find(candidate => candidate.id === scheduledTask.workspaceId)
  return t('自定义 · {name}', { name: workspace?.name ?? t('工作区不可用') })
})

const frequency = computed(() => {
  const scheduledTask = props.scheduledTask
  const time = new Date(scheduledTask.localStartAt).toLocaleTimeString(locale.value, {
    hour: '2-digit',
    minute: '2-digit',
  })
  if (scheduledTask.frequency === 'Once') {
    return new Date(scheduledTask.localStartAt).toLocaleString(locale.value, {
      year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
    })
  }
  if (scheduledTask.frequency === 'Daily') return t('每天 {time}', { time })
  if (scheduledTask.frequency === 'Weekdays') return t('工作日 {time}', { time })
  const days = [
    [1, '周一'], [2, '周二'], [4, '周三'], [8, '周四'], [16, '周五'], [32, '周六'], [64, '周日'],
  ].filter(([bit]) => (scheduledTask.daysOfWeek & Number(bit)) !== 0)
    .map(([, label]) => t(String(label)))
    .join('、')
  return t('每周 {days} {time}', { days, time })
})

const nextRun = computed(() => {
  const scheduledTask = props.scheduledTask
  if (!scheduledTask.isEnabled) return t('已暂停')
  if (!scheduledTask.nextRunAt) return t('没有下次运行')
  return formatNextRun(scheduledTask.nextRunAt, { locale: locale.value, t })
})

const showTimeZone = computed(() => props.scheduledTask.timeZoneId !== localTimeZone)

const nextRunTitle = computed(() => {
  const scheduledTask = props.scheduledTask
  if (!scheduledTask.isEnabled || !scheduledTask.nextRunAt) return undefined
  const date = new Date(scheduledTask.nextRunAt)
  if (Number.isNaN(date.getTime())) return undefined
  return date.toLocaleString(locale.value)
})

const occurrence = computed(() => {
  const last = props.scheduledTask.lastOccurrence
  if (!last) return t('尚未运行')
  if (last.status === 'Enqueued') return t('最近一次已创建任务')
  if (last.status === 'Dispatching') return t('正在创建任务')
  if (last.status === 'Skipped') return t('最近一次已跳过')
  return t('最近一次创建失败')
})

const failed = computed(() => props.scheduledTask.lastOccurrence?.status === 'Failed')
const error = computed(() => props.scheduledTask.lastOccurrence?.error ?? '')

function openTask() {
  const taskId = props.scheduledTask.lastOccurrence?.taskId
  if (taskId) emit('openTask', taskId)
}
</script>

<template>
  <article
    class="scheduled-task-row"
    role="listitem"
    :class="{ disabled: !scheduledTask.isEnabled }"
    :aria-busy="pending"
  >
    <div class="scheduled-task-name">
      <strong :title="scheduledTask.name">{{ scheduledTask.name }}</strong>
      <small>{{ source }}</small>
    </div>
    <div class="scheduled-task-meta">
      <span class="scheduled-task-frequency" :title="frequency">{{ frequency }}</span>
      <div class="scheduled-task-next">
        <span :title="nextRunTitle">{{ nextRun }}</span>
        <small v-if="showTimeZone">{{ scheduledTask.timeZoneId }}</small>
      </div>
      <span class="scheduled-task-result" :class="{ danger: failed }">{{ occurrence }}</span>
    </div>
    <UiSwitch
      class="scheduled-task-switch"
      :model-value="scheduledTask.isEnabled"
      :aria-label="t(scheduledTask.isEnabled ? '暂停定时任务' : '启用定时任务')"
      :disabled="pending"
      @update:model-value="emit('toggle', scheduledTask, $event)"
    />
    <div class="scheduled-task-actions">
      <UiButton
        variant="secondary"
        size="sm"
        type="button"
        :disabled="pending"
        @click="emit('runNow', scheduledTask)"
      >{{ t('立即运行') }}</UiButton>
      <UiMenu
        v-model="menuOpen"
        content-class="app-more-popover"
        :aria-label="t('更多操作')"
        align="end"
      >
        <template #trigger="{ open }">
          <UiButton
            class="scheduled-task-more"
            variant="ghost"
            size="icon"
            type="button"
            :aria-label="t('更多操作')"
            :aria-expanded="open"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1.7" /><circle cx="12" cy="12" r="1.7" /><circle cx="19" cy="12" r="1.7" /></svg>
          </UiButton>
        </template>
        <UiMenuItem
          v-if="scheduledTask.lastOccurrence?.taskId"
          @select="openTask"
        >{{ t('打开任务') }}</UiMenuItem>
        <UiMenuItem :disabled="pending" @select="emit('edit', scheduledTask)">{{ t('编辑') }}</UiMenuItem>
        <UiMenuItem danger :disabled="pending" @select="emit('delete', scheduledTask)">{{ t('删除') }}</UiMenuItem>
      </UiMenu>
    </div>
    <p v-if="error" class="scheduled-task-error">{{ error }}</p>
  </article>
</template>
