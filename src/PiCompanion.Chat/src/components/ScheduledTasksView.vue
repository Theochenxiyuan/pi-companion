<script setup lang="ts">
import { computed } from 'vue'
import { UiButton, UiSwitch } from '@/components/ui'
import type { ScheduledTask, TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { useI18n } from '@/i18n'

const { t, locale } = useI18n()
const props = defineProps<{
  scheduledTasks: ScheduledTask[]
  templates: TaskTemplate[]
  workspaces: WorkspaceHistoryEntry[]
  sidebarCollapsed: boolean
}>()

defineEmits<{
  toggleSidebar: []
  create: []
  edit: [scheduledTask: ScheduledTask]
  toggle: [scheduledTask: ScheduledTask, enabled: boolean]
  runNow: [scheduledTask: ScheduledTask]
  delete: [scheduledTask: ScheduledTask]
  openTask: [taskId: string]
}>()

const templateById = computed(() => new Map(props.templates.map(template => [template.id, template])))
const workspaceById = computed(() => new Map(props.workspaces.map(workspace => [workspace.id, workspace])))

function sourceLabel(scheduledTask: ScheduledTask) {
  if (scheduledTask.templateId) {
    return t('关联模板：{name}', {
      name: templateById.value.get(scheduledTask.templateId)?.name ?? t('模板不可用'),
    })
  }
  if (scheduledTask.targetKind === 'GeneralChat') return t('自定义 · 直接对话')
  return t('自定义 · {name}', {
    name: workspaceById.value.get(scheduledTask.workspaceId ?? '')?.name ?? t('工作区不可用'),
  })
}

function frequencyLabel(scheduledTask: ScheduledTask) {
  const time = new Date(scheduledTask.localStartAt).toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' })
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
}

function nextRunLabel(scheduledTask: ScheduledTask) {
  if (!scheduledTask.isEnabled) return t('已暂停')
  if (!scheduledTask.nextRunAt) return t('没有下次运行')
  return t('下次：{time}', { time: new Date(scheduledTask.nextRunAt).toLocaleString(locale.value) })
}

function occurrenceLabel(scheduledTask: ScheduledTask) {
  const occurrence = scheduledTask.lastOccurrence
  if (!occurrence) return t('尚未运行')
  if (occurrence.status === 'Enqueued') return t('最近一次已创建任务')
  if (occurrence.status === 'Dispatching') return t('正在创建任务')
  if (occurrence.status === 'Skipped') return t('最近一次已跳过')
  return t('最近一次创建失败')
}
</script>

<template>
  <main class="management-main scheduled-tasks-view">
    <header class="topbar management-topbar">
      <div class="topbar-leading">
        <UiButton
          class="sidebar-toggle"
          type="button"
          :aria-label="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
          :title="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
          @click="$emit('toggleSidebar')"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3.5" y="4" width="17" height="16" rx="2" /><path d="M9 4v16" /></svg>
        </UiButton>
        <div class="location management-location">
          <strong>{{ t('定时任务') }}</strong>
          <span>{{ t('按计划在后台创建独立任务') }}</span>
        </div>
      </div>
      <UiButton class="scheduled-task-create" variant="secondary" size="md" type="button" @click="$emit('create')">
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
        <span>{{ t('新建定时任务') }}</span>
      </UiButton>
    </header>

    <section class="management-content scheduled-task-content">
      <p class="scheduled-task-runtime-note">{{ t('Pi Companion 需要在后台运行才能准时触发；完全退出后会在下次启动时按错过策略处理。') }}</p>
      <div v-if="scheduledTasks.length" class="scheduled-task-grid">
        <article v-for="scheduledTask in scheduledTasks" :key="scheduledTask.id" class="surface-card scheduled-task-card" :class="{ disabled: !scheduledTask.isEnabled }">
          <header>
            <div>
              <strong>{{ scheduledTask.name }}</strong>
              <span>{{ sourceLabel(scheduledTask) }}</span>
            </div>
            <UiSwitch
              :model-value="scheduledTask.isEnabled"
              :aria-label="t(scheduledTask.isEnabled ? '暂停定时任务' : '启用定时任务')"
              @update:model-value="$emit('toggle', scheduledTask, $event)"
            />
          </header>
          <div class="scheduled-task-timing">
            <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="10" cy="10" r="7" /><path d="M10 6v4l3 2" /></svg>
            <div><strong>{{ frequencyLabel(scheduledTask) }}</strong><span>{{ nextRunLabel(scheduledTask) }} · {{ scheduledTask.timeZoneId }}</span></div>
          </div>
          <p v-if="scheduledTask.lastOccurrence?.error" class="scheduled-task-error">{{ scheduledTask.lastOccurrence.error }}</p>
          <footer>
            <span>{{ occurrenceLabel(scheduledTask) }}</span>
            <div>
              <UiButton v-if="scheduledTask.lastOccurrence?.taskId" variant="secondary" size="sm" type="button" @click="$emit('openTask', scheduledTask.lastOccurrence.taskId)">{{ t('打开任务') }}</UiButton>
              <UiButton variant="secondary" size="sm" type="button" @click="$emit('runNow', scheduledTask)">{{ t('立即运行') }}</UiButton>
              <UiButton variant="secondary" size="sm" type="button" @click="$emit('edit', scheduledTask)">{{ t('编辑') }}</UiButton>
              <UiButton class="danger-action" variant="ghost" size="sm" type="button" @click="$emit('delete', scheduledTask)">{{ t('删除') }}</UiButton>
            </div>
          </footer>
        </article>
      </div>

      <div v-else class="scheduled-task-empty">
        <div aria-hidden="true"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg></div>
        <h1>{{ t('还没有定时任务') }}</h1>
        <p>{{ t('按一次、每天、工作日或每周计划自动创建任务。') }}</p>
        <UiButton class="scheduled-task-create" variant="secondary" size="md" type="button" @click="$emit('create')">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
          <span>{{ t('新建定时任务') }}</span>
        </UiButton>
      </div>
    </section>
  </main>
</template>

<style scoped>
.scheduled-task-create { display: inline-flex; flex: none; align-items: center; gap: 6px; white-space: nowrap; }
.scheduled-task-create svg { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-linecap: round; stroke-width: 1.6; }
.scheduled-task-create:hover { border-color: var(--color-border-strong); background: var(--color-bg-hover); color: var(--color-text-primary); }
.scheduled-task-grid { display: grid; width: min(100%, 1080px); gap: 12px; margin: 0 auto; }
.scheduled-task-runtime-note { box-sizing: border-box; width: min(100%, 1080px); margin: 0 auto 14px; padding: 10px 12px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-tone-2); color: var(--color-text-secondary); font-size: var(--font-size-body-sm); line-height: var(--line-height-body); }
.scheduled-task-card { padding: 16px; }
.scheduled-task-card.disabled { background: var(--color-tone-2); }
.scheduled-task-card > header, .scheduled-task-card > footer { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
.scheduled-task-card > header > div, .scheduled-task-timing > div { display: grid; gap: 4px; min-width: 0; }
.scheduled-task-card header strong { overflow: hidden; color: var(--color-text-primary); font-size: var(--font-size-body); text-overflow: ellipsis; white-space: nowrap; }
.scheduled-task-card header span, .scheduled-task-card footer > span, .scheduled-task-timing span { color: var(--color-text-secondary); font-size: var(--font-size-caption); }
.scheduled-task-timing { display: flex; align-items: center; gap: 10px; margin: 14px 0; padding: 11px 12px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-bg-elevated); }
.scheduled-task-timing svg { width: 20px; height: 20px; flex: none; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.6; }
.scheduled-task-card > footer { margin-top: 14px; padding-top: 12px; border-top: 1px solid var(--color-border-subtle); }
.scheduled-task-card footer > div { display: flex; flex-wrap: wrap; gap: 6px; justify-content: flex-end; }
.scheduled-task-card footer .ui-button--secondary:hover { border-color: var(--color-border-strong); background: var(--color-bg-hover); color: var(--color-text-primary); }
.scheduled-task-card footer .danger-action { color: var(--color-danger-text); }
.scheduled-task-card footer .danger-action:hover { border-color: var(--color-danger-border); background: var(--color-danger-surface); color: var(--color-danger-text-strong); }
.scheduled-task-error { margin: -6px 0 14px; color: var(--color-danger-text-strong); font-size: var(--font-size-body-sm); }
.scheduled-task-empty { display: grid; width: min(100%, 1080px); min-height: 360px; place-items: center; align-content: center; gap: 10px; margin: 0 auto; color: var(--color-text-secondary); text-align: center; }
.scheduled-task-empty h1, .scheduled-task-empty p { margin: 0; }
.scheduled-task-empty h1 { color: var(--color-text-primary); font-size: var(--font-size-title-md); font-weight: var(--font-weight-semibold); }
.scheduled-task-empty p { font-size: var(--font-size-body-sm); }
.scheduled-task-empty > div { display: grid; width: 52px; height: 52px; place-items: center; border: 1px solid var(--color-border-subtle); border-radius: 14px; background: var(--color-tone-2); }
.scheduled-task-empty svg { width: 28px; fill: none; stroke: currentColor; stroke-width: 1.5; }

@container (max-width: 620px) {
  .scheduled-task-card > footer { align-items: flex-start; flex-direction: column; gap: 10px; }
  .scheduled-task-card footer > div { justify-content: flex-start; }
  .scheduled-task-timing span { overflow-wrap: anywhere; }
}

@container (max-width: 420px) {
  .management-topbar > .scheduled-task-create { width: var(--control-height-md); justify-content: center; padding: 0; }
  .management-topbar > .scheduled-task-create span { display: none; }
}
</style>
