import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { TaskTemplate } from '@/types/bridge'
import TaskTemplatePicker from './TaskTemplatePicker.vue'

const timestamp = '2026-08-04T00:00:00.000Z'
const systemTemplate: TaskTemplate = {
  id: 'builtin:check',
  name: '系统检查',
  prompt: '检查这个工程',
  targetKind: 'Workspace',
  workspaceId: null,
  model: null,
  thinkingLevel: null,
  permissionMode: 'read-only',
  isPinned: true,
  createdAt: timestamp,
  updatedAt: timestamp,
  isBuiltIn: true,
}
const userTemplate: TaskTemplate = {
  ...systemTemplate,
  id: 'template-1',
  name: '我的检查',
  isBuiltIn: false,
}

describe('TaskTemplatePicker', () => {
  it('stays a lightweight apply surface and links to full management', async () => {
    const wrapper = mount(TaskTemplatePicker, {
      props: { templates: [systemTemplate, userTemplate], workspaces: [] },
    })

    expect(wrapper.text()).toContain('选择模板后会填入输入框，不会立即运行。')
    expect(wrapper.findAll('.task-template-main')).toHaveLength(2)
    expect(wrapper.text()).not.toContain('删除模板')
    expect(wrapper.text()).not.toContain('新建模板')

    await wrapper.findAll('.task-template-main')[0]!.trigger('click')
    expect(wrapper.emitted('apply')).toEqual([[systemTemplate]])

    await wrapper.findAll('button').find(button => button.text() === '管理任务模板')!.trigger('click')
    expect(wrapper.emitted('manage')).toHaveLength(1)
  })
})
