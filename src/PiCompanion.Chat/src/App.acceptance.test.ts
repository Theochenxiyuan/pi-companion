import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import App from './App.vue'
import SkillsView from './components/SkillsView.vue'
import { UiSelect } from './components/ui'
import { createSkillsPreview, createTaskHistoryPreview, createTranscriptPreview } from './preview'
import { useTaskStore } from './stores/task'
import { clearStoredTaskPromptDrafts, loadTaskPromptDraft } from './utils/taskPromptDrafts'
import {
  bridgeProtocolVersion,
  type InitializeSnapshot,
  type SettingsSnapshot,
} from './types/bridge'

describe('Agent Chat stage 5 acceptance', () => {
  const mountedWrappers: ReturnType<typeof mount>[] = []

  beforeEach(() => {
    window.history.replaceState({}, '', '/')
    window.localStorage.removeItem('pi-companion:inspector-collapsed')
    window.localStorage.removeItem('pi-companion.inspector-width')
    clearStoredTaskPromptDrafts()
  })

  afterEach(() => {
    for (const wrapper of mountedWrappers) wrapper.unmount()
    mountedWrappers.length = 0
    document.body.innerHTML = ''
    delete window.chrome
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('loads grouped skill cards while presets and scheduled remain placeholders', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)

    const skillsNavigation = wrapper.findAll('.sidebar > nav .nav-row')
      .find(button => button.text() === '技能')!
    await skillsNavigation.trigger('click')
    const request = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'LoadSkills')
    expect(request).toEqual(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      type: 'LoadSkills',
      payload: { requestId: expect.stringMatching(/^skills-/) },
    }))
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillsLoaded',
        payload: createSkillsPreview(request.payload.requestId),
      },
    } as WebViewMessageEvent)
    await nextTick()

    const skillsView = wrapper.get('.skills-view')
    expect(skillsView.text()).toContain('find-skills')
    expect(skillsView.findAll('.skill-card')).toHaveLength(3)
    expect(skillsView.find('.skills-tabs').exists()).toBe(false)
    expect(skillsView.text()).not.toContain('技能库')
    expect(skillsView.text()).toContain('导入技能')
    expect(skillsView.findAll('.skill-card-footer button')).toHaveLength(3)
    expect(skillsView.findAll('.skill-card-footer button')
      .every(button => button.text() === '查看详情')).toBe(true)
    expect(wrapper.get('.sidebar > nav .nav-row.selected').text()).toBe('技能')

    await skillsView.get('.skills-import').trigger('click')
    wrapper.getComponent(SkillsView).vm.$emit('beginImport', 'zip')
    await nextTick()
    const beginImportRequest = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'BeginSkillImport')
    expect(beginImportRequest).toEqual(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      type: 'BeginSkillImport',
      payload: {
        requestId: expect.stringMatching(/^skill-import-/),
        sourceKind: 'zip',
      },
    }))
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillImportSourceInspected',
        payload: {
          requestId: beginImportRequest.payload.requestId,
          succeeded: true,
          cancelled: false,
          message: '技能来源已就绪。',
          source: {
            token: 'source-import',
            name: 'scripted',
            description: 'Runs a script.',
            sourceKind: 'zip',
            contentHash: 'source-hash',
            fileCount: 2,
            totalBytes: 2048,
            files: [
              { relativePath: 'SKILL.md', size: 1024, kind: 'file' },
              { relativePath: 'scripts/run.ps1', size: 1024, kind: 'script' },
            ],
            scriptFiles: ['scripts/run.ps1'],
            executableFiles: [],
          },
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    const destinationSelect = wrapper.getComponent(SkillsView).findAllComponents(UiSelect)[0]!
    destinationSelect.vm.$emit('update:modelValue', 'global')
    await nextTick()
    const prepareImportRequest = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'PrepareSkillImport')
    expect(prepareImportRequest.payload).toEqual({
      requestId: beginImportRequest.payload.requestId,
      sourceToken: 'source-import',
      targetScope: 'global',
    })
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillImportReady',
        payload: {
          requestId: beginImportRequest.payload.requestId,
          succeeded: true,
          message: '导入位置已就绪。',
          preparation: {
            token: 'prepared-import',
            sourceToken: 'source-import',
            name: 'scripted',
            description: 'Runs a script.',
            targetScope: 'global',
            workspaceId: null,
            workspaceName: null,
            targetPath: 'C:\\Users\\you\\.pi\\agent\\skills\\scripted',
            sourceKind: 'zip',
            contentHash: 'source-hash',
            fileCount: 2,
            totalBytes: 2048,
            files: [
              { relativePath: 'SKILL.md', size: 1024, kind: 'file' },
              { relativePath: 'scripts/run.ps1', size: 1024, kind: 'script' },
            ],
            scriptFiles: ['scripts/run.ps1'],
            executableFiles: [],
            requiresProjectTrust: false,
            trustStatus: 'not-required',
          },
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.get('.skill-import-dialog').text()).toContain('scripts/run.ps1')
    await wrapper.get('.skill-import-dialog .primary').trigger('click')
    expect(postMessage.mock.calls.map(call => call[0]).find(
      message => message.type === 'ConfirmSkillImport',
    )).toEqual(expect.objectContaining({
      payload: {
        requestId: beginImportRequest.payload.requestId,
        token: 'prepared-import',
      },
    }))
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillImportCompleted',
        payload: {
          requestId: beginImportRequest.payload.requestId,
          succeeded: true,
          cancelled: false,
          message: '已导入技能“scripted”。',
          skillName: 'scripted',
          targetPath: 'C:\\Users\\you\\.pi\\agent\\skills\\scripted',
          snapshot: createSkillsPreview(beginImportRequest.payload.requestId),
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.get('.skills-action-result').text()).toContain('已导入技能“scripted”')

    const releaseCard = skillsView.findAll('.skill-card')
      .find(card => card.text().includes('release-notes'))!
    await releaseCard.get('.skill-card-footer button').trigger('click')
    await wrapper.get('.skill-remove-button').trigger('click')
    await wrapper.get('.skill-removal-confirm-backdrop .danger').trigger('click')
    const removalRequest = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'RemoveSkillInstallation')
    expect(removalRequest).toEqual(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      type: 'RemoveSkillInstallation',
      payload: {
        requestId: expect.stringMatching(/^skill-removal-/),
        installationId: 'release-notes-workspace-pi',
        expectedContentHash:
          '2222222222222222222222222222222222222222222222222222222222222222',
      },
    }))
    const refreshed = createSkillsPreview(removalRequest.payload.requestId)
    refreshed.skills = refreshed.skills.filter(skill => skill.name !== 'release-notes')
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillRemovalCompleted',
        payload: {
          requestId: removalRequest.payload.requestId,
          succeeded: true,
          message: '已移动到恢复区。',
          removedInstallationId: 'release-notes-workspace-pi',
          recoveryPath: 'D:\\Dev\\pi-companion\\.pi\\skills\\.pi-companion-trash\\removed',
          snapshot: refreshed,
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.get('.skills-action-result').text()).toContain('已移动到恢复区')
    expect(wrapper.find('.skill-detail-dialog').exists()).toBe(false)
    expect(wrapper.findAll('.skill-card')).toHaveLength(2)

    for (const [label, view] of [
      ['预置任务', 'presets'],
      ['定时任务', 'scheduled'],
    ] as const) {
      const navigation = wrapper.findAll('.sidebar > nav .nav-row').find(button => button.text() === label)!
      await navigation.trigger('click')
      expect(wrapper.get('.feature-placeholder-view').classes()).toContain(`management-${view}`)
      expect(wrapper.get('.management-location strong').text()).toBe(label)
      expect(wrapper.get('.feature-placeholder-content').text()).toContain('暂未开放')
    }
  })

  it('round-trips an explicit workspace trust decision and refreshes skills', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const wrapper = mount(App, {
      attachTo: document.body,
      global: { plugins: [createPinia()] },
    })
    mountedWrappers.push(wrapper)

    const skillsNavigation = wrapper.findAll('.sidebar > nav .nav-row')
      .find(button => button.text() === '技能')!
    await skillsNavigation.trigger('click')
    const loadRequest = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'LoadSkills')
    const untrusted = createSkillsPreview(loadRequest.payload.requestId)
    untrusted.workspaceTrust[0]!.status = 'undecided'
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillsLoaded',
        payload: untrusted,
      },
    } as WebViewMessageEvent)
    await nextTick()

    wrapper.getComponent(SkillsView).vm.$emit('trustWorkspace', 'preview-workspace')
    await nextTick()
    expect(postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'TrustSkillWorkspace')).toBeUndefined()
    expect(wrapper.get('.skill-trust-confirm-dialog').text()).toContain('信任“pi-companion”？')
    expect(wrapper.get('.skill-trust-confirm-dialog').text()).toContain('其他项目级 Pi 资源')
    expect(wrapper.get('.skill-trust-confirm-dialog code').text()).toContain('D:\\Dev\\pi-companion')

    await wrapper.get('.skill-trust-confirm-dialog .primary').trigger('click')
    await nextTick()
    const request = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'TrustSkillWorkspace')
    expect(request).toEqual(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      payload: {
        requestId: expect.stringMatching(/^skill-trust-/),
        workspaceId: 'preview-workspace',
      },
    }))

    const trusted = createSkillsPreview(request.payload.requestId)
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillWorkspaceTrustCompleted',
        payload: {
          requestId: request.payload.requestId,
          succeeded: true,
          message: '工作区已受 Pi 信任。',
          workspaceId: 'preview-workspace',
          snapshot: trusted,
        },
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.skills-action-result').text()).toContain('工作区已受 Pi 信任')
    expect(wrapper.getComponent(SkillsView).props('snapshot')).toEqual(trusted)
  })

  it('requires an explicit workspace trust choice before the first task and then resumes it', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const wrapper = mount(App, {
      attachTo: document.body,
      global: { plugins: [createPinia()] },
    })
    mountedWrappers.push(wrapper)
    const workingDirectory = 'D:\\Dev\\desktop_software\\pi-companion'
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: null,
          lastSequence: 0,
          workspaces: [{
            id: 'preview-workspace',
            name: 'pi-companion',
            workingDirectory,
            createdAt: '2026-07-31T00:00:00.000Z',
            updatedAt: '2026-07-31T00:00:00.000Z',
            taskCount: 0,
            hasActiveTask: false,
            trustStatus: 'undecided',
            trustDecisionPath: null,
            trustInherited: false,
          }],
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: {
            workingDirectory,
            prompt: '',
            model: 'openai-codex/gpt-5.6-sol',
            thinkingLevel: 'high',
            permissionMode: 'standard',
            attachments: [],
          },
          capabilities: ['workspace-trust-preflight'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.workspace-trust-badge').text()).toContain('尚未选择信任')
    await wrapper.get('.composer > textarea').setValue('Inspect the project')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage.mock.calls.some(([message]) => message.type === 'SendPrompt')).toBe(false)
    expect(wrapper.get('.workspace-trust-dialog').text()).toContain('是否信任“pi-companion”？')
    expect(wrapper.get('.workspace-trust-dialog').text()).toContain('不会改变文件访问或命令执行权限')
    await wrapper.get('.workspace-trust-dialog').trigger('mousedown')
    expect(wrapper.find('.workspace-trust-dialog').exists()).toBe(true)
    await wrapper.get('.dialog-backdrop > .ui-dialog-overlay').trigger('mousedown')
    expect(wrapper.find('.workspace-trust-dialog').exists()).toBe(true)

    await wrapper.get('.workspace-trust-dialog .primary').trigger('click')
    const decision = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'SetWorkspaceTrustDecision')
    expect(decision).toEqual(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      payload: {
        requestId: expect.stringMatching(/^workspace-trust-/),
        workspaceId: 'preview-workspace',
        trusted: true,
      },
    }))

    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'WorkspaceTrustDecisionCompleted',
        payload: {
          requestId: decision.payload.requestId,
          succeeded: true,
          message: '已信任工作区“pi-companion”。',
          workspaceId: 'preview-workspace',
          status: 'trusted',
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    await nextTick()

    expect(wrapper.find('.workspace-trust-dialog').exists()).toBe(false)
    expect(wrapper.get('.app-toast').text()).toContain('已信任工作区')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SendPrompt',
      payload: expect.objectContaining({
        prompt: 'Inspect the project',
        workingDirectory,
      }),
    }))
  })

  it('opens the same workspace skill manager modal from the conversation and All Tasks', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const currentTask = createTranscriptPreview()
    const workspace = {
      id: 'preview-workspace',
      name: 'pi-companion',
      workingDirectory: currentTask.workingDirectory,
      createdAt: '2026-07-27T00:00:00.000Z',
      updatedAt: '2026-07-27T00:00:00.000Z',
      taskCount: 1,
      hasActiveTask: false,
    }
    const historyTask = {
      id: currentTask.id,
      runId: currentTask.runId,
      workspaceId: workspace.id,
      title: currentTask.title,
      workingDirectory: currentTask.workingDirectory,
      scopeKind: 'Workspace' as const,
      status: currentTask.status,
      statusText: currentTask.statusText,
      summary: currentTask.summary,
      updatedAt: '2026-07-27T00:00:00.000Z',
      deletedAt: null,
    }
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask,
          lastSequence: currentTask.lastSequence,
          workspaces: [workspace],
          recentTasks: [historyTask],
          historyTasks: [historyTask],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['skill-native-discovery', 'skill-content-fingerprints', 'skill-pi-removal'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    await wrapper.get('.topbar-skill-button').trigger('click')
    let loadRequest = postMessage.mock.calls
      .map(call => call[0])
      .findLast(message => message.type === 'LoadSkills')
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillsLoaded',
        payload: createSkillsPreview(loadRequest.payload.requestId),
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.skill-manager').text()).toContain('pi-companion 的技能')
    expect(wrapper.get('.skill-manager').text()).toContain('release-notes')
    expect(wrapper.findAll('.skills-view')).toHaveLength(0)
    expect(wrapper.findAll('.conversation-run')).toHaveLength(1)
    await wrapper.get('.skill-manager-close').trigger('click')

    const allTasks = wrapper.findAll('.sidebar > nav .nav-row')
      .find(button => button.text() === '全部任务')!
    await allTasks.trigger('click')
    await wrapper.get('.management-workspace-more summary').trigger('click')
    const viewSkills = wrapper.findAll('.management-workspace-menu button')
      .find(button => button.text() === '查看工作区技能')!
    await viewSkills.trigger('click')
    loadRequest = postMessage.mock.calls
      .map(call => call[0])
      .findLast(message => message.type === 'LoadSkills')
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillsLoaded',
        payload: createSkillsPreview(loadRequest.payload.requestId),
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.skill-manager').text()).toContain('pi-companion 的技能')
    expect(wrapper.findAll('.skills-view')).toHaveLength(0)
    expect(wrapper.findAll('.management-history')).toHaveLength(1)
  })

  it('opens a Direct Chat modal containing only global skills', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const currentTask = createTranscriptPreview()
    currentTask.scopeKind = 'GeneralChat'
    currentTask.workingDirectory = 'C:\\Users\\you\\AppData\\Local\\PiCompanion\\direct-chat\\private'
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask,
          lastSequence: currentTask.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['general-chat', 'skill-native-discovery', 'skill-pi-removal'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    await wrapper.get('.topbar-skill-button').trigger('click')
    const loadRequest = postMessage.mock.calls
      .map(call => call[0])
      .findLast(message => message.type === 'LoadSkills')
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillsLoaded',
        payload: createSkillsPreview(loadRequest.payload.requestId),
      },
    } as WebViewMessageEvent)
    await nextTick()

    const skillManager = wrapper.get('.skill-manager')
    expect(skillManager.text()).toContain('Direct Chat 技能')
    expect(skillManager.text()).toContain('find-skills')
    expect(skillManager.text()).not.toContain('release-notes')
    expect(skillManager.text()).not.toContain('AppData')
    expect(skillManager.text()).toContain('只读')
    expect(wrapper.findAll('.skills-view')).toHaveLength(0)
    expect(wrapper.findAll('.conversation-run')).toHaveLength(1)
  })

  it('enters Direct Chat explicitly before sending without a directory and exposes published file actions', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: null,
          lastSequence: 0,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['general-chat', 'published-artifacts'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.location-path').text()).toBe('尚未选择模式')
    expect(wrapper.get('.composer-scope-badge').text()).toBe('请选择模式')
    expect(wrapper.get('.composer > textarea').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('.mode-picker button')).toHaveLength(2)

    await wrapper.get('.direct-chat-picker').trigger('click')
    expect(wrapper.get('.location-path').text()).toBe('直接对话 · 隔离空间')
    expect(wrapper.get('.composer-scope-badge').text()).toBe('隔离空间')
    expect(wrapper.get('.composer > textarea').attributes('disabled')).toBeUndefined()
    await wrapper.get('.composer > textarea').setValue('生成一份 CSV')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SendPrompt',
      payload: expect.objectContaining({
        prompt: '生成一份 CSV',
        workingDirectory: undefined,
      }),
    }))

    const task = createTranscriptPreview()
    task.scopeKind = 'GeneralChat'
    task.workingDirectory = ''
    const artifact = {
      id: 'artifact-1',
      runId: task.runId,
      displayName: 'report.csv',
      contentType: 'text/csv',
      size: 42,
      sha256: 'abc',
      createdAt: new Date().toISOString(),
    }
    task.artifacts = [artifact]
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: task,
          lastSequence: task.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['general-chat', 'published-artifacts'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.artifact-card').text()).toContain('report.csv')
    const artifactButtons = wrapper.findAll('.artifact-card button')
    await artifactButtons[0].trigger('click')
    await artifactButtons[1].trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'OpenArtifact',
      payload: { artifactId: 'artifact-1' },
    }))
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SaveArtifact',
      payload: { artifactId: 'artifact-1' },
    }))

    const workspaceTask = {
      ...createTranscriptPreview(),
      id: 'workspace-task',
      runId: 'workspace-run',
      title: 'Workspace task',
      scopeKind: 'Workspace' as const,
      workingDirectory: 'D:\\workspaces\\actual-project',
    }
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: workspaceTask,
          lastSequence: workspaceTask.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['general-chat'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.location-path').text()).toBe('D:\\workspaces\\actual-project')

    postMessage.mockClear()
    await wrapper.get('.workspace-location-trigger').trigger('contextmenu')
    expect(wrapper.get('.workspace-location-popover').text()).toContain('在终端中打开')
    await wrapper.findAll('.workspace-location-popover button')[0].trigger('click')
    await wrapper.get('.workspace-location-trigger').trigger('click')
    await wrapper.findAll('.workspace-location-popover button')[0].trigger('click')
    await wrapper.get('.workspace-location-trigger').trigger('click')
    await wrapper.findAll('.workspace-location-popover button')[1].trigger('click')
    await wrapper.get('.workspace-location-trigger').trigger('click')
    await wrapper.findAll('.workspace-location-popover button')[2].trigger('click')

    expect(postMessage).toHaveBeenNthCalledWith(1, expect.objectContaining({
      type: 'OpenWorkspaceLocation',
      payload: {
        workingDirectory: 'D:\\workspaces\\actual-project',
        action: 'terminal',
      },
    }))
    expect(postMessage).toHaveBeenNthCalledWith(2, expect.objectContaining({
      type: 'OpenWorkspaceLocation',
      payload: {
        workingDirectory: 'D:\\workspaces\\actual-project',
        action: 'terminal',
      },
    }))
    expect(postMessage).toHaveBeenNthCalledWith(3, expect.objectContaining({
      type: 'OpenWorkspaceLocation',
      payload: {
        workingDirectory: 'D:\\workspaces\\actual-project',
        action: 'explorer',
      },
    }))
    expect(postMessage).toHaveBeenNthCalledWith(4, expect.objectContaining({
      type: 'OpenWorkspaceLocation',
      payload: {
        workingDirectory: 'D:\\workspaces\\actual-project',
        action: 'copy',
      },
    }))
  })

  it('intercepts app commands, validates skill calls, and supports escaped slash text', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const wrapper = mount(App, {
      attachTo: document.body,
      global: { plugins: [createPinia()] },
    })
    mountedWrappers.push(wrapper)
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: null,
          lastSequence: 0,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['general-chat'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()
    await wrapper.get('.direct-chat-picker').trigger('click')
    postMessage.mockClear()

    await wrapper.get('.composer > textarea').setValue('/settings')
    await wrapper.get('.send-button').trigger('click')
    expect(wrapper.find('.settings-modal').exists()).toBe(true)
    expect(postMessage.mock.calls.some(([message]) => message.type === 'SendPrompt')).toBe(false)
    await wrapper.get('.settings-close').trigger('click')

    await wrapper.get('.composer > textarea').setValue('/unknown')
    await wrapper.get('.send-button').trigger('click')
    expect(wrapper.get('.app-toast').text()).toContain('未知指令')
    expect(postMessage.mock.calls.some(([message]) => message.type === 'SendPrompt')).toBe(false)

    await wrapper.get('.composer > textarea').setValue('//compact')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SendPrompt',
      payload: expect.objectContaining({ prompt: '/compact' }),
    }))

    postMessage.mockClear()
    await wrapper.get('.composer > textarea').setValue('/skill:find')
    const skillsRequest = postMessage.mock.calls
      .map(call => call[0])
      .find(message => message.type === 'LoadSkills')
    expect(skillsRequest).toBeDefined()
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SkillsLoaded',
        payload: createSkillsPreview(skillsRequest.payload.requestId),
      },
    } as WebViewMessageEvent)
    await nextTick()
    postMessage.mockClear()
    await wrapper.get('.composer > textarea').setValue('/skill:find-skills 查找前端技能')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SendPrompt',
      payload: expect.objectContaining({
        prompt: '/skill:find-skills 查找前端技能',
      }),
    }))

    const completedTask = createTranscriptPreview()
    completedTask.status = 'Completed'
    completedTask.statusText = '已完成'
    completedTask.scopeKind = 'GeneralChat'
    completedTask.workingDirectory = ''
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: completedTask,
          lastSequence: completedTask.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['general-chat'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()
    postMessage.mockClear()
    await wrapper.get('.composer > textarea').setValue('/compact 保留关键决策')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'CompactSession',
      payload: {
        taskId: completedTask.id,
        customInstructions: '保留关键决策',
      },
    }))
  })

  it('sends a draft that contains attachments without requiring prompt text', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: null,
          lastSequence: 0,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: {
            workingDirectory: 'D:\\work',
            prompt: '',
            model: 'provider/vision',
            thinkingLevel: 'high',
            attachments: [{
              path: 'D:\\images\\clipboard.png',
              displayName: 'clipboard.png',
              kind: '文件',
              isAvailable: true,
              previewDataUrl: 'data:image/png;base64,cHJldmlldw==',
            }],
          },
          capabilities: ['image-attachment-thumbnails'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.get('.send-button').attributes('disabled')).toBeUndefined()
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SendPrompt',
      payload: expect.objectContaining({ prompt: '' }),
    }))
  })

  it('opens task history and manages the recycle bin from settings', async () => {
    const postMessage = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const store = useTaskStore(pinia)
    const preview = createTaskHistoryPreview()
    const snapshot: InitializeSnapshot = {
      currentTask: null,
      lastSequence: 0,
      recentTasks: [
        ...preview.history,
        ...preview.history.map((task, index) => ({ ...task, id: `${task.id}-older-${index}`, runId: `${task.runId}-older-${index}` })),
      ],
      historyTasks: preview.history,
      recycleBinTasks: preview.recycleBin,
      draft: {
        workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion',
        prompt: '',
        model: 'Pi 默认模型',
        thinkingLevel: '高',
        attachments: [],
      },
      capabilities: ['incremental-task-delta'],
    }
    store.consume({ protocolVersion: bridgeProtocolVersion, type: 'InitializeSnapshot', payload: snapshot })
    await nextTick()

    expect(wrapper.findAll('.history-item')).toHaveLength(5)
    expect(wrapper.findAll('.history-updated')).toHaveLength(5)
    expect(wrapper.get('.history-updated').attributes('title')).toBeUndefined()
    expect(wrapper.get('nav').text()).not.toContain('工作区')
    expect(wrapper.get('nav').text()).not.toContain('回收站')
    await wrapper.get('nav button:first-of-type').trigger('click')
    expect(wrapper.get('.management-location strong').text()).toBe('全部任务')
    expect(wrapper.get('.management-main').classes()).toContain('management-history')
    expect(wrapper.findAll('.management-task-progress .history-status')).toHaveLength(preview.history.length)
    expect(wrapper.find('.management-task-body > .history-status').exists()).toBe(false)
    expect(wrapper.find('.management-heading-icon').exists()).toBe(false)
    expect(wrapper.find('.composer').exists()).toBe(false)

    await wrapper.get('input[aria-label="搜索任务"]').setValue('打包')
    const matchingTasks = wrapper.findAll('.management-task-body')
    expect(matchingTasks).toHaveLength(1)
    expect(matchingTasks[0].text()).toContain('修复打包脚本')
    await matchingTasks[0].trigger('click')
    expect(wrapper.find('.composer').exists()).toBe(true)
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SelectTask',
      payload: { taskId: 'preview-failed' },
    }))

    await wrapper.get('.composer-add-button').trigger('click')
    await wrapper.get('[role="menuitem"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SelectAttachments',
      payload: expect.objectContaining({
        workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion',
      }),
    }))

    await wrapper.get('.sidebar-footer .settings').trigger('click')
    const recycleTab = wrapper.findAll('.settings-nav button').find(button => button.text() === '回收站')
    await recycleTab!.trigger('click')
    expect(wrapper.get('.settings-recycle-item').text()).toContain('旧版界面评审')
    await wrapper.get('.recycle-item-actions .settings-secondary').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'RestoreTaskFromRecycleBin',
      payload: { taskId: 'preview-deleted' },
    }))
  })

  it('keeps settings open while previewing language and theme selections', async () => {
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)

    await wrapper.get('.sidebar-footer .settings').trigger('click')
    expect(wrapper.find('.settings-modal').exists()).toBe(true)

    const appearanceSelects = wrapper.findAll('.settings-row .app-select-trigger')
    expect(appearanceSelects).toHaveLength(3)

    await appearanceSelects[0].trigger('click')
    const languageMenu = wrapper.get('.app-select-menu')
    expect(wrapper.get('.ui-dialog-content').element.contains(languageMenu.element)).toBe(true)
    await languageMenu.findAll('[role="option"]')[1].trigger('click')
    await nextTick()

    expect(wrapper.find('.settings-modal').exists()).toBe(true)
    expect(wrapper.get('#settings-title').text()).toBe('General')
    expect(document.documentElement.lang).toBe('en-US')

    await wrapper.findAll('.settings-row .app-select-trigger')[1].trigger('click')
    const themeMenu = wrapper.get('.app-select-menu')
    await themeMenu.findAll('[role="option"]')[1].trigger('click')
    await nextTick()

    expect(wrapper.find('.settings-modal').exists()).toBe(true)
    expect(document.documentElement.dataset.theme).toBe('light')
  })

  it('sends the draft permission with a new task and locks it after task creation', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: null,
          lastSequence: 0,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: {
            workingDirectory: 'D:\\Dev\\desktop_software\\pi-companion',
            prompt: '',
            model: 'openai-codex/gpt-5.6-sol',
            thinkingLevel: 'high',
            permissionMode: 'standard',
            attachments: [],
          },
          capabilities: ['workspace-permissions'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    const permission = wrapper.get('button[aria-label="Companion Extension 权限"]')
    expect(permission.text()).toContain('标准访问')
    expect(permission.attributes('disabled')).toBeUndefined()
    await wrapper.get('textarea').setValue('Inspect the project')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SendPrompt',
      payload: expect.objectContaining({ permissionMode: 'standard' }),
    }))

    const currentTask = createTranscriptPreview()
    currentTask.permissionMode = 'read-only'
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask,
          lastSequence: currentTask.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['workspace-permissions'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()
    const lockedPermission = wrapper.get('button[aria-label="Companion Extension 权限"]')
    expect(lockedPermission.text()).toContain('只读')
    expect(lockedPermission.attributes('disabled')).toBeDefined()
    await wrapper.get('.composer > textarea').setValue('Check the failing tests first')
    expect(wrapper.get('.send-button').text()).toContain('加入')
    await wrapper.get('.send-button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'QueueLocalMessage',
      payload: { message: 'Check the failing tests first' },
    }))
    const firstPendingItem = wrapper.findAll('.local-queue-item')[0]
    await firstPendingItem.get('button[aria-label="编辑"]').trigger('click')
    expect(wrapper.get('.local-message-editor-dialog').text()).not.toContain('模型')
    await wrapper.get('.local-message-editor-dialog textarea').setValue('Updated pending task')
    await wrapper.findAll('.local-message-editor-dialog footer button').find(button => button.text() === '确认')!.trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'UpdateLocalMessage',
      payload: expect.objectContaining({ messageId: 'preview-local-message-1', message: 'Updated pending task', attachments: [] }),
    }))
  })

  it('opens the monitored task directly in chat and closes settings', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const currentTask = createTranscriptPreview()
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask,
          lastSequence: currentTask.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['open-current-task'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    await wrapper.get('.sidebar-footer .settings').trigger('click')
    expect(wrapper.find('.settings-modal').exists()).toBe(true)
    expect(wrapper.get('.workspace-content').attributes('inert')).toBeDefined()
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'OpenCurrentTask',
        payload: { taskId: currentTask.id },
      },
    } as WebViewMessageEvent)
    await nextTick()

    expect(wrapper.find('.settings-modal').exists()).toBe(false)
    expect(wrapper.get('.workspace-content').attributes('inert')).toBeUndefined()
    expect(wrapper.find('.composer').exists()).toBe(true)
    expect(postMessage.mock.calls.some(([message]) => message.type === 'SelectTask')).toBe(false)

    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'OpenCurrentTask',
        payload: { taskId: 'another-task' },
      },
    } as WebViewMessageEvent)
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SelectTask',
      payload: { taskId: 'another-task' },
    }))
  })

  it('stores composer text by task id and restores it across task switches', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const firstTask = createTranscriptPreview()
    firstTask.id = 'TASK-DRAFT-FIRST'
    firstTask.runId = 'task-draft-first-run'
    const secondTask = {
      ...createTranscriptPreview(),
      id: 'task-draft-second',
      runId: 'task-draft-second-run',
    }
    const initializeTask = (task: typeof firstTask) => bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: task,
          lastSequence: task.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['incremental-task-delta'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)

    initializeTask(firstTask)
    await nextTick()
    await wrapper.get('.composer > textarea').setValue('first task unfinished prompt')
    expect(loadTaskPromptDraft(firstTask.id)).toBe('first task unfinished prompt')

    initializeTask(secondTask)
    await nextTick()
    expect((wrapper.get('.composer > textarea').element as HTMLTextAreaElement).value).toBe('')
    await wrapper.get('.composer > textarea').setValue('second task unfinished prompt')

    initializeTask(firstTask)
    await nextTick()
    expect((wrapper.get('.composer > textarea').element as HTMLTextAreaElement).value)
      .toBe('first task unfinished prompt')

    await wrapper.get('.new-task').trigger('click')
    expect((wrapper.get('.composer > textarea').element as HTMLTextAreaElement).value).toBe('')
    expect(loadTaskPromptDraft(firstTask.id)).toBe('first task unfinished prompt')

    initializeTask(secondTask)
    await nextTick()
    expect((wrapper.get('.composer > textarea').element as HTMLTextAreaElement).value)
      .toBe('second task unfinished prompt')
  })

  it('automatically dismisses transient bridge errors', async () => {
    vi.useFakeTimers()
    window.chrome = {
      webview: {
        postMessage: vi.fn(),
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const store = useTaskStore(pinia)

    store.bridgeError = 'Temporary bridge error'
    await nextTick()
    expect(wrapper.get('.app-toast').text()).toContain('Temporary bridge error')

    vi.advanceTimersByTime(4999)
    await nextTick()
    expect(wrapper.find('.app-toast').exists()).toBe(true)

    vi.advanceTimersByTime(1)
    await nextTick()
    expect(wrapper.find('.app-toast').exists()).toBe(false)
  })

  it('keeps the current task model when that model is hidden in Companion', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const currentTask = createTranscriptPreview()
    currentTask.model = 'openai-codex/gpt-5.6-sol'
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask,
          lastSequence: currentTask.lastSequence,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['task-execution-defaults'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()
    postMessage.mockClear()

    const currentSettings = (wrapper.vm as unknown as { settingsSnapshot: SettingsSnapshot }).settingsSnapshot
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SettingsUpdated',
        payload: {
          ...currentSettings,
          values: {
            ...currentSettings.values,
            modelVisibility: {
              hiddenModelReferences: ['openai-codex/gpt-5.6-sol'],
              legacyPiScopeMigrationCompleted: true,
            },
          },
        },
      },
    } as WebViewMessageEvent)
    await new Promise(resolve => window.setTimeout(resolve, 180))

    expect(wrapper.get('button[aria-label="模型"]').text()).toContain('GPT-5.6 Sol')
    expect(postMessage.mock.calls.some(([message]) => message.type === 'UpdateTaskExecutionDefaults')).toBe(false)
  })

  it('loads complete task history before grouping it by workspace', async () => {
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | undefined
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const seed = createTaskHistoryPreview().history[0]!
    const historyTasks = Array.from({ length: 10 }, (_, index) => ({
      ...seed,
      id: `history-${index}`,
      runId: `run-${index}`,
      title: `History ${index}`,
    }))
    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: null,
          lastSequence: 0,
          recentTasks: historyTasks,
          historyTasks,
          historyHasMore: true,
          recycleBinTasks: [],
          draft: null,
          capabilities: ['task-history'],
        } satisfies InitializeSnapshot,
      },
    } as WebViewMessageEvent)
    await nextTick()

    await wrapper.get('nav button:first-of-type').trigger('click')
    expect(wrapper.findAll('.management-task')).toHaveLength(5)
    expect(wrapper.findAll('.management-workspace-show-all')).toHaveLength(1)
    const loadAllRequest = postMessage.mock.calls.map(call => call[0]).find(message => message.type === 'LoadAllTaskHistory')
    expect(loadAllRequest.payload.offset).toBe(0)
    const completeHistory = [
      ...historyTasks,
      { ...seed, id: 'history-10', runId: 'run-10', title: 'Needle task' },
      { ...seed, id: 'history-11', runId: 'run-11', title: 'Other workspace', workingDirectory: 'D:\\Dev\\other-workspace' },
    ]

    bridgeListener?.({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'TaskHistoryPageLoaded',
        payload: {
          requestId: loadAllRequest.payload.requestId,
          offset: 0,
          items: completeHistory,
          hasMore: false,
          replaces: true,
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.findAll('.management-workspace')).toHaveLength(2)
    expect(wrapper.findAll('.management-task')).toHaveLength(6)
    expect(wrapper.findAll('.management-workspace-show-all')).toHaveLength(1)
    await wrapper.get('.management-workspace-show-all').trigger('click')
    expect(wrapper.findAll('.management-task')).toHaveLength(12)
    expect(wrapper.find('.management-workspace-show-all').exists()).toBe(false)

    await wrapper.get('input[type="search"]').setValue('Needle')
    await nextTick()
    expect(wrapper.findAll('.management-workspace')).toHaveLength(1)
    expect(wrapper.findAll('.management-task')).toHaveLength(1)
    expect(wrapper.find('.management-workspace-show-all').exists()).toBe(false)
  })

  it('clears task selection in history and shows a selected non-recent task above the full recent list', async () => {
    const postMessage = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const store = useTaskStore(pinia)
    const currentTask = createTranscriptPreview()
    currentTask.id = 'selected-outside-recent'
    currentTask.runId = 'selected-outside-recent-run'
    currentTask.title = '历史中选中的任务'
    currentTask.status = 'Completed'
    currentTask.statusText = '已完成'
    const historySeed = createTaskHistoryPreview().history[1]!
    const recentTasks = Array.from({ length: 5 }, (_, index) => ({
      ...historySeed,
      id: `recent-${index + 1}`,
      runId: `recent-run-${index + 1}`,
      title: `最近任务 ${index + 1}`,
      updatedAt: new Date(Date.UTC(2026, 0, 5 - index)).toISOString(),
    }))
    const currentEntry = {
      ...historySeed,
      id: currentTask.id,
      runId: currentTask.runId,
      title: currentTask.title,
      status: currentTask.status,
      statusText: currentTask.statusText,
      summary: currentTask.summary,
      updatedAt: new Date(Date.UTC(2025, 0, 1)).toISOString(),
    }
    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'InitializeSnapshot',
      payload: {
        currentTask,
        lastSequence: currentTask.lastSequence,
        recentTasks,
        historyTasks: [currentEntry, ...recentTasks],
        recycleBinTasks: [],
        draft: null,
        capabilities: ['incremental-task-delta'],
      } satisfies InitializeSnapshot,
    })
    await nextTick()

    let sidebarTasks = wrapper.findAll('.history-item')
    expect(sidebarTasks).toHaveLength(6)
    expect(sidebarTasks[0]!.text()).toContain(currentTask.title)
    expect(sidebarTasks[0]!.classes()).toContain('selected-history-item')
    expect(sidebarTasks.some(task => task.text().includes('最近任务 5'))).toBe(true)
    expect(wrapper.get('.selected-history-item + .section-label').text()).toBe('最近')
    expect(wrapper.get('.history-region > .section-label').text()).toBe('最近')
    expect(wrapper.find('.history > .section-label').exists()).toBe(false)
    expect(wrapper.findAll('.history-item.current')).toHaveLength(1)

    await wrapper.get('nav button:first-of-type').trigger('click')
    expect(wrapper.findAll('.history-item.current')).toHaveLength(0)
    expect(wrapper.findAll('.management-task.current')).toHaveLength(0)

    sidebarTasks = wrapper.findAll('.history-item')
    const selectedRecent = sidebarTasks.find(task => task.text().includes('最近任务 2'))
    await selectedRecent!.trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'SelectTask',
      payload: { taskId: 'recent-2' },
    }))
    store.currentTask = {
      ...currentTask,
      id: recentTasks[1]!.id,
      runId: recentTasks[1]!.runId,
      title: recentTasks[1]!.title,
      workingDirectory: recentTasks[1]!.workingDirectory,
      status: recentTasks[1]!.status,
      statusText: recentTasks[1]!.statusText,
      summary: recentTasks[1]!.summary,
    }
    await nextTick()

    sidebarTasks = wrapper.findAll('.history-item')
    expect(sidebarTasks.map(task => task.find('strong').text())).toEqual(recentTasks.map(task => task.title))
    expect(wrapper.get('.history-item.current strong').text()).toBe('最近任务 2')
  })

  it('renders stage 6 evidence and requests diff and hash-guarded recovery through the Bridge', async () => {
    const postMessage = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const store = useTaskStore(pinia)
    const task = createTranscriptPreview()
    task.status = 'Completed'
    task.statusText = '已完成'
    task.runs = [{
      id: task.runId,
      prompt: task.prompt,
      model: task.model,
      thinkingLevel: task.thinkingLevel,
      messageAttachments: [`${task.workingDirectory}\\README.md`],
      status: task.status,
      statusText: task.statusText,
      summary: task.summary,
      assistantText: task.assistantText,
      finalAnswer: task.finalAnswer,
      lastSequence: task.lastSequence,
      pendingSteering: [],
      pendingFollowUps: [],
      transcript: task.transcript,
      activities: task.activities,
      evidence: {
        runId: task.runId,
        finalized: true,
        isGitRepository: true,
        gitRoot: task.workingDirectory,
        headBefore: 'before',
        headAfter: 'after',
        testStatus: 'Passed',
        files: [{
          id: 'change-1',
          path: `${task.workingDirectory}\\README.md`,
          relativePath: 'README.md',
          kind: 'Modified',
          confidence: 'Confirmed',
          source: 'BackupComparison',
          beforeHash: 'before-hash',
          afterHash: 'after-hash',
          beforeSize: 10,
          afterSize: 12,
          isBinary: false,
          hasDiff: true,
          addedLines: 1,
          deletedLines: 1,
          diffTruncated: false,
          recovery: 'Available',
          recoveryMessage: '当前内容 Hash 匹配时可恢复',
        }, {
          id: 'change-binary',
          path: `${task.workingDirectory}\\image.png`,
          relativePath: 'image.png',
          kind: 'Modified',
          confidence: 'Confirmed',
          source: 'BackupComparison',
          beforeHash: 'binary-before',
          afterHash: 'binary-after',
          beforeSize: 256,
          afterSize: 512,
          isBinary: true,
          hasDiff: false,
          addedLines: 0,
          deletedLines: 0,
          diffTruncated: false,
          recovery: 'Available',
          recoveryMessage: '当前内容 Hash 匹配时可恢复',
        }, {
          id: 'change-pre-existing',
          path: `${task.workingDirectory}\\AGENTS.md`,
          relativePath: 'AGENTS.md',
          kind: 'Added',
          confidence: 'PreExisting',
          source: 'GitDiff',
          beforeHash: null,
          afterHash: 'pre-existing-hash',
          beforeSize: null,
          afterSize: 128,
          isBinary: false,
          hasDiff: true,
          addedLines: 10,
          deletedLines: 0,
          diffTruncated: false,
          recovery: 'Unavailable',
          recoveryMessage: '运行前已有',
        }],
        commands: [{
          id: 'command-1',
          toolCallId: 'bash-1',
          command: 'dotnet test',
          workingDirectory: task.workingDirectory,
          startedAt: new Date().toISOString(),
          durationMilliseconds: 1250,
          exitCode: 0,
          cancelled: false,
          timedOut: false,
          outputSummary: '64 tests passed',
          fullOutputPath: null,
          isTest: true,
          detectedFramework: 'dotnet',
          status: 'Passed',
        }],
        tests: [],
        warnings: [{
          code: 'git-dirty-baseline',
          message: 'Run 开始前 Git 工作区已有变化；这些变化不会自动归因给 Agent。',
          createdAt: new Date().toISOString(),
        }],
      },
    }]
    task.runs.push({
      ...task.runs[0],
      id: `${task.runId}-follow-up`,
      prompt: '继续检查',
      messageAttachments: [],
      transcript: [{
        ...task.transcript[0],
        id: 'follow-up-user',
        content: '继续检查',
      }],
      evidence: {
        runId: `${task.runId}-follow-up`,
        finalized: true,
        isGitRepository: true,
        gitRoot: task.workingDirectory,
        headBefore: 'before',
        headAfter: 'after',
        testStatus: 'NotRun',
        files: [],
        commands: [],
        tests: [],
        warnings: [],
      },
    })
    const snapshot: InitializeSnapshot = {
      currentTask: task,
      lastSequence: task.lastSequence,
      recentTasks: [],
      historyTasks: [],
      recycleBinTasks: [],
      draft: null,
      capabilities: ['file-evidence', 'git-diff', 'test-evidence', 'safe-file-recovery'],
    }

    store.consume({ protocolVersion: bridgeProtocolVersion, type: 'InitializeSnapshot', payload: snapshot })
    await nextTick()

    expect(wrapper.findAll('.evidence-panel')).toHaveLength(1)
    expect(wrapper.find('.run-context-alert').exists()).toBe(false)
    expect(wrapper.get('.evidence-panel').text()).not.toContain('Git 工作区')
    expect(wrapper.get('.evidence-panel').text()).toContain('测试通过')
    expect(wrapper.get('.conversation').text()).not.toContain('未运行测试')
    expect(wrapper.get('.evidence-toggle').attributes('aria-expanded')).toBe('false')
    await wrapper.get('.evidence-toggle').trigger('click')
    expect(wrapper.get('.evidence-panel').text()).toContain('Agent 修改')
    expect(wrapper.get('.evidence-panel').text()).not.toContain('BackupComparison')
    expect(wrapper.findAll('.message-attachments')).toHaveLength(1)
    expect(wrapper.get('.message-attachments').text()).toContain('README.md')
    expect(wrapper.get('.message-attachments').text()).not.toContain('附件 1')
    expect(wrapper.get('.evidence-panel').text()).toContain('README.md')
    expect(wrapper.get('.evidence-panel').text()).not.toContain('AGENTS.md')
    expect(wrapper.get('.evidence-panel').text()).not.toContain('运行前已有')
    expect(wrapper.get('.evidence-header').text()).toContain('2 个文件')
    expect(wrapper.get('.file-diff-stats').text()).toContain('+1')
    expect(wrapper.get('.file-diff-stats').text()).toContain('-1')
    expect(wrapper.get('.evidence-panel').text()).not.toContain('dotnet test')
    expect(wrapper.find('.evidence-run-details').exists()).toBe(false)
    const fileDiffButtons = wrapper.findAll('.file-diff-button')
    expect(fileDiffButtons).toHaveLength(2)
    expect(fileDiffButtons[1]!.attributes()).toHaveProperty('disabled')
    await fileDiffButtons[0]!.trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'GetFileDiff',
      payload: { changeId: 'change-1' },
    }))
    await fileDiffButtons[1]!.trigger('click')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'GetFileDiff')).toHaveLength(1)

    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'FileDiffLoaded',
      payload: {
        changeId: 'change-1',
        runId: task.runId,
        path: `${task.workingDirectory}\\README.md`,
        diffText: '--- a/README.md\n+++ b/README.md\n@@ -1,2 +1,2 @@\n-old\n+new\n context\n',
        isBinary: false,
        truncated: false,
        source: 'BackupComparison',
      },
    })
    await nextTick()
    expect(wrapper.get('.diff-line.added').text()).toContain('+new')
    expect(wrapper.get('.diff-line.removed').text()).toContain('-old')
    expect(wrapper.get('.diff-line.added .new').text()).toBe('1')
    expect(wrapper.get('.diff-line.removed .old').text()).toBe('1')
    expect(wrapper.get('.diff-meta').text()).toContain('+1')
    expect(wrapper.get('.diff-meta').text()).toContain('-1')
    expect(wrapper.get('.diff-meta').text()).not.toContain('BackupComparison')

    await wrapper.get('.restore-file-button').trigger('click')
    expect(wrapper.get('.recovery-dialog').text()).toContain('重新校验当前 Hash')
    await wrapper.get('.recovery-dialog .primary').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'RestoreFile',
      payload: { changeId: 'change-1' },
    }))
  })

  it('opens the collapsed Git sidebar from the compact composer warning', async () => {
    vi.useFakeTimers()
    window.localStorage.setItem('pi-companion:inspector-collapsed', 'true')
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | null = null
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const store = useTaskStore(pinia)
    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'InitializeSnapshot',
      payload: {
        currentTask: null,
        lastSequence: 0,
        recentTasks: [],
        historyTasks: [],
        recycleBinTasks: [],
        draft: {
          workingDirectory: 'D:\\work',
          prompt: '',
          model: 'provider/model',
          thinkingLevel: 'high',
          attachments: [],
        },
        capabilities: ['workspace-git-browser'],
      } satisfies InitializeSnapshot,
    })
    await nextTick()
    await vi.runOnlyPendingTimersAsync()

    const refreshRequest = postMessage.mock.calls
      .map(([message]) => message)
      .find(message => message.type === 'RefreshWorkspaceGit')
    expect(refreshRequest).toBeTruthy()
    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'WorkspaceGitStatusLoaded',
        payload: {
          requestId: refreshRequest.payload.requestId,
          workingDirectory: 'D:\\work',
          isRepository: true,
          repositoryRoot: 'D:\\work',
          branch: 'main',
          isDetached: false,
          entries: [
            {
              relativePath: 'README.md', originalRelativePath: null, status: ' M',
              indexStatus: ' ', workingTreeStatus: 'M', kind: 'Modified',
              isStaged: false, isUnstaged: true, isUntracked: false,
              isBinary: false, addedLines: 1, deletedLines: 0,
            },
            {
              relativePath: 'new.txt', originalRelativePath: null, status: '??',
              indexStatus: '?', workingTreeStatus: '?', kind: 'Added',
              isStaged: false, isUnstaged: true, isUntracked: true,
              isBinary: false, addedLines: 2, deletedLines: 0,
            },
          ],
          error: null,
        },
      },
    } as WebViewMessageEvent)
    await nextTick()

    const indicator = wrapper.get('.workspace-git-indicator')
    expect(indicator.text()).toContain('Git工作区中未提交更改')
    expect(indicator.get('.workspace-git-indicator-count').text()).toBe('2')
    expect(wrapper.find('.workspace-inspector').exists()).toBe(false)

    await indicator.trigger('click')
    expect(wrapper.find('.workspace-inspector').exists()).toBe(true)
    expect(wrapper.get('.inspector-tabs button.active').text()).toBe('Git')
    expect(wrapper.get('.git-change-files').text()).toContain('README.md')

    const currentSettings = (wrapper.vm as unknown as { settingsSnapshot: SettingsSnapshot }).settingsSnapshot
    const refreshCountBeforeAuto = postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshWorkspaceGit').length
    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SettingsUpdated',
        payload: {
          ...currentSettings,
          values: {
            ...currentSettings.values,
            general: { ...currentSettings.values.general, gitAutoRefreshSeconds: 5 },
          },
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    await vi.advanceTimersByTimeAsync(4_999)
    expect(postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshWorkspaceGit')).toHaveLength(refreshCountBeforeAuto)
    await vi.advanceTimersByTimeAsync(1)
    expect(postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshWorkspaceGit')).toHaveLength(refreshCountBeforeAuto + 1)

    await wrapper.findAll('.git-change-action')[0]!.trigger('click')
    const stageRequest = postMessage.mock.calls
      .map(([message]) => message)
      .find(message => message.type === 'RunWorkspaceGitAction' && message.payload.action === 'stage')
    expect(stageRequest).toEqual(expect.objectContaining({
      payload: expect.objectContaining({
        workingDirectory: 'D:\\work',
        action: 'stage',
        relativePaths: ['README.md'],
      }),
    }))

    await wrapper.findAll('.git-mode-tabs button')[2]!.trigger('click')
    const historyRequest = postMessage.mock.calls
      .map(([message]) => message)
      .find(message => message.type === 'RefreshWorkspaceGitHistory')
    expect(historyRequest).toBeTruthy()

    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'WorkspaceGitHistoryLoaded',
        payload: {
          requestId: historyRequest.payload.requestId,
          workingDirectory: 'D:\\work',
          offset: 0,
          hasMore: true,
          entries: [{
            hash: '0123456789abcdef',
            shortHash: '0123456',
            subject: 'feat: structured commit diff',
            authorName: 'Pi User',
            authorEmail: 'pi@example.test',
            timestamp: '2026-07-25T00:00:00+08:00',
            parents: ['fedcba9876543210'],
          }],
          error: null,
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    await wrapper.get('.git-history-load-more button').trigger('click')
    const moreHistoryRequest = postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshWorkspaceGitHistory')
      .at(-1)
    expect(moreHistoryRequest?.payload.offset).toBe(1)
    await wrapper.get('.git-history-list button').trigger('click')
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'GetWorkspaceGitCommitDiff',
      payload: {
        workingDirectory: 'D:\\work',
        commitHash: '0123456789abcdef',
      },
    }))

    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'WorkspaceGitCommitDiffLoaded',
        payload: {
          workingDirectory: 'D:\\work',
          hash: '0123456789abcdef',
          shortHash: '0123456',
          subject: 'feat: structured commit diff',
          truncated: false,
          files: [{
            relativePath: 'README.md',
            originalRelativePath: null,
            status: 'Modified',
            addedLines: 1,
            deletedLines: 1,
            diffText: 'diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-old\n+new\n',
            isBinary: false,
            truncated: false,
          }],
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.get('.commit-diff-dialog').text()).toContain('README.md')
    await wrapper.get('.commit-diff-dialog button[aria-label="关闭 Diff"]').trigger('click')
    expect(wrapper.find('.commit-diff-dialog').exists()).toBe(false)

    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'WorkspaceGitActionCompleted',
        payload: {
          requestId: stageRequest.payload.requestId,
          workingDirectory: 'D:\\work',
          action: 'stage',
          succeeded: false,
          message: '暂存失败',
          error: 'index.lock 已存在',
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.find('.git-action-notice').exists()).toBe(false)
    expect(wrapper.get('.app-toast').text()).toContain('暂存失败')
    await wrapper.get('.app-toast button').trigger('click')
    await wrapper.get('button[aria-label="刷新 Git 变更"]').trigger('click')
    expect(wrapper.find('.app-toast').exists()).toBe(false)
  })

  it('caches Session statistics while switching context tabs and models', async () => {
    vi.useFakeTimers()
    const postMessage = vi.fn()
    let bridgeListener: ((event: WebViewMessageEvent) => void) | null = null
    window.chrome = {
      webview: {
        postMessage,
        addEventListener(_type, listener) { bridgeListener = listener },
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'InitializeSnapshot',
        payload: {
          currentTask: { ...createTranscriptPreview(), model: 'openai-codex/gpt-5.6-sol', status: 'Completed' },
          lastSequence: 0,
          recentTasks: [],
          historyTasks: [],
          recycleBinTasks: [],
          draft: null,
          capabilities: ['session-statistics'],
        },
      },
    } as WebViewMessageEvent)
    await nextTick()

    const contextTab = wrapper.findAll('.inspector-tabs button').find(button => button.text() === '上下文')!
    await contextTab.trigger('click')
    const firstRequest = postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshSessionStatistics')
      .at(-1)
    expect(firstRequest).toBeTruthy()
    expect(firstRequest.payload.loadHistoricalSession).toBe(false)

    bridgeListener!({
      data: {
        protocolVersion: bridgeProtocolVersion,
        type: 'SessionStatisticsLoaded',
        payload: {
          requestId: firstRequest.payload.requestId,
          taskId: firstRequest.payload.taskId,
          available: true,
          statistics: {
            sessionId: 'session-1', sessionFile: null,
            userMessages: 2, assistantMessages: 4, toolCalls: 3, toolResults: 3, totalMessages: 6,
            inputTokens: 1200, outputTokens: 80, cacheReadTokens: 400, cacheWriteTokens: 0,
            totalTokens: 1680, cost: 0,
            contextUsage: { tokens: 27200, contextWindow: 272000, percent: 10 },
          },
          error: null,
        },
      },
    } as WebViewMessageEvent)
    await nextTick()
    expect(wrapper.get('.context-session-panel').text()).toContain('已使用 10.0%')

    const cachedRequestCount = postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshSessionStatistics').length
    const filesTab = wrapper.findAll('.inspector-tabs button').find(button => button.text() === '文件')!
    await filesTab.trigger('click')
    await contextTab.trigger('click')
    await vi.runOnlyPendingTimersAsync()
    expect(postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshSessionStatistics')).toHaveLength(cachedRequestCount)

    await wrapper.get('.context-session-heading button').trigger('click')
    const manualRequest = postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshSessionStatistics')
      .at(-1)
    expect(manualRequest.payload.loadHistoricalSession).toBe(true)

    const refreshCount = postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshSessionStatistics').length
    await wrapper.get('button[aria-label="模型"]').trigger('click')
    const nextModel = Array.from(document.body.querySelectorAll<HTMLElement>('.app-select-menu[aria-label="模型"] .app-select-option'))
      .find(option => !option.classList.contains('selected'))!
    nextModel.click()
    await nextTick()
    await vi.runOnlyPendingTimersAsync()
    expect(postMessage.mock.calls
      .map(([message]) => message)
      .filter(message => message.type === 'RefreshSessionStatistics')).toHaveLength(refreshCount)
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'UpdateTaskExecutionDefaults',
      payload: expect.objectContaining({
        taskId: 'preview-task',
        model: expect.any(String),
      }),
    }))
  })

  it('adds dropped files through native WebView2 objects and shows the drop target', async () => {
    const postMessage = vi.fn()
    const postMessageWithAdditionalObjects = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        postMessageWithAdditionalObjects,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const pinia = createPinia()
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [pinia] } })
    mountedWrappers.push(wrapper)
    const store = useTaskStore(pinia)
    store.consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'InitializeSnapshot',
      payload: {
        currentTask: null,
        lastSequence: 0,
        recentTasks: [],
        historyTasks: [],
        recycleBinTasks: [],
        draft: {
          workingDirectory: 'D:\\work',
          prompt: '检查附件',
          model: 'Pi 默认模型',
          thinkingLevel: '高',
          attachments: [],
        },
        capabilities: [],
      } satisfies InitializeSnapshot,
    })
    await nextTick()

    const file = new File(['content'], 'notes.txt', { type: 'text/plain' })
    const dataTransfer = { types: ['Files'], files: [file], dropEffect: 'none' }
    const dragEnter = new Event('dragenter', { cancelable: true }) as DragEvent
    Object.defineProperty(dragEnter, 'dataTransfer', { value: dataTransfer })
    window.dispatchEvent(dragEnter)
    await nextTick()
    expect(wrapper.get('.attachment-drop-overlay').text()).toContain('松开以添加附件')

    const drop = new Event('drop', { cancelable: true }) as DragEvent
    Object.defineProperty(drop, 'dataTransfer', { value: dataTransfer })
    window.dispatchEvent(drop)
    await nextTick()

    expect(wrapper.find('.attachment-drop-overlay').exists()).toBe(false)
    expect(postMessageWithAdditionalObjects).toHaveBeenCalledWith(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      type: 'AddDroppedAttachments',
      payload: expect.objectContaining({ workingDirectory: 'D:\\work' }),
    }), [file])
  })
})
