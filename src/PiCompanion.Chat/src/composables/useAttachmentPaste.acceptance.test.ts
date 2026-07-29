import { mount } from '@vue/test-utils'
import { defineComponent } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { bridgeProtocolVersion } from '@/types/bridge'
import { useAttachmentPaste } from './useAttachmentPaste'

describe('clipboard image attachments', () => {
  afterEach(() => {
    delete window.chrome
    document.body.innerHTML = ''
  })

  it('turns a pasted image in the composer into a native attachment request', async () => {
    const postMessage = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const wrapper = mount(defineComponent({
      setup() {
        useAttachmentPaste({
          isTaskActive: () => false,
          isChatView: () => true,
          getPayload: () => ({
            workingDirectory: 'D:\\work',
            prompt: '检查图片',
            model: 'test-model',
            thinkingLevel: 'high',
          }),
          reportError: vi.fn(),
        })
      },
      template: '<div class="composer"><textarea /></div>',
    }), { attachTo: document.body })
    const image = new File(['image-content'], 'clipboard.png', { type: 'image/png' })
    const paste = new Event('paste', { bubbles: true, cancelable: true }) as ClipboardEvent
    Object.defineProperty(paste, 'clipboardData', {
      value: {
        items: [{
          kind: 'file',
          type: 'image/png',
          getAsFile: () => image,
        }],
      },
    })

    wrapper.get('textarea').element.dispatchEvent(paste)

    await vi.waitFor(() => expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      protocolVersion: bridgeProtocolVersion,
      type: 'AddClipboardImageAttachment',
      payload: expect.objectContaining({
        workingDirectory: 'D:\\work',
        fileName: 'clipboard.png',
        mimeType: 'image/png',
        data: expect.any(String),
      }),
    })))
    expect(paste.defaultPrevented).toBe(true)
    wrapper.unmount()
  })

  it('leaves ordinary text paste unchanged', () => {
    const postMessage = vi.fn()
    window.chrome = {
      webview: {
        postMessage,
        addEventListener() {},
        removeEventListener() {},
      },
    }
    const wrapper = mount(defineComponent({
      setup() {
        useAttachmentPaste({
          isTaskActive: () => false,
          isChatView: () => true,
          getPayload: () => ({ prompt: '', model: 'test-model', thinkingLevel: 'high' }),
          reportError: vi.fn(),
        })
      },
      template: '<div class="composer"><textarea /></div>',
    }), { attachTo: document.body })
    const paste = new Event('paste', { bubbles: true, cancelable: true }) as ClipboardEvent
    Object.defineProperty(paste, 'clipboardData', { value: { items: [] } })

    wrapper.get('textarea').element.dispatchEvent(paste)

    expect(paste.defaultPrevented).toBe(false)
    expect(postMessage).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
