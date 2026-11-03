<script setup lang="ts">
import { computed } from 'vue'
import { UiButton } from '@/components/ui'
import AppMoreMenu from '@/components/AppMoreMenu.vue'
import ScheduledTaskRow from '@/components/ScheduledTaskRow.vue'
import type { ScheduledTask, TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { useI18n } from '@/i18n'

const { t } = useI18n()
const props = defineProps<{
  scheduledTasks: ScheduledTask[]
  templates: TaskTemplate[]
  workspaces: WorkspaceHistoryEntry[]
  sidebarCollapsed: boolean
  detailLevel?: 'summary' | 'normal' | 'verbose'
  pendingAction?: { taskId: string; action: 'save' | 'run' | 'delete' } | null
  loading?: boolean
}>()

defineEmits<{
  toggleSidebar: []
  toggleMonitor: []
  setDetail: [level: 'summary' | 'normal' | 'verbose']
  exit: []
  create: []
  edit: [scheduledTask: ScheduledTask]
  toggle: [scheduledTask: ScheduledTask, enabled: boolean]
  runNow: [scheduledTask: ScheduledTask]
  delete: [scheduledTask: ScheduledTask]
  openTask: [taskId: string]
}>()

const enabledCount = computed(() => props.scheduledTasks.filter(task => task.isEnabled).length)
const pausedCount = computed(() => props.scheduledTasks.length - enabledCount.value)
const subtitle = computed(() => {
  const parts: string[] = []
  if (enabledCount.value) parts.push(t('{enabled} 个运行中', { enabled: enabledCount.value }))
  if (pausedCount.value) parts.push(t('{paused} 个已暂停', { paused: pausedCount.value }))
  return parts.join(' · ')
})
const showLoading = computed(() => Boolean(props.loading) && props.scheduledTasks.length === 0)

function isPending(scheduledTask: ScheduledTask) {
  return props.pendingAction?.taskId === scheduledTask.id
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
          <small v-if="subtitle">{{ subtitle }}</small>
        </div>
      </div>
      <div class="topbar-actions">
        <UiButton class="scheduled-task-create" variant="secondary" size="md" type="button" @click="$emit('create')">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
          <span>{{ t('新建定时任务') }}</span>
        </UiButton>
        <AppMoreMenu
          :detail-level="detailLevel ?? 'normal'"
          @toggle-monitor="$emit('toggleMonitor')"
          @set-detail="$emit('setDetail', $event)"
          @exit="$emit('exit')"
        />
      </div>
    </header>

    <section class="management-content scheduled-task-content">
      <div
        v-if="showLoading"
        class="loading-skeleton management-loading-skeleton"
        role="status"
        :aria-label="t('正在读取定时任务…')"
      >
        <span v-for="index in 4" :key="index" class="loading-skeleton-row"></span>
      </div>

      <div v-else-if="scheduledTasks.length" class="surface-card scheduled-task-surface">
        <div class="scheduled-task-list-head" aria-hidden="true">
          <span>{{ t('名称') }}</span>
          <span>{{ t('频率') }}</span>
          <span>{{ t('下次运行') }}</span>
          <span>{{ t('最近结果') }}</span>
          <span></span>
          <span></span>
        </div>
        <div class="scheduled-task-list" role="list">
          <ScheduledTaskRow
            v-for="scheduledTask in scheduledTasks"
            :key="scheduledTask.id"
            :scheduled-task="scheduledTask"
            :templates="templates"
            :workspaces="workspaces"
            :pending="isPending(scheduledTask)"
            @toggle="(task, enabled) => $emit('toggle', task, enabled)"
            @run-now="$emit('runNow', $event)"
            @edit="$emit('edit', $event)"
            @delete="$emit('delete', $event)"
            @open-task="$emit('openTask', $event)"
          />
        </div>
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
.scheduled-task-empty { display: grid; width: min(100%, var(--management-content-width)); flex: 1; place-items: center; align-content: center; gap: 10px; margin: 0 auto; color: var(--color-text-secondary); text-align: center; }
.scheduled-task-empty h1, .scheduled-task-empty p { margin: 0; }
.scheduled-task-empty h1 { color: var(--color-text-primary); font-size: var(--font-size-title-md); font-weight: var(--font-weight-semibold); }
.scheduled-task-empty p { font-size: var(--font-size-body-sm); }
.scheduled-task-empty > div { display: grid; width: 52px; height: 52px; place-items: center; border: 1px solid var(--color-border-subtle); border-radius: 14px; background: var(--color-tone-2); }
.scheduled-task-empty svg { width: 28px; fill: none; stroke: currentColor; stroke-width: 1.5; }
</style>
