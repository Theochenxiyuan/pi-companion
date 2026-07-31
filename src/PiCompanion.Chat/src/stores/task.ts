import { defineStore } from 'pinia'
import { postBridgeMessage } from '@/bridge'
import type {
  AppendEvents,
  BridgeEnvelope,
  ComposerDraft,
  InitializeSnapshot,
  RunEvent,
  TaskCollections,
  FileDiffEvidence,
  RecoveryCompleted,
  RunEvidence,
  TaskHistoryEntry,
  TaskDelta,
  TaskSnapshot,
  TranscriptBlock,
  WorkspaceHistoryEntry,
  WorkspaceGitCommitDiff,
} from '@/types/bridge'
import { bridgeProtocolVersion } from '@/types/bridge'
import { sortTasksByActivity, upsertTaskByActivity } from '@/utils/taskHistory'
import { t } from '@/i18n'

interface TaskState {
  connected: boolean
  currentTask: TaskSnapshot | null
  workspaces: WorkspaceHistoryEntry[]
  recentTasks: TaskHistoryEntry[]
  historyTasks: TaskHistoryEntry[]
  recycleBinTasks: TaskHistoryEntry[]
  draft: ComposerDraft | null
  capabilities: string[]
  events: RunEvent[]
  bridgeError: string | null
  fileDiff: FileDiffEvidence | null
  commitDiff: WorkspaceGitCommitDiff | null
  recoveryNotice: RecoveryCompleted | null
}

const activityLimit = 40
const eventDebugLimit = 100
const transcriptIndexes = new WeakMap<TranscriptBlock[], Map<string, number>>()

function getTranscriptIndex(blocks: TranscriptBlock[]) {
  let index = transcriptIndexes.get(blocks)
  if (index) return index

  index = new Map(blocks.map((block, blockIndex) => [block.id, blockIndex]))
  transcriptIndexes.set(blocks, index)
  return index
}

function upsertTranscriptBlocks(blocks: TranscriptBlock[], upserts: TranscriptBlock[]) {
  const index = getTranscriptIndex(blocks)
  for (const block of upserts) {
    const existingIndex = index.get(block.id)
    if (existingIndex === undefined) {
      index.set(block.id, blocks.length)
      blocks.push(block)
    } else {
      blocks.splice(existingIndex, 1, block)
    }
  }
}

function normalizeWorkspacePath(path: string) {
  return path.trim().replace(/\//gu, '\\').replace(/\\+$/gu, '').toLocaleLowerCase('en-US')
}

export function applyIncrementalTaskDelta(task: TaskSnapshot, delta: TaskDelta) {
  if (task.id !== delta.id || task.runId !== delta.runId) return false

  const run = task.runs.find((candidate) => candidate.id === delta.runId)
  const transcript = run?.transcript ?? task.transcript
  const activities = run?.activities ?? task.activities
  upsertTranscriptBlocks(transcript, delta.transcriptUpserts)

  for (const activity of delta.activityUpserts) {
    const index = activities.findIndex((candidate) => candidate.sequence === activity.sequence)
    if (index >= 0) activities.splice(index, 1, activity)
    else activities.push(activity)
  }
  if (activities.length > activityLimit) activities.splice(0, activities.length - activityLimit)

  Object.assign(task, {
    status: delta.status,
    statusText: delta.statusText,
    summary: delta.summary,
    aiSummaryStatus: delta.aiSummaryStatus,
    activityStatus: delta.activityStatus,
    assistantText: delta.assistantText,
    finalAnswer: delta.finalAnswer,
    lastSequence: delta.lastSequence,
    pendingSteering: delta.pendingSteering,
    pendingFollowUps: delta.pendingFollowUps,
    transcript,
    activities,
  })
  if (run) {
    Object.assign(run, {
      status: delta.status,
      statusText: delta.statusText,
      summary: delta.summary,
      aiSummaryStatus: delta.aiSummaryStatus,
      activityStatus: delta.activityStatus,
      assistantText: delta.assistantText,
      finalAnswer: delta.finalAnswer,
      lastSequence: delta.lastSequence,
      pendingSteering: delta.pendingSteering,
      pendingFollowUps: delta.pendingFollowUps,
      transcript,
      activities,
    })
  }

  return true
}

export const useTaskStore = defineStore('task', {
  state: (): TaskState => ({
    connected: false,
    currentTask: null,
    workspaces: [],
    recentTasks: [],
    historyTasks: [],
    recycleBinTasks: [],
    draft: null,
    capabilities: [],
    events: [],
    bridgeError: null,
    fileDiff: null,
    commitDiff: null,
    recoveryNotice: null,
  }),
  getters: {
    isActive: (state) =>
      ['Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling'].includes(
        state.currentTask?.status ?? '',
      ),
    needsInteraction: (state) =>
      ['WaitingForApproval', 'WaitingForAnswer'].includes(state.currentTask?.status ?? ''),
  },
  actions: {
    consume(message: BridgeEnvelope) {
      if (message.protocolVersion !== bridgeProtocolVersion) {
        this.bridgeError = t('Bridge 协议不兼容：{version}', { version: message.protocolVersion })
        return
      }

      switch (message.type) {
        case 'InitializeSnapshot': {
          const snapshot = message.payload as InitializeSnapshot
          this.connected = true
          this.currentTask = snapshot.currentTask
          this.workspaces = snapshot.workspaces ?? []
          this.recentTasks = sortTasksByActivity(snapshot.recentTasks)
          this.historyTasks = sortTasksByActivity(snapshot.historyTasks)
          this.recycleBinTasks = snapshot.recycleBinTasks
          this.draft = snapshot.draft
          this.capabilities = snapshot.capabilities
          this.events = []
          this.bridgeError = null
          this.fileDiff = null
          this.commitDiff = null
          this.recoveryNotice = null
          break
        }
        case 'TaskUpdated': {
          const task = message.payload as TaskSnapshot
          this.currentTask = task
          const existingIndex = this.recentTasks.findIndex((candidate) => candidate.id === task.id)
          const existing = existingIndex >= 0 ? this.recentTasks[existingIndex] : null
          const historyEntry: TaskHistoryEntry = {
            id: task.id,
            runId: task.runId,
            title: task.title,
            workingDirectory: task.workingDirectory,
            scopeKind: task.scopeKind,
            status: task.status,
            statusText: task.statusText,
            summary: task.summary,
            aiSummaryStatus: task.aiSummaryStatus,
            updatedAt: existing?.updatedAt ?? new Date().toISOString(),
            deletedAt: null,
            workspaceId: task.scopeKind === 'Workspace'
              ? existing?.workspaceId ?? this.workspaces.find(workspace =>
                normalizeWorkspacePath(workspace.workingDirectory) ===
                normalizeWorkspacePath(task.workingDirectory))?.id ?? null
              : null,
          }
          this.recentTasks = upsertTaskByActivity(this.recentTasks, historyEntry, 20)
          this.historyTasks = upsertTaskByActivity(this.historyTasks, historyEntry)
          break
        }
        case 'TaskCollectionsUpdated': {
          const collections = message.payload as TaskCollections
          this.workspaces = collections.workspaces ?? []
          this.recentTasks = sortTasksByActivity(collections.recentTasks)
          this.historyTasks = sortTasksByActivity(collections.historyTasks)
          this.recycleBinTasks = collections.recycleBinTasks
          break
        }
        case 'AppendEvents': {
          const update = message.payload as AppendEvents
          if (!this.currentTask ||
            this.currentTask.id !== update.task.id ||
            this.currentTask.runId !== update.task.runId) {
            postBridgeMessage('BridgeReady', { reason: 'task-mismatch', lastSequence: this.currentTask?.lastSequence ?? 0 })
            return
          }

          let expectedSequence = this.currentTask.lastSequence
          const accepted: RunEvent[] = []
          for (const event of update.events) {
            if (this.events.some((candidate) => candidate.eventId === event.eventId)) continue
            if (event.sequence <= expectedSequence) continue
            if (event.sequence !== expectedSequence + 1) {
              postBridgeMessage('BridgeReady', { reason: 'sequence-gap', lastSequence: expectedSequence })
              return
            }
            expectedSequence = event.sequence
            accepted.push(event)
          }

          if (accepted.length === 0) break
          if (update.task.lastSequence !== expectedSequence ||
            !applyIncrementalTaskDelta(this.currentTask, update.task)) {
            postBridgeMessage('BridgeReady', { reason: 'delta-mismatch', lastSequence: this.currentTask.lastSequence })
            return
          }

          this.events.push(...accepted)
          this.events = this.events.slice(-eventDebugLimit)
          const historyEntry: TaskHistoryEntry = {
            id: this.currentTask.id,
            runId: this.currentTask.runId,
            title: this.currentTask.title,
            workingDirectory: this.currentTask.workingDirectory,
            scopeKind: this.currentTask.scopeKind,
            status: update.task.status,
            statusText: update.task.statusText,
            summary: update.task.summary,
            aiSummaryStatus: update.task.aiSummaryStatus,
            updatedAt: update.task.updatedAt,
            deletedAt: null,
            workspaceId: this.recentTasks.find(candidate =>
              candidate.id === this.currentTask?.id)?.workspaceId ??
              this.workspaces.find(workspace =>
                normalizeWorkspacePath(workspace.workingDirectory) ===
                normalizeWorkspacePath(this.currentTask?.workingDirectory ?? ''))?.id ??
              null,
          }
          this.recentTasks = upsertTaskByActivity(this.recentTasks, historyEntry, 20)
          this.historyTasks = upsertTaskByActivity(this.historyTasks, historyEntry)
          break
        }
        case 'DraftLoaded':
          this.draft = message.payload as ComposerDraft
          this.bridgeError = null
          break
        case 'EvidenceUpdated': {
          const evidence = message.payload as RunEvidence
          const run = this.currentTask?.runs.find((candidate) => candidate.id === evidence.runId)
          if (run) run.evidence = evidence
          break
        }
        case 'FileDiffLoaded':
        case 'WorkspaceGitDiffLoaded':
          this.fileDiff = message.payload as FileDiffEvidence
          break
        case 'WorkspaceGitCommitDiffLoaded':
          this.commitDiff = message.payload as WorkspaceGitCommitDiff
          break
        case 'RecoveryCompleted':
          this.recoveryNotice = message.payload as RecoveryCompleted
          break
        case 'BridgeError':
          this.bridgeError = t((message.payload as { message: string }).message)
          break
      }
    },
    clearDraft() {
      this.draft = null
    },
    clearFileDiff() {
      this.fileDiff = null
    },
    clearCommitDiff() {
      this.commitDiff = null
    },
    clearRecoveryNotice() {
      this.recoveryNotice = null
    },
  },
})
