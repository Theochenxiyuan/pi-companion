export const activeTaskStatuses = ['Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling']

export function taskStatusTone(status: string) {
  if (status === 'WaitingForApproval') return 'waiting'
  if (status === 'WaitingForAnswer') return 'running'
  if (status === 'Completed') return 'success'
  if (['Failed', 'Interrupted'].includes(status)) return 'danger'
  if (activeTaskStatuses.includes(status)) return 'running'
  return 'idle'
}
