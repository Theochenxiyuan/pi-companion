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

async function openMenu(card: { get: (selector: string) => { trigger: (event: string) => Promise<void> } }) {
  await card.get('.task-template-more').trigger('click')
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
    expect(wrapper.get('.management-location small').text())
      .toBe('1 个我的模板 · 1 个系统模板')
    expect(wrapper.get('.task-template-management-search').element.tagName).toBe('LABEL')
    expect(wrapper.get('.task-template-management-search').classes()).toContain('management-search')
    expect(wrapper.get('.task-template-create').classes()).toContain('ui-button--secondary')
    expect(wrapper.findAll('.task-template-management-card')).toHaveLength(2)
    const cards = wrapper.findAll('.task-template-management-card')
    expect(cards[0]!.text()).toContain('发布前检查')
    expect(cards[1]!.text()).toContain('分析工程')
    expect(wrapper.findAll('.task-template-target').map(target => target.text()))
      .toEqual(['工作区（使用时选择）', '工作区（使用时选择）'])
    expect(wrapper.find('.task-template-management-card > header em').text()).toBe('AI创建')

    await wrapper.findAll('button').find(button => button.text() === '新建模板')!.trigger('click')
    expect(wrapper.emitted('create')).toHaveLength(1)

    const userCard = wrapper.findAll('.task-template-management-card')[0]!
    const systemCard = wrapper.findAll('.task-template-management-card')[1]!
    expect(userCard.get('.task-template-apply-button').classes()).toContain('ui-button--secondary')
    await userCard.get('.task-template-apply-button').trigger('click')

    await openMenu(userCard)
    const userItems = userCard.findAll('[role="menuitem"]')
    expect(userItems.map(item => item.text())).toEqual(['编辑', '复制', '删除'])
    await userItems[0]!.trigger('click')
    await openMenu(userCard)
    await userCard.findAll('[role="menuitem"]')[1]!.trigger('click')
    await openMenu(userCard)
    await userCard.findAll('[role="menuitem"]')[2]!.trigger('click')

    await openMenu(systemCard)
    const systemItems = systemCard.findAll('[role="menuitem"]')
    expect(systemItems.map(item => item.text())).toEqual(['复制为我的模板'])
    await systemItems[0]!.trigger('click')

    expect(wrapper.emitted('edit')).toEqual([[userTemplate]])
    expect(wrapper.emitted('duplicate')).toEqual([[userTemplate], [systemTemplate]])
    expect(wrapper.emitted('delete')).toEqual([[userTemplate]])
    expect(wrapper.emitted('apply')).toEqual([[userTemplate]])

    await wrapper.get('input[type="search"]').setValue('不存在')
    expect(wrapper.findAll('.task-template-management-card')).toHaveLength(0)
    expect(wrapper.text()).toContain('未找到匹配的模板。')
  })

  it('shows a compact empty row for my templates', () => {
    const wrapper = mount(TaskTemplatesView, {
      props: {
        templates: [systemTemplate],
        workspaces: [],
        sidebarCollapsed: false,
      },
    })

    const empty = wrapper.get('.task-template-management-empty')
    expect(empty.text()).toContain('还没有我的模板。')
    expect(empty.get('button').text()).toBe('新建模板')
  })

  it('shows a loading skeleton until templates arrive', () => {
    const wrapper = mount(TaskTemplatesView, {
      props: {
        templates: [],
        workspaces: [],
        sidebarCollapsed: false,
        loading: true,
      },
    })

    const skeleton = wrapper.get('.management-loading-skeleton')
    expect(skeleton.attributes('role')).toBe('status')
    expect(skeleton.attributes('aria-label')).toBe('正在读取任务模板…')
    expect(wrapper.find('.task-template-management-card').exists()).toBe(false)
  })
})
