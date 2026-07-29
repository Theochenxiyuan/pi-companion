<script setup lang="ts" generic="T">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

const props = withDefaults(defineProps<{
  items: T[]
  itemKey: (item: T, index: number) => string | number
  estimatedItemHeight?: number
  overscan?: number
}>(), {
  estimatedItemHeight: 64,
  overscan: 8,
})

defineSlots<{
  default(props: { item: T; index: number }): unknown
}>()

const root = ref<HTMLElement | null>(null)
const offsets = ref<number[]>([0])
const range = ref({ start: 0, end: 0 })
const measuredHeights = new Map<string, number>()
const observedRows = new Map<string, HTMLElement>()
let scrollParent: HTMLElement | null = null
let rowResizeObserver: ResizeObserver | null = null
let layoutResizeObserver: ResizeObserver | null = null
let scheduledFrame = 0

const totalHeight = computed(() => offsets.value.at(-1) ?? 0)
const visibleRows = computed(() => {
  const rows: Array<{ item: T; index: number; key: string; offset: number }> = []
  for (let index = range.value.start; index < range.value.end; index += 1) {
    const item = props.items[index]
    if (item === undefined) continue
    rows.push({
      item,
      index,
      key: String(props.itemKey(item, index)),
      offset: offsets.value[index] ?? 0,
    })
  }
  return rows
})

function rebuildOffsets() {
  const next = new Array<number>(props.items.length + 1)
  next[0] = 0
  for (let index = 0; index < props.items.length; index += 1) {
    const key = String(props.itemKey(props.items[index], index))
    next[index + 1] = next[index] + (measuredHeights.get(key) ?? props.estimatedItemHeight)
  }
  offsets.value = next
  scheduleRangeUpdate()
}

function findItemAt(position: number) {
  let low = 0
  let high = props.items.length
  while (low < high) {
    const middle = Math.floor((low + high) / 2)
    if ((offsets.value[middle + 1] ?? totalHeight.value) <= position) low = middle + 1
    else high = middle
  }
  return Math.min(low, props.items.length)
}

function updateRange() {
  scheduledFrame = 0
  const element = root.value
  if (!element || props.items.length === 0) {
    range.value = { start: 0, end: 0 }
    return
  }

  const rootRect = element.getBoundingClientRect()
  const viewportRect = scrollParent?.getBoundingClientRect()
  const viewportHeight = scrollParent?.clientHeight || window.innerHeight || 600
  const viewportTop = viewportRect ? viewportRect.top : 0
  const visibleTop = Math.max(0, viewportTop - rootRect.top)
  const visibleBottom = Math.max(visibleTop, visibleTop + viewportHeight)
  const first = findItemAt(visibleTop)
  const last = findItemAt(visibleBottom) + 1
  range.value = {
    start: Math.max(0, first - props.overscan),
    end: Math.min(props.items.length, last + props.overscan),
  }
}

function scheduleRangeUpdate() {
  if (scheduledFrame) return
  scheduledFrame = window.requestAnimationFrame(updateRange)
}

function observeRow(element: Element | null, item: T, index: number) {
  const key = String(props.itemKey(item, index))
  const existing = observedRows.get(key)
  if (existing && existing !== element) rowResizeObserver?.unobserve(existing)
  if (!(element instanceof HTMLElement)) {
    observedRows.delete(key)
    return
  }

  observedRows.set(key, element)
  element.dataset.virtualKey = key
  rowResizeObserver?.observe(element)
}

watch(
  () => props.items,
  () => { void nextTick(rebuildOffsets) },
  { immediate: true },
)

onMounted(() => {
  scrollParent = root.value?.closest<HTMLElement>('.transcript') ?? root.value?.parentElement ?? null
  if (typeof ResizeObserver !== 'undefined') {
    rowResizeObserver = new ResizeObserver((entries) => {
      let changed = false
      for (const entry of entries) {
        const key = (entry.target as HTMLElement).dataset.virtualKey
        const height = entry.borderBoxSize[0]?.blockSize ?? entry.contentRect.height
        if (!key || height <= 0 || Math.abs((measuredHeights.get(key) ?? 0) - height) < 0.5) continue
        measuredHeights.set(key, height)
        changed = true
      }
      if (changed) rebuildOffsets()
    })
    layoutResizeObserver = new ResizeObserver(scheduleRangeUpdate)
    if (root.value) layoutResizeObserver.observe(root.value)
    if (scrollParent) layoutResizeObserver.observe(scrollParent)
  }
  for (const element of observedRows.values()) rowResizeObserver?.observe(element)
  scrollParent?.addEventListener('scroll', scheduleRangeUpdate, { passive: true })
  window.addEventListener('resize', scheduleRangeUpdate)
  rebuildOffsets()
})

onBeforeUnmount(() => {
  if (scheduledFrame) window.cancelAnimationFrame(scheduledFrame)
  scrollParent?.removeEventListener('scroll', scheduleRangeUpdate)
  window.removeEventListener('resize', scheduleRangeUpdate)
  rowResizeObserver?.disconnect()
  layoutResizeObserver?.disconnect()
})
</script>

<template>
  <div
    ref="root"
    class="virtual-list"
    :style="{ height: `${totalHeight}px` }"
    :data-virtual-count="items.length"
  >
    <div
      v-for="row in visibleRows"
      :key="row.key"
      :ref="(element) => observeRow(element as Element | null, row.item, row.index)"
      class="virtual-list-row"
      :style="{ transform: `translateY(${row.offset}px)` }"
      :data-virtual-index="row.index"
    >
      <slot :item="row.item" :index="row.index"></slot>
    </div>
  </div>
</template>

<style scoped>
.virtual-list {
  position: relative;
  width: 100%;
  min-height: 1px;
  overflow-anchor: none;
}

.virtual-list-row {
  position: absolute;
  top: 0;
  right: 0;
  left: 0;
  min-width: 0;
  contain: layout style;
  will-change: transform;
}
</style>
