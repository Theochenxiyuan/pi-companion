import { describe, expect, it } from 'vitest'
import type { TaskHistoryEntry } from '@/types/bridge'
import {
  buildSidebarWorkspaceGroups,
  sortTasksByActivity,
  upsertTaskByActivity,
} from './taskHistory'

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

  it('groups tasks by workspace and direct chat ordered by latest activity', () => {
    const directChatTask: TaskHistoryEntry = {
      ...task('direct-1', new Date(3000).toISOString()),
      scopeKind: 'GeneralChat',
      workingDirectory: 'C:\\isolated',
    }
    const workspaceATask: TaskHistoryEntry = {
      ...task('ws-a-1', new Date(5000).toISOString()),
      workspaceId: 'ws-a',
      workingDirectory: 'D:\\repo-a',
      status: 'Running',
      statusText: '运行中',
    }
    const workspaceBTask: TaskHistoryEntry = {
      ...task('ws-b-1', new Date(1000).toISOString()),
      workingDirectory: 'D:\\repo-b',
    }

    const groups = buildSidebarWorkspaceGroups({
      recentTasks: [workspaceATask],
      historyTasks: [directChatTask, workspaceBTask],
      workspaces: [{
        id: 'ws-a',
        name: 'Repo A',
        workingDirectory: 'D:\\repo-a',
        createdAt: new Date(0).toISOString(),
        updatedAt: new Date(5000).toISOString(),
        taskCount: 1,
        hasActiveTask: true,
        iconKey: 'code',
        colorKey: 'teal',
      }],
      directChatLabel: '直接对话',
      directChatPath: '由 Pi Companion 管理的隔离空间',
    })

    expect(groups.map(group => ({
      name: group.name,
      hasActiveTask: group.hasActiveTask,
      taskIds: group.tasks.map(entry => entry.id),
    }))).toEqual([
      { name: 'Repo A', hasActiveTask: true, taskIds: ['ws-a-1'] },
      { name: '直接对话', hasActiveTask: false, taskIds: ['direct-1'] },
      { name: 'repo-b', hasActiveTask: false, taskIds: ['ws-b-1'] },
    ])
  })
})
