import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { TaskTemplate } from '@/types/bridge'
import TaskTemplatesView from './TaskTemplatesView.vue'

const timestamp = '2026-08-05T00:00:00.000Z'
const systemTemplate: TaskTemplate = {
  id: 'builtin:review',
  name: '分析工程',
  prompt: '检查工程结构并总结主要模块',
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
  name: '发布前检查',
  prompt: '检查发布风险并列出待办',
  model: 'openai-codex/gpt-5.6-sol',
  thinkingLevel: 'high',
  isBuiltIn: false,
}

describe('TaskTemplatesView', () => {
  it('owns template discovery and all management actions', async () => {
    const wrapper = mount(TaskTemplatesView, {
      props: {
        templates: [systemTemplate, userTemplate],
        workspaces: [],
        sidebarCollapsed: false,
      },
    })

    expect(wrapper.get('.management-location strong').text()).toBe('任务模板')
    expect(wrapper.get('.task-template-management-search').element.tagName).toBe('LABEL')
    expect(wrapper.get('.task-template-management-search').classes()).toContain('management-search')
    expect(wrapper.get('.task-template-create').classes()).toContain('ui-button--secondary')
    expect(wrapper.findAll('.task-template-management-card')).toHaveLength(2)
    expect(wrapper.text()).toContain('模型：openai-codex/gpt-5.6-sol')
    expect(wrapper.text()).toContain('推理：high')

    await wrapper.findAll('button').find(button => button.text() === '新建模板')!.trigger('click')
    expect(wrapper.emitted('create')).toHaveLength(1)

    const userCard = wrapper.findAll('.task-template-management-card')[1]!
    expect(userCard.get('.task-template-apply-button').classes()).toContain('ui-button--secondary')
    expect(userCard.get('.danger-action').classes()).toContain('ui-button--ghost')
    await userCard.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    await userCard.findAll('button').find(button => button.text() === '复制模板')!.trigger('click')
    await userCard.findAll('button').find(button => button.text() === '删除模板')!.trigger('click')
    await userCard.findAll('button').find(button => button.text() === '使用模板')!.trigger('click')
    expect(wrapper.emitted('edit')).toEqual([[userTemplate]])
    expect(wrapper.emitted('duplicate')).toEqual([[userTemplate]])
    expect(wrapper.emitted('delete')).toEqual([[userTemplate]])
    expect(wrapper.emitted('apply')).toEqual([[userTemplate]])

    await wrapper.get('input[type="search"]').setValue('不存在')
    expect(wrapper.findAll('.task-template-management-card')).toHaveLength(0)
    expect(wrapper.text()).toContain('未找到匹配的模板。')
  })
})
