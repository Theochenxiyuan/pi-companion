import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ComposerActionMenu from './ComposerActionMenu.vue'

describe('composer action menu acceptance', () => {
  it('offers attachments and manual skill invocation from one extensible menu', async () => {
    const wrapper = mount(ComposerActionMenu, { attachTo: document.body })

    expect(wrapper.get('.composer-add-button svg').attributes('viewBox')).toBe('0 0 20 20')
    expect(wrapper.get('.composer-add-button path').attributes('d')).toBe('M10 4v12M4 10h12')
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)
    await wrapper.get('.composer-add-button').trigger('click')

    const items = wrapper.findAll('[role="menuitem"]')
    expect(items).toHaveLength(2)
    expect(items.map(item => item.text())).toEqual(['添加附件', '调用技能'])

    await items[0].trigger('click')
    expect(wrapper.emitted('selectAttachments')).toHaveLength(1)
    expect(wrapper.find('[role="menu"]').exists()).toBe(false)

    await wrapper.get('.composer-add-button').trigger('click')
    await wrapper.findAll('[role="menuitem"]')[1].trigger('click')
    expect(wrapper.emitted('invokeSkill')).toHaveLength(1)
    wrapper.unmount()
  })

  it('keeps skill invocation available while attachments are disabled', async () => {
    const wrapper = mount(ComposerActionMenu, {
      props: { attachmentsDisabled: true, skillsDisabled: false },
    })
    await wrapper.get('.composer-add-button').trigger('click')
    const items = wrapper.findAll('[role="menuitem"]')
    expect(items[0].attributes('disabled')).toBeDefined()
    expect(items[1].attributes('disabled')).toBeUndefined()
  })

  it('cannot open before a conversation mode is selected', async () => {
    const wrapper = mount(ComposerActionMenu, {
      props: { attachmentsDisabled: true, skillsDisabled: true },
    })
    expect(wrapper.get('.composer-add-button').attributes('disabled')).toBeDefined()
  })
})
