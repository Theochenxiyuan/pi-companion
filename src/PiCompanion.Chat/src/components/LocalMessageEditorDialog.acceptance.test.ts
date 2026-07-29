import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import LocalMessageEditorDialog from './LocalMessageEditorDialog.vue'

describe('LocalMessageEditorDialog', () => {
  it('edits text and attachments without exposing execution settings', async () => {
    const wrapper = mount(LocalMessageEditorDialog, {
      props: {
        item: {
          id: 'message-1',
          message: '原任务',
          createdAt: '2026-07-23T10:00:00Z',
          attachments: [{
            path: 'C:\\old.txt',
            displayName: 'old.txt',
            kind: '文件',
            isAvailable: true,
          }],
        },
      },
    })

    expect(wrapper.get('[role="dialog"]').attributes('aria-modal')).toBe('true')
    expect(wrapper.text()).not.toContain('模型')
    expect(wrapper.text()).not.toContain('推理')
    expect(wrapper.text()).not.toContain('权限')
    await wrapper.get('textarea').setValue('更新后的任务')
    await wrapper.get('button[aria-label="移除附件 old.txt"]').trigger('click')
    await wrapper.findAll('footer button').find(button => button.text() === '确认')!.trigger('click')

    expect(wrapper.emitted('confirm')).toEqual([['更新后的任务', []]])
  })

  it('requests more attachments and merges the returned selection into the draft', async () => {
    const wrapper = mount(LocalMessageEditorDialog, {
      props: {
        item: {
          id: 'message-1',
          message: '未来任务',
          createdAt: '2026-07-23T10:00:00Z',
          attachments: [],
        },
      },
    })

    await wrapper.findAll('button').find(button => button.text() === '添加附件')!.trigger('click')
    expect(wrapper.emitted('selectAttachments')).toEqual([[[]]])
    await wrapper.setProps({
      selectedAttachments: [{
        path: 'C:\\new.txt',
        displayName: 'new.txt',
        kind: '文件',
        isAvailable: true,
      }],
    })
    expect(wrapper.text()).toContain('new.txt')
    expect(wrapper.text()).toContain('只能作为新一轮发送')
  })
})
