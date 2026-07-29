import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it } from 'vitest'
import type { TaskHistoryEntry, WorkspaceHistoryEntry } from '@/types/bridge'
import TaskManagementView from './TaskManagementView.vue'

const emptyWorkspace: WorkspaceHistoryEntry = {
  id: 'workspace-1',
  name: 'pi-companion',
  workingDirectory: 'D:\\Dev\\pi-companion',
  createdAt: '2026-07-26T12:00:00.000Z',
  updatedAt: '2026-07-26T12:00:00.000Z',
  taskCount: 0,
  hasActiveTask: false,
  iconKey: 'code',
  colorKey: 'violet',
}

describe('TaskManagementView workspaces', () => {
  beforeEach(() => {
    window.localStorage.removeItem('pi-companion:task-management-collapsed-workspaces')
  })

  it('renders an independent workspace with no tasks and starts its first task', async () => {
    const wrapper = mount(TaskManagementView, {
      props: {
        tasks: [],
        workspaces: [emptyWorkspace],
        sidebarCollapsed: false,
        search: '',
        status: 'all',
      },
    })

    expect(wrapper.findAll('.management-workspace')).toHaveLength(1)
    expect(wrapper.get('button[aria-label="按最新进度状态筛选"]').text()).toContain('最新进度：全部')
    expect(wrapper.get('.management-workspace-copy').text()).toContain(emptyWorkspace.name)
    expect(wrapper.get('.management-workspace-empty').text()).toContain('暂无任务')
    expect(wrapper.get('.workspace-icon-visual').classes()).toContain('workspace-icon-color-violet')
    expect(wrapper.get('.management-workspace-new-task svg').element.tagName.toLowerCase()).toBe('svg')

    await wrapper.get('.management-workspace-more summary').trigger('click')
    await wrapper.findAll('.management-workspace-menu button')[0]!.trigger('click')
    expect(wrapper.emitted('manageWorkspaceSkills')).toEqual([[emptyWorkspace.id]])

    await wrapper.get('.management-workspace-more summary').trigger('click')
    await wrapper.findAll('.management-workspace-menu button')[1]!.trigger('click')
    expect(wrapper.emitted('editWorkspace')).toEqual([[emptyWorkspace.id]])

    await wrapper.get('.management-workspace-more summary').trigger('click')
    await wrapper.findAll('.management-workspace-menu button')[2]!.trigger('click')
    expect(wrapper.emitted('hideWorkspace')).toEqual([[emptyWorkspace.id]])

    await wrapper.get('.management-workspace-new-task').trigger('click')

    expect(wrapper.emitted('newTaskInWorkspace')).toEqual([[emptyWorkspace.id]])
  })

  it('does not show tasks from a hidden workspace until that workspace is added again', async () => {
    const task = {
      id: 'task-1',
      runId: 'run-1',
      workspaceId: emptyWorkspace.id,
      title: 'Hidden task',
      workingDirectory: emptyWorkspace.workingDirectory,
      status: 'Completed',
      statusText: '已完成',
      summary: '已整理技能信息',
      updatedAt: '2026-07-26T12:00:00.000Z',
      deletedAt: null,
      scopeKind: 'Workspace',
    } as const
    const wrapper = mount(TaskManagementView, {
      props: {
        tasks: [task],
        workspaces: [],
        sidebarCollapsed: false,
        search: '',
        status: 'all',
      },
    })

    expect(wrapper.findAll('.management-task')).toHaveLength(0)

    await wrapper.setProps({ workspaces: [emptyWorkspace] })
    expect(wrapper.findAll('.management-task')).toHaveLength(1)
    expect(wrapper.get('.management-task-primary-row time').text()).not.toBe('')
    expect(wrapper.get('.management-task-progress').text()).toContain('最新进度：')
    expect(wrapper.get('.management-task-progress').text()).toContain('已整理技能信息')
    expect(wrapper.get('.management-task-progress small').attributes('title')).toBe('已整理技能信息')
    expect(wrapper.get('.management-task-progress .history-status').attributes('aria-label')).toBe('已完成')
    expect(wrapper.find('.management-task-state-value').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('最新一轮')
  })

  it('offers a directory picker entry for creating another workspace', async () => {
    const wrapper = mount(TaskManagementView, {
      props: {
        tasks: [],
        workspaces: [],
        sidebarCollapsed: false,
        search: '',
        status: 'all',
      },
    })

    await wrapper.get('.management-add-workspace').trigger('click')

    expect(wrapper.emitted('createWorkspace')).toEqual([[]])
  })

  it('remembers collapsed workspaces and only expands them temporarily while searching', async () => {
    const props = {
      tasks: [],
      workspaces: [emptyWorkspace],
      sidebarCollapsed: false,
      search: '',
      status: 'all',
    }
    const wrapper = mount(TaskManagementView, { props })

    await wrapper.get('.management-workspace-toggle').trigger('click')
    expect(wrapper.get('.management-workspace-toggle').attributes('aria-expanded')).toBe('false')
    expect(JSON.parse(window.localStorage.getItem('pi-companion:task-management-collapsed-workspaces')!))
      .toContain(emptyWorkspace.id)
    wrapper.unmount()

    const restored = mount(TaskManagementView, { props })
    expect(restored.get('.management-workspace-toggle').attributes('aria-expanded')).toBe('false')

    await restored.setProps({ search: 'pi-companion' })
    expect(restored.get('.management-workspace-toggle').attributes('aria-expanded')).toBe('true')
    await restored.setProps({ search: '' })
    expect(restored.get('.management-workspace-toggle').attributes('aria-expanded')).toBe('false')
  })

  it('reveals tasks independently within each workspace', async () => {
    const workspaceTasks = Array.from({ length: 6 }, (_, index): TaskHistoryEntry => ({
      id: `workspace-task-${index}`,
      runId: `workspace-run-${index}`,
      workspaceId: emptyWorkspace.id,
      title: `Workspace task ${index}`,
      workingDirectory: emptyWorkspace.workingDirectory,
      status: 'Completed',
      statusText: '已完成',
      summary: '',
      updatedAt: `2026-07-26T12:${String(index).padStart(2, '0')}:00.000Z`,
      deletedAt: null,
      scopeKind: 'Workspace',
    }))
    const directTasks = Array.from({ length: 11 }, (_, index): TaskHistoryEntry => ({
      ...workspaceTasks[0]!,
      id: `direct-task-${index}`,
      runId: `direct-run-${index}`,
      workspaceId: null,
      title: `Direct task ${index}`,
      workingDirectory: '',
      updatedAt: `2026-07-26T13:${String(index).padStart(2, '0')}:00.000Z`,
      scopeKind: 'GeneralChat',
    }))
    const wrapper = mount(TaskManagementView, {
      props: {
        tasks: [...workspaceTasks, ...directTasks],
        workspaces: [{ ...emptyWorkspace, taskCount: workspaceTasks.length }],
        sidebarCollapsed: false,
        search: '',
        status: 'all',
      },
    })

    expect(wrapper.findAll('.management-task')).toHaveLength(15)
    expect(wrapper.findAll('.management-workspace-show-all')).toHaveLength(2)
    const summarylessRow = wrapper.get('.management-task-secondary-row')
    expect(summarylessRow.text()).toBe('最新进度：已完成')
    expect(summarylessRow.get('.management-task-progress-status').text()).toBe('已完成')
    expect(summarylessRow.get('.history-status').attributes('title')).toBe('已完成')

    const workspaceSection = wrapper.findAll('.management-workspace')
      .find(section => section.get('.management-workspace-copy strong').text() === emptyWorkspace.name)!
    await workspaceSection.get('.management-workspace-show-all').trigger('click')

    expect(workspaceSection.findAll('.management-task')).toHaveLength(6)
    expect(workspaceSection.find('.management-workspace-show-all').exists()).toBe(false)
  })
})
