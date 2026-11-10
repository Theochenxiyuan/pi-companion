import type {
  TaskHistoryEntry,
  WorkspaceColorKey,
  WorkspaceHistoryEntry,
  WorkspaceIconKey,
} from '@/types/bridge'
import { activeTaskStatuses } from '@/utils/taskStatus'

export type SidebarTaskStatusFilter = 'all' | 'active' | 'completed' | 'stopped' | 'failed'

export interface SidebarWorkspaceGroup {
  key: string
  collapseKey: string
  workspaceId: string | null
  name: string
  workingDirectory: string
  generalChat: boolean
  iconKey: WorkspaceIconKey
  colorKey: WorkspaceColorKey
  hasActiveTask: boolean
  updatedAt: string
  tasks: TaskHistoryEntry[]
}

export function matchesSidebarTaskStatus(
  task: TaskHistoryEntry,
  statusFilter: SidebarTaskStatusFilter = 'all',
) {
  if (statusFilter === 'all') return true
  if (statusFilter === 'active') return activeTaskStatuses.includes(task.status)
  if (statusFilter === 'completed') return task.status === 'Completed'
  if (statusFilter === 'stopped') return task.status === 'Interrupted'
  return task.status === 'Failed'
}

function activityTime(task: TaskHistoryEntry) {
  const timestamp = Date.parse(task.updatedAt)
  return Number.isFinite(timestamp) ? timestamp : 0
}

export function normalizeWorkspacePath(path: string) {
  const normalized = path.trim().replace(/\//gu, '\\')
  if (/^[a-z]:\\$/iu.test(normalized)) return normalized.toLocaleLowerCase('en-US')
  return normalized.replace(/\\+$/gu, '').toLocaleLowerCase('en-US')
}

function displayWorkspacePath(path: string) {
  const normalized = path.trim().replace(/\//gu, '\\')
  if (/^[a-z]:\\$/iu.test(normalized)) return normalized
  return normalized.replace(/\\+$/gu, '') || path
}

function fallbackWorkspaceName(path: string) {
  const segments = path.split(/[\\/]/u).filter(Boolean)
  return segments.at(-1) ?? path
}

export function compareTaskActivity(left: TaskHistoryEntry, right: TaskHistoryEntry) {
  const timeDifference = activityTime(right) - activityTime(left)
  return timeDifference || left.id.localeCompare(right.id)
}

export function sortTasksByActivity(tasks: TaskHistoryEntry[]) {
  return [...tasks].sort(compareTaskActivity)
}

export function upsertTaskByActivity(
  tasks: TaskHistoryEntry[],
  task: TaskHistoryEntry,
  limit?: number,
) {
  const updated = tasks.filter((candidate) => candidate.id !== task.id)
  updated.push(task)
  const sorted = sortTasksByActivity(updated)
  return limit === undefined ? sorted : sorted.slice(0, limit)
}

export function buildSidebarWorkspaceGroups(options: {
  recentTasks: TaskHistoryEntry[]
  historyTasks?: TaskHistoryEntry[]
  selectedHistoryTask?: TaskHistoryEntry | null
  workspaces?: WorkspaceHistoryEntry[]
  statusFilter?: SidebarTaskStatusFilter
  directChatLabel: string
  directChatPath: string
}): SidebarWorkspaceGroup[] {
  const merged = new Map<string, TaskHistoryEntry>()
  for (const task of options.historyTasks ?? []) {
    merged.set(task.id, task)
  }
  for (const task of options.recentTasks) {
    merged.set(task.id, task)
  }
  if (options.selectedHistoryTask) {
    merged.set(options.selectedHistoryTask.id, options.selectedHistoryTask)
  }

  const statusFilter = options.statusFilter ?? 'all'
  const sortedTasks = sortTasksByActivity([...merged.values()])
    .filter((task) => matchesSidebarTaskStatus(task, statusFilter))
  const workspaces = options.workspaces ?? []
  const groups = new Map<string, SidebarWorkspaceGroup>()

  for (const task of sortedTasks) {
    const generalChat = task.scopeKind === 'GeneralChat'
    const matchedWorkspace = generalChat
      ? null
      : (task.workspaceId
          ? workspaces.find((workspace) => workspace.id === task.workspaceId)
          : undefined)
        ?? workspaces.find((workspace) =>
          normalizeWorkspacePath(workspace.workingDirectory) === normalizeWorkspacePath(task.workingDirectory))
        ?? null

    const normalizedPath = generalChat ? '' : normalizeWorkspacePath(task.workingDirectory)
    const key = generalChat
      ? 'general-chat'
      : matchedWorkspace
        ? `workspace:${matchedWorkspace.id}`
        : `path:${normalizedPath}`

    const existing = groups.get(key)
    if (existing) {
      existing.tasks.push(task)
      if (activeTaskStatuses.includes(task.status)) {
        existing.hasActiveTask = true
      }
      continue
    }

    const displayPath = generalChat
      ? options.directChatPath
      : displayWorkspacePath(matchedWorkspace?.workingDirectory ?? task.workingDirectory)

    groups.set(key, {
      key,
      collapseKey: generalChat ? 'general-chat' : matchedWorkspace?.id ?? key,
      workspaceId: matchedWorkspace?.id ?? null,
      name: generalChat
        ? options.directChatLabel
        : matchedWorkspace?.name || fallbackWorkspaceName(displayPath),
      workingDirectory: displayPath,
      generalChat,
      iconKey: generalChat ? 'app' : matchedWorkspace?.iconKey ?? 'folder',
      colorKey: generalChat ? 'indigo' : matchedWorkspace?.colorKey ?? 'blue',
      hasActiveTask: activeTaskStatuses.includes(task.status),
      updatedAt: task.updatedAt,
      tasks: [task],
    })
  }

  return [...groups.values()]
}
