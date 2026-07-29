import { describe, expect, it } from 'vitest'
import type { TaskHistoryEntry } from '@/types/bridge'
import { sortTasksByActivity, upsertTaskByActivity } from './taskHistory'

function task(id: string, updatedAt: string): TaskHistoryEntry {
  return {
    id,
    runId: `${id}-run`,
    title: id,
    workingDirectory: 'D:\\work',
    status: 'Completed',
    statusText: '已完成',
    summary: '',
    updatedAt,
    deletedAt: null,
  }
}

describe('recent task activity ordering', () => {
  it('sorts by latest activity and uses task id as a stable tie breaker', () => {
    const sameTime = new Date(1000).toISOString()
    const tasks = [task('b', sameTime), task('older', new Date(0).toISOString()), task('a', sameTime)]

    expect(sortTasksByActivity(tasks).map((entry) => entry.id)).toEqual(['a', 'b', 'older'])
    expect(tasks.map((entry) => entry.id)).toEqual(['b', 'older', 'a'])
  })

  it('does not move a selected task until its activity timestamp changes', () => {
    const selected = task('selected', new Date(0).toISOString())
    const newest = task('newest', new Date(2000).toISOString())

    expect(upsertTaskByActivity([newest, selected], { ...selected, title: '已选中' }).map((entry) => entry.id))
      .toEqual(['newest', 'selected'])

    const active = { ...selected, updatedAt: new Date(3000).toISOString() }
    expect(upsertTaskByActivity([newest, selected], active).map((entry) => entry.id))
      .toEqual(['selected', 'newest'])
  })
})
