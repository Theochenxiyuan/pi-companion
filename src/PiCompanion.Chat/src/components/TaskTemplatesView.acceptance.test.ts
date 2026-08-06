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
  origin: 'Agent',
  sourceTaskId: '11111111-1111-1111-1111-111111111111',
  sourceRunId: '22222222-2222-2222-2222-222222222222',
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
    const userPreferenceRows = wrapper.findAll('.task-template-management-card')[1]!
      .findAll('.task-template-preferences > div')
    expect(userPreferenceRows.map(row => [row.get('dt').text(), row.get('dd').text()])).toEqual([
      ['任务环境', '工作区（使用时选择）'],
    ])
    expect(wrapper.find('.task-template-management-card > header em').text()).toBe('AI创建')

    await wrapper.findAll('button').find(button => button.text() === '新建模板')!.trigger('click')
    expect(wrapper.emitted('create')).toHaveLength(1)

    const userCard = wrapper.findAll('.task-template-management-card')[1]!
    expect(userCard.get('.task-template-apply-button').classes()).toContain('ui-button--secondary')
    expect(userCard.get('.danger-action').classes()).toContain('ui-button--ghost')
    await userCard.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    await userCard.findAll('button').find(button => button.text() === '复制')!.trigger('click')
    await userCard.findAll('button').find(button => button.text() === '删除')!.trigger('click')
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
