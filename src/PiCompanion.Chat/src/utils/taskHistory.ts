import type { TaskHistoryEntry } from '@/types/bridge'

function activityTime(task: TaskHistoryEntry) {
  const timestamp = Date.parse(task.updatedAt)
  return Number.isFinite(timestamp) ? timestamp : 0
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
