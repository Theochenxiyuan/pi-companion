import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { shallowMount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import UiSelect from '@/components/ui/UiSelect.vue'
import ComposerActionMenu from '@/components/ComposerActionMenu.vue'
import ComposerPanel from '@/components/ComposerPanel.vue'
import { setLocale } from '@/i18n'

const styles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8')

afterEach(() => setLocale('zh-CN'))

function createProps(overrides: Record<string, unknown> = {}) {
  return {
    taskActive: false,
    hasCurrentTask: false,
    modeSelected: true,
    generalChat: false,
    attachments: [],
    localQueuedMessages: [],
    modelOptions: [{ value: 'provider/model', label: 'Model' }],
    thinkingLevelOptions: [{ value: 'high', label: 'High' }],
    skillOptions: [],
    workspaceGitChangeCount: 0,
    prompt: '',
    selectedModel: 'provider/model',
    selectedThinkingLevel: 'high' as const,
    selectedPermissionMode: 'standard' as const,
    ...overrides,
  }
}

describe('ComposerPanel options', () => {
  it('keeps the composer disabled until a conversation mode is selected', () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({ modeSelected: false, prompt: '尚未选择模式' }),
    })

    expect(wrapper.get('textarea').attributes('disabled')).toBeDefined()
    expect(wrapper.get('textarea').attributes('placeholder')).toBe('请先选择工作目录或直接对话')
    expect(wrapper.get('.composer-scope-badge').text()).toBe('请选择模式')
    expect(wrapper.get('.send-button').attributes('disabled')).toBeDefined()
  })

  it('allows a Direct Chat prompt after the mode is selected', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({ generalChat: true, prompt: '整理这些附件' }),
    })

    expect(wrapper.get('.composer-scope-badge').text()).toBe('隔离空间')
    expect(wrapper.findAllComponents(UiSelect)).toHaveLength(2)
    expect(wrapper.get('.send-button').attributes('disabled')).toBeUndefined()
    await wrapper.get('.send-button').trigger('click')
    expect(wrapper.emitted('submit')).toHaveLength(1)
  })

  it('allows an attachment-only task and previews images for vision models', async () => {
    const attachment = {
      path: 'C:\\Temp\\clipboard.png',
      displayName: 'clipboard.png',
      kind: '文件',
      isAvailable: true,
      previewDataUrl: 'data:image/png;base64,cHJldmlldw==',
    }
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        prompt: '',
        attachments: [attachment],
        selectedModelSupportsImages: false,
      }),
    })

    expect(wrapper.find('.draft-image-attachment').exists()).toBe(false)
    expect(wrapper.find('.draft-attachment img').exists()).toBe(false)
    expect(wrapper.get('.send-button').attributes('disabled')).toBeUndefined()

    await wrapper.setProps({ selectedModelSupportsImages: true })
    expect(wrapper.get('.draft-image-attachment img').attributes('src')).toBe(attachment.previewDataUrl)
    expect(styles).toContain('.draft-attachment.draft-image-attachment { position: relative; width: 56px; height: 56px;')

    await wrapper.get('.send-button').trigger('click')
    expect(wrapper.emitted('submit')).toHaveLength(1)
  })

  it('uses unlabeled compact selectors and requires confirmation before full access', async () => {
    setLocale('en-US')
    const wrapper = shallowMount(ComposerPanel, { props: createProps() })

    expect(wrapper.findAll('.composer-options label > span')).toHaveLength(0)
    const selectors = wrapper.findAllComponents(UiSelect)
    expect(selectors).toHaveLength(3)
    expect(selectors[2].props('options').map((option: { value: string; label: string; tone?: string }) => ({
      value: option.value,
      label: option.label,
      tone: option.tone,
    }))).toEqual([
      { value: 'read-only', label: 'Read only', tone: undefined },
      { value: 'standard', label: 'Standard access', tone: undefined },
      { value: 'full-access', label: 'Full access', tone: 'danger' },
    ])
    selectors[2].vm.$emit('update:modelValue', 'full-access')
    await wrapper.vm.$nextTick()
    expect(wrapper.emitted('requestFullAccess')).toHaveLength(1)
    expect(wrapper.emitted('update:selectedPermissionMode')).toBeUndefined()
    expect(styles).toContain('.composer-options .composer-thinking-option .app-select, .composer-options .composer-permission-option .app-select { width: max-content; }')
    expect(styles).not.toMatch(/composer-(?:thinking|permission)-option \.app-select \{ width: \d+px;/u)
    expect(styles).toContain('.composer .ui-textarea:focus-visible { outline: 0; }')
    expect(styles).toContain('.composer:focus-within { border-color: var(--color-focus-ring); box-shadow: 0 0 0 1px var(--color-focus-ring); }')
  })

  it('keeps Enter as a newline shortcut and uses Ctrl+Enter to add while active', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        taskActive: true,
        hasCurrentTask: true,
        prompt: '补充检查测试',
      }),
    })
    const textarea = wrapper.get('.composer > textarea')

    await textarea.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('submit')).toBeUndefined()

    await textarea.trigger('keydown', { key: 'Enter', ctrlKey: true })
    expect(wrapper.emitted('submit')).toHaveLength(1)
    expect(wrapper.get('.send-button').text()).toContain('加入')
  })

  it('keeps commands and skill names in separate completion levels', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        prompt: '/',
        skillOptions: [{
          name: 'find-skills',
          description: 'Find and install skills',
          location: '全局',
          manualOnly: false,
        }],
      }),
    })

    const rootSuggestions = wrapper.findAll('.composer-suggestions [role="option"]').map(item => item.text())
    expect(rootSuggestions.some(item =>
      item.includes('/skill:') &&
      item.includes('<技能名> <任务要求>') &&
      item.includes('手动调用一个当前可用的技能'))).toBe(true)
    expect(rootSuggestions.some(item =>
      item.includes('/name') &&
      item.includes('<新名称>') &&
      item.includes('新名称必填'))).toBe(true)
    expect(wrapper.text()).not.toContain('find-skills')

    await wrapper.setProps({ prompt: '/skill:find' })
    expect(wrapper.findAll('.composer-suggestions [role="option"]')).toHaveLength(1)
    expect(wrapper.get('.composer-suggestions [role="option"]').text()).toContain('find-skills')
    expect(wrapper.emitted('requestSkills')).toBeDefined()
  })

  it('keeps a concise syntax hint after selecting a command that needs arguments', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({ prompt: '/name ' }),
    })

    expect(wrapper.get('.composer-command-hint code').text()).toBe('/name <新名称>')
    expect(wrapper.get('.composer-command-hint').text()).toContain('输入任务的新名称，然后发送。')
    expect(wrapper.find('.composer-suggestions > p').exists()).toBe(false)

    await wrapper.setProps({ prompt: '/compact ' })
    expect(wrapper.get('.composer-command-hint code').text()).toBe('/compact [压缩要求]')
    expect(wrapper.get('.composer-command-hint').text()).toContain('可以直接发送')

    await wrapper.setProps({ prompt: '/skill:find-skills ' })
    expect(wrapper.get('.composer-command-hint code').text()).toBe('/skill:find-skills <任务要求>')
    expect(wrapper.find('.composer-suggestions > p').exists()).toBe(false)
    expect(styles).toContain('.composer-command-hint { display: grid; gap: 2px; padding: 7px 9px 8px;')
    expect(styles).toContain('font-size: var(--font-size-caption);')
  })

  it('reuses skill completion from the plus menu and preserves the existing draft as arguments', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        prompt: '帮我查找一个前端技能',
        skillOptions: [{
          name: 'find-skills',
          description: 'Find and install skills',
          location: '全局',
          manualOnly: false,
        }],
      }),
    })
    const actionMenu = wrapper.getComponent(ComposerActionMenu)

    actionMenu.vm.$emit('invokeSkill')
    await wrapper.vm.$nextTick()
    expect(wrapper.get('textarea').element.value).toBe('/skill:')
    expect(wrapper.emitted('requestSkills')).toHaveLength(1)

    await wrapper.get('.composer-suggestions [role="option"]').trigger('mousedown')
    expect(wrapper.get('textarea').element.value)
      .toBe('/skill:find-skills 帮我查找一个前端技能')
  })

  it('offers model values only after the model command is selected', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        prompt: '/model ',
        modelOptions: [
          { value: 'openai/gpt-5.4', label: 'GPT-5.4', group: 'OpenAI' },
          { value: 'anthropic/claude-opus-4-6', label: 'Claude Opus 4.6', group: 'Anthropic' },
        ],
      }),
    })

    const modelSuggestions = wrapper.findAll('.composer-suggestions [role="option"]').map(item => item.text())
    expect(modelSuggestions[0]).toContain('GPT-5.4')
    expect(modelSuggestions[0]).toContain('openai/gpt-5.4')
    expect(modelSuggestions[1]).toContain('Claude Opus 4.6')
    expect(modelSuggestions[1]).toContain('anthropic/claude-opus-4-6')
  })

  it('expands the prompt editor to half the chat height and restores it from beside the add button', async () => {
    const wrapper = shallowMount(ComposerPanel, { props: createProps() })
    const composerOptions = wrapper.get('.composer-options')
    const addMenu = composerOptions.getComponent(ComposerActionMenu)
    const expandButton = wrapper.get('button[aria-label="展开输入框"]')

    expect(addMenu.element.nextElementSibling).toBe(expandButton.element)
    expect(wrapper.get('.composer-area').classes()).not.toContain('composer-expanded')
    expect(expandButton.attributes('aria-pressed')).toBe('false')

    await expandButton.trigger('click')

    expect(wrapper.get('.composer-area').classes()).toContain('composer-expanded')
    expect(wrapper.get('button[aria-label="还原输入框"]').attributes('aria-pressed')).toBe('true')
    expect(wrapper.find('.composer-expand-button svg').exists()).toBe(true)
    expect(styles).toContain('.composer-area.composer-expanded .composer textarea { height: 50vh; min-height: 50vh; max-height: 50vh; }')

    await wrapper.get('button[aria-label="还原输入框"]').trigger('click')

    expect(wrapper.get('.composer-area').classes()).not.toContain('composer-expanded')
    expect(wrapper.get('button[aria-label="展开输入框"]').attributes('aria-pressed')).toBe('false')
  })

  it('offers steer, follow-up, and icon actions for each local item', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        taskActive: true,
        hasCurrentTask: true,
        localQueuedMessages: [{
          id: 'message-1',
          message: '先检查失败测试',
          createdAt: '2026-07-23T10:00:00Z',
        }],
      }),
    })
    const actions = wrapper.findAll('.local-queue-actions button')

    expect(actions.map(button => button.text())).toEqual(['立即调整', '定为后续', '', ''])
    expect(wrapper.get('button[aria-label="编辑"]').find('svg').exists()).toBe(true)
    expect(wrapper.get('button[aria-label="取消"]').find('svg').exists()).toBe(true)
    await actions[0].trigger('click')
    await actions[1].trigger('click')
    await wrapper.get('button[aria-label="取消"]').trigger('click')
    expect(wrapper.emitted('dispatchLocalMessage')).toEqual([
      ['message-1', 'steer'],
      ['message-1', 'follow-up'],
    ])
    expect(wrapper.emitted('removeLocalMessage')).toEqual([['message-1']])
  })

  it('turns a retained item into a new run after the task ends', () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        hasCurrentTask: true,
        localQueuedMessages: [{
          id: 'message-1',
          message: '继续整理结果',
          createdAt: '2026-07-23T10:00:00Z',
        }],
      }),
    })

    expect(wrapper.findAll('.local-queue-actions button').map(button => button.text()))
      .toEqual(['发送新一轮', '', ''])
  })

  it('disables steer and follow-up for items with attachments and supports reordering', async () => {
    const wrapper = shallowMount(ComposerPanel, {
      props: createProps({
        taskActive: true,
        hasCurrentTask: true,
        localQueuedMessages: [{
          id: 'message-1',
          message: '带附件的未来任务',
          createdAt: '2026-07-23T10:00:00Z',
          attachments: [{ path: 'C:\\one.txt', displayName: 'one.txt', kind: '文件', isAvailable: true }],
        }, {
          id: 'message-2',
          message: '另一个任务',
          createdAt: '2026-07-23T10:01:00Z',
          attachments: [],
        }],
      }),
    })
    const firstItem = wrapper.findAll('.local-queue-item')[0]
    const actions = firstItem.findAll('.local-queue-actions button')

    expect(actions[0].attributes('disabled')).toBeDefined()
    expect(actions[1].attributes('disabled')).toBeDefined()
    expect(firstItem.text()).toContain('1 个附件')
    await firstItem.get('.local-queue-drag-handle').trigger('dragstart')
    await wrapper.findAll('.local-queue-item')[1].trigger('drop')
    expect(wrapper.emitted('moveLocalMessage')).toEqual([['message-1', 1]])
  })
})
