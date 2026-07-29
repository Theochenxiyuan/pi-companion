import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import VirtualList from './VirtualList.vue'

describe('virtual transcript acceptance', () => {
  it('keeps a 5000-row transcript windowed', async () => {
    const items = Array.from({ length: 5000 }, (_, index) => ({ id: `event-${index}`, text: `事件 ${index}` }))
    const wrapper = mount(VirtualList, {
      attachTo: document.body,
      props: {
        items,
        itemKey: (item: unknown) => (item as { id: string }).id,
        estimatedItemHeight: 48,
        overscan: 8,
      },
      slots: {
        default: ({ item }: { item: unknown }) => (item as { text: string }).text,
      },
    })

    await nextTick()
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))

    expect(wrapper.attributes('data-virtual-count')).toBe('5000')
    expect(wrapper.element.getAttribute('style')).toContain('height: 240000px')
    expect(wrapper.findAll('[data-virtual-index]').length).toBeGreaterThan(0)
    expect(wrapper.findAll('[data-virtual-index]').length).toBeLessThan(40)
    wrapper.unmount()
  })
})
