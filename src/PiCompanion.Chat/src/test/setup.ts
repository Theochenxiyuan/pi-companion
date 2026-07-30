import { config } from '@vue/test-utils'

class ResizeObserverMock implements ResizeObserver {
  disconnect() {}
  observe() {}
  unobserve() {}
}

globalThis.ResizeObserver ??= ResizeObserverMock
globalThis.requestAnimationFrame ??= (callback: FrameRequestCallback) =>
  window.setTimeout(() => callback(performance.now()), 0)
globalThis.cancelAnimationFrame ??= (handle: number) => window.clearTimeout(handle)

config.global.stubs = {
  ...config.global.stubs,
  DialogPortal: {
    template: '<slot />',
  },
  UiButton: false,
  UiDialog: false,
  UiInput: false,
  UiMenu: false,
  UiMenuItem: false,
  UiNativeSelect: false,
  UiSelect: false,
  UiSwitch: false,
  UiTextarea: false,
}
