import { describe, expect, it } from 'vitest'
import { taskStatusTone } from './taskStatus'

describe('task status tone', () => {
  it('distinguishes approval warnings from in-progress questions', () => {
    expect(taskStatusTone('WaitingForApproval')).toBe('waiting')
    expect(taskStatusTone('WaitingForAnswer')).toBe('running')
  })
})
