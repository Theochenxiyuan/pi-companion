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

afterEach(() => {
  setLocale('zh-CN')
  window.localStorage.removeItem('pi-companion:sidebar-task-list-mode')
  window.localStorage.removeItem('pi-companion:sidebar-task-status-filter')
  window.localStorage.removeItem('pi-companion:sidebar-collapsed-workspaces')
})

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

  it('switches to workspace grouping with compact task rows and mode-specific hover cards', async () => {
    const workspaceTask: TaskHistoryEntry = {
      ...completedTask,
      id: 'task-ws',
      title: 'Refactor sidebar workspace grouping and hover behavior',
      workspaceId: 'workspace-1',
      summary: 'Grouped sidebar tasks by workspace with compact single-line rows.',
      updatedAt: '2026-07-24T13:00:00.000Z',
    }
    const olderHistoryTask: TaskHistoryEntry = {
      ...completedTask,
      id: 'task-older',
      title: 'Direct chat scratchpad note',
      scopeKind: 'GeneralChat',
      workingDirectory: 'C:\\Users\\test\\AppData\\Local\\PiCompanion\\GeneralChat',
      summary: 'Saved a quick note in Direct Chat.',
      updatedAt: '2026-07-24T11:00:00.000Z',
    }
    const wrapper = mount(WorkspaceSidebar, {
      props: {
        recentTasks: [workspaceTask],
        historyTasks: [workspaceTask, olderHistoryTask],
        selectedHistoryTask: olderHistoryTask,
        currentTaskId: olderHistoryTask.id,
        workspaces: [{
          id: 'workspace-1',
          name: 'Companion Core',
          displayName: 'Companion Core',
          workingDirectory: 'D:\\work\\pi-companion',
          createdAt: '2026-07-24T10:00:00.000Z',
          updatedAt: '2026-07-24T13:00:00.000Z',
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

    expect(wrapper.find('.selected-history-item').exists()).toBe(true)
    expect(wrapper.get('.history-heading > span').text()).toBe('任务')

    await wrapper.get('.history-mode-toggle').trigger('click')
    const menuOptions = wrapper.get('.history-view-popover').findAll('[role="menuitemradio"]')
    expect(wrapper.get('.history-view-popover').text()).toContain('分组方式')
    expect(wrapper.get('.history-view-popover').text()).toContain('状态筛选')
    expect(menuOptions.map(node => node.text())).toEqual([
      expect.stringContaining('按工作区'),
      expect.stringContaining('单列表'),
      expect.stringContaining('全部'),
      expect.stringContaining('进行中'),
      expect.stringContaining('已完成'),
      expect.stringContaining('已停止'),
      expect.stringContaining('失败'),
    ])

    await menuOptions[0]!.trigger('click')

    expect(wrapper.emitted('requestFullHistory')).toBeTruthy()
    expect(wrapper.get('.history-heading > span').text()).toBe('任务')
    expect(wrapper.find('.selected-history-item').exists()).toBe(false)

    const groupNames = wrapper.findAll('.sidebar-workspace-name').map(node => node.text())
    expect(groupNames).toEqual(['Companion Core', '直接对话'])

    const compactRows = wrapper.findAll('.history-item-compact')
    expect(compactRows).toHaveLength(2)
    expect(compactRows[0]!.find('.history-meta').exists()).toBe(false)
    expect(compactRows[0]!.find('.history-status.success').exists()).toBe(true)
    expect(compactRows[0]!.get('strong').text()).toBe(workspaceTask.title)
    expect(compactRows[1]!.classes()).toContain('current')

    // Hovering a compact task row shows full task title + summary without repeating workspace path
    await compactRows[0]!.trigger('pointerenter')
    let card = wrapper.get('.recent-task-hover-card')
    expect(card.classes()).toContain('recent-task-hover-card--task-only')
    expect(card.get('.recent-task-hover-title').text()).toBe(workspaceTask.title)
    expect(card.text()).toContain(workspaceTask.summary)
    expect(card.text()).not.toContain('D:\\work\\pi-companion')

    // Hovering a workspace group header shows workspace icon + name + path without task summary
    const groupToggle = wrapper.get('.sidebar-workspace-toggle')
    await groupToggle.trigger('pointerenter')
    card = wrapper.get('.recent-task-hover-card')
    expect(card.classes()).toContain('recent-task-hover-card--workspace-only')
    expect(card.text()).toContain('Companion Core')
    expect(card.text()).toContain('D:\\work\\pi-companion')
    expect(card.find('.recent-task-hover-summary').exists()).toBe(false)

    // Clicking the workspace + button emits newTaskInWorkspace
    await wrapper.get('.sidebar-workspace-new-task').trigger('click')
    expect(wrapper.emitted('newTaskInWorkspace')).toEqual([['workspace-1']])

    // Filtering by active status shows empty state when all tasks are completed
    await wrapper.get('.history-mode-toggle').trigger('click')
    const activeFilterOption = wrapper.get('.history-view-popover').findAll('[role="menuitemradio"]')[3]
    await activeFilterOption!.trigger('click')
    expect(wrapper.findAll('.history-item-compact')).toHaveLength(0)
    expect(wrapper.get('.history-empty').text()).toBe('没有匹配的任务')

    wrapper.unmount()
  })
})
