import { describe, expect, it } from 'vitest'
import { sessionStatisticsAutoLoadMode, taskStatusTone } from './taskStatus'

describe('task status tone', () => {
  it('distinguishes approval warnings from in-progress questions', () => {
    expect(taskStatusTone('WaitingForApproval')).toBe('waiting')
    expect(taskStatusTone('WaitingForAnswer')).toBe('running')
  })

  it('selects live or historical Session statistics only for readable task states', () => {
    for (const status of ['Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling']) {
      expect(sessionStatisticsAutoLoadMode(status)).toBe('live')
    }
    for (const status of ['Completed', 'Failed', 'Interrupted']) {
      expect(sessionStatisticsAutoLoadMode(status)).toBe('historical')
    }
    for (const status of ['Draft', 'Deleted', '']) {
      expect(sessionStatisticsAutoLoadMode(status)).toBeNull()
    }
  })
})
