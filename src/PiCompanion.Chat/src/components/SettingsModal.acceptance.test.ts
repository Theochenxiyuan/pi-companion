import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import SettingsModal from './SettingsModal.vue'
import type { SettingsSnapshot, TaskHistoryEntry } from '@/types/bridge'
import { setLocale } from '@/i18n'

afterEach(() => setLocale('zh-CN'))

const snapshot: SettingsSnapshot = {
  values: {
    general: { launchAtLogin: false, keepRunningInTray: true, language: 'zh-CN', theme: 'dark', logLevel: 'information', uiScalePercent: 100, gitAutoRefreshSeconds: 0, conversationDetailLevel: 'normal' },
    monitor: { position: 'top-right', showOnStartup: true, alwaysOnTop: true, autoCollapseSeconds: 8, animationsEnabled: true },
    tasks: { aiTitleEnabled: true, aiTitleModel: 'openai-codex/gpt-5.6-luna', aiSummaryEnabled: true, aiSummaryModel: 'openai-codex/gpt-5.6-luna', aiMetadataModel: 'openai-codex/gpt-5.6-luna', recentTaskCount: 5, recentTaskSubtitle: 'workspace', permissionMode: 'standard', fileChangesExpandedByDefault: false, completionBehavior: 'keep-expanded', autoStartLocalQueueEnabled: false, autoStartLocalQueueDelaySeconds: 15 },
    agent: { defaultModel: 'openai-codex/gpt-5.6-sol', defaultThinkingLevel: 'xhigh', autoCompact: true, autoRetry: true, compactionReserveTokens: 16384, compactionKeepRecentTokens: 20000, retryMaxRetries: 3, retryBaseDelayMilliseconds: 2000, retryMaxDelayMilliseconds: 60000, steeringMode: 'one-at-a-time', followUpMode: 'one-at-a-time' },
    notifications: { notifyOnCompletion: true, notifyOnFailure: true, notifyWhenAttentionRequired: true, playSound: true, onlyWhenAppIsInBackground: true },
    dataRetention: { taskHistoryDays: 0, recycleBinDays: 30, logDays: 30 },
  },
  pi: {
    available: true,
    version: '0.83.0',
    runtimePath: 'C:\\PiRuntime\\dist\\cli.js',
    defaultModel: 'openai-codex/gpt-5.6-sol',
    defaultThinkingLevel: 'xhigh',
    autoCompact: true,
    autoRetry: true,
    compactionReserveTokens: 16384,
    compactionKeepRecentTokens: 20000,
    retryMaxRetries: 3,
    retryBaseDelayMilliseconds: 2000,
    retryMaxDelayMilliseconds: 60000,
    steeringMode: 'one-at-a-time',
    followUpMode: 'one-at-a-time',
    providers: [
      { id: 'openai-codex', name: 'OpenAI Codex', configured: true, authType: 'oauth', authSource: 'stored', supportsApiKey: false, supportsOAuth: true, capabilities: ['web-search'] },
      { id: 'anthropic', name: 'Anthropic', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: true, capabilities: ['web-search'] },
      { id: 'kimi-coding', name: 'Kimi For Coding', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: true },
      { id: 'openrouter', name: 'OpenRouter', configured: false, authType: null, authSource: null, supportsApiKey: true, supportsOAuth: true },
    ],
    models: [
      { provider: 'openai-codex', id: 'gpt-5.6-sol', name: 'GPT-5.6 Sol', reasoning: true, contextWindow: 272000, input: ['text', 'image'], thinkingLevels: ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'], api: 'openai-codex-responses', webSearchSupport: 'native' },
      { provider: 'openai-codex', id: 'gpt-5.6-luna', name: 'GPT-5.6 Luna', reasoning: true, contextWindow: 272000, input: ['text', 'image'], thinkingLevels: ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'], api: 'openai-codex-responses', webSearchSupport: 'native' },
      { provider: 'anthropic', id: 'claude-sonnet-4-5', name: 'Claude Sonnet 4.5', reasoning: true, contextWindow: 200000, input: ['text', 'image'], thinkingLevels: ['off', 'low', 'medium', 'high'], api: 'anthropic-messages', webSearchSupport: 'native' },
    ],
    enabledModels: null,
    customProviders: [],
    modelsConfigRevision: null,
    error: null,
  },
  dataDirectory: 'C:\\Users\\test\\AppData\\Local\\PiCompanion',
  logDirectory: 'C:\\Users\\test\\AppData\\Local\\PiCompanion\\logs',
}

const recycleBinTasks: TaskHistoryEntry[] = [{
  id: 'deleted-task',
  runId: 'deleted-run',
  title: '旧版界面评审',
  workingDirectory: 'D:\\Dev\\pi-companion',
  status: 'Completed',
  statusText: '已完成',
  summary: '界面评审已归档',
  updatedAt: '2026-07-18T08:00:00.000Z',
  deletedAt: '2026-07-20T08:00:00.000Z',
}]

describe('stage 7 settings modal', () => {
  it('does not expose a skill source setting', () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    expect(wrapper.findAll('.settings-nav button').some(button => button.text() === '技能')).toBe(false)
    expect(wrapper.text()).not.toContain('技能读取来源')
  })

  it('previews and saves the English interface language', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    await wrapper.get('button[aria-label="语言"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '英语')!.trigger('click')

    expect(document.documentElement.lang).toBe('en-US')
    expect(wrapper.get('#settings-title').text()).toBe('General')
    expect(wrapper.get('button[aria-label="Language"]').text()).toBe('English')
    expect(wrapper.get('.settings-auto-save-status').text()).toContain('Saving')
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({ general: { language: 'en-US' } }))
    await wrapper.setProps({ action: { message: 'Saved automatically.', succeeded: true, operation: 'companion-auto-save', silent: true } })
    expect(wrapper.get('.settings-auto-save-status').text()).toContain('Saved automatically')
  })

  it('adjusts and resets the persisted Agent Chat scale', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    expect(wrapper.get('.scale-value').text()).toBe('100%')
    await wrapper.get('button[aria-label="放大界面"]').trigger('click')
    expect(wrapper.get('.scale-value').text()).toBe('110%')
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({ general: { uiScalePercent: 110 } }))

    await wrapper.get('button[aria-label="恢复默认界面缩放"]').trigger('click')
    expect(wrapper.get('.scale-value').text()).toBe('100%')
  })

  it('persists the conversation display preference from General settings', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    expect(wrapper.get('button[aria-label="对话详情级别"]').text()).toBe('标准')
    await wrapper.get('button[aria-label="对话详情级别"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '详细')!.trigger('click')

    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({
      general: { conversationDetailLevel: 'verbose' },
    }))
  })

  it('configures workspace defaults and local Git auto-refresh', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    await wrapper.findAll('.settings-nav button').find(button => button.text() === '工作区')!.trigger('click')
    expect(wrapper.get('.settings-heading h1').text()).toBe('工作区')
    await wrapper.get('button[aria-label="新任务默认权限模式"]').trigger('click')
    expect(wrapper.findAll('[role="option"]').map(option => option.text())).toEqual(['只读', '标准访问'])
    expect(wrapper.text()).toContain('完全访问只会按任务开启')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '只读')!.trigger('click')
    await wrapper.get('[role="switch"][aria-label="默认展开文件变更"]').trigger('click')
    expect(wrapper.get('button[aria-label="Git 自动刷新间隔"]').text()).toBe('不自动刷新')
    await wrapper.get('button[aria-label="Git 自动刷新间隔"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '每 10 秒')!.trigger('click')

    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({
      general: { gitAutoRefreshSeconds: 10 },
      tasks: { permissionMode: 'read-only', fileChangesExpandedByDefault: true },
    }))
  })

  it('uses a modal with left tabs and saves application settings', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot, recycleBinTasks } })

    expect(wrapper.get('[role="dialog"]').attributes('aria-modal')).toBe('true')
    expect(wrapper.findAll('.settings-group-title').map(title => title.text())).toEqual(['应用', '工作流', '数据', 'PI'])
    expect(wrapper.findAll('.settings-nav button').map(button => button.text())).toEqual([
      '常规', '通知', '任务监视器', '任务', '工作区', '存储与诊断', '回收站', 'Agent', 'Provider',
    ])
    const monitorTab = wrapper.findAll('.settings-nav button').find(button => button.text() === '任务监视器')!
    expect(monitorTab.find('svg path').attributes('d')).toBe('M3 12h4l2.2-5 4.2 10 2.2-5H21')
    expect(monitorTab.find('svg rect').exists()).toBe(false)

    const runtimeRefresh = wrapper.get('button[aria-label="重新加载 Pi 本地状态"]')
    expect(runtimeRefresh.text()).toBe('重新加载')
    expect(runtimeRefresh.attributes('title')).toBe('只重新读取本地 Runtime、配置与缓存模型')
    expect(runtimeRefresh.find('svg').exists()).toBe(true)
    await runtimeRefresh.trigger('click')
    expect(wrapper.get('.runtime-refresh').attributes('aria-busy')).toBe('true')
    expect(wrapper.get('.runtime-refresh').text()).toBe('加载中')
    expect(wrapper.emitted('reloadPi')).toHaveLength(1)
    await wrapper.setProps({ action: { message: '已刷新。', succeeded: true } })
    expect(wrapper.get('button[aria-label="重新加载 Pi 本地状态"]').attributes('aria-busy')).toBe('false')

    const launchRow = wrapper.findAll('.toggle-row')[0]
    const launchToggle = launchRow.get('[role="switch"]')
    await launchRow.get('span').trigger('click')
    expect(launchToggle.attributes('aria-checked')).toBe('false')
    await launchToggle.trigger('click')
    expect(launchToggle.attributes('aria-checked')).toBe('true')

    await monitorTab.trigger('click')
    await wrapper.get('button[aria-label="任务监视器应用启动时出现位置"]').trigger('click')
    expect(wrapper.get('.app-select-menu').element.parentElement).toBe(wrapper.get('.settings-modal').element)
    expect(wrapper.get('.settings-modal').element.contains(wrapper.get('.app-select-menu').element)).toBe(true)
    expect(wrapper.findAll('[role="option"]').some(option => option.text() === '上次关闭时位置')).toBe(true)
    const bottomLeft = wrapper.findAll('[role="option"]').find(option => option.text() === '左下角')
    expect(bottomLeft).toBeDefined()
    await bottomLeft!.trigger('click')
    expect(wrapper.get('.settings-auto-save-status').text()).toContain('保存中')
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({ monitor: { position: 'bottom-left' } }))
    await wrapper.setProps({ action: { message: '已自动保存。', succeeded: true, operation: 'companion-auto-save', silent: true } })
    expect(wrapper.get('.settings-auto-save-status').text()).toContain('已自动保存')

    await wrapper.get('.settings-close').trigger('click')
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('uses one generation model for independently enabled AI titles and summaries', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })
    const tasksTab = wrapper.findAll('.settings-nav button').find(button => button.text() === '任务')
    await tasksTab!.trigger('click')

    const metadataSection = wrapper.findAll('.settings-section').find(section => section.get('h2').text() === 'AI 标题与总结')!
    const metadataModel = wrapper.get('button[aria-label="标题与总结生成模型"]')
    expect(metadataSection.text()).toContain('AI 生成任务标题')
    expect(metadataSection.text()).toContain('AI 生成任务总结')
    expect(metadataSection.text()).toContain('不会改变任务本身使用的模型')
    expect(metadataSection.text()).not.toContain('Session')
    expect(wrapper.find('button[aria-label="AI 任务标题模型"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="AI Run 总结模型"]').exists()).toBe(false)
    expect(metadataModel.attributes('disabled')).toBeUndefined()
    expect(metadataModel.text()).toContain('GPT-5.6 Luna')
    await wrapper.get('input[aria-label="最近任务显示数量"]').setValue(8)
    expect(wrapper.get('button[aria-label="最近任务副信息"]').text()).toBe('工作区名称')
    await wrapper.get('button[aria-label="最近任务副信息"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '最近一轮状态')!.trigger('click')

    await wrapper.get('[role="switch"][aria-label="AI 生成任务标题"]').trigger('click')
    expect(metadataModel.attributes('disabled')).toBeUndefined()

    await wrapper.get('[role="switch"][aria-label="AI 生成任务总结"]').trigger('click')
    expect(metadataModel.attributes('disabled')).toBeDefined()
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({
      tasks: {
        aiTitleEnabled: false,
        aiSummaryEnabled: false,
        recentTaskCount: 8,
        recentTaskSubtitle: 'latest-run',
      },
    }))
  })

  it('configures notifications, completion behavior, retention, and Pi strategies', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    await wrapper.findAll('.settings-nav button').find(button => button.text() === '通知')!.trigger('click')
    expect(wrapper.get('.settings-scroll').text()).toContain('任务需要你授权或回答问题时提醒你')
    await wrapper.get('[role="switch"][aria-label="任务完成通知"]').trigger('click')
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({
      notifications: { notifyOnCompletion: false },
    }))
    await wrapper.setProps({ action: { message: '通知已保存', succeeded: true, operation: 'companion-auto-save', silent: true } })

    await wrapper.findAll('.settings-nav button').find(button => button.text() === '工作区')!.trigger('click')
    expect(wrapper.get('.settings-scroll').text()).toContain('方便立即查看改了什么')
    expect(wrapper.get('.settings-scroll').text()).toContain('其他工具或窗口产生的分支与文件变更')
    await wrapper.get('[role="switch"][aria-label="默认展开文件变更"]').trigger('click')
    await wrapper.findAll('.settings-nav button').find(button => button.text() === '任务')!.trigger('click')
    expect(wrapper.get('.settings-scroll').text()).toContain('自动开始待发送区中的第一项')
    await wrapper.get('button[aria-label="任务结束后的行为"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '收起任务监视器')!.trigger('click')
    await wrapper.get('[role="switch"][aria-label="自动开始下一个待发送任务"]').trigger('click')
    await wrapper.get('button[aria-label="自动开始等待时间"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '30 秒')!.trigger('click')
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({
      tasks: {
        fileChangesExpandedByDefault: true,
        completionBehavior: 'collapse-monitor',
        autoStartLocalQueueEnabled: true,
        autoStartLocalQueueDelaySeconds: 30,
      },
    }))
    await wrapper.setProps({ action: { message: '任务设置已保存', succeeded: true, operation: 'companion-auto-save', silent: true } })

    await wrapper.findAll('.settings-nav button').find(button => button.text() === '存储与诊断')!.trigger('click')
    await wrapper.get('button[aria-label="任务历史保留期限"]').trigger('click')
    await wrapper.findAll('[role="option"]').find(option => option.text() === '30 天')!.trigger('click')
    await vi.waitFor(() => expect(wrapper.emitted('saveCompanion')?.at(-1)?.[0]).toMatchObject({
      dataRetention: { taskHistoryDays: 30 },
    }))
    await wrapper.setProps({ action: { message: '数据设置已保存', succeeded: true, operation: 'companion-auto-save', silent: true } })

    await wrapper.findAll('.settings-nav button').find(button => button.text() === 'Agent')!.trigger('click')
    expect(wrapper.get('.settings-scroll').text()).toContain('减少任务因内容过长而中断')
    expect(wrapper.get('.settings-scroll').text()).toContain('遇到临时网络或服务问题时自动再试')
    await wrapper.get('input[aria-label="最大重试次数"]').setValue(5)
    expect(wrapper.get('.settings-scroll').text()).not.toContain('消息队列')
    await wrapper.get('.settings-primary').trigger('click')
    expect(wrapper.emitted('saveAgent')?.at(-1)?.[0]).toMatchObject({
      retryMaxRetries: 5,
      steeringMode: 'one-at-a-time',
      followUpMode: 'one-at-a-time',
    })
  })

  it('shows models and providers returned by Pi and routes API keys back to Pi', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })

    const agentTab = wrapper.findAll('.settings-nav button').find(button => button.text() === 'Agent')
    await agentTab!.trigger('click')
    expect(wrapper.get('.settings-primary').text()).toContain('应用 Pi 设置')
    expect(wrapper.find('.settings-heading p').exists()).toBe(false)
    expect(wrapper.get('.settings-section h2').text()).toBe('配置')
    expect(wrapper.get('.settings-scroll').text()).not.toContain('直接读写')
    expect(wrapper.get('.agent-model-inputs').findAll('.app-select')).toHaveLength(2)
    expect(wrapper.get('.agent-model-row').text()).toContain('默认模型与推理等级')
    expect(wrapper.find('button[aria-label="默认权限模式"]').exists()).toBe(false)
    expect(wrapper.get('.settings-scroll').text()).toContain('GPT-5.6 Sol')
    expect(wrapper.get('.runtime-card').text()).toContain('Pi 0.83.0')
    await wrapper.get('[role="switch"][aria-label="自动重试"]').trigger('click')
    await wrapper.get('.settings-primary').trigger('click')
    expect(wrapper.emitted('saveAgent')?.[0]?.[0]).toMatchObject({ autoRetry: false })
    await wrapper.setProps({ action: { message: 'Pi Agent 设置已保存。', succeeded: true, operation: 'pi-agent-save' } })
    await wrapper.setProps({ snapshot: { ...snapshot, pi: { ...snapshot.pi, version: '0.83.1' } } })
    expect(wrapper.get('.runtime-card').text()).toContain('Pi 0.83.1')
    expect(wrapper.get('.settings-runtime-mini').text()).toContain('v0.83.1')
    await wrapper.get('button[aria-label="默认模型"]').trigger('click')
    expect(wrapper.get('.app-select-group-label').text()).toBe('OpenAI Codex')
    expect(wrapper.get('[role="option"]').attributes('title')).toContain('上下文窗口：272,000 tokens')
    expect(wrapper.get('[role="option"]').attributes('title')).toContain('推理：支持')
    expect(wrapper.get('[role="option"]').attributes('title')).toContain('图像输入：支持')
    expect(wrapper.findAll('[role="option"]').map(option => option.text())).not.toContain('跟随 Pi 默认模型')
    expect(wrapper.get('.app-select-options').attributes('class')).toContain('app-select-options')
    await wrapper.get('.app-select-search').setValue('Sol')
    expect(wrapper.findAll('[role="option"]').map(option => option.text())).toEqual(['GPT-5.6 Sol'])
    await wrapper.get('button[aria-label="默认模型"]').trigger('click')
    await wrapper.get('button[aria-label="默认推理等级"]').trigger('click')
    expect(wrapper.findAll('[role="option"]').map(option => option.text())).toEqual(expect.arrayContaining([
      'None', 'Minimal', 'Low', 'Medium', 'High', 'Xhigh', 'Max',
    ]))

    const providerTab = wrapper.findAll('.settings-nav button').find(button => button.text() === 'Provider')
    await providerTab!.trigger('click')
    expect(wrapper.find('.provider-oauth').exists()).toBe(false)
    expect(wrapper.find('.provider-logout').exists()).toBe(true)
    expect(wrapper.find('.provider-explainer').exists()).toBe(false)
    expect(wrapper.find('.settings-heading .refresh').exists()).toBe(false)
    expect(wrapper.find('.provider-avatar').exists()).toBe(false)
    expect(wrapper.get('.provider-web-search-badge').text()).toContain('自带网络搜索')
    expect(wrapper.get('.provider-web-search-badge').text()).not.toContain('实验性')
    expect(wrapper.get('.provider-web-search-badge').attributes('title')).toBe('此 Provider 的部分模型支持原生网络搜索，具体可用性取决于所选模型、API 版本和账号权限；Pi Companion 不保证所有模型均可使用。')
    expect(wrapper.get('.provider-model-explainer').text()).toBe('控制模型是否出现在任务的模型选择器中。')
    expect(wrapper.find('.provider-list-capability').exists()).toBe(false)
    expect(wrapper.findAll('.provider-model-meta .web-search')).toHaveLength(0)
    expect(wrapper.findAll('.provider-model-items article').every(model => !model.attributes('title')?.includes('网络搜索'))).toBe(true)
    const modelCatalogRefresh = wrapper.get('button[aria-label="联网刷新模型目录"]')
    expect(modelCatalogRefresh.attributes('title')).toBe('强制联网获取最新模型目录')
    await modelCatalogRefresh.trigger('click')
    expect(wrapper.emitted('refreshModelCatalog')).toHaveLength(1)
    expect(wrapper.get('button[aria-label="正在联网刷新模型目录"]').attributes('aria-busy')).toBe('true')
    await wrapper.setProps({ action: { message: '模型目录已刷新。', succeeded: true } })
    const anthropic = wrapper.findAll('.provider-items > button').find(button => button.text().includes('Anthropic'))
    expect(anthropic).toBeDefined()
    await anthropic!.trigger('click')
    expect(wrapper.get('.provider-web-search-badge').text()).toBe('支持网络搜索')
    expect(wrapper.get('.provider-web-search-badge').attributes('title')).toBe('此 Provider 的部分模型支持原生网络搜索，具体可用性取决于所选模型、API 版本和账号权限；Pi Companion 不保证所有模型均可使用。')
    expect(wrapper.get('.provider-web-search-badge').classes()).not.toContain('available')
    expect(wrapper.find('.provider-models-section').exists()).toBe(false)
    await wrapper.get('.provider-key-form input').setValue('sk-ant-test')
    await wrapper.get('.provider-key-form').trigger('submit')

    expect(wrapper.emitted('savePiApiKey')).toEqual([['anthropic', 'sk-ant-test']])
    expect(wrapper.get('.provider-save-key').text()).toContain('等待 Pi')
  })

  it('creates a custom provider in the right detail pane and selects it after Pi saves it', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })
    const providerTab = wrapper.findAll('.settings-nav button').find(button => button.text() === 'Provider')
    await providerTab!.trigger('click')

    const toolbar = wrapper.get('.provider-toolbar')
    expect(toolbar.element.lastElementChild).toBe(wrapper.get('.provider-add').element)
    await wrapper.get('.provider-add').trigger('click')
    expect(wrapper.find('.custom-provider-editor').exists()).toBe(true)
    expect(wrapper.get('.custom-provider-editor h2').text()).toBe('添加自定义 Provider')
    expect(wrapper.get('.custom-provider-editor').text()).not.toContain('配置一个 Pi 支持的兼容接口')
    expect(wrapper.get('.custom-provider-editor').text()).not.toContain('models.json')
    expect(wrapper.get('.custom-provider-editor').text()).not.toContain('创建后不可修改')
    expect(wrapper.get('.custom-provider-editor').text()).not.toContain('密钥不会写入')
    expect(wrapper.get('.custom-provider-editor').text()).not.toContain('至少添加一个模型')

    await wrapper.get('input[placeholder="例如：公司模型网关"]').setValue('Company Gateway')
    expect((wrapper.get('input[placeholder="company-gateway"]').element as HTMLInputElement).value).toBe('company-gateway')
    await wrapper.get('input[placeholder="https://api.example.com/v1"]').setValue('https://models.example.com/v1/')
    await wrapper.get('input[placeholder="保存到 Pi auth.json"]').setValue('sk-company-test')
    await wrapper.get('input[placeholder="model-id"]').setValue('company-coder')
    await wrapper.get('input[placeholder="留空则使用模型 ID"]').setValue('Company Coder')
    const modelLimits = wrapper.findAll('.custom-model-grid input[type="number"]')
    await modelLimits[0]!.setValue('261121')
    await modelLimits[1]!.setValue('300000')
    const capabilityToggles = wrapper.findAll('.custom-model-capabilities [role="switch"]')
    expect(capabilityToggles).toHaveLength(2)
    await capabilityToggles[0]!.trigger('click')
    await capabilityToggles[1]!.trigger('click')
    expect(capabilityToggles[0]!.attributes('aria-checked')).toBe('true')
    expect(capabilityToggles[1]!.attributes('aria-checked')).toBe('true')
    await wrapper.get('.custom-provider-form').trigger('submit')

    const emitted = wrapper.emitted('addPiCustomProvider')?.[0]
    expect(emitted?.[0]).toMatchObject({
      id: 'company-gateway',
      name: 'Company Gateway',
      baseUrl: 'https://models.example.com/v1',
      api: 'openai-completions',
      credentialMode: 'api-key',
      models: [{ id: 'company-coder', name: 'Company Coder', reasoning: true, imageInput: true, contextWindow: 261120, maxTokens: 261120 }],
    })
    expect(emitted?.[1]).toBe('sk-company-test')
    expect(emitted?.[2]).toBeNull()
    expect(wrapper.get('.custom-provider-actions .settings-primary').text()).toContain('正在验证并保存')

    const savedSnapshot = JSON.parse(JSON.stringify(snapshot)) as SettingsSnapshot
    savedSnapshot.pi.modelsConfigRevision = 'new-revision'
    savedSnapshot.pi.customProviders.push(emitted?.[0] as SettingsSnapshot['pi']['customProviders'][number])
    savedSnapshot.pi.providers.push({
      id: 'company-gateway',
      name: 'Company Gateway',
      configured: true,
      authType: 'api_key',
      authSource: 'stored',
      supportsApiKey: true,
      supportsOAuth: false,
    })
    savedSnapshot.pi.models.push({
      provider: 'company-gateway',
      id: 'company-coder',
      name: 'Company Coder',
      reasoning: false,
      contextWindow: 128000,
      input: ['text'],
      thinkingLevels: ['off'],
    })
    await wrapper.setProps({ snapshot: savedSnapshot })

    expect(wrapper.find('.custom-provider-editor').exists()).toBe(false)
    expect(wrapper.get('.provider-title').text()).toContain('Company Gateway')
    expect(wrapper.get('.provider-title').text()).toContain('自定义')
    expect(wrapper.find('.provider-models-section').exists()).toBe(true)

    await wrapper.get('.provider-edit').trigger('click')
    expect(wrapper.get('.custom-provider-editor h2').text()).toBe('编辑自定义 Provider')
    const providerIdInput = wrapper.get('input[placeholder="company-gateway"]')
    expect((providerIdInput.element as HTMLInputElement).value).toBe('company-gateway')
    expect((providerIdInput.element as HTMLInputElement).disabled).toBe(true)
    expect((wrapper.get('input[placeholder="例如：公司模型网关"]').element as HTMLInputElement).value).toBe('Company Gateway')
    expect((wrapper.get('input[placeholder="model-id"]').element as HTMLInputElement).value).toBe('company-coder')
    expect(wrapper.find('input[placeholder="留空则保留现有 API Key"]').exists()).toBe(true)

    await wrapper.get('input[placeholder="例如：公司模型网关"]').setValue('Company Gateway 2')
    await wrapper.get('.custom-provider-form').trigger('submit')
    const updated = wrapper.emitted('updatePiCustomProvider')?.[0]
    expect(updated?.[0]).toMatchObject({
      id: 'company-gateway',
      name: 'Company Gateway 2',
      models: [{ id: 'company-coder', name: 'Company Coder' }],
    })
    expect(updated?.[1]).toBe('')
    expect(updated?.[2]).toBe('new-revision')

    const updatedSnapshot = JSON.parse(JSON.stringify(savedSnapshot)) as SettingsSnapshot
    updatedSnapshot.pi.modelsConfigRevision = 'updated-revision'
    updatedSnapshot.pi.customProviders[0]!.name = 'Company Gateway 2'
    updatedSnapshot.pi.providers.find(provider => provider.id === 'company-gateway')!.name = 'Company Gateway 2'
    await wrapper.setProps({ snapshot: updatedSnapshot })
    expect(wrapper.find('.custom-provider-editor').exists()).toBe(false)
    expect(wrapper.get('.provider-title').text()).toContain('Company Gateway 2')
  })

  it('prioritizes disconnected custom providers, labels them as custom, and can delete them', async () => {
    const customSnapshot = JSON.parse(JSON.stringify(snapshot)) as SettingsSnapshot
    customSnapshot.pi.modelsConfigRevision = 'custom-revision'
    customSnapshot.pi.customProviders.push({
      id: 'company-gateway',
      name: 'Company Gateway',
      baseUrl: 'https://models.example.com/v1',
      api: 'openai-completions',
      credentialMode: 'api-key',
      models: [{ id: 'company-coder', name: 'Company Coder', reasoning: true, imageInput: false, contextWindow: 128000, maxTokens: 16384 }],
    })
    customSnapshot.pi.providers.push({
      id: 'company-gateway',
      name: 'Company Gateway',
      configured: false,
      authType: null,
      authSource: null,
      supportsApiKey: true,
      supportsOAuth: false,
    })
    customSnapshot.pi.models.push({
      provider: 'company-gateway',
      id: 'company-coder',
      name: 'Company Coder',
      reasoning: true,
      contextWindow: 128000,
      input: ['text'],
      thinkingLevels: ['off', 'low', 'medium', 'high'],
    })

    const wrapper = mount(SettingsModal, { props: { snapshot: customSnapshot } })
    const providerTab = wrapper.findAll('.settings-nav button').find(button => button.text() === 'Provider')
    await providerTab!.trigger('click')

    const providerItems = wrapper.findAll('.provider-items > button')
    expect(providerItems.map(item => item.find('strong').text())).toEqual([
      'OpenAI Codex',
      'Company Gateway',
      'Anthropic',
      'Kimi For Coding',
      'OpenRouter',
    ])
    const customItem = providerItems[1]!
    expect(customItem.get('.provider-list-meta').text()).toBe('自定义')
    expect(customItem.get('.provider-list-meta').text()).not.toContain('1')

    await customItem.trigger('click')
    expect(wrapper.get('.provider-delete').text()).toBe('删除')
    await wrapper.get('.provider-delete').trigger('click')
    expect(wrapper.get('.settings-confirm-dialog h2').text()).toBe('删除自定义 Provider？')
    expect(wrapper.get('.settings-confirm-dialog').text()).toContain('Company Gateway')
    expect(wrapper.get('.settings-confirm-actions .danger').text()).toBe('删除')
    await wrapper.get('.settings-confirm-actions .danger').trigger('click')
    expect(wrapper.emitted('deletePiCustomProvider')).toEqual([['company-gateway', 'custom-revision']])
  })

  it('hides credential entry for configured providers and restores it after logout', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot } })
    const providerTab = wrapper.findAll('.settings-nav button').find(button => button.text() === 'Provider')
    await providerTab!.trigger('click')

    expect(wrapper.findAll('.provider-items > button i')).toHaveLength(1)
    const anthropic = wrapper.findAll('.provider-items > button').find(button => button.text().includes('Anthropic'))
    await anthropic!.trigger('click')
    expect(wrapper.find('.provider-key-form').exists()).toBe(true)

    const configuredSnapshot = JSON.parse(JSON.stringify(snapshot)) as SettingsSnapshot
    configuredSnapshot.pi.providers[1]!.configured = true
    configuredSnapshot.pi.providers[1]!.authType = 'api_key'
    await wrapper.setProps({ snapshot: configuredSnapshot })
    expect(wrapper.find('.provider-key-form').exists()).toBe(false)
    expect(wrapper.find('.provider-oauth').exists()).toBe(false)

    await wrapper.get('.provider-logout').trigger('click')
    expect(wrapper.emitted('logoutPiProvider')).toEqual([['anthropic']])
    expect(wrapper.get('.provider-logout').text()).toContain('退出中')
    expect(wrapper.get('.provider-logout').attributes('aria-busy')).toBe('true')
    expect(wrapper.get('.provider-logout').attributes('disabled')).toBeDefined()
    expect(wrapper.find('.provider-logout > i').exists()).toBe(true)
    await wrapper.setProps({ action: { message: '退出失败。', succeeded: false } })
    expect(wrapper.get('.provider-logout').text()).toBe('退出')
    expect(wrapper.get('.provider-logout').attributes('aria-busy')).toBe('false')

    const loggedOutSnapshot = JSON.parse(JSON.stringify(configuredSnapshot)) as SettingsSnapshot
    loggedOutSnapshot.pi.providers[1]!.configured = false
    loggedOutSnapshot.pi.providers[1]!.authType = null
    await wrapper.setProps({ snapshot: loggedOutSnapshot })
    expect(wrapper.find('.provider-key-form').exists()).toBe(true)
    expect(wrapper.find('.provider-oauth').exists()).toBe(true)
    await wrapper.get('.provider-login').trigger('click')
    expect(wrapper.emitted('openPiLogin')).toEqual([['anthropic']])
    expect(wrapper.get('.provider-login').text()).toContain('正在打开浏览器')
    expect(wrapper.get('.provider-login').attributes('aria-busy')).toBe('true')
    await wrapper.setProps({ oauthLoginProgress: { providerId: 'anthropic', phase: 'waiting' } })
    expect(wrapper.get('.provider-login').text()).toContain('等待授权')
    expect(wrapper.get('.provider-login').attributes('aria-busy')).toBe('false')
    expect(wrapper.find('.provider-login > i').exists()).toBe(false)
    const cancelLogin = wrapper.findAll('.provider-login-actions button').find(button => button.text() === '取消')
    await cancelLogin!.trigger('click')
    expect(wrapper.emitted('cancelPiLogin')).toEqual([['anthropic']])
    await wrapper.setProps({ action: { message: 'OAuth 登录已完成。', succeeded: true } })
    expect(wrapper.get('.provider-login').text()).toContain('等待授权')
    await wrapper.setProps({ oauthLoginProgress: null })
    expect(wrapper.get('.provider-login').text()).toContain('在浏览器中登录')
  })

  it('keeps the latest optimistic model scope while an older Pi snapshot arrives', async () => {
    const configuredSnapshot = JSON.parse(JSON.stringify(snapshot)) as SettingsSnapshot
    configuredSnapshot.pi.providers[1]!.configured = true
    configuredSnapshot.pi.providers[1]!.authType = 'api_key'
    const wrapper = mount(SettingsModal, { props: { snapshot: configuredSnapshot } })
    const providerTab = wrapper.findAll('.settings-nav button').find(button => button.text() === 'Provider')
    await providerTab!.trigger('click')
    const anthropic = wrapper.findAll('.provider-items > button').find(button => button.text().includes('Anthropic'))
    await anthropic!.trigger('click')

    await wrapper.get('.provider-model-items button').trigger('click')
    await vi.waitFor(() => {
      expect(wrapper.emitted('savePiEnabledModels')).toEqual([[['openai-codex/gpt-5.6-sol', 'openai-codex/gpt-5.6-luna']]])
    })

    await wrapper.get('.provider-model-items button').trigger('click')
    expect(wrapper.get('.provider-model-items article').classes()).not.toContain('hidden')

    const firstWriteSnapshot = JSON.parse(JSON.stringify(configuredSnapshot)) as SettingsSnapshot
    firstWriteSnapshot.pi.enabledModels = ['openai-codex/gpt-5.6-sol', 'openai-codex/gpt-5.6-luna']
    await wrapper.setProps({ snapshot: firstWriteSnapshot })

    expect(wrapper.get('.provider-model-items article').classes()).not.toContain('hidden')
    await vi.waitFor(() => {
      expect(wrapper.emitted('savePiEnabledModels')).toEqual([
        [['openai-codex/gpt-5.6-sol', 'openai-codex/gpt-5.6-luna']],
        [null],
      ])
    })
  })

  it('uses accessible confirmation dialogs for maintenance actions', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot, recycleBinTasks } })

    const dataTab = wrapper.findAll('.settings-nav button').find(button => button.text() === '存储与诊断')
    await dataTab!.trigger('click')
    expect(wrapper.find('.settings-recycle-item').exists()).toBe(false)

    const clearCache = wrapper.findAll('button').find(button => button.text() === '清理缓存')
    expect(clearCache).toBeDefined()
    await clearCache!.trigger('click')

    expect(wrapper.get('[role="alertdialog"]').attributes('aria-modal')).toBe('true')
    expect(wrapper.get('.settings-confirm-dialog').text()).toContain('任务、对话和账号信息不会受到影响')
    await wrapper.get('.settings-confirm-actions button').trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="alertdialog"]').exists()).toBe(false)
    expect(wrapper.emitted('close')).toBeUndefined()

    await clearCache!.trigger('click')
    await wrapper.get('.settings-confirm-actions .confirm').trigger('click')
    expect(wrapper.emitted('clearCache')).toHaveLength(1)
    expect(wrapper.find('[role="alertdialog"]').exists()).toBe(false)

    const recycleTab = wrapper.findAll('.settings-nav button').find(button => button.text() === '回收站')
    await recycleTab!.trigger('click')
    expect(wrapper.find('.recycle-settings-section').exists()).toBe(false)
    expect(wrapper.find('.recycle-settings-heading').exists()).toBe(false)
    expect(wrapper.find('.settings-recycle-list').exists()).toBe(true)
    expect(wrapper.find('.recycle-item-icon').exists()).toBe(false)
    await wrapper.get('input[aria-label="搜索回收站任务"]').setValue('不存在的任务')
    expect(wrapper.find('.settings-recycle-item').exists()).toBe(false)
    expect(wrapper.get('.settings-recycle-empty').text()).toContain('没有匹配的任务')
    await wrapper.get('input[aria-label="搜索回收站任务"]').setValue('')
    await wrapper.get('button[aria-label="筛选回收站任务状态"]').trigger('click')
    const failedRecycleFilter = wrapper.findAll('[role="option"]').find(option => option.text() === '失败')
    await failedRecycleFilter!.trigger('click')
    expect(wrapper.find('.settings-recycle-item').exists()).toBe(false)
    await wrapper.get('button[aria-label="筛选回收站任务状态"]').trigger('click')
    const allRecycleFilter = wrapper.findAll('[role="option"]').find(option => option.text() === '全部状态')
    await allRecycleFilter!.trigger('click')
    expect(wrapper.get('.settings-recycle-item').text()).toContain('旧版界面评审')
    await wrapper.get('.recycle-item-actions .settings-secondary').trigger('click')
    expect(wrapper.emitted('restoreRecycleTask')).toEqual([['deleted-task']])

    await wrapper.get('.recycle-item-actions .text-danger-button').trigger('click')
    expect(wrapper.get('.settings-confirm-dialog').text()).toContain('旧版界面评审')
    await wrapper.get('.settings-confirm-actions .danger').trigger('click')
    expect(wrapper.emitted('deleteRecycleTask')).toEqual([['deleted-task']])

    const emptyRecycleBin = wrapper.get('.settings-heading .danger-button')
    expect(emptyRecycleBin.text()).toBe('清空回收站')
    await emptyRecycleBin.trigger('click')
    expect(wrapper.get('.settings-confirm-dialog').text()).toContain('无法撤销')
    await wrapper.get('.settings-confirm-actions .danger').trigger('click')
    expect(wrapper.emitted('emptyRecycleBin')).toHaveLength(1)
  })

  it('centers the empty recycle bin in the available content area', async () => {
    const wrapper = mount(SettingsModal, { props: { snapshot, recycleBinTasks: [] } })

    const recycleTab = wrapper.findAll('.settings-nav button').find(button => button.text() === '回收站')
    await recycleTab!.trigger('click')

    expect(wrapper.get('.settings-scroll').classes()).toContain('recycle-empty-scroll')
    expect(wrapper.get('.settings-recycle-empty').text()).toContain('回收站为空')
  })
})
