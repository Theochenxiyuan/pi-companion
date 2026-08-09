import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import ScheduledTaskEditorDialog from './ScheduledTaskEditorDialog.vue'
import type { TaskTemplate } from '@/types/bridge'

const templates: TaskTemplate[] = [
  {
    id: 'template-fixed',
    name: '固定工作区检查',
    prompt: '检查工作区并给出摘要',
    targetKind: 'Workspace',
    workspaceId: 'workspace-1',
    model: 'provider/model-1',
    thinkingLevel: 'high',
    permissionMode: 'read-only',
    isPinned: false,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
  {
    id: 'template-context',
    name: '当前上下文检查',
    prompt: '检查当前上下文',
    targetKind: 'CurrentContext',
    workspaceId: null,
    model: null,
    thinkingLevel: null,
    permissionMode: null,
    isPinned: false,
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
]

const props = {
  scheduledTask: null,
  templates,
  workspaces: [{
    id: 'workspace-1',
    name: 'Companion',
    workingDirectory: 'D:\work',
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
    taskCount: 1,
    hasActiveTask: false,
  }],
  modelOptions: [{ value: 'provider/model-1', label: 'Model One', thinkingLevels: ['low', 'high'] }],
}

describe('ScheduledTaskEditorDialog', () => {
  it('uses the shared dialog surface and exposes source selection states', async () => {
    const wrapper = mount(ScheduledTaskEditorDialog, { attachTo: document.body, props })
    const dialog = document.body.querySelector('.scheduled-task-editor')
    const sourceButtons = wrapper.findAll('.scheduled-source-tabs button')

    expect(dialog?.classList.contains('task-template-editor')).toBe(true)
    expect(wrapper.find('.task-template-dialog-header').exists()).toBe(true)
    expect(wrapper.find('.task-template-editor-body').exists()).toBe(true)
    expect(sourceButtons[0]!.attributes('aria-pressed')).toBe('true')
    expect(sourceButtons[1]!.attributes('aria-pressed')).toBe('false')
    expect(sourceButtons.every(button => button.classes('ui-button--ghost'))).toBe(true)
    expect(wrapper.get('.scheduled-template-fill button').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('.scheduled-preference-grid > label').map(label => label.get('span').text()))
      .toEqual(['运行位置', '权限', '模型', '推理等级'])
    expect(wrapper.find('.scheduled-timezone-note').text()).toContain('自动设置')
    expect(wrapper.findAll('input')).toHaveLength(2)

    await sourceButtons[1]!.trigger('click')
    expect(sourceButtons[0]!.attributes('aria-pressed')).toBe('false')
    expect(sourceButtons[1]!.attributes('aria-pressed')).toBe('true')
    wrapper.unmount()
  })

  it('uses template selection only as a fill action for custom tasks', async () => {
    const wrapper = mount(ScheduledTaskEditorDialog, { attachTo: document.body, props })
    await wrapper.findAll('input')[0]!.setValue('每日检查')
    await wrapper.get('.scheduled-template-fill select').setValue('template-fixed')
    await wrapper.get('.scheduled-template-fill button').trigger('click')
    await nextTick()

    expect(wrapper.get('textarea').element.value).toBe('检查工作区并给出摘要')
    expect(wrapper.get('select').element.value).toBe('template-fixed')
    await wrapper.get('form').trigger('submit')

    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({
      name: '每日检查',
      templateId: null,
      prompt: '检查工作区并给出摘要',
      targetKind: 'Workspace',
      workspaceId: 'workspace-1',
      model: 'provider/model-1',
      thinkingLevel: 'high',
      permissionMode: 'read-only',
    }))
  })

  it('persists only a template relationship in linked mode', async () => {
    const wrapper = mount(ScheduledTaskEditorDialog, { attachTo: document.body, props })
    await wrapper.findAll('input')[0]!.setValue('关联检查')
    await wrapper.findAll('.scheduled-source-tabs button')[1]!.trigger('click')
    const linkedSelect = wrapper.get('.scheduled-linked-fields select')

    expect(linkedSelect.findAll('option').map(option => option.attributes('value')))
      .toEqual(['', 'template-fixed'])
    await linkedSelect.setValue('template-fixed')
    await wrapper.get('form').trigger('submit')

    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({
      name: '关联检查',
      templateId: 'template-fixed',
      prompt: null,
      targetKind: null,
      workspaceId: null,
      model: null,
      thinkingLevel: null,
      permissionMode: null,
    }))
  })
})
