import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  clearStoredTaskPromptDrafts,
  loadTaskPromptDraft,
  saveTaskPromptDraft,
} from './taskPromptDrafts'

describe('task prompt drafts', () => {
  beforeEach(() => {
    clearStoredTaskPromptDrafts()
  })

  it('normalizes task ids and removes an empty draft', () => {
    saveTaskPromptDraft('  TASK-ID  ', 'unfinished prompt')
    expect(loadTaskPromptDraft('task-id')).toBe('unfinished prompt')

    saveTaskPromptDraft('task-id', '')
    expect(loadTaskPromptDraft('TASK-ID')).toBe('')
  })

  it('ignores malformed or unavailable storage', () => {
    window.localStorage.setItem('pi-companion:task-prompt-drafts:v1', '{invalid')
    expect(loadTaskPromptDraft('task-id')).toBe('')

    const unavailableStorage = {
      getItem: vi.fn(() => { throw new Error('unavailable') }),
      setItem: vi.fn(() => { throw new Error('unavailable') }),
      removeItem: vi.fn(() => { throw new Error('unavailable') }),
      clear: vi.fn(),
      key: vi.fn(),
      length: 0,
    } satisfies Storage
    expect(() => saveTaskPromptDraft('task-id', 'draft', unavailableStorage)).not.toThrow()
    expect(loadTaskPromptDraft('task-id', unavailableStorage)).toBe('')
  })
})
