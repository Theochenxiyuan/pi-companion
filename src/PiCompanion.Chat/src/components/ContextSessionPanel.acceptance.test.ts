import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ContextSessionPanel from './ContextSessionPanel.vue'
import type { PiModelInfo, SessionStatisticsSnapshot } from '@/types/bridge'

const model: PiModelInfo = {
  provider: 'openai-codex',
  id: 'gpt-test',
  name: 'GPT Test',
  reasoning: true,
  contextWindow: 100000,
  input: ['text'],
  thinkingLevels: ['high'],
}

const update: SessionStatisticsSnapshot = {
  requestId: 'stats-1',
  taskId: 'task-1',
  available: true,
  statistics: {
    sessionId: 'session-12345678',
    sessionFile: null,
    userMessages: 18,
    assistantMessages: 132,
    toolCalls: 93,
    toolResults: 91,
    totalMessages: 150,
    inputTokens: 16125,
    outputTokens: 127,
    cacheReadTokens: 24576,
    cacheWriteTokens: 0,
    totalTokens: 40828,
    cost: 0,
    contextUsage: { tokens: 25000, contextWindow: 200000, percent: 12.5 },
  },
  error: null,
}

describe('ContextSessionPanel', () => {
  it('renders RPC statistics and recomputes pressure using the selected model capacity', async () => {
    const wrapper = mount(ContextSessionPanel, {
      props: {
        taskId: 'task-1',
        taskTitle: '实现上下文面板',
        selectedModel: model,
        selectedModelReference: 'openai-codex/gpt-test',
        sessionModelReference: 'openai-codex/gpt-session',
        update,
        loading: false,
        manualLoadAvailable: false,
      },
    })

    expect(wrapper.text()).toContain('25,000 / 100,000')
    expect(wrapper.text()).toContain('已使用 25.0%')
    expect(wrapper.text()).toContain('按当前选中模型容量计算')
    expect(wrapper.text()).toContain('150')
    expect(wrapper.text()).toContain('93')
    expect(wrapper.text()).toContain('60.4%')

    await wrapper.setProps({
      selectedModel: { ...model, id: 'gpt-small', name: 'GPT Small', contextWindow: 50000 },
      selectedModelReference: 'openai-codex/gpt-small',
    })
    expect(wrapper.text()).toContain('25,000 / 50,000')
    expect(wrapper.text()).toContain('已使用 50.0%')

    await wrapper.get('.context-session-heading button').trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
    await wrapper.setProps({ loading: true })
    expect(wrapper.attributes('aria-busy')).toBe('true')
    expect(wrapper.get('.context-session-heading button').classes()).toContain('loading')
    expect(wrapper.get('.context-session-heading button').attributes()).toHaveProperty('disabled')
  })

  it('shows unavailable and unknown context values without inventing statistics', () => {
    const wrapper = mount(ContextSessionPanel, {
      props: {
        taskId: 'task-1',
        taskTitle: '未运行任务',
        selectedModel: model,
        selectedModelReference: 'openai-codex/gpt-test',
        sessionModelReference: 'openai-codex/gpt-test',
        update: { requestId: 'stats-2', taskId: 'task-1', available: false, statistics: null, error: null },
        loading: false,
        manualLoadAvailable: true,
      },
    })

    expect(wrapper.text()).toContain('— / 100,000')
    expect(wrapper.text()).toContain('当前没有可读取的 Pi Session')
    expect(wrapper.text()).toContain('点击右上角刷新，读取历史 Pi Session')
    expect(wrapper.text()).not.toContain('Session 累计')
  })
})
