import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AppendEvents, BridgeEnvelope, InitializeSnapshot, TaskSnapshot, TranscriptBlock } from '@/types/bridge'
import { bridgeProtocolVersion } from '@/types/bridge'
import { setLocale } from '@/i18n'
import { useTaskStore } from './task'

const taskId = '00000000-0000-0000-0000-000000000001'
const runId = '00000000-0000-0000-0000-000000000002'

function initialBlock(): TranscriptBlock {
  return {
    id: 'initial', kind: 'UserMessage', status: 'Completed', title: '你', content: '开始',
    firstSequence: 0, lastSequence: 0, timestamp: new Date(0).toISOString(), input: null, output: null,
    interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
  }
}

function initialTask(): TaskSnapshot {
  const transcript = [initialBlock()]
  return {
    id: taskId, runId, title: '性能验收', prompt: '开始', workingDirectory: 'D:\\work', model: 'Pi',
    thinkingLevel: 'high', attachments: [], status: 'Running', statusText: '运行中', summary: '运行中',
    assistantText: null, finalAnswer: null, lastSequence: 0, pendingSteering: [], pendingFollowUps: [],
    transcript, activities: [], runs: [{
      id: runId, prompt: '开始', model: 'Pi', thinkingLevel: 'high', status: 'Running', statusText: '运行中',
      summary: '运行中', assistantText: null, finalAnswer: null, lastSequence: 0, pendingSteering: [],
      pendingFollowUps: [], messageAttachments: [], transcript, activities: [],
    }],
  }
}

function eventUpdate(sequence: number): BridgeEnvelope<AppendEvents> {
  const timestamp = new Date(sequence).toISOString()
  const block: TranscriptBlock = {
    id: `notice-${sequence}`, kind: 'Notice', status: 'Completed', title: '状态', content: `事件 ${sequence}`,
    firstSequence: sequence, lastSequence: sequence, timestamp, input: null, output: null,
    interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
  }
  return {
    protocolVersion: bridgeProtocolVersion,
    type: 'AppendEvents',
    payload: {
      events: [{
        eventId: `event-${sequence}`, taskId, runId, sequence, kind: 'WarningRaised', status: 'Running',
        timestamp, payload: { activity: block.content },
      }],
      task: {
        id: taskId, runId, status: 'Running', statusText: '运行中', summary: `事件 ${sequence}`,
        assistantText: null, finalAnswer: null, lastSequence: sequence, pendingSteering: [], pendingFollowUps: [],
        updatedAt: timestamp, transcriptUpserts: [block], activityUpserts: [],
      },
    },
  }
}

describe('incremental Bridge acceptance', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => {
    delete window.chrome
    setLocale('zh-CN')
  })

  it('localizes desktop bridge errors in the active UI language', () => {
    setLocale('en-US')
    const store = useTaskStore()

    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'BridgeError',
      payload: { message: '只能修改当前任务的模型设置。' },
    })

    expect(store.bridgeError).toBe("Only the current task's model settings can be changed.")
  })

  it('projects 5000 ordered events without full task snapshots', () => {
    const store = useTaskStore()
    const task = initialTask()
    const snapshot: InitializeSnapshot = {
      currentTask: task,
      lastSequence: 0,
      recentTasks: [{
        id: taskId, runId, title: task.title, workingDirectory: task.workingDirectory, status: task.status,
        statusText: task.statusText, summary: task.summary, updatedAt: new Date(0).toISOString(), deletedAt: null,
      }],
      historyTasks: [], recycleBinTasks: [], draft: null, capabilities: ['incremental-task-delta'],
    }
    store.consume({ protocolVersion: bridgeProtocolVersion, type: 'InitializeSnapshot', payload: snapshot })

    const startedAt = performance.now()
    for (let sequence = 1; sequence <= 5000; sequence += 1) store.consume(eventUpdate(sequence))
    const elapsed = performance.now() - startedAt

    expect(store.currentTask?.lastSequence).toBe(5000)
    expect(store.currentTask?.runs[0].transcript).toHaveLength(5001)
    expect(store.events).toHaveLength(100)
    expect(elapsed).toBeLessThan(4000)
  }, 10000)

  it('requests a fresh snapshot when an event sequence has a gap', () => {
    const postMessage = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const store = useTaskStore()
    const task = initialTask()
    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'InitializeSnapshot',
      payload: {
        currentTask: task, lastSequence: 0, recentTasks: [], historyTasks: [], recycleBinTasks: [],
        draft: null, capabilities: ['incremental-task-delta'],
      } satisfies InitializeSnapshot,
    })

    store.consume(eventUpdate(2))

    expect(store.currentTask?.lastSequence).toBe(0)
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'BridgeReady',
      payload: { reason: 'sequence-gap', lastSequence: 0 },
    }))
  })

  it('keeps selection independent from recent activity ordering and moves tasks only after new activity', () => {
    const store = useTaskStore()
    const task = initialTask()
    const newerTaskId = '00000000-0000-0000-0000-000000000099'
    const olderTimestamp = new Date(-1000).toISOString()
    const newerTimestamp = new Date(0).toISOString()
    const olderEntry = {
      id: taskId, runId, title: task.title, workingDirectory: task.workingDirectory, status: task.status,
      statusText: task.statusText, summary: task.summary, updatedAt: olderTimestamp, deletedAt: null,
    }
    const newerEntry = {
      ...olderEntry,
      id: newerTaskId,
      runId: '00000000-0000-0000-0000-000000000098',
      title: '更新的任务',
      updatedAt: newerTimestamp,
    }

    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'InitializeSnapshot',
      payload: {
        currentTask: task,
        lastSequence: 0,
        recentTasks: [olderEntry, newerEntry],
        historyTasks: [olderEntry, newerEntry],
        recycleBinTasks: [],
        draft: null,
        capabilities: ['incremental-task-delta'],
      } satisfies InitializeSnapshot,
    })
    expect(store.recentTasks.map((entry) => entry.id)).toEqual([newerTaskId, taskId])

    store.consume({ protocolVersion: bridgeProtocolVersion, type: 'TaskUpdated', payload: task })
    expect(store.recentTasks.map((entry) => entry.id)).toEqual([newerTaskId, taskId])

    store.consume(eventUpdate(1))
    expect(store.recentTasks.map((entry) => entry.id)).toEqual([taskId, newerTaskId])
    expect(store.recentTasks[0].updatedAt).toBe(new Date(1).toISOString())
  })
})
