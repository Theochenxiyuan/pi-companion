<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { UiButton, UiInput, UiSelect } from '@/components/ui'
import WorkspaceIcon from '@/components/WorkspaceIcon.vue'
import { useMinuteClock } from '@/composables/useMinuteClock'
import type {
  TaskHistoryEntry,
  WorkspaceColorKey,
  WorkspaceHistoryEntry,
  WorkspaceIconKey,
} from '@/types/bridge'
import { formatFullTimestamp, formatRelativeTimestamp } from '@/utils/dateTime'
import { activeTaskStatuses, taskStatusTone } from '@/utils/taskStatus'
import { useI18n } from '@/i18n'

const { locale, t } = useI18n()
const relativeTimeNow = useMinuteClock()

const props = withDefaults(defineProps<{
  tasks: TaskHistoryEntry[]
  workspaces?: WorkspaceHistoryEntry[]
  sidebarCollapsed: boolean
}>(), {
  workspaces: () => [],
})

const search = defineModel<string>('search', { required: true })
const status = defineModel<string>('status', { required: true })

const emit = defineEmits<{
  toggleSidebar: []
  selectTask: [taskId: string]
  openContextMenu: [event: MouseEvent, task: TaskHistoryEntry, recycled: boolean]
  createWorkspace: []
  newTaskInWorkspace: [workspaceId: string]
  manageWorkspaceSkills: [workspaceId: string]
  editWorkspace: [workspaceId: string]
  hideWorkspace: [workspaceId: string]
}>()

const generalChatInitialTaskLimit = 10
const workspaceInitialTaskLimit = 5
const collapsedWorkspacesStorageKey = 'pi-companion:task-management-collapsed-workspaces'

const statusOptions = computed(() => [
  { value: 'all', label: t('最新进度：全部') },
  { value: 'active', label: t('最新进度：进行中') },
  { value: 'completed', label: t('最新进度：已完成') },
  { value: 'stopped', label: t('最新进度：已停止') },
  { value: 'failed', label: t('最新进度：失败') },
])

interface WorkspaceGroup {
  key: string
  collapseKey: string
  workspaceId: string | null
  name: string
  path: string
  tasks: TaskHistoryEntry[]
  updatedAt: string
  taskCount: number
  hasActiveTask: boolean
  matchesSearch: boolean
  generalChat: boolean
  iconKey: WorkspaceIconKey
  colorKey: WorkspaceColorKey
}

const collapsedWorkspaceKeys = ref(loadCollapsedWorkspaceKeys())
const workspaceTaskLimits = ref(new Map<string, number>())
const managementRoot = ref<HTMLElement | null>(null)
const workspaceColumnCount = ref(1)
let workspaceResizeObserver: ResizeObserver | null = null

const visibleTasks = computed(() => {
  const query = search.value.trim().toLocaleLowerCase('zh-CN')
  return props.tasks.filter((task) => {
    if (query && ![
      task.title,
      task.workingDirectory,
      task.summary,
      task.scopeKind === 'GeneralChat' ? t('直接对话') : '',
      workspaceForTask(task)?.name ?? '',
    ]
      .some((value) => value.toLocaleLowerCase('zh-CN').includes(query))) return false
    if (status.value === 'all') return true
    if (status.value === 'active') return activeTaskStatuses.includes(task.status)
    if (status.value === 'completed') return task.status === 'Completed'
    if (status.value === 'stopped') return task.status === 'Interrupted'
    return task.status === 'Failed'
  })
})

const workspaceGroups = computed<WorkspaceGroup[]>(() => {
  const groups = new Map<string, WorkspaceGroup>()
  const query = search.value.trim().toLocaleLowerCase('zh-CN')
  for (const workspace of props.workspaces) {
    const path = normalizedWorkspacePath(workspace.workingDirectory)
    const key = path.toLocaleLowerCase('en-US')
    groups.set(key, {
      key,
      collapseKey: workspace.id,
      workspaceId: workspace.id,
      name: workspace.name || workspaceName(path),
      path,
      tasks: [],
      updatedAt: workspace.updatedAt,
      taskCount: workspace.taskCount,
      hasActiveTask: workspace.hasActiveTask,
      matchesSearch: !query || [workspace.name, path]
        .some(value => value.toLocaleLowerCase('zh-CN').includes(query)),
      generalChat: false,
      iconKey: workspace.iconKey ?? 'folder',
      colorKey: workspace.colorKey ?? 'blue',
    })
  }

  for (const task of visibleTasks.value) {
    const generalChat = task.scopeKind === 'GeneralChat'
    if (!generalChat && task.workspaceId &&
      !props.workspaces.some(workspace => workspace.id === task.workspaceId)) {
      continue
    }
    const path = generalChat ? t('由 Pi Companion 管理的隔离空间') : normalizedWorkspacePath(task.workingDirectory)
    const key = generalChat ? 'general-chat' : path.toLocaleLowerCase('en-US')
    const group = groups.get(key)
    if (group) {
      group.tasks.push(task)
      if (!group.workspaceId) group.taskCount = group.tasks.length
      if (new Date(task.updatedAt).getTime() > new Date(group.updatedAt).getTime()) group.updatedAt = task.updatedAt
      if (activeTaskStatuses.includes(task.status)) group.hasActiveTask = true
      group.matchesSearch = true
      continue
    }
    groups.set(key, {
      key,
      collapseKey: generalChat ? 'general-chat' : task.workspaceId ?? key,
      workspaceId: null,
      name: generalChat ? t('直接对话') : workspaceName(path),
      path,
      tasks: [task],
      updatedAt: task.updatedAt,
      taskCount: 1,
      hasActiveTask: activeTaskStatuses.includes(task.status),
      matchesSearch: true,
      generalChat,
      iconKey: generalChat ? 'app' : 'folder',
      colorKey: generalChat ? 'indigo' : 'blue',
    })
  }
  return Array.from(groups.values())
    .filter(group => group.tasks.length > 0 ||
      (status.value === 'all' && group.matchesSearch))
    .map(group => ({
      ...group,
      tasks: group.tasks.sort((left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime()),
    }))
    .sort((left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime())
})

const workspaceColumns = computed(() => {
  const columns = Array.from({ length: workspaceColumnCount.value }, () => [] as WorkspaceGroup[])
  const estimatedHeights = Array.from({ length: workspaceColumnCount.value }, () => 0)
  for (const workspace of workspaceGroups.value) {
    let targetColumn = 0
    for (let index = 1; index < estimatedHeights.length; index += 1) {
      if (estimatedHeights[index] < estimatedHeights[targetColumn]) targetColumn = index
    }
    columns[targetColumn].push(workspace)
    estimatedHeights[targetColumn] += 1 + visibleWorkspaceTasks(workspace).length
  }
  return columns
})

const emptyText = computed(() => {
  if (search.value.trim()) return t('没有匹配的任务')
  return t('暂无任务')
})

function normalizedWorkspacePath(path: string) {
  const normalized = path.trim().replace(/\//g, '\\')
  if (/^[a-z]:\\$/i.test(normalized)) return normalized
  return normalized.replace(/\\+$/, '') || path
}

function workspaceName(path: string) {
  const segments = path.split(/[\\/]/).filter(Boolean)
  return segments.at(-1) ?? path
}

function loadCollapsedWorkspaceKeys() {
  if (typeof window === 'undefined') return new Set<string>()
  try {
    const stored = JSON.parse(window.localStorage.getItem(collapsedWorkspacesStorageKey) ?? '[]')
    if (!Array.isArray(stored)) return new Set<string>()
    return new Set(stored.filter((key): key is string => typeof key === 'string'))
  }
  catch {
    return new Set<string>()
  }
}

function saveCollapsedWorkspaceKeys(keys: Set<string>) {
  window.localStorage.setItem(collapsedWorkspacesStorageKey, JSON.stringify([...keys]))
}

function workspaceForTask(task: TaskHistoryEntry) {
  if (task.workspaceId) {
    const byId = props.workspaces.find(workspace => workspace.id === task.workspaceId)
    if (byId) return byId
  }

  if (task.scopeKind === 'GeneralChat') return null
  const path = normalizedWorkspacePath(task.workingDirectory).toLocaleLowerCase('en-US')
  return props.workspaces.find(workspace =>
    normalizedWorkspacePath(workspace.workingDirectory).toLocaleLowerCase('en-US') === path)
}

function isWorkspaceExpanded(key: string) {
  return Boolean(search.value.trim()) || !collapsedWorkspaceKeys.value.has(key)
}

function initialWorkspaceTaskLimit(workspace: WorkspaceGroup) {
  return workspace.generalChat ? generalChatInitialTaskLimit : workspaceInitialTaskLimit
}

function workspaceTaskLimit(workspace: WorkspaceGroup) {
  return workspaceTaskLimits.value.get(workspace.key) ?? initialWorkspaceTaskLimit(workspace)
}

function visibleWorkspaceTasks(workspace: WorkspaceGroup) {
  return workspace.tasks.slice(0, workspaceTaskLimit(workspace))
}

function hasMoreWorkspaceTasks(workspace: WorkspaceGroup) {
  return workspace.tasks.length > workspaceTaskLimit(workspace)
}

function showAllWorkspaceTasks(workspace: WorkspaceGroup) {
  const nextLimits = new Map(workspaceTaskLimits.value)
  nextLimits.set(workspace.key, workspace.tasks.length)
  workspaceTaskLimits.value = nextLimits
}

function toggleWorkspace(key: string) {
  if (search.value.trim()) return
  const nextKeys = new Set(collapsedWorkspaceKeys.value)
  if (nextKeys.has(key)) nextKeys.delete(key)
  else nextKeys.add(key)
  collapsedWorkspaceKeys.value = nextKeys
  saveCollapsedWorkspaceKeys(nextKeys)
}

function updateWorkspaceColumnCount(width: number) {
  workspaceColumnCount.value = width > 980 ? 2 : 1
}

onMounted(() => {
  const root = managementRoot.value
  if (!root) return
  updateWorkspaceColumnCount(root.getBoundingClientRect().width)
  document.addEventListener('click', closeWorkspaceMenus)
  document.addEventListener('keydown', closeWorkspaceMenusOnEscape)
  if (typeof ResizeObserver === 'undefined') return
  workspaceResizeObserver = new ResizeObserver(([entry]) => {
    if (entry) updateWorkspaceColumnCount(entry.contentRect.width)
  })
  workspaceResizeObserver.observe(root)
})

onUnmounted(() => {
  workspaceResizeObserver?.disconnect()
  document.removeEventListener('click', closeWorkspaceMenus)
  document.removeEventListener('keydown', closeWorkspaceMenusOnEscape)
})

function formatDate(task: TaskHistoryEntry) {
  const timestamp = task.deletedAt ?? task.updatedAt
  return formatRelativeTimestamp(timestamp, locale.value, relativeTimeNow.value, t('刚刚'))
}

function fullTaskTime(task: TaskHistoryEntry) {
  return formatFullTimestamp(task.deletedAt ?? task.updatedAt, locale.value)
}

function emitMenuFromButton(event: MouseEvent, task: TaskHistoryEntry) {
  const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect()
  const positionedEvent = { clientX: bounds.right, clientY: bounds.bottom } as MouseEvent
  emit('openContextMenu', positionedEvent, task, false)
}

function closeWorkspaceMenu(event: Event) {
  (event.currentTarget as HTMLElement).closest('details')?.removeAttribute('open')
}

function closeWorkspaceMenus(event: Event) {
  if ((event.target as HTMLElement | null)?.closest('.management-workspace-more')) return
  managementRoot.value
    ?.querySelectorAll<HTMLDetailsElement>('.management-workspace-more[open]')
    .forEach(menu => menu.removeAttribute('open'))
}

function closeWorkspaceMenusOnEscape(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  managementRoot.value
    ?.querySelectorAll<HTMLDetailsElement>('.management-workspace-more[open]')
    .forEach(menu => menu.removeAttribute('open'))
}
</script>

<template>
  <main ref="managementRoot" class="management-main management-history">
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
          <strong>{{ t('全部任务') }}</strong>
          <span>{{ t('按工作区排列 · {workspaceCount} 个工作区 · {taskCount} 项任务', {
            workspaceCount: workspaceGroups.length,
            taskCount: visibleTasks.length,
          }) }}</span>
        </div>
      </div>
    </header>

    <section class="management-content">
      <div class="management-controls">
        <label class="management-search">
          <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" /><path d="m13 13 4 4" /></svg>
          <UiInput v-model="search" type="search" :placeholder="t('搜索名称、目录或摘要')" :aria-label="t('搜索任务')" />
        </label>
        <UiSelect v-model="status" :ariaLabelText="t('按最新进度状态筛选')" :options="statusOptions" />
        <UiButton class="management-add-workspace" type="button" @click="$emit('createWorkspace')">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M3 6h5l1.5 2H17v8H3z" /><path d="M13 2.5v5M10.5 5h5" /></svg>
          {{ t('添加工作区') }}
        </UiButton>
      </div>

      <div v-if="workspaceGroups.length" class="management-workspace-grid">
        <div
          v-for="(column, columnIndex) in workspaceColumns"
          :key="columnIndex"
          class="management-workspace-column"
        >
          <section
            v-for="workspace in column"
            :key="workspace.key"
            class="management-workspace"
          >
            <div class="management-workspace-header">
              <UiButton
                class="management-workspace-toggle"
                type="button"
                :aria-expanded="isWorkspaceExpanded(workspace.collapseKey)"
                :aria-label="t(isWorkspaceExpanded(workspace.collapseKey) ? '收起工作区' : '展开工作区')"
                @click="toggleWorkspace(workspace.collapseKey)"
              >
                <WorkspaceIcon :icon-key="workspace.iconKey" :color-key="workspace.colorKey" />
                <span class="management-workspace-copy">
                  <strong>{{ workspace.name }}</strong>
                  <small :title="workspace.path">{{ workspace.path }}</small>
                </span>
                <span class="management-workspace-meta">
                  <span>
                    <span v-if="workspace.hasActiveTask" class="history-status running"></span>
                    <b>{{ t('{count} 个任务', {
                      count: search.trim() || status !== 'all' ? workspace.tasks.length : workspace.taskCount,
                    }) }}</b>
                  </span>
                </span>
                <svg class="management-workspace-chevron" :class="{ collapsed: !isWorkspaceExpanded(workspace.collapseKey) }" viewBox="0 0 20 20" aria-hidden="true"><path d="m6 8 4 4 4-4" /></svg>
              </UiButton>
              <details
                v-if="workspace.workspaceId && !workspace.generalChat"
                class="management-workspace-more"
                name="workspace-actions"
              >
                <summary
                  :aria-label="t('{name} 的更多操作', { name: workspace.name })"
                  :title="t('更多')"
                >•••</summary>
                <div class="management-workspace-menu" role="menu">
                  <UiButton
                    type="button"
                    role="menuitem"
                    @click="$emit('manageWorkspaceSkills', workspace.workspaceId); closeWorkspaceMenu($event)"
                  >{{ t('查看工作区技能') }}</UiButton>
                  <UiButton
                    type="button"
                    role="menuitem"
                    @click="$emit('editWorkspace', workspace.workspaceId); closeWorkspaceMenu($event)"
                  >{{ t('编辑工作区') }}</UiButton>
                  <UiButton
                    type="button"
                    role="menuitem"
                    :disabled="workspace.hasActiveTask"
                    :title="workspace.hasActiveTask ? t('工作区仍有运行中的任务，请先停止任务再隐藏。') : undefined"
                    @click="$emit('hideWorkspace', workspace.workspaceId); closeWorkspaceMenu($event)"
                  >{{ t('隐藏工作区') }}</UiButton>
                </div>
              </details>
              <UiButton
                v-if="workspace.workspaceId && !workspace.generalChat"
                class="management-workspace-new-task"
                type="button"
                :aria-label="t('在 {name} 中新建任务', { name: workspace.name })"
                :title="t('在此工作区新建任务')"
                @click="$emit('newTaskInWorkspace', workspace.workspaceId)"
              >
                <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
              </UiButton>
            </div>

            <div v-show="isWorkspaceExpanded(workspace.collapseKey)" class="management-workspace-tasks">
              <p v-if="workspace.tasks.length === 0 && workspace.taskCount === 0" class="management-workspace-empty">
                {{ t('暂无任务，点击 + 创建首个任务') }}
              </p>
              <p v-else-if="workspace.tasks.length === 0" class="management-workspace-empty">
                {{ t('该工作区的任务尚未加载') }}
              </p>
              <article
                v-for="task in visibleWorkspaceTasks(workspace)"
                :key="task.id"
                class="management-task"
                @contextmenu.prevent.stop="$emit('openContextMenu', $event, task, false)"
              >
                <UiButton
                  class="management-task-body"
                  type="button"
                  @click="$emit('selectTask', task.id)"
                >
                  <span class="management-task-copy">
                    <span class="management-task-primary-row">
                      <strong>{{ task.title }}</strong>
                      <time :datetime="task.deletedAt ?? task.updatedAt" :title="fullTaskTime(task)">{{ formatDate(task) }}</time>
                    </span>
                    <span class="management-task-secondary-row">
                      <span class="management-task-progress">
                        <span
                          class="history-status"
                          :class="taskStatusTone(task.status)"
                          :aria-label="t(task.statusText)"
                          :title="t(task.statusText)"
                        ></span>
                        <span class="management-task-progress-label">{{ t('最新进度：') }}</span>
                        <small v-if="task.summary" :title="task.summary">{{ task.summary }}</small>
                        <span v-else class="management-task-progress-status">{{ t(task.statusText) }}</span>
                      </span>
                    </span>
                  </span>
                </UiButton>
                <div class="management-task-actions">
                  <UiButton
                    class="task-more-button"
                    type="button"
                    :aria-label="t('任务操作')"
                    @click.stop="emitMenuFromButton($event, task)"
                  >•••</UiButton>
                </div>
              </article>
              <UiButton
                v-if="hasMoreWorkspaceTasks(workspace)"
                class="management-workspace-show-all"
                type="button"
                :aria-label="t('显示 {name} 中的全部任务', { name: workspace.name })"
                @click="showAllWorkspaceTasks(workspace)"
              >
                {{ t('显示全部任务') }}
              </UiButton>
            </div>
          </section>
        </div>
      </div>
      <div v-else class="management-empty">
        <span>⌕</span>
        <h2>{{ emptyText }}</h2>
        <p>{{ t('尝试修改搜索词或状态筛选。') }}</p>
      </div>
    </section>
  </main>
</template>
