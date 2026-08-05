import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import TaskTemplateEditorDialog from './TaskTemplateEditorDialog.vue'

const styles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8')

describe('TaskTemplateEditorDialog', () => {
  it('gives every native form control the same styled surface', () => {
    expect(styles).toContain('.task-template-editor-body .ui-native-select { box-sizing: border-box;')
    expect(styles).toContain('.task-template-editor-body .ui-native-select { min-height: 38px; }')
    expect(styles).toContain('.task-template-editor-body .ui-input:focus-visible,')
  })

  it('saves a prompt with optional execution preferences but no run command', async () => {
    const wrapper = mount(TaskTemplateEditorDialog, {
      attachTo: document.body,
      props: {
        template: null,
        workspaces: [{
          id: 'workspace-1',
          name: 'Companion',
          workingDirectory: 'D:\\work',
          createdAt: '2026-08-04T00:00:00.000Z',
          updatedAt: '2026-08-04T00:00:00.000Z',
          taskCount: 1,
          hasActiveTask: false,
        }],
        modelOptions: [
          { value: 'model-1', label: 'Model One', thinkingLevels: ['off', 'low', 'high'] },
          { value: 'model-2', label: 'Model Two', thinkingLevels: ['off', 'medium'] },
        ],
        currentModel: 'model-1',
      },
    })

    expect(wrapper.findAll('input')).toHaveLength(1)
    expect(wrapper.get('input').classes()).toContain('ui-input')
    expect(wrapper.get('textarea').classes()).toContain('ui-textarea')
    expect(wrapper.text()).not.toContain('立即运行')
    await wrapper.get('input').setValue('工程检查')
    await wrapper.get('textarea').setValue('检查工程并给出摘要')
    await wrapper.get('.task-template-advanced-toggle').trigger('click')
    await nextTick()
    const selects = wrapper.findAll('select')
    expect(selects).toHaveLength(4)
    expect(selects.every(select => select.classes().includes('ui-native-select'))).toBe(true)
    expect(wrapper.findAll('.task-template-advanced > label').map(field => field.attributes('class'))).toEqual([
      'task-template-target-field',
      'task-template-permission-field',
      'task-template-model-field',
      'task-template-thinking-field',
    ])
    const targetSelect = wrapper.get('.task-template-target-field select')
    const permissionSelect = wrapper.get('.task-template-permission-field select')
    const modelSelect = wrapper.get('.task-template-model-field select')
    const thinkingSelect = wrapper.get('.task-template-thinking-field select')
    expect(thinkingSelect.findAll('option').map(option => option.attributes('value'))).toEqual(['', 'off', 'low', 'high'])

    await targetSelect.setValue('Workspace:workspace-1')
    await permissionSelect.setValue('read-only')
    await thinkingSelect.setValue('high')
    await modelSelect.setValue('model-2')
    await nextTick()
    expect(thinkingSelect.findAll('option').map(option => option.attributes('value'))).toEqual(['', 'off', 'medium'])
    expect((thinkingSelect.element as HTMLSelectElement).value).toBe('medium')
    await wrapper.get('form').trigger('submit')

    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({
      name: '工程检查',
      prompt: '检查工程并给出摘要',
      targetKind: 'Workspace',
      workspaceId: 'workspace-1',
      model: 'model-2',
      thinkingLevel: 'medium',
      permissionMode: 'read-only',
    }))
  })
})
