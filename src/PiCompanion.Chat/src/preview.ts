import type {
  SkillsLoaded,
  TaskHistoryEntry,
  TaskSnapshot,
  TranscriptBlock,
  WorkspaceGitCommitDiff,
} from '@/types/bridge'

const now = new Date().toISOString()

const block = (
  id: string,
  kind: TranscriptBlock['kind'],
  status: TranscriptBlock['status'],
  title: string,
  content: string,
  sequence: number,
  extra: Partial<TranscriptBlock> = {},
): TranscriptBlock => ({
  id,
  kind,
  status,
  title,
  content,
  firstSequence: sequence,
  lastSequence: sequence,
  timestamp: now,
  input: null,
  output: null,
  interactionId: null,
  interactionMethod: null,
  interactionKind: null,
  interactionOptions: [],
  ...extra,
})

export function createTranscriptPreview(): TaskSnapshot {
  return {
    id: 'preview-task',
    runId: 'preview-run',
    title: '分析工程结构并给出改进建议',
    prompt: '请检查这个工程的结构，说明核心模块，并给出进入下一阶段前最重要的改进建议。',
    workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion',
    scopeKind: 'Workspace',
    model: 'deepseek-v4-flash',
    thinkingLevel: 'high',
    attachments: [],
    status: 'WaitingForAnswer',
    statusText: '等待回答',
    summary: 'Pi Agent 需要确认下一步关注范围',
    assistantText: '我已经完成了第一轮检查。',
    finalAnswer: null,
    lastSequence: 11,
    pendingSteering: [],
    pendingFollowUps: ['完成当前检查后汇总阶段 4 风险'],
    localQueuedMessages: [{
      id: 'preview-local-message-1',
      message: '先确认现有失败测试是否和这次修改有关',
      createdAt: now,
    }, {
      id: 'preview-local-message-2',
      message: '完成后补充一段迁移说明',
      createdAt: now,
      attachments: [{
        path: 'D:\\Dev\\desktop_software\\pi-companion\\docs\\stage-7-delivery.md',
        displayName: 'stage-7-delivery.md',
        kind: 'file',
        isAvailable: true,
      }],
    }],
    localQueueAutoStartMessageId: 'preview-local-message-1',
    localQueueAutoStartAt: new Date(Date.now() + 30_000).toISOString(),
    runs: [],
    activities: [],
    transcript: [
      block('user-1', 'UserMessage', 'Completed', '你', '请检查这个工程的结构，说明核心模块，并给出进入下一阶段前最重要的改进建议。', 0),
      block('thinking-1', 'Thinking', 'Completed', '思考过程', '需要先确认项目入口、领域层以及桌面 Bridge 的边界，再检查持久化和测试覆盖。', 2),
      block('assistant-1', 'AssistantMessage', 'Completed', 'Pi Companion', '我先检查工程入口和主要模块，然后确认事件如何流向桌面界面。\n\n重点关注：\n\n- **领域投影**是否独立于 Pi JSON\n- SQLite 恢复是否幂等\n- 工具调用是否能稳定映射到 UI', 3),
      block('tool-1', 'Tool', 'Completed', 'read', 'read 完成：README.md', 4, { input: 'README.md', output: '执行完成' }),
      block('tool-1b', 'Tool', 'Completed', 'find', 'find 完成', 5, { input: '**/*.cs', output: '找到 12 个文件' }),
      block('tool-1c', 'Tool', 'Completed', 'read', 'read 完成：stage-3-progress.md', 6, { input: 'docs/stage-3-progress.md', output: '执行完成' }),
      block('assistant-2', 'AssistantMessage', 'Completed', 'Pi Companion', 'README 已确认。接下来我会继续定位 `TaskProjection` 的事件合并边界。', 7),
      block('tool-2', 'Tool', 'Running', 'grep', '正在搜索 TaskProjection', 8, { input: 'TaskProjection', output: '仍在运行' }),
      block('tool-3', 'Tool', 'Completed', 'read', 'read 完成：TaskProjection.cs', 9, { input: 'src/PiCompanion.Core/Tasks/TaskProjection.cs', output: '执行完成' }),
      block('interaction-approval', 'Interaction', 'Completed', '需要授权', '运行 dotnet test --configuration Release', 11, {
        output: '允许一次',
        interactionId: 'preview-approval',
        interactionMethod: 'select',
        interactionKind: 'Approval',
        interactionOptions: ['允许一次', '本任务内允许同类操作', '拒绝'],
      }),
      block('interaction-answer', 'Interaction', 'Completed', '需要你的回答', '下一步应该优先检查哪一项？', 12, {
        output: '运行时打包',
        interactionId: 'preview-answer',
        interactionMethod: 'select',
        interactionKind: 'Question',
        interactionOptions: ['运行时打包', '流式事件性能'],
      }),
      block('interaction-1', 'Interaction', 'Pending', '需要你的回答', '下一步应该优先检查运行时打包，还是先优化流式事件性能？', 13, {
        interactionId: 'preview-interaction',
        interactionMethod: 'select',
        interactionKind: 'Question',
        interactionOptions: ['运行时打包', '流式事件性能'],
      }),
    ],
  }
}

export function createPerformancePreview(): TaskSnapshot {
  const task = createTranscriptPreview()
  const initial = task.transcript[0]
  const events = Array.from({ length: 5000 }, (_, index) => {
    const sequence = index + 1
    return block(
      `performance-${sequence}`,
      'Notice',
      'Completed',
      '性能事件',
      `第 ${sequence} 条增量事件`,
      sequence,
    )
  })
  return {
    ...task,
    title: '5000 事件性能验收',
    status: 'Completed',
    statusText: '已完成',
    summary: '5000 条事件已载入',
    lastSequence: 5000,
    transcript: [initial, ...events],
  }
}

export function createTaskHistoryPreview(): { history: TaskHistoryEntry[]; recycleBin: TaskHistoryEntry[] } {
  const history: TaskHistoryEntry[] = [
    {
      id: 'preview-task', runId: 'preview-run', title: '分析工程结构并给出改进建议',
      workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion', status: 'WaitingForAnswer',
      statusText: '等待回答', summary: '需要确认下一步关注范围', updatedAt: now, deletedAt: null,
    },
    {
      id: 'preview-completed', runId: 'preview-completed-run', title: '整理发布前检查清单',
      workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion', status: 'Completed',
      statusText: '已完成', summary: '检查清单已整理', updatedAt: new Date(Date.now() - 86400000).toISOString(), deletedAt: null,
    },
    {
      id: 'preview-failed', runId: 'preview-failed-run', title: '修复打包脚本',
      workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion', status: 'Failed',
      statusText: '失败', summary: '构建未通过', updatedAt: new Date(Date.now() - 172800000).toISOString(), deletedAt: null,
    },
  ]
  return {
    history,
    recycleBin: [{
      id: 'preview-deleted', runId: 'preview-deleted-run', title: '旧版界面评审',
      workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion', status: 'Completed',
      statusText: '已完成', summary: '界面评审已归档', updatedAt: new Date(Date.now() - 259200000).toISOString(),
      deletedAt: new Date(Date.now() - 86400000).toISOString(),
    }],
  }
}

export function createCommitDiffPreview(): WorkspaceGitCommitDiff {
  return {
    workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion',
    hash: '0123456789abcdef0123456789abcdef01234567',
    shortHash: '0123456',
    subject: 'feat: group commit changes by file',
    truncated: true,
    files: [
      {
        relativePath: 'src/PiCompanion.Chat/src/components/CommitDiffDialog.vue',
        originalRelativePath: null,
        status: 'Added',
        addedLines: 6,
        deletedLines: 0,
        diffText: 'diff --git a/src/PiCompanion.Chat/src/components/CommitDiffDialog.vue b/src/PiCompanion.Chat/src/components/CommitDiffDialog.vue\n--- /dev/null\n+++ b/src/PiCompanion.Chat/src/components/CommitDiffDialog.vue\n@@ -0,0 +1,6 @@\n+<script setup lang="ts">\n+import { computed } from \'vue\'\n+\n+const files = computed(() => [])\n+</script>\n+\n',
        isBinary: false,
        truncated: false,
      },
      {
        relativePath: 'src/PiCompanion.Application/Files/WorkspaceGitBrowser.cs',
        originalRelativePath: null,
        status: 'Modified',
        addedLines: 18,
        deletedLines: 5,
        diffText: 'diff --git a/src/PiCompanion.Application/Files/WorkspaceGitBrowser.cs b/src/PiCompanion.Application/Files/WorkspaceGitBrowser.cs\n--- a/src/PiCompanion.Application/Files/WorkspaceGitBrowser.cs\n+++ b/src/PiCompanion.Application/Files/WorkspaceGitBrowser.cs\n@@ -179,2 +179,3 @@\n-        var diff = CreateDiff(workspace, hash, output);\n+        var files = ParseCommitFiles(output);\n+        return new WorkspaceGitCommitDiff(files);\n         return diff;\n',
        isBinary: false,
        truncated: true,
      },
      {
        relativePath: 'docs/git-history.md',
        originalRelativePath: 'docs/git-diff.md',
        status: 'Renamed',
        addedLines: 0,
        deletedLines: 0,
        diffText: 'diff --git a/docs/git-diff.md b/docs/git-history.md\nsimilarity index 100%\nrename from docs/git-diff.md\nrename to docs/git-history.md\n',
        isBinary: false,
        truncated: false,
      },
      {
        relativePath: 'assets/commit-diff-preview.png',
        originalRelativePath: null,
        status: 'Added',
        addedLines: null,
        deletedLines: null,
        diffText: null,
        isBinary: true,
        truncated: false,
      },
    ],
  }
}

export function createSkillsPreview(
  requestId = 'skills-preview',
  trustStatus: 'trusted' | 'undecided' = 'trusted',
): SkillsLoaded {
  const workspaceId = 'preview-workspace'
  const descriptionDiagnostic = {
    code: 'description-required',
    severity: 'warning' as const,
    message: 'description 为必填字段；Pi 不会加载此技能。',
    path: 'D:\\Dev\\pi-companion\\.agents\\skills\\draft\\SKILL.md',
    winnerPath: null,
    workspaceId,
    workspaceName: 'pi-companion',
  }
  const snapshot: SkillsLoaded = {
    requestId,
    scannedAt: now,
    skills: [
      {
        id: 'find-skills',
        name: 'find-skills',
        variants: [{
          id: '1111111111111111111111111111111111111111111111111111111111111111',
          contentHash: '1111111111111111111111111111111111111111111111111111111111111111',
          description: 'Discovers installable Agent skills for a requested capability.',
          version: '1.2.0',
          license: 'MIT',
          metadata: {
            name: 'find-skills',
            description: 'Discovers installable Agent skills for a requested capability.',
            version: '1.2.0',
            license: 'MIT',
            author: 'openai/find-skills',
            compatibility: 'Pi and compatible agent skill runtimes.',
          },
          disableModelInvocation: false,
          isAvailable: true,
          fileCount: 4,
          totalSize: 8192,
          lastModifiedAt: now,
          installations: [{
            id: 'find-skills-global-agents',
            filePath: 'C:\\Users\\you\\.agents\\skills\\find-skills\\SKILL.md',
            baseDirectory: 'C:\\Users\\you\\.agents\\skills\\find-skills',
            canonicalPath: 'C:\\Users\\you\\.agents\\skills\\find-skills\\SKILL.md',
            installPath: 'C:\\Users\\you\\.agents\\skills\\find-skills',
            isSingleFile: false,
            isGloballyEffective: true,
            effectiveWorkspaceIds: [workspaceId],
            origins: [{
              scope: 'global',
              source: 'agents',
              rootPath: 'C:\\Users\\you\\.agents\\skills',
              workspaceId: null,
              workspaceName: null,
              workspacePath: null,
              inherited: false,
              installPath: 'C:\\Users\\you\\.agents\\skills\\find-skills',
              isCompatibilityLink: false,
              linkTarget: null,
            }],
            diagnostics: [],
            removable: false,
            removalReason: '只有 Pi 专属目录中的技能可以卸载。',
          }],
        }],
        diagnostics: [],
      },
      {
        id: 'release-notes',
        name: 'release-notes',
        variants: [{
          id: '2222222222222222222222222222222222222222222222222222222222222222',
          contentHash: '2222222222222222222222222222222222222222222222222222222222222222',
          description: 'Builds concise release notes from the current workspace history.',
          version: '2.0.0',
          license: 'Apache-2.0',
          metadata: {
            name: 'release-notes',
            description: 'Builds concise release notes from the current workspace history.',
            version: '2.0.0',
            license: 'Apache-2.0',
          },
          disableModelInvocation: false,
          isAvailable: true,
          fileCount: 3,
          totalSize: 6144,
          lastModifiedAt: now,
          installations: [{
            id: 'release-notes-workspace-pi',
            filePath: 'D:\\Dev\\pi-companion\\.pi\\skills\\release-notes\\SKILL.md',
            baseDirectory: 'D:\\Dev\\pi-companion\\.pi\\skills\\release-notes',
            canonicalPath: 'D:\\Dev\\pi-companion\\.pi\\skills\\release-notes\\SKILL.md',
            installPath: 'D:\\Dev\\pi-companion\\.pi\\skills\\release-notes',
            isSingleFile: false,
            isGloballyEffective: false,
            effectiveWorkspaceIds: [workspaceId],
            origins: [{
              scope: 'workspace',
              source: 'pi',
              rootPath: 'D:\\Dev\\pi-companion\\.pi\\skills',
              workspaceId,
              workspaceName: 'pi-companion',
              workspacePath: 'D:\\Dev\\pi-companion',
              inherited: false,
              installPath: 'D:\\Dev\\pi-companion\\.pi\\skills\\release-notes',
              isCompatibilityLink: false,
              linkTarget: null,
            }],
            diagnostics: [],
            removable: true,
            removalReason: null,
          }],
        }],
        diagnostics: [],
      },
      {
        id: 'draft',
        name: 'draft',
        variants: [{
          id: '3333333333333333333333333333333333333333333333333333333333333333',
          contentHash: '3333333333333333333333333333333333333333333333333333333333333333',
          description: null,
          version: null,
          license: null,
          metadata: { name: 'draft' },
          disableModelInvocation: false,
          isAvailable: false,
          fileCount: 1,
          totalSize: 128,
          lastModifiedAt: now,
          installations: [{
            id: 'draft-workspace-agents',
            filePath: 'D:\\Dev\\pi-companion\\.agents\\skills\\draft\\SKILL.md',
            baseDirectory: 'D:\\Dev\\pi-companion\\.agents\\skills\\draft',
            canonicalPath: 'D:\\Dev\\pi-companion\\.agents\\skills\\draft\\SKILL.md',
            installPath: 'D:\\Dev\\pi-companion\\.agents\\skills\\draft',
            isSingleFile: false,
            isGloballyEffective: false,
            effectiveWorkspaceIds: [],
            origins: [{
              scope: 'workspace',
              source: 'agents',
              rootPath: 'D:\\Dev\\pi-companion\\.agents\\skills',
              workspaceId,
              workspaceName: 'pi-companion',
              workspacePath: 'D:\\Dev\\pi-companion',
              inherited: false,
              installPath: 'D:\\Dev\\pi-companion\\.agents\\skills\\draft',
              isCompatibilityLink: false,
              linkTarget: null,
            }],
            diagnostics: [descriptionDiagnostic],
            removable: false,
            removalReason: '只有 Pi 专属目录中的技能可以卸载。',
          }],
        }],
        diagnostics: [descriptionDiagnostic],
      },
    ],
    locations: [
      {
        id: 'global:agents',
        scope: 'global',
        source: 'agents',
        path: 'C:\\Users\\you\\.agents\\skills',
        status: 'loaded',
        skillCount: 1,
        workspaceId: null,
        workspaceName: null,
        workspacePath: null,
        inherited: false,
        message: null,
      },
      {
        id: 'workspace:pi',
        scope: 'workspace',
        source: 'pi',
        path: 'D:\\Dev\\pi-companion\\.pi\\skills',
        status: 'loaded',
        skillCount: 1,
        workspaceId,
        workspaceName: 'pi-companion',
        workspacePath: 'D:\\Dev\\pi-companion',
        inherited: false,
        message: null,
      },
      {
        id: 'workspace:agents',
        scope: 'workspace',
        source: 'agents',
        path: 'D:\\Dev\\pi-companion\\.agents\\skills',
        status: 'loaded',
        skillCount: 1,
        workspaceId,
        workspaceName: 'pi-companion',
        workspacePath: 'D:\\Dev\\pi-companion',
        inherited: false,
        message: null,
      },
    ],
    diagnostics: [],
    workspaceTrust: [{
      workspaceId,
      workspaceName: 'pi-companion',
      workspacePath: 'D:\\Dev\\pi-companion',
      status: trustStatus,
      decisionPath: trustStatus === 'trusted' ? 'D:\\Dev\\pi-companion' : null,
      inherited: false,
    }],
  }
  if (trustStatus !== 'trusted') {
    for (const skill of snapshot.skills) {
      const affected = skill.variants.flatMap(variant => variant.installations)
        .filter(installation => installation.origins.some(origin =>
          origin.scope === 'workspace' && origin.workspaceId === workspaceId))
      if (affected.length === 0) continue
      const diagnostic = {
        code: 'workspace-untrusted',
        severity: 'warning' as const,
        message: '工作区“pi-companion”尚未受 Pi 信任；该工作区中的技能不会被加载。',
        path: affected[0]!.filePath,
        winnerPath: null,
        workspaceId,
        workspaceName: 'pi-companion',
      }
      skill.diagnostics.push(diagnostic)
      for (const installation of affected) {
        installation.effectiveWorkspaceIds =
          installation.effectiveWorkspaceIds.filter(id => id !== workspaceId)
        installation.diagnostics.push(diagnostic)
      }
    }
  }
  return snapshot
}
