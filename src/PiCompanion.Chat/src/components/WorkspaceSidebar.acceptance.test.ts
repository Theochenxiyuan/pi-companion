import { mount, shallowMount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { setLocale } from '@/i18n'
import type { TaskHistoryEntry } from '@/types/bridge'
import WorkspaceSidebar from './WorkspaceSidebar.vue'

const completedTask: TaskHistoryEntry = {
  id: 'task-1',
  runId: 'run-1',
  title: 'Silksong Release Date',
  workingDirectory: 'D:\\work',
  scopeKind: 'Workspace',
  status: 'Completed',
  statusText: '已完成',
  summary: '',
  updatedAt: '2026-07-24T12:00:00.000Z',
  deletedAt: null,
}

afterEach(() => setLocale('zh-CN'))

describe('WorkspaceSidebar', () => {
  it('shows workspace presentation and the latest summary immediately instead of a native title', async () => {
    const task = {
      ...completedTask,
      workspaceId: 'workspace-1',
      summary: 'Implemented workspace customization and verified the migration.',
    }
    const wrapper = mount(WorkspaceSidebar, {
      props: {
        recentTasks: [task],
        workspaces: [{
          id: 'workspace-1',
          name: 'Companion Core',
          displayName: 'Companion Core',
          workingDirectory: 'D:\\work',
          createdAt: '2026-07-24T10:00:00.000Z',
          updatedAt: '2026-07-24T12:00:00.000Z',
          taskCount: 1,
          hasActiveTask: false,
          iconKey: 'code',
          colorKey: 'violet',
        }],
        view: 'chat',
        width: 232,
      },
      global: { stubs: { Teleport: true } },
    })

    const taskButton = wrapper.get('.history-item')
    expect(taskButton.attributes('title')).toBeUndefined()
    expect(taskButton.get('.history-state').text()).toBe('Companion Core')
    expect(taskButton.find('.history-status').exists()).toBe(false)
    expect(wrapper.find('.recent-task-hover-card').exists()).toBe(false)

    await taskButton.trigger('focus')
    expect(wrapper.find('.recent-task-hover-card').exists()).toBe(false)

    await taskButton.trigger('pointerenter')

    const card = wrapper.get('.recent-task-hover-card')
    expect(card.text()).toContain('Companion Core')
    expect(card.text()).toContain('D:\\work')
    expect(card.get('.recent-task-hover-progress').text()).toContain('最新进度')
    expect(card.get('.recent-task-hover-progress').text()).toContain('最新进度已完成')
    expect(card.get('.recent-task-hover-progress').text()).not.toContain('最近一轮')
    expect(card.text()).toContain(task.summary)
    expect(card.get('.workspace-icon-visual').classes()).toContain('workspace-icon-color-violet')
    wrapper.unmount()
  })

  it('localizes recent and selected task status text', () => {
    setLocale('en-US')
    const wrapper = shallowMount(WorkspaceSidebar, {
      props: {
        recentTasks: [completedTask],
        selectedHistoryTask: completedTask,
        recentTaskSubtitle: 'latest-run',
        view: 'chat',
        width: 232,
      },
    })

    expect(wrapper.findAll('.history-state small').map(item => item.text())).toEqual([
      'Latest: Completed',
      'Latest: Completed',
    ])
    expect(wrapper.text()).not.toContain('已完成')
  })

  it('keeps other tasks selectable while the focused task is active', async () => {
    const runningTask: TaskHistoryEntry = {
      ...completedTask,
      id: 'task-running',
      runId: 'run-running',
      title: 'Active task',
      status: 'Running',
      statusText: '运行中',
    }
    const wrapper = shallowMount(WorkspaceSidebar, {
      props: {
        recentTasks: [runningTask, completedTask],
        currentTaskId: runningTask.id,
        view: 'chat',
        width: 232,
      },
    })

    const completedButton = wrapper.findAll('.history-item')
      .find(button => button.text().includes(completedTask.title))
    expect(completedButton).toBeDefined()
    expect(completedButton!.attributes('aria-disabled')).toBeUndefined()

    await completedButton!.trigger('click')

    expect(wrapper.emitted('selectTask')).toContainEqual([completedTask.id])
  })
})
