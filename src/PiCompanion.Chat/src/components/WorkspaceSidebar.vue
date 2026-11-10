<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { DropdownMenuRadioGroup } from 'reka-ui'
import { UiButton, UiMenu, UiMenuRadioItem } from '@/components/ui'
import TaskHoverCard, {
  type HoverWorkspacePresentation,
  type TaskHoverCardMode,
} from '@/components/TaskHoverCard.vue'
import WorkspaceIcon from '@/components/WorkspaceIcon.vue'
import type { MainView } from '@/composables/useTaskManagement'
import { useMinuteClock } from '@/composables/useMinuteClock'
import type { TaskHistoryEntry, WorkspaceHistoryEntry } from '@/types/bridge'
import { formatRelativeTimestamp } from '@/utils/dateTime'
import {
  buildSidebarWorkspaceGroups,
  matchesSidebarTaskStatus,
  normalizeWorkspacePath,
  type SidebarTaskStatusFilter,
  type SidebarWorkspaceGroup,
} from '@/utils/taskHistory'
import { taskStatusTone } from '@/utils/taskStatus'
import { useI18n } from '@/i18n'

export type SidebarTaskListMode = 'recent' | 'workspace'

const sidebarListModeStorageKey = 'pi-companion:sidebar-task-list-mode'
const sidebarStatusFilterStorageKey = 'pi-companion:sidebar-task-status-filter'
const sidebarCollapsedWorkspacesStorageKey = 'pi-companion:sidebar-collapsed-workspaces'
const defaultWorkspaceTaskLimit = 5

const { locale, t } = useI18n()
const relativeTimeNow = useMinuteClock()

const props = withDefaults(defineProps<{
  recentTasks: TaskHistoryEntry[]
  historyTasks?: TaskHistoryEntry[]
  historyLoading?: boolean
  workspaces?: WorkspaceHistoryEntry[]
  recentTaskSubtitle?: 'workspace' | 'latest-run'
  selectedHistoryTask?: TaskHistoryEntry | null
  currentTaskId?: string
  view: MainView
  width: number
}>(), {
  historyTasks: () => [],
  historyLoading: false,
  recentTaskSubtitle: 'workspace',
})

const emit = defineEmits<{
  newTask: []
  newTaskInWorkspace: [workspaceId: string]
  requestFullHistory: []
  showView: [view: Exclude<MainView, 'chat'>]
  selectTask: [taskId: string]
  openContextMenu: [event: MouseEvent, task: TaskHistoryEntry, recycled: boolean]
  beginResize: [event: PointerEvent]
  setWidth: [width: number]
  openSettings: []
}>()

const viewMenuOpen = ref(false)
const listMode = ref<SidebarTaskListMode>(loadListMode())
const statusFilter = ref<SidebarTaskStatusFilter>(loadStatusFilter())
const collapsedWorkspaceKeys = ref(loadCollapsedWorkspaceKeys())
const expandedTaskGroupKeys = ref(new Set<string>())

const statusFilterOptions = computed<Array<{
  value: SidebarTaskStatusFilter
  label: string
  tone?: string
}>>(() => [
  { value: 'all', label: t('全部') },
  { value: 'active', label: t('进行中'), tone: 'running' },
  { value: 'completed', label: t('已完成'), tone: 'success' },
  { value: 'stopped', label: t('已停止'), tone: 'idle' },
  { value: 'failed', label: t('失败'), tone: 'danger' },
])

const visibleSelectedHistoryTask = computed(() =>
  props.selectedHistoryTask && matchesSidebarTaskStatus(props.selectedHistoryTask, statusFilter.value)
    ? props.selectedHistoryTask
    : null)

const visibleRecentTasks = computed(() =>
  statusFilter.value === 'all'
    ? props.recentTasks
    : props.recentTasks.filter(task => matchesSidebarTaskStatus(task, statusFilter.value)))

const hasAnyTasks = computed(() =>
  props.recentTasks.length > 0 || props.historyTasks.length > 0 || Boolean(props.selectedHistoryTask))

const emptyTasksLabel = computed(() =>
  statusFilter.value !== 'all' && hasAnyTasks.value ? t('没有匹配的任务') : t('暂无任务'))

const workspaceGroups = computed(() =>
  buildSidebarWorkspaceGroups({
    recentTasks: props.recentTasks,
    historyTasks: props.historyTasks,
    selectedHistoryTask: props.selectedHistoryTask,
    workspaces: props.workspaces,
    statusFilter: statusFilter.value,
    directChatLabel: t('直接对话'),
    directChatPath: t('由 Pi Companion 管理的隔离空间'),
  }))

watch(
  [listMode, statusFilter, () => props.recentTasks.length, () => props.historyTasks.length],
  ([mode, filter]) => {
    if (mode === 'workspace' || filter !== 'all') {
      emit('requestFullHistory')
    }
  },
  { immediate: true },
)

watch(viewMenuOpen, (open) => {
  if (open) hideTaskHoverImmediately()
})

function loadListMode(): SidebarTaskListMode {
  if (typeof window === 'undefined') return 'recent'
  return window.localStorage.getItem(sidebarListModeStorageKey) === 'workspace'
    ? 'workspace'
    : 'recent'
}

function setListMode(value: unknown) {
  if (value !== 'recent' && value !== 'workspace') return
  hideTaskHoverImmediately()
  listMode.value = value
  window.localStorage.setItem(sidebarListModeStorageKey, value)
}

function loadStatusFilter(): SidebarTaskStatusFilter {
  if (typeof window === 'undefined') return 'all'
  const stored = window.localStorage.getItem(sidebarStatusFilterStorageKey)
  if (stored === 'active' || stored === 'completed' || stored === 'stopped' || stored === 'failed') {
    return stored
  }
  return 'all'
}

function setStatusFilter(value: unknown) {
  if (
    value !== 'all' &&
    value !== 'active' &&
    value !== 'completed' &&
    value !== 'stopped' &&
    value !== 'failed'
  ) {
    return
  }
  hideTaskHoverImmediately()
  statusFilter.value = value
  window.localStorage.setItem(sidebarStatusFilterStorageKey, value)
}

function loadCollapsedWorkspaceKeys() {
  if (typeof window === 'undefined') return new Set<string>()
  try {
    const stored = JSON.parse(window.localStorage.getItem(sidebarCollapsedWorkspacesStorageKey) ?? '[]')
    if (!Array.isArray(stored)) return new Set<string>()
    return new Set(stored.filter((key): key is string => typeof key === 'string'))
  } catch {
    return new Set<string>()
  }
}

function isGroupExpanded(collapseKey: string) {
  return !collapsedWorkspaceKeys.value.has(collapseKey)
}

function toggleGroup(collapseKey: string) {
  const nextKeys = new Set(collapsedWorkspaceKeys.value)
  if (nextKeys.has(collapseKey)) nextKeys.delete(collapseKey)
  else nextKeys.add(collapseKey)
  collapsedWorkspaceKeys.value = nextKeys
  window.localStorage.setItem(sidebarCollapsedWorkspacesStorageKey, JSON.stringify([...nextKeys]))
}

function visibleGroupTasks(group: SidebarWorkspaceGroup) {
  if (expandedTaskGroupKeys.value.has(group.key) || group.tasks.length <= defaultWorkspaceTaskLimit) {
    return group.tasks
  }
  const base = group.tasks.slice(0, defaultWorkspaceTaskLimit)
  if (!props.currentTaskId || base.some((task) => task.id === props.currentTaskId)) {
    return base
  }
  const currentInGroup = group.tasks.find((task) => task.id === props.currentTaskId)
  return currentInGroup ? [...base, currentInGroup] : base
}

function hiddenGroupTaskCount(group: SidebarWorkspaceGroup) {
  return Math.max(0, group.tasks.length - visibleGroupTasks(group).length)
}

function expandGroupTasks(groupKey: string) {
  const nextKeys = new Set(expandedTaskGroupKeys.value)
  nextKeys.add(groupKey)
  expandedTaskGroupKeys.value = nextKeys
}

function formatRecentTaskTime(value: string) {
  return formatRelativeTimestamp(value, locale.value, relativeTimeNow.value, t('刚刚'))
}

const hoverMode = ref<TaskHoverCardMode>('task-with-workspace')
const hoveredTask = ref<TaskHistoryEntry | null>(null)
const hoveredGroup = ref<SidebarWorkspaceGroup | null>(null)
const hoverLeft = ref(0)
const hoverTop = ref(0)
let hoverCloseTimer = 0

const hoverActive = computed(() =>
  hoverMode.value === 'workspace-only'
    ? Boolean(hoveredGroup.value)
    : Boolean(hoveredTask.value))

const hoveredWorkspace = computed<HoverWorkspacePresentation | null>(() => {
  if (hoverMode.value === 'workspace-only') {
    const group = hoveredGroup.value
    if (!group) return null
    return {
      name: group.name,
      workingDirectory: group.workingDirectory,
      iconKey: group.iconKey,
      colorKey: group.colorKey,
      generalChat: group.generalChat,
    }
  }
  const task = hoveredTask.value
  if (!task || task.scopeKind === 'GeneralChat') return null
  return workspaceForTask(task)
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

function positionHoverCard(target: HTMLElement, cardHeight: number) {
  const bounds = target.getBoundingClientRect()
  const cardWidth = 320
  const gap = 10
  hoverLeft.value = bounds.right + gap + cardWidth <= window.innerWidth
    ? bounds.right + gap
    : Math.max(8, bounds.left - cardWidth - gap)
  hoverTop.value = Math.min(
    Math.max(8, bounds.top - 8),
    Math.max(8, window.innerHeight - cardHeight - 8),
  )
}

function showTaskHover(
  event: Event,
  task: TaskHistoryEntry,
  mode: 'task-with-workspace' | 'task-only' = 'task-with-workspace',
) {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = 0
  positionHoverCard(event.currentTarget as HTMLElement, 154)
  hoverMode.value = mode
  hoveredGroup.value = null
  hoveredTask.value = task
}

function showWorkspaceHover(event: Event, group: SidebarWorkspaceGroup) {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = 0
  positionHoverCard(event.currentTarget as HTMLElement, 68)
  hoverMode.value = 'workspace-only'
  hoveredTask.value = null
  hoveredGroup.value = group
}

function scheduleHideTaskHover() {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = window.setTimeout(() => {
    hoveredTask.value = null
    hoveredGroup.value = null
    hoverCloseTimer = 0
  }, 100)
}

function hideTaskHoverImmediately() {
  if (hoverCloseTimer) window.clearTimeout(hoverCloseTimer)
  hoverCloseTimer = 0
  hoveredTask.value = null
  hoveredGroup.value = null
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
        {{ t('自定义模块') }}
      </UiButton>
      <UiButton class="nav-row" :class="{ selected: view === 'templates' }" type="button" @click="$emit('showView', 'templates')">
        <span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><rect x="5" y="3.5" width="14" height="17" rx="2" /><path d="M9 3.5v3h6v-3M9 11h6M9 15h4" /></svg></span>
        {{ t('任务模板') }}
      </UiButton>
      <UiButton class="nav-row" :class="{ selected: view === 'scheduled' }" type="button" @click="$emit('showView', 'scheduled')">
        <span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg></span>
        {{ t('定时任务') }}
      </UiButton>
    </nav>

    <div class="history-region">
      <UiButton
        v-if="listMode === 'recent' && visibleSelectedHistoryTask"
        class="history-item selected-history-item"
        :class="{ current: view === 'chat' && visibleSelectedHistoryTask.id === currentTaskId }"
        type="button"
        :aria-describedby="hoverMode === 'task-with-workspace' && hoveredTask?.id === visibleSelectedHistoryTask.id ? 'recent-task-hover-card' : undefined"
        @pointerenter="showTaskHover($event, visibleSelectedHistoryTask, 'task-with-workspace')"
        @pointerleave="scheduleHideTaskHover"
        @click="hideTaskHoverImmediately(); $emit('selectTask', visibleSelectedHistoryTask.id)"
        @contextmenu.prevent.stop="hideTaskHoverImmediately(); $emit('openContextMenu', $event, visibleSelectedHistoryTask, false)"
      >
        <span class="history-copy">
          <strong>{{ visibleSelectedHistoryTask.title }}</strong>
          <span class="history-meta">
            <span v-if="recentTaskSubtitle === 'latest-run'" class="history-state"><span class="history-status" :class="taskStatusTone(visibleSelectedHistoryTask.status)"></span><small>{{ latestRunStatus(visibleSelectedHistoryTask) }}</small></span>
            <span v-else class="history-state"><small>{{ recentTaskWorkspaceName(visibleSelectedHistoryTask) }}</small></span>
            <time v-if="formatRecentTaskTime(visibleSelectedHistoryTask.updatedAt)" class="history-updated" :datetime="visibleSelectedHistoryTask.updatedAt">{{ formatRecentTaskTime(visibleSelectedHistoryTask.updatedAt) }}</time>
          </span>
        </span>
      </UiButton>
      <div class="section-label history-heading">
        <span>{{ t('任务') }}</span>
        <UiMenu
          v-model="viewMenuOpen"
          class="history-view-menu"
          content-class="history-view-popover"
          :aria-label="t('任务列表选项')"
          align="end"
        >
          <template #trigger="{ open: menuOpen }">
            <UiButton
              class="history-mode-toggle"
              :class="{ active: listMode === 'workspace' || statusFilter !== 'all' }"
              type="button"
              :aria-expanded="menuOpen"
              :aria-label="t('任务列表选项')"
              :title="t('任务列表选项')"
            >
              <svg viewBox="0 0 16 16" aria-hidden="true">
                <path d="M2 5h3.5M8.5 5H14M2 11h6.5M11.5 11H14" />
                <circle cx="7" cy="5" r="1.5" />
                <circle cx="10" cy="11" r="1.5" />
              </svg>
            </UiButton>
          </template>
          <div class="history-view-section-label">{{ t('分组方式') }}</div>
          <DropdownMenuRadioGroup :model-value="listMode" @update:model-value="setListMode">
            <UiMenuRadioItem value="workspace" class="history-view-option">
              <span class="history-view-option-leading" aria-hidden="true">
                <svg class="history-view-option-icon" viewBox="0 0 16 16">
                  <path d="M2 3.5h4.2l1.2 1.5H14v7.5H2z" />
                </svg>
              </span>
              <span class="history-view-option-label">{{ t('按工作区') }}</span>
              <span class="history-view-option-check" aria-hidden="true">{{ listMode === 'workspace' ? '✓' : '' }}</span>
            </UiMenuRadioItem>
            <UiMenuRadioItem value="recent" class="history-view-option">
              <span class="history-view-option-leading" aria-hidden="true">
                <svg class="history-view-option-icon" viewBox="0 0 16 16">
                  <path d="M2.5 4h11M2.5 8h11M2.5 12h11" />
                </svg>
              </span>
              <span class="history-view-option-label">{{ t('单列表') }}</span>
              <span class="history-view-option-check" aria-hidden="true">{{ listMode === 'recent' ? '✓' : '' }}</span>
            </UiMenuRadioItem>
          </DropdownMenuRadioGroup>
          <div class="history-view-separator" role="separator" />
          <div class="history-view-section-label">{{ t('状态筛选') }}</div>
          <DropdownMenuRadioGroup :model-value="statusFilter" @update:model-value="setStatusFilter">
            <UiMenuRadioItem
              v-for="option in statusFilterOptions"
              :key="option.value"
              :value="option.value"
              class="history-view-option"
            >
              <span class="history-view-option-leading" aria-hidden="true">
                <svg v-if="option.value === 'all'" class="history-view-option-icon" viewBox="0 0 16 16">
                  <circle cx="8" cy="8" r="5" />
                </svg>
                <span v-else class="history-status" :class="option.tone"></span>
              </span>
              <span class="history-view-option-label">{{ option.label }}</span>
              <span class="history-view-option-check" aria-hidden="true">{{ statusFilter === option.value ? '✓' : '' }}</span>
            </UiMenuRadioItem>
          </DropdownMenuRadioGroup>
        </UiMenu>
      </div>
      <section v-if="listMode === 'recent'" class="history">
        <UiButton
          v-for="task in visibleRecentTasks"
          :key="task.id"
          class="history-item"
          :class="{ current: view === 'chat' && task.id === currentTaskId }"
          type="button"
          :aria-describedby="hoverMode === 'task-with-workspace' && hoveredTask?.id === task.id ? 'recent-task-hover-card' : undefined"
          @pointerenter="showTaskHover($event, task, 'task-with-workspace')"
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
        <p v-if="visibleRecentTasks.length === 0 && !visibleSelectedHistoryTask" class="history-empty">{{ emptyTasksLabel }}</p>
      </section>
      <section v-else class="history history-grouped" :aria-busy="historyLoading">
        <div
          v-if="historyLoading && workspaceGroups.length === 0"
          class="loading-skeleton sidebar-loading-skeleton"
          role="status"
          :aria-label="t('正在读取任务…')"
        >
          <span v-for="index in 3" :key="index" class="loading-skeleton-row"></span>
        </div>
        <template v-else-if="workspaceGroups.length > 0">
          <div
            v-for="group in workspaceGroups"
            :key="group.key"
            class="sidebar-workspace-group"
          >
            <div class="sidebar-workspace-header">
              <UiButton
                class="sidebar-workspace-toggle"
                type="button"
                :aria-expanded="isGroupExpanded(group.collapseKey)"
                :aria-label="t(isGroupExpanded(group.collapseKey) ? '收起工作区' : '展开工作区')"
                :aria-describedby="hoverMode === 'workspace-only' && hoveredGroup?.key === group.key ? 'recent-task-hover-card' : undefined"
                @pointerenter="showWorkspaceHover($event, group)"
                @pointerleave="scheduleHideTaskHover"
                @click="hideTaskHoverImmediately(); toggleGroup(group.collapseKey)"
              >
                <svg
                  class="sidebar-workspace-chevron"
                  :class="{ collapsed: !isGroupExpanded(group.collapseKey) }"
                  viewBox="0 0 16 16"
                  aria-hidden="true"
                >
                  <path d="m4.5 6 3.5 3.5L11.5 6" />
                </svg>
                <WorkspaceIcon :icon-key="group.iconKey" :color-key="group.colorKey" />
                <strong class="sidebar-workspace-name">{{ group.name }}</strong>
                <span
                  v-if="!isGroupExpanded(group.collapseKey) && group.hasActiveTask"
                  class="history-status running"
                ></span>
              </UiButton>
              <UiButton
                v-if="group.workspaceId && !group.generalChat"
                class="sidebar-workspace-new-task"
                type="button"
                :aria-label="t('在 {name} 中新建任务', { name: group.name })"
                :title="t('在此工作区新建任务')"
                @click="hideTaskHoverImmediately(); $emit('newTaskInWorkspace', group.workspaceId)"
              >
                <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 3v10M3 8h10" /></svg>
              </UiButton>
            </div>
            <div v-show="isGroupExpanded(group.collapseKey)" class="sidebar-workspace-tasks">
              <UiButton
                v-for="task in visibleGroupTasks(group)"
                :key="task.id"
                class="history-item history-item-compact"
                :class="{ current: view === 'chat' && task.id === currentTaskId }"
                type="button"
                :aria-describedby="hoverMode === 'task-only' && hoveredTask?.id === task.id ? 'recent-task-hover-card' : undefined"
                @pointerenter="showTaskHover($event, task, 'task-only')"
                @pointerleave="scheduleHideTaskHover"
                @click="hideTaskHoverImmediately(); $emit('selectTask', task.id)"
                @contextmenu.prevent.stop="hideTaskHoverImmediately(); $emit('openContextMenu', $event, task, false)"
              >
                <span class="history-compact-row">
                  <span class="history-status" :class="taskStatusTone(task.status)"></span>
                  <strong>{{ task.title }}</strong>
                  <time
                    v-if="formatRecentTaskTime(task.updatedAt)"
                    class="history-updated"
                    :datetime="task.updatedAt"
                  >{{ formatRecentTaskTime(task.updatedAt) }}</time>
                </span>
              </UiButton>
              <UiButton
                v-if="hiddenGroupTaskCount(group) > 0"
                class="sidebar-workspace-more"
                type="button"
                :aria-label="t('显示 {name} 中的全部任务', { name: group.name })"
                @click="expandGroupTasks(group.key)"
              >
                {{ t('展开其余 {count} 项', { count: hiddenGroupTaskCount(group) }) }}
              </UiButton>
            </div>
          </div>
        </template>
        <p v-else class="history-empty">{{ emptyTasksLabel }}</p>
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
        v-if="hoverActive"
        :mode="hoverMode"
        :task="hoveredTask"
        :workspace="hoveredWorkspace"
        :left="hoverLeft"
        :top="hoverTop"
      />
    </Teleport>
  </aside>
</template>
