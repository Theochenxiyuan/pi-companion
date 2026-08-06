export const activeTaskStatuses = ['Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling']
const historicalSessionStatuses = ['Completed', 'Failed', 'Interrupted']

export type SessionStatisticsAutoLoadMode = 'live' | 'historical'

export function sessionStatisticsAutoLoadMode(status: string): SessionStatisticsAutoLoadMode | null {
  if (activeTaskStatuses.includes(status)) return 'live'
  if (historicalSessionStatuses.includes(status)) return 'historical'
  return null
}

export function taskStatusTone(status: string) {
  if (status === 'WaitingForApproval') return 'waiting'
  if (status === 'WaitingForAnswer') return 'running'
  if (status === 'Completed') return 'success'
  if (['Failed', 'Interrupted'].includes(status)) return 'danger'
  if (activeTaskStatuses.includes(status)) return 'running'
  return 'idle'
}
