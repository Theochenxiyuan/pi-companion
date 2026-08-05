import { describe, expect, it } from 'vitest'
import type { TaskSnapshot, TaskTemplate } from '@/types/bridge'
import {
  createBuiltInTaskTemplates,
  taskTemplateMatchesTask,
  taskTemplateTargetLabel,
} from './taskTemplates'

const translate = (source: string) => source

function template(overrides: Partial<TaskTemplate> = {}): TaskTemplate {
  return {
    id: 'template-1',
    name: '检查工程',
    prompt: '检查工程并给出摘要',
    targetKind: 'CurrentContext',
    workspaceId: null,
    model: null,
    thinkingLevel: null,
    permissionMode: null,
    isPinned: false,
    createdAt: new Date(0).toISOString(),
    updatedAt: new Date(0).toISOString(),
    ...overrides,
  }
}

function task(scopeKind: 'Workspace' | 'GeneralChat'): TaskSnapshot {
  return {
    id: 'task-1',
    runId: 'run-1',
    title: '任务',
    prompt: '任务',
    workingDirectory: scopeKind === 'Workspace' ? 'D:\\work' : '',
    scopeKind,
    model: 'model-1',
    thinkingLevel: 'high',
    attachments: [],
    status: 'Completed',
    statusText: '已完成',
    summary: '',
    assistantText: null,
    finalAnswer: null,
    lastSequence: 0,
    pendingSteering: [],
    pendingFollowUps: [],
    transcript: [],
    runs: [],
    activities: [],
  }
}

describe('task template resolution', () => {
  it('keeps the built-ins as read-only reusable drafts', () => {
    const templates = createBuiltInTaskTemplates(translate)

    expect(templates).toHaveLength(3)
    expect(templates.every(candidate => candidate.isBuiltIn)).toBe(true)
    expect(templates.every(candidate => candidate.permissionMode === 'read-only')).toBe(true)
    expect(templates.every(candidate => candidate.targetKind === 'Workspace')).toBe(true)
  })

  it('matches only compatible task targets', () => {
    expect(taskTemplateMatchesTask(template(), task('GeneralChat'), null)).toBe(true)
    expect(taskTemplateMatchesTask(
      template({ targetKind: 'GeneralChat' }),
      task('Workspace'),
      'workspace-1',
    )).toBe(false)
    expect(taskTemplateMatchesTask(
      template({ targetKind: 'Workspace', workspaceId: 'workspace-1' }),
      task('Workspace'),
      'workspace-1',
    )).toBe(true)
    expect(taskTemplateMatchesTask(
      template({ targetKind: 'Workspace', workspaceId: 'workspace-2' }),
      task('Workspace'),
      'workspace-1',
    )).toBe(false)
  })

  it('labels unavailable bound workspaces without silently retargeting them', () => {
    const candidate = template({ targetKind: 'Workspace', workspaceId: 'missing' })
    expect(taskTemplateTargetLabel(candidate, [], translate)).toBe('工作区不可用')
  })
})
