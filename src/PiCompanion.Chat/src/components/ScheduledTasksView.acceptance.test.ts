import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ScheduledTasksView from './ScheduledTasksView.vue'
import type { ScheduledTask } from '@/types/bridge'

const scheduledTask: ScheduledTask = {
  id: 'schedule-1',
  name: '每日项目摘要',
  isEnabled: true,
  templateId: null,
  prompt: '总结项目进展',
  targetKind: 'GeneralChat',
  workspaceId: null,
  model: null,
  thinkingLevel: null,
  permissionMode: null,
  frequency: 'Daily',
  localStartAt: '2026-08-08T09:00:00',
  daysOfWeek: 0,
  timeZoneId: 'Asia/Shanghai',
  nextRunAt: '2026-08-09T01:00:00Z',
  createdAt: '2026-08-08T00:00:00Z',
  updatedAt: '2026-08-08T00:00:00Z',
  lastOccurrence: {
    id: 'occurrence-1',
    scheduledFor: '2026-08-08T01:00:00Z',
    status: 'Enqueued',
    taskId: 'task-1',
    runId: 'run-1',
    error: null,
    createdAt: '2026-08-08T01:00:00Z',
    updatedAt: '2026-08-08T01:00:00Z',
  },
}

const baseProps = {
  templates: [],
  workspaces: [],
  sidebarCollapsed: false,
}

describe('ScheduledTasksView', () => {
  it('uses the shared secondary action treatment in the empty state', () => {
    const wrapper = mount(ScheduledTasksView, {
      props: { ...baseProps, scheduledTasks: [] },
    })
    const createButtons = wrapper.findAll('.scheduled-task-create')

    expect(createButtons).toHaveLength(2)
    expect(createButtons.every(button => button.classes('ui-button--secondary'))).toBe(true)
    expect(createButtons.every(button => button.classes('ui-button--md'))).toBe(true)
    expect(wrapper.get('.scheduled-task-empty h1').text()).toBe('还没有定时任务')
    expect(wrapper.find('.management-location small').exists()).toBe(false)
  })

  it('lists tasks by row with counts in the subtitle', () => {
    const wrapper = mount(ScheduledTasksView, {
      props: { ...baseProps, scheduledTasks: [scheduledTask] },
    })

    expect(wrapper.get('.management-location small').text()).toBe('1 个运行中')
    expect(wrapper.findAll('.scheduled-task-row')).toHaveLength(1)
    expect(wrapper.get('.scheduled-task-list').attributes('role')).toBe('list')
    expect(wrapper.get('.scheduled-task-row').attributes('role')).toBe('listitem')
    expect(wrapper.get('.scheduled-task-frequency').text()).toBe('每天 09:00')
    expect(wrapper.get('.scheduled-task-result').text()).toBe('最近一次已创建任务')
    expect(wrapper.get('[role="switch"]').attributes('aria-label')).toBe('暂停定时任务')
    expect(wrapper.get('.scheduled-task-actions .ui-button--secondary').text()).toBe('立即运行')
  })

  it('keeps task actions reachable while a scheduled task request is pending', async () => {
    const wrapper = mount(ScheduledTasksView, {
      props: {
        ...baseProps,
        scheduledTasks: [scheduledTask],
        pendingAction: { taskId: scheduledTask.id, action: 'run' },
      },
    })

    expect(wrapper.get('.scheduled-task-row').attributes('aria-busy')).toBe('true')
    expect(wrapper.get('[role="switch"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('.scheduled-task-actions .ui-button--secondary').attributes('disabled'))
      .toBeDefined()

    await wrapper.get('.scheduled-task-more').trigger('click')
    const items = wrapper.findAll('[role="menuitem"]')
    expect(items.map(item => item.text())).toEqual(['打开任务', '编辑', '删除'])
    expect(items[0]!.attributes('disabled')).toBeUndefined()
    expect(items[1]!.attributes('disabled')).toBeDefined()
    expect(items[2]!.attributes('disabled')).toBeDefined()
  })

  it('opens a task from the row menu', async () => {
    const wrapper = mount(ScheduledTasksView, {
      props: { ...baseProps, scheduledTasks: [scheduledTask] },
    })

    await wrapper.get('.scheduled-task-more').trigger('click')
    await wrapper.findAll('[role="menuitem"]')[0]!.trigger('click')
    expect(wrapper.emitted('openTask')).toEqual([['task-1']])
  })

  it('shows a loading skeleton until scheduled tasks arrive', () => {
    const wrapper = mount(ScheduledTasksView, {
      props: { ...baseProps, scheduledTasks: [], loading: true },
    })

    const skeleton = wrapper.get('.management-loading-skeleton')
    expect(skeleton.attributes('role')).toBe('status')
    expect(skeleton.attributes('aria-label')).toBe('正在读取定时任务…')
    expect(wrapper.find('.scheduled-task-empty').exists()).toBe(false)
  })
})
