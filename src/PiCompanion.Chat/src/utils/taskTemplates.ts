import type { TaskSnapshot, TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'

type Translate = (source: string, parameters?: Record<string, string | number>) => string

const builtInTimestamp = '1970-01-01T00:00:00.000Z'

export function createBuiltInTaskTemplates(t: Translate): TaskTemplate[] {
  return [
    {
      id: 'builtin:analyze-project',
      name: t('分析工程'),
      prompt: t('检查这个目录的工程结构并总结主要模块'),
      targetKind: 'Workspace',
      workspaceId: null,
      model: null,
      thinkingLevel: null,
      permissionMode: 'read-only',
      isPinned: true,
      createdAt: builtInTimestamp,
      updatedAt: builtInTimestamp,
      isBuiltIn: true,
    },
    {
      id: 'builtin:review-todos',
      name: t('检查 TODO'),
      prompt: t('查找这个工程中可能需要关注的 TODO 并给出摘要'),
      targetKind: 'Workspace',
      workspaceId: null,
      model: null,
      thinkingLevel: null,
      permissionMode: 'read-only',
      isPinned: true,
      createdAt: builtInTimestamp,
      updatedAt: builtInTimestamp,
      isBuiltIn: true,
    },
    {
      id: 'builtin:read-docs',
      name: t('阅读文档'),
      prompt: t('阅读 README 和项目文档，概括当前实现状态'),
      targetKind: 'Workspace',
      workspaceId: null,
      model: null,
      thinkingLevel: null,
      permissionMode: 'read-only',
      isPinned: true,
      createdAt: builtInTimestamp,
      updatedAt: builtInTimestamp,
      isBuiltIn: true,
    },
  ]
}

export function taskTemplateTargetLabel(
  template: TaskTemplate,
  workspaces: WorkspaceHistoryEntry[],
  t: Translate,
) {
  if (template.targetKind === 'CurrentContext') return t('不指定')
  if (template.targetKind === 'GeneralChat') return t('直接对话')
  if (!template.workspaceId) return t('工作区（使用时选择）')
  return workspaces.find(workspace => workspace.id === template.workspaceId)?.name ?? t('工作区不可用')
}

export function taskTemplateMatchesTask(
  template: TaskTemplate,
  task: TaskSnapshot | null,
  currentWorkspaceId: string | null,
) {
  if (!task) return false
  if (template.targetKind === 'CurrentContext') return true
  if (template.targetKind === 'GeneralChat') return task.scopeKind === 'GeneralChat'
  return task.scopeKind === 'Workspace' &&
    (!template.workspaceId || template.workspaceId === currentWorkspaceId)
}
