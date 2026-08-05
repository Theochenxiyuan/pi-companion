import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { TaskTemplate } from '@/types/bridge'
import TaskTemplatePicker from './TaskTemplatePicker.vue'

const styles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8')
const uiStyles = readFileSync(resolve(process.cwd(), 'src/ui-components.css'), 'utf8')

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
    expect(wrapper.get('.task-template-picker-search').classes()).toContain('management-search')
    expect(wrapper.get('input[type="search"]').attributes('autofocus')).toBeDefined()
    expect(wrapper.text()).not.toContain('删除模板')
    expect(wrapper.text()).not.toContain('新建模板')
    expect(uiStyles).toContain('.ui-button:not(:disabled) { cursor: pointer; }')
    expect(styles).toContain('.task-template-dialog-header > button:hover')
    expect(styles).toContain('.task-template-main:hover')
    expect(styles).toContain('.task-template-picker-footer button:hover')

    await wrapper.get('input[type="search"]').setValue('我的')
    expect(wrapper.findAll('.task-template-main')).toHaveLength(1)
    expect(wrapper.get('.task-template-main').text()).toContain('我的检查')
    await wrapper.get('input[type="search"]').setValue('不存在')
    expect(wrapper.findAll('.task-template-main')).toHaveLength(0)
    expect(wrapper.text()).toContain('未找到匹配的模板。')
    await wrapper.get('input[type="search"]').setValue('')

    await wrapper.findAll('.task-template-main')[0]!.trigger('click')
    expect(wrapper.emitted('apply')).toEqual([[systemTemplate]])

    await wrapper.findAll('button').find(button => button.text() === '管理任务模板')!.trigger('click')
    expect(wrapper.emitted('manage')).toHaveLength(1)
  })
})
