import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import WorkspacePresentationDialog from './WorkspacePresentationDialog.vue'

describe('WorkspacePresentationDialog', () => {
  it('saves display-only workspace presentation and supports resetting the name', async () => {
    const wrapper = mount(WorkspacePresentationDialog, {
      props: {
        workspace: {
          id: 'workspace-1',
          name: 'Companion Core',
          displayName: 'Companion Core',
          workingDirectory: 'D:\\Dev\\pi-companion',
          createdAt: '2026-07-26T12:00:00.000Z',
          updatedAt: '2026-07-26T12:00:00.000Z',
          taskCount: 2,
          hasActiveTask: false,
          iconKey: 'folder',
          colorKey: 'blue',
        },
      },
    })

    await wrapper.get('input[aria-label="工作区显示名称"]').setValue('')
    await wrapper.findAll('.workspace-icon-options button')[1]!.trigger('click')
    await wrapper.get('.workspace-color-option-violet').trigger('click')
    await wrapper.get('form').trigger('submit')

    expect(wrapper.emitted('save')).toEqual([[
      {
        workspaceId: 'workspace-1',
        displayName: null,
        iconKey: 'code',
        colorKey: 'violet',
      },
    ]])
    expect((wrapper.findAll('.workspace-presentation-field input')[1]!.element as HTMLInputElement).value)
      .toBe('D:\\Dev\\pi-companion')
  })
})
