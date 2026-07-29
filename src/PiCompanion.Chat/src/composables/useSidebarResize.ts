import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { Ref } from 'vue'

interface SidebarResizeOptions {
  collapsed?: Ref<boolean>
  defaultWidth?: number
  minimumWidth?: number
  maximumWidth?: number
  storageKey?: string
  cssVariable?: `--${string}`
  edge?: 'left' | 'right'
}

export function useSidebarResize(options: SidebarResizeOptions = {}) {
  const defaultWidth = options.defaultWidth ?? 232
  const minimumWidth = options.minimumWidth ?? 220
  const maximumWidth = options.maximumWidth ?? 420
  const storageKey = options.storageKey ?? 'pi-companion.sidebar-width'
  const cssVariable = options.cssVariable ?? '--sidebar-width'
  const edge = options.edge ?? 'left'
  const collapsed = options.collapsed ?? ref(false)
  const width = ref(defaultWidth)
  const workspaceStyle = computed(() => ({ [cssVariable]: `${width.value}px` }))
  let stopResize: (() => void) | null = null

  function clamp(nextWidth: number) {
    return Math.min(maximumWidth, Math.max(minimumWidth, nextWidth))
  }

  function setWidth(nextWidth: number) {
    width.value = clamp(nextWidth)
    localStorage.setItem(storageKey, String(width.value))
  }

  function beginResize(event: PointerEvent) {
    if (collapsed.value || window.matchMedia('(max-width: 760px)').matches) return
    event.preventDefault()
    stopResize?.()
    const startX = event.clientX
    const startWidth = width.value
    const onPointerMove = (moveEvent: PointerEvent) => {
      const delta = moveEvent.clientX - startX
      width.value = clamp(startWidth + (edge === 'left' ? delta : -delta))
    }
    const finish = () => {
      localStorage.setItem(storageKey, String(width.value))
      document.body.classList.remove('sidebar-resizing')
      window.removeEventListener('pointermove', onPointerMove)
      window.removeEventListener('pointerup', finish)
      window.removeEventListener('pointercancel', finish)
      stopResize = null
    }
    stopResize = finish
    document.body.classList.add('sidebar-resizing')
    window.addEventListener('pointermove', onPointerMove)
    window.addEventListener('pointerup', finish)
    window.addEventListener('pointercancel', finish)
  }

  onMounted(() => {
    const savedWidth = Number.parseInt(localStorage.getItem(storageKey) ?? '', 10)
    if (Number.isFinite(savedWidth)) width.value = clamp(savedWidth)
  })

  onBeforeUnmount(() => stopResize?.())

  return {
    collapsed,
    width,
    workspaceStyle,
    setWidth,
    beginResize,
  }
}
