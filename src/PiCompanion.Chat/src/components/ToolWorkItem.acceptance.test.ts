import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { describe, expect, it } from 'vitest'
import ToolWorkItem from './ToolWorkItem.vue'
import type { CommandExecutionEvidence, TranscriptBlock } from '@/types/bridge'
import { setLocale } from '@/i18n'

describe('ToolWorkItem', () => {
  it('shows a bash command inline and its execution evidence inside the tool details', () => {
    const block: TranscriptBlock = {
      id: 'tool-bash-1', kind: 'Tool', status: 'Completed', title: 'bash', content: 'bash 完成',
      firstSequence: 1, lastSequence: 2, timestamp: new Date().toISOString(),
      input: 'dotnet test', output: '执行完成',
      interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
    }
    const command: CommandExecutionEvidence = {
      id: 'command-1', toolCallId: 'bash-1', command: 'dotnet test --configuration Release',
      workingDirectory: 'D:\\work', startedAt: new Date().toISOString(), durationMilliseconds: 1250,
      exitCode: 0, cancelled: false, timedOut: false, outputSummary: '45 tests passed',
      fullOutputPath: null, isTest: true, detectedFramework: 'dotnet', status: 'Passed',
    }

    const wrapper = mount(ToolWorkItem, { props: { block, command, open: true } })

    expect(wrapper.get('summary .work-result').text()).toBe('dotnet test --configuration Release')
    expect(wrapper.get('.tool-detail').text()).toContain('命令')
    expect(wrapper.get('.tool-detail').text()).toContain('退出码 0')
    expect(wrapper.get('.tool-detail').text()).toContain('1.3 s')
    expect(wrapper.get('.tool-detail').text()).toContain('45 tests passed')
    expect(wrapper.get('.tool-detail').text()).toContain('D:\\work')
  })

  it('renders web search sources as safe clickable links', async () => {
    const block: TranscriptBlock = {
      id: 'tool-search-1', kind: 'WebSearch', status: 'Completed', title: 'web_search', content: 'web_search 完成',
      firstSequence: 1, lastSequence: 2, timestamp: new Date().toISOString(),
      input: 'Pi Companion', output: '## Sources\n1. [OpenAI](https://openai.com/)',
      interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
    }
    const wrapper = mount(ToolWorkItem, { props: { block, open: true } })

    expect(wrapper.classes()).toContain('web-search-item')
    expect(wrapper.find('.work-glyph svg circle').exists()).toBe(true)
    expect(wrapper.get('.work-label').text()).toBe('网络搜索')
    expect(wrapper.get('summary .work-result').text()).toBe('Pi Companion')
    expect(wrapper.get('.tool-detail > div:first-child > span').text()).toBe('搜索内容')
    expect(wrapper.get('.tool-detail > div:first-child > pre').text()).toBe('Pi Companion')
    expect(wrapper.get('.tool-detail > div:last-child > span').text()).toBe('搜索结果与来源')
    await wrapper.get('.web-search-result a').trigger('click')
    expect(wrapper.emitted('openExternalLink')).toEqual([['https://openai.com/']])
  })

  it('does not present legacy execution status text as web-search output', () => {
    const block: TranscriptBlock = {
      id: 'tool-search-status', kind: 'WebSearch', status: 'Completed', title: 'web_search', content: 'web_search 完成',
      firstSequence: 1, lastSequence: 2, timestamp: new Date().toISOString(),
      input: null, output: '执行完成',
      interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
    }
    const wrapper = mount(ToolWorkItem, { props: { block, open: true } })

    expect(wrapper.find('.web-search-result').exists()).toBe(false)
    expect(wrapper.find('.tool-detail').exists()).toBe(false)
    expect(wrapper.find('summary .work-chevron').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('执行完成')
  })

  it('localizes ask_user when its tool record must remain visible', () => {
    const block: TranscriptBlock = {
      id: 'tool-question-failed', kind: 'Tool', status: 'Failed', title: 'ask_user', content: 'ask_user 失败',
      firstSequence: 1, lastSequence: 1, timestamp: new Date().toISOString(),
      input: null, output: '参数校验失败',
      interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
    }

    const wrapper = mount(ToolWorkItem, { props: { block } })

    expect(wrapper.get('.work-label').text()).toBe('向用户提问')
    expect(wrapper.text()).not.toContain('ask_user')
  })

  it('localizes the available-skills tool without changing its protocol name', async () => {
    const block: TranscriptBlock = {
      id: 'tool-list-skills', kind: 'Tool', status: 'Completed', title: 'list_available_skills',
      content: 'list_available_skills 已完成',
      firstSequence: 1, lastSequence: 1, timestamp: new Date().toISOString(),
      input: null, output: 'find-skills',
      interactionId: null, interactionMethod: null, interactionKind: null, interactionOptions: [],
    }
    const wrapper = mount(ToolWorkItem, { props: { block } })

    try {
      expect(wrapper.get('.work-label').text()).toBe('列出可用技能')
      expect(wrapper.get('.work-label').text()).not.toContain('list_available_skills')

      setLocale('en-US')
      await nextTick()
      expect(wrapper.get('.work-label').text()).toBe('List available skills')
    } finally {
      setLocale('zh-CN')
    }
  })
})
