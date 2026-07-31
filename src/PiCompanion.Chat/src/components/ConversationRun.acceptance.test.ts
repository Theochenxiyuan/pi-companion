import { shallowMount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import ConversationRun from './ConversationRun.vue'
import ToolWorkItem from './ToolWorkItem.vue'
import type { TaskRunSnapshot, TranscriptBlock } from '@/types/bridge'
import { setLocale } from '@/i18n'

const VirtualListStub = {
  props: ['items'],
  template: '<div><template v-for="item in items" :key="item.key"><slot :item="item" /></template></div>',
}
const globalStyles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8')

afterEach(() => {
  setLocale('zh-CN')
  Reflect.deleteProperty(navigator, 'clipboard')
})

function createRun(summary: string): TaskRunSnapshot {
  return {
    id: 'run-1',
    prompt: '检查结果',
    model: 'provider/model',
    thinkingLevel: 'high',
    messageAttachments: [],
    status: 'Completed',
    statusText: '已完成',
    summary,
    aiSummaryStatus: summary ? 'Available' : 'NotRequested',
    assistantText: null,
    finalAnswer: null,
    lastSequence: 1,
    pendingSteering: [],
    pendingFollowUps: [],
    transcript: [],
    activities: [],
  }
}

describe('ConversationRun summary', () => {
  it('keeps the generated-file primary action legible on hover', () => {
    expect(globalStyles).toContain('.artifact-card > .primary-button:hover')
    expect(globalStyles).toMatch(
      /\.artifact-card > \.primary-button:hover\s*\{[^}]*background:\s*var\(--color-tone-14\);[^}]*color:\s*var\(--color-text-inverse\);/u,
    )
  })

  it('shows one transient activity status above the run-state divider', () => {
    const run = createRun('')
    run.status = 'Starting'
    run.statusText = '正在启动'
    run.activityStatus = '正在连接 Pi RPC'

    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: true },
    })

    expect(wrapper.get('.run-activity-status').text()).toBe('正在连接 Pi RPC')
    expect(wrapper.find('.run-summary-content').exists()).toBe(false)
    expect(
      wrapper.get('.run-activity-status').element.compareDocumentPosition(
        wrapper.get('.run-state-block').element,
      ) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy()
  })

  it('localizes active runtime status text in English', () => {
    setLocale('en-US')
    const run = createRun('')
    run.status = 'Running'
    run.statusText = '执行中'
    run.activityStatus = '正在生成回答'

    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: true },
    })

    expect(wrapper.get('.run-activity-status').text()).toBe('Generating response')
    expect(wrapper.get('.run-state-line').text()).toBe('Running')
  })

  it('hides zero work counters in summary mode', () => {
    const wrapper = shallowMount(ConversationRun, {
      props: { run: createRun('已完成。'), viewMode: 'summary', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.find('.run-summary-stats').exists()).toBe(false)
  })

  it('does not render a summary row when no AI summary was requested', () => {
    const wrapper = shallowMount(ConversationRun, {
      props: {
        run: createRun(''),
        viewMode: 'normal',
        needsInteraction: false,
        taskActive: false,
      },
    })

    expect(wrapper.find('.run-summary-row').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('总结：')
  })

  it('keeps an existing summary visible independently of the generation setting', () => {
    const wrapper = shallowMount(ConversationRun, {
      props: {
        run: createRun('Runtime 原始结果'),
        viewMode: 'normal',
        needsInteraction: false,
        taskActive: false,
      },
    })

    expect(wrapper.get('.run-summary-row').text()).toContain('Runtime 原始结果')
  })

  it('shows generation progress only for an explicit generating state', () => {
    const run = createRun('')
    run.aiSummaryStatus = 'Generating'
    const wrapper = shallowMount(ConversationRun, {
      props: {
        run,
        viewMode: 'normal',
        needsInteraction: false,
        taskActive: false,
      },
    })

    expect(wrapper.get('.run-summary-loading').text()).toBe('正在生成 AI 总结')
    expect(wrapper.find('.run-summary-content .run-summary-text').exists()).toBe(false)
  })

  it('shows only non-zero work counters in summary mode', () => {
    const run = createRun('已完成。')
    run.transcript = [{
      id: 'thinking-1',
      kind: 'Thinking',
      status: 'Completed',
      title: '思考过程',
      content: '检查实现。',
      firstSequence: 1,
      lastSequence: 1,
      timestamp: new Date().toISOString(),
      input: null,
      output: null,
      interactionId: null,
      interactionMethod: null,
      interactionKind: null,
      interactionOptions: [],
    }]
    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'summary', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.get('.run-summary-stats').text()).toContain('思考1')
    expect(wrapper.find('.thinking-summary-icon svg').exists()).toBe(true)
    expect(wrapper.get('.run-summary-stats').text()).not.toContain('工具调用')
  })

  it('uses the model name captured for the run as the agent label', () => {
    const wrapper = shallowMount(ConversationRun, {
      props: {
        run: createRun('已完成配置更新。'),
        agentName: 'GPT-5.6 Sol',
        viewMode: 'normal',
        needsInteraction: false,
        taskActive: false,
      },
    })

    expect(wrapper.get('.message.agent > header strong').text()).toBe('GPT-5.6 Sol (High)')
  })

  it('shows thinking, tools, and web search as separate non-zero summary counters', () => {
    const run = createRun('已完成。')
    run.transcript = [
      {
        id: 'thinking-1', kind: 'Thinking', status: 'Completed', title: '思考过程', content: '检查实现。',
        firstSequence: 1, lastSequence: 1, timestamp: new Date().toISOString(), input: null, output: null,
        interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
      },
      {
        id: 'tool-1', kind: 'Tool', status: 'Completed', title: '读取', content: 'README.md',
        firstSequence: 2, lastSequence: 2, timestamp: new Date().toISOString(), input: null, output: null,
        interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
      },
      {
        id: 'search-1', kind: 'WebSearch', status: 'Completed', title: 'web_search', content: '搜索新闻',
        firstSequence: 3, lastSequence: 3, timestamp: new Date().toISOString(), input: null, output: null,
        interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
      },
    ]
    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'summary', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.find('.thinking-summary-icon svg').exists()).toBe(true)
    expect(wrapper.find('.tool-summary-icon svg').exists()).toBe(true)
    expect(wrapper.find('.web-search-summary-icon svg').exists()).toBe(true)
    expect(wrapper.get('.run-summary-stats').text()).toContain('工具调用1')
    expect(wrapper.get('.run-summary-stats').text()).toContain('网络搜索1')
  })

  it('keeps web search outside consecutive tool-call groups', () => {
    const run = createRun('已完成。')
    const block = (id: string, kind: TranscriptBlock['kind'], title: string): TranscriptBlock => ({
      id, kind, status: 'Completed', title, content: title,
      firstSequence: 1, lastSequence: 1, timestamp: new Date().toISOString(), input: null, output: null,
      interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
    })
    run.transcript = [
      block('tool-1', 'Tool', 'read'),
      block('search-1', 'WebSearch', 'web_search'),
      block('tool-2', 'Tool', 'write'),
    ]

    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: false },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    expect(wrapper.find('.tool-group').exists()).toBe(false)
    expect(wrapper.findAll('tool-work-item-stub')).toHaveLength(3)
  })

  it('copies user and agent messages and places the agent action beside the run status', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const run = createRun('完成了检查。')
    run.transcript = [
      {
        id: 'user-1', kind: 'UserMessage', status: 'Completed', title: '用户', content: '检查这个项目',
        firstSequence: 1, lastSequence: 1, timestamp: new Date().toISOString(), input: null, output: null,
        interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
      },
      {
        id: 'assistant-1', kind: 'AssistantMessage', status: 'Completed', title: '助手', content: '项目检查完成。',
        firstSequence: 2, lastSequence: 2, timestamp: new Date().toISOString(), input: null, output: null,
        interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
      },
    ]
    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: false },
    })

    await wrapper.get('.user-copy-actions .message-copy-button').trigger('click')
    await wrapper.get('.run-summary-row .agent-copy-button').trigger('click')

    expect(writeText).toHaveBeenNthCalledWith(1, '检查这个项目')
    expect(writeText).toHaveBeenNthCalledWith(2, '项目检查完成。')
    expect(wrapper.find('.run-state-line .run-summary-text').exists()).toBe(false)
    expect(wrapper.get('.run-summary-row .run-summary-text').text()).toBe('完成了检查。')
    expect(wrapper.get('.run-summary-label').text()).toBe('总结：')
  })

  it('localizes persisted transcript labels and approval responses in English', () => {
    setLocale('en-US')
    const run = createRun('Completed.')
    const block = (
      id: string,
      kind: TranscriptBlock['kind'],
      title: string,
      content: string,
      output: string | null = null,
    ): TranscriptBlock => ({
      id,
      kind,
      status: 'Completed',
      title,
      content,
      firstSequence: 1,
      lastSequence: 1,
      timestamp: new Date().toISOString(),
      input: null,
      output,
      interactionId: kind === 'Interaction' ? id : null,
      interactionMethod: kind === 'Interaction' ? 'confirm' : null,
      interactionKind: kind === 'Interaction' ? 'Approval' : null,
      interactionOptions: [],
    })
    run.transcript = [
      block('user-1', 'UserMessage', '你', 'Split the image.'),
      block('thinking-1', 'Thinking', '思考过程', 'Planning image splitting.'),
      block('approval-1', 'Interaction', '需要授权', 'Run a command.', '允许一次'),
      block('approval-2', 'Interaction', '需要授权', 'Run another command.', '本任务内允许同类操作'),
    ]

    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: false },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    expect(wrapper.get('.transcript-message header strong').text()).toBe('You')
    expect(wrapper.get('.thinking-item .work-label').text()).toBe('Thinking process')
    expect(wrapper.findAll('.interaction-item .work-result').map(item => item.text())).toEqual([
      'Allow once',
      'Allow similar operations for this task',
    ])
    expect(wrapper.get('.interaction-detail small').text()).toBe('Response: Allow once')
  })

  it('keeps five message attachments and the overflow control on one collapsed row', async () => {
    const run = createRun('已完成。')
    run.messageAttachments = Array.from({ length: 11 }, (_, index) => `C:\\files\\attachment-${index + 1}.txt`)
    run.transcript = [{
      id: 'user-1',
      kind: 'UserMessage',
      status: 'Completed',
      title: '你',
      content: '处理附件',
      firstSequence: 0,
      lastSequence: 0,
      timestamp: new Date().toISOString(),
      input: null,
      output: null,
      interactionId: null,
      interactionMethod: null,
      interactionKind: null,
      interactionOptions: [],
    }]
    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.findAll('.message-attachments span.message-attachment')).toHaveLength(5)
    expect(wrapper.get('.message-attachments').classes()).not.toContain('expanded')
    expect(wrapper.get('.attachment-overflow-toggle').text()).toBe('还有 6 个附件')

    await wrapper.get('.attachment-overflow-toggle').trigger('click')
    expect(wrapper.findAll('.message-attachments span.message-attachment')).toHaveLength(11)
    expect(wrapper.get('.message-attachments').classes()).toContain('expanded')
    expect(wrapper.get('.attachment-overflow-toggle').text()).toBe('收起附件')
  })

  it('does not stretch a single message attachment', () => {
    const run = createRun('已完成。')
    run.messageAttachments = ['C:\\files\\elements.png']
    run.transcript = [{
      id: 'user-1',
      kind: 'UserMessage',
      status: 'Completed',
      title: '你',
      content: '处理附件',
      firstSequence: 0,
      lastSequence: 0,
      timestamp: new Date().toISOString(),
      input: null,
      output: null,
      interactionId: null,
      interactionMethod: null,
      interactionKind: null,
      interactionOptions: [],
    }]
    const wrapper = shallowMount(ConversationRun, {
      props: { run, viewMode: 'normal', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.get('.message-attachments').classes()).not.toContain('has-overflow')
    expect(wrapper.findAll('.message-attachment')).toHaveLength(1)
  })

  it('offers the full text as a tooltip and expands a long summary', async () => {
    const summary = '这是一个足够长的任务总结，用来验证默认状态只显示单行，同时允许用户点击展开查看完整内容。'.repeat(3)
    const wrapper = shallowMount(ConversationRun, {
      props: { run: createRun(summary), viewMode: 'normal', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.get('.run-summary-text').attributes('title')).toBe(summary)
    expect(wrapper.get('.run-summary-toggle').text()).toBe('展开')
    expect(wrapper.get('.run-summary-row').classes()).not.toContain('expanded')

    await wrapper.get('.run-summary-toggle').trigger('click')
    expect(wrapper.get('.run-summary-row').classes()).toContain('expanded')
    expect(wrapper.get('.run-summary-toggle').text()).toBe('收起')
  })

  it('does not show an expand control for a short summary', () => {
    const wrapper = shallowMount(ConversationRun, {
      props: { run: createRun('已完成配置更新。'), viewMode: 'normal', needsInteraction: false, taskActive: false },
    })

    expect(wrapper.find('.run-summary-toggle').exists()).toBe(false)
  })

  it('lets the current run cancel an active automatic retry', async () => {
    const run = createRun('自动重试中')
    const retryNotice: TranscriptBlock = {
      id: 'retry-notice',
      kind: 'Notice',
      status: 'Running',
      title: '自动重试',
      content: '第 2/3 次重试将在 4 秒后开始',
      firstSequence: 2,
      lastSequence: 2,
      timestamp: new Date().toISOString(),
      input: null,
      output: null,
      interactionId: null,
      interactionMethod: null,
      interactionKind: null,
      interactionOptions: [],
    }
    run.status = 'Running'
    run.transcript = [retryNotice]

    const wrapper = shallowMount(ConversationRun, {
      props: {
        run,
        currentRunId: run.id,
        viewMode: 'normal',
        needsInteraction: false,
        taskActive: true,
      },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    await wrapper.get('.notice-card button').trigger('click')
    expect(wrapper.emitted('abortRetry')).toHaveLength(1)
  })

  it('keeps preset choices visible while entering an other answer', async () => {
    const run = createRun('')
    run.status = 'WaitingForAnswer'
    run.transcript = [{
      id: 'question-1',
      kind: 'Interaction',
      status: 'Pending',
      title: '需要你的回答',
      content: '下一步检查什么？',
      firstSequence: 1,
      lastSequence: 1,
      timestamp: new Date().toISOString(),
      input: null,
      output: null,
      interactionId: 'question-1',
      interactionMethod: 'select',
      interactionKind: 'Question',
      interactionOptions: ['权限策略', '队列状态', '其他…'],
    }]
    const wrapper = shallowMount(ConversationRun, {
      props: {
        run,
        currentRunId: run.id,
        viewMode: 'normal',
        needsInteraction: true,
        taskActive: true,
      },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    const optionButtons = wrapper.findAll('.interaction-options button')
    expect(wrapper.get('.interaction-card').classes()).toContain('question')
    expect(globalStyles).toMatch(
      /\.interaction-card\.question\s*\{[^}]*border-color:\s*var\(--color-info\);[^}]*background:\s*var\(--color-info-surface\);/u,
    )
    await optionButtons.find(button => button.text() === '其他…')!.trigger('click')

    expect(wrapper.findAll('.interaction-options button').map(button => button.text())).toEqual([
      '权限策略',
      '队列状态',
      '其他…',
      '取消',
    ])
    expect(wrapper.find('.interaction-custom-answer textarea').exists()).toBe(true)
    expect(wrapper.findAll('.interaction-actions')).toHaveLength(1)
    expect(wrapper.emitted('resolveInteraction')).toBeUndefined()

    await wrapper.get('.interaction-custom-answer textarea').setValue('检查日志聚合')
    await wrapper.get('.interaction-custom-answer .primary-button').trigger('click')
    expect(wrapper.emitted('resolveInteraction')?.[0]).toEqual([
      expect.objectContaining({ interactionId: 'question-1' }),
      true,
      '检查日志聚合',
    ])
  })

  it('keeps long approval details scrollable, expandable, and copyable without hiding actions', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const run = createRun('')
    const content = `Shell 命令请求\n\nnode -e "${'x'.repeat(1200)}" --visible-tail\n\n工作目录：D:\\work`
    run.status = 'WaitingForApproval'
    run.transcript = [{
      id: 'approval-long',
      kind: 'Interaction',
      status: 'Pending',
      title: '需要授权',
      content,
      firstSequence: 1,
      lastSequence: 1,
      timestamp: new Date().toISOString(),
      input: null,
      output: null,
      interactionId: 'approval-long',
      interactionMethod: 'select',
      interactionKind: 'Approval',
      interactionOptions: ['允许一次', '本任务内允许同类操作', '拒绝'],
    }]
    const wrapper = shallowMount(ConversationRun, {
      props: {
        run,
        currentRunId: run.id,
        viewMode: 'normal',
        needsInteraction: true,
        taskActive: true,
      },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    expect(globalStyles).toMatch(
      /\.interaction-card \.interaction-content\s*\{[^}]*max-height:[^;]+;[^}]*overflow:\s*auto;[^}]*overflow-wrap:\s*anywhere;[^}]*white-space:\s*pre-wrap;/u,
    )
    expect(wrapper.get('.interaction-content').classes()).not.toContain('expanded')
    expect(wrapper.findAll('.interaction-actions button')).toHaveLength(3)

    await wrapper.findAll('.interaction-content-actions button')[0]!.trigger('click')
    expect(wrapper.get('.interaction-content').classes()).toContain('expanded')
    expect(wrapper.findAll('.interaction-actions button')).toHaveLength(3)

    await wrapper.findAll('.interaction-content-actions button')[1]!.trigger('click')
    expect(writeText).toHaveBeenCalledWith(content)
  })

  it('shows a structured question without duplicating its ask_user tool record', () => {
    const run = createRun('')
    run.transcript = [
      {
        id: 'tool-ask-1',
        kind: 'Tool',
        status: 'Completed',
        title: 'ask_user',
        content: 'ask_user 完成',
        firstSequence: 1,
        lastSequence: 4,
        timestamp: new Date().toISOString(),
        input: null,
        output: '用户回答：休息放空',
        interactionId: null,
        interactionMethod: null,
        interactionKind: null,
        interactionOptions: [],
      },
      {
        id: 'interaction-question-1',
        kind: 'Interaction',
        status: 'Completed',
        title: '需要你的回答',
        content: '你今天最想做什么？',
        firstSequence: 2,
        lastSequence: 3,
        timestamp: new Date().toISOString(),
        input: null,
        output: '休息放空',
        interactionId: 'question-1',
        interactionMethod: 'select',
        interactionKind: 'Question',
        interactionOptions: ['学习新东西', '休息放空'],
      },
    ]

    const wrapper = shallowMount(ConversationRun, {
      props: { run, currentRunId: run.id, viewMode: 'normal', needsInteraction: false, taskActive: false },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    expect(wrapper.find('.tool-item').exists()).toBe(false)
    expect(wrapper.get('.interaction-item').text()).toContain('提问')
    expect(wrapper.get('.interaction-item').text()).toContain('休息放空')
    expect(wrapper.get('.interaction-item').classes()).toContain('question')
  })

  it('keeps a failed ask_user tool visible when no question interaction was created', () => {
    const run = createRun('')
    run.transcript = [{
      id: 'tool-ask-failed',
      kind: 'Tool',
      status: 'Failed',
      title: 'ask_user',
      content: 'ask_user 失败',
      firstSequence: 1,
      lastSequence: 2,
      timestamp: new Date().toISOString(),
      input: null,
      output: 'Cannot read properties of null',
      interactionId: null,
      interactionMethod: null,
      interactionKind: null,
      interactionOptions: [],
    }]

    const wrapper = shallowMount(ConversationRun, {
      props: { run, currentRunId: run.id, viewMode: 'normal', needsInteraction: false, taskActive: false },
      global: { stubs: { VirtualList: VirtualListStub } },
    })

    const toolItem = wrapper.getComponent(ToolWorkItem)
    expect(toolItem.props('block')).toEqual(expect.objectContaining({
      title: 'ask_user',
      status: 'Failed',
      output: 'Cannot read properties of null',
    }))
  })
})
