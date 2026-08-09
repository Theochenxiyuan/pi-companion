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
  })

  it('renders pointer actions with explicit variants and accessible switch state', () => {
    const wrapper = mount(ScheduledTasksView, {
      props: { ...baseProps, scheduledTasks: [scheduledTask] },
    })
    const actionButtons = wrapper.findAll('.scheduled-task-card footer button')

    expect(actionButtons).toHaveLength(4)
    expect(actionButtons.slice(0, 3).every(button => button.classes('ui-button--secondary'))).toBe(true)
    expect(actionButtons[3]!.classes('ui-button--ghost')).toBe(true)
    expect(actionButtons[3]!.classes('danger-action')).toBe(true)
    expect(wrapper.get('[role="switch"]').attributes('aria-label')).toBe('暂停定时任务')
  })
})
