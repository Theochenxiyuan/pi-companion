<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { UiButton } from '@/components/ui'
import TaskHoverCard from '@/components/TaskHoverCard.vue'
import type { MainView } from '@/composables/useTaskManagement'
import { useMinuteClock } from '@/composables/useMinuteClock'
import type { TaskHistoryEntry, WorkspaceHistoryEntry } from '@/types/bridge'
import { formatRelativeTimestamp } from '@/utils/dateTime'
import { taskStatusTone } from '@/utils/taskStatus'
import { useI18n } from '@/i18n'

const { locale, t } = useI18n()
const relativeTimeNow = useMinuteClock()

const props = withDefaults(defineProps<{
  recentTasks: TaskHistoryEntry[]
  workspaces?: WorkspaceHistoryEntry[]
  recentTaskSubtitle?: 'workspace' | 'latest-run'
  selectedHistoryTask?: TaskHistoryEntry | null
  currentTaskId?: string
  view: MainView
  width: number
}>(), {
  recentTaskSubtitle: 'workspace',
})

defineEmits<{
  newTask: []
  showView: [view: Exclude<MainView, 'chat'>]
  selectTask: [taskId: string]
  openContextMenu: [event: MouseEvent, task: TaskHistoryEntry, recycled: boolean]
  beginResize: [event: PointerEvent]
  setWidth: [width: number]
  openSettings: []
}>()

function formatRecentTaskTime(value: string) {
  return formatRelativeTimestamp(value, locale.value, relativeTimeNow.value, t('刚刚'))
}

const hoveredTask = ref<TaskHistoryEntry | null>(null)
const hoverLeft = ref(0)
const hoverTop = ref(0)
let hoverCloseTimer = 0

const hoveredWorkspace = computed(() => {
  const task = hoveredTask.value
  if (!task || task.scopeKind === 'GeneralChat') return null
  if (task.workspaceId) {
    const byId = props.workspaces?.find(workspace => workspace.id === task.workspaceId)
    if (byId) return byId
  }
  const path = normalizeWorkspacePath(task.workingDirectory)
  return props.workspaces?.find(workspace =>
    normalizeWorkspacePath(workspace.workingDirectory) === path) ?? null
})

function workspaceForTask(task: TaskHistoryEntry) {
  if (task.scopeKind === 'GeneralChat') return null
  if (task.workspaceId) {
    const byId = props.workspaces?.find(workspace => workspace.id === task.workspaceId)
    if (byId) return byId
  }
  const path = normalizeWorkspacePath(task.workingDirectory)
  return props.workspaces?.find(workspace =>
    normalizeWorkspacePath(workspace.workingDirectory) === path) ?? null
}

function recentTaskWorkspaceName(task: TaskHistoryEntry) {
  if (task.scopeKind === 'GeneralChat') return t('直接对话')
  const workspace = workspaceForTask(task)
  if (workspace?.name) return workspace.name
  const segments = task.workingDirectory.split(/[\\/]/).filter(Boolean)
  return segments.at(-1) ?? task.workingDirectory
}

function latestRunStatus(task: TaskHistoryEntry) {
  return t('最近一轮：{status}', { status: t(task.statusText) })
}

function normalizeWorkspacePath(path: string) {
  return path.trim().replace(/\//gu, '\\').replace(/\\+$/gu, '').toLocaleLowerCase('en-US')
}

function showTaskHover(event: Event, task: TaskHistoryEntry) {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = 0
  const target = event.currentTarget as HTMLElement
  const bounds = target.getBoundingClientRect()
  const cardWidth = 320
  const cardHeight = 154
  const gap = 10
  hoverLeft.value = bounds.right + gap + cardWidth <= window.innerWidth
    ? bounds.right + gap
    : Math.max(8, bounds.left - cardWidth - gap)
  hoverTop.value = Math.min(
    Math.max(8, bounds.top - 8),
    Math.max(8, window.innerHeight - cardHeight - 8),
  )
  hoveredTask.value = task
}

function scheduleHideTaskHover() {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = window.setTimeout(() => {
    hoveredTask.value = null
    hoverCloseTimer = 0
  }, 100)
}

function hideTaskHoverImmediately() {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = 0
  hoveredTask.value = null
}

onBeforeUnmount(() => {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
})
</script>

<template>
  <aside class="sidebar">
    <UiButton class="new-task" type="button" @click="$emit('newTask')">
      <span class="new-task-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" /></svg>
      </span>
      {{ t('新建任务') }}
      <kbd>Ctrl N</kbd>
    </UiButton>

    <nav :aria-label="t('任务导航')">
      <UiButton class="nav-row" :class="{ selected: view === 'history' }" type="button" @click="$emit('showView', 'history')">
        <span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M7 6h13M7 12h13M7 18h13" /><circle cx="3.5" cy="6" r=".7" /><circle cx="3.5" cy="12" r=".7" /><circle cx="3.5" cy="18" r=".7" /></svg></span>
        {{ t('全部任务') }}
      </UiButton>
      <UiButton class="nav-row" :class="{ selected: view === 'skills' }" type="button" @click="$emit('showView', 'skills')">
        <span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M9.2 4.5a2.8 2.8 0 1 1 5.6 0V7H17v2.2a2.8 2.8 0 1 1 0 5.6V17h-2.2a2.8 2.8 0 1 1-5.6 0H7v-2.2a2.8 2.8 0 1 1 0-5.6V7h2.2z" /></svg></span>
        {{ t('技能') }}
      </UiButton>
      <UiButton class="nav-row" :class="{ selected: view === 'presets' }" type="button" @click="$emit('showView', 'presets')">
        <span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><rect x="5" y="3.5" width="14" height="17" rx="2" /><path d="M9 3.5v3h6v-3M9 11h6M9 15h4" /></svg></span>
        {{ t('预置任务') }}
      </UiButton>
      <UiButton class="nav-row" :class="{ selected: view === 'scheduled' }" type="button" @click="$emit('showView', 'scheduled')">
        <span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg></span>
        {{ t('定时任务') }}
      </UiButton>
    </nav>

    <div class="history-region">
      <UiButton
        v-if="selectedHistoryTask"
        class="history-item selected-history-item"
        :class="{ current: view === 'chat' && selectedHistoryTask.id === currentTaskId }"
        type="button"
        :aria-describedby="hoveredTask?.id === selectedHistoryTask.id ? 'recent-task-hover-card' : undefined"
        @pointerenter="showTaskHover($event, selectedHistoryTask)"
        @pointerleave="scheduleHideTaskHover"
        @click="hideTaskHoverImmediately(); $emit('selectTask', selectedHistoryTask.id)"
        @contextmenu.prevent.stop="hideTaskHoverImmediately(); $emit('openContextMenu', $event, selectedHistoryTask, false)"
      >
        <span class="history-copy">
          <strong>{{ selectedHistoryTask.title }}</strong>
          <span class="history-meta">
            <span v-if="recentTaskSubtitle === 'latest-run'" class="history-state"><span class="history-status" :class="taskStatusTone(selectedHistoryTask.status)"></span><small>{{ latestRunStatus(selectedHistoryTask) }}</small></span>
            <span v-else class="history-state"><small>{{ recentTaskWorkspaceName(selectedHistoryTask) }}</small></span>
            <time v-if="formatRecentTaskTime(selectedHistoryTask.updatedAt)" class="history-updated" :datetime="selectedHistoryTask.updatedAt">{{ formatRecentTaskTime(selectedHistoryTask.updatedAt) }}</time>
          </span>
        </span>
      </UiButton>
      <p class="section-label">{{ t('最近') }}</p>
      <section class="history">
        <UiButton
          v-for="task in recentTasks"
          :key="task.id"
          class="history-item"
          :class="{ current: view === 'chat' && task.id === currentTaskId }"
          type="button"
          :aria-describedby="hoveredTask?.id === task.id ? 'recent-task-hover-card' : undefined"
          @pointerenter="showTaskHover($event, task)"
          @pointerleave="scheduleHideTaskHover"
          @click="hideTaskHoverImmediately(); $emit('selectTask', task.id)"
          @contextmenu.prevent.stop="hideTaskHoverImmediately(); $emit('openContextMenu', $event, task, false)"
        >
          <span class="history-copy">
            <strong>{{ task.title }}</strong>
            <span class="history-meta">
              <span v-if="recentTaskSubtitle === 'latest-run'" class="history-state"><span class="history-status" :class="taskStatusTone(task.status)"></span><small>{{ latestRunStatus(task) }}</small></span>
              <span v-else class="history-state"><small>{{ recentTaskWorkspaceName(task) }}</small></span>
              <time v-if="formatRecentTaskTime(task.updatedAt)" class="history-updated" :datetime="task.updatedAt">{{ formatRecentTaskTime(task.updatedAt) }}</time>
            </span>
          </span>
        </UiButton>
        <p v-if="recentTasks.length === 0" class="history-empty">{{ t('暂无任务') }}</p>
      </section>
    </div>

    <div class="sidebar-footer">
      <UiButton class="nav-row settings" type="button" @click="$emit('openSettings')">
        <span class="nav-icon" aria-hidden="true">
          <svg viewBox="0 0 24 24">
            <path d="M4 7h10M18 7h2M4 17h2M10 17h10" />
            <circle cx="16" cy="7" r="2" />
            <circle cx="8" cy="17" r="2" />
          </svg>
        </span>
        {{ t('设置') }}
      </UiButton>
    </div>
    <div
      class="sidebar-resizer"
      role="separator"
      :aria-label="t('调整侧栏宽度')"
      aria-orientation="vertical"
      :aria-valuemin="220"
      :aria-valuemax="420"
      :aria-valuenow="width"
      tabindex="0"
      @pointerdown="$emit('beginResize', $event)"
      @dblclick="$emit('setWidth', 232)"
      @keydown.left.prevent="$emit('setWidth', width - 12)"
      @keydown.right.prevent="$emit('setWidth', width + 12)"
    ></div>
    <Teleport to="body">
      <TaskHoverCard
        v-if="hoveredTask"
        :task="hoveredTask"
        :workspace="hoveredWorkspace"
        :left="hoverLeft"
        :top="hoverTop"
      />
    </Teleport>
  </aside>
</template>
