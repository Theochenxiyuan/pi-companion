<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { t } from '@/i18n'

export interface UiSelectOption {
  value: string
  label: string
  group?: string
  tooltip?: string
  tone?: 'default' | 'danger'
}

const props = withDefaults(defineProps<{
  modelValue: string
  options: UiSelectOption[]
  ariaLabelText: string
  disabled?: boolean
  searchable?: boolean
  searchPlaceholder?: string
  emptyLabel?: string
  placement?: 'top' | 'bottom'
  align?: 'start' | 'end'
}>(), {
  disabled: false,
  searchable: false,
  searchPlaceholder: '',
  emptyLabel: '',
  placement: 'bottom',
  align: 'start',
})

const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const root = ref<HTMLElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const searchInput = ref<HTMLInputElement | null>(null)
const open = ref(false)
const searchQuery = ref('')
const teleportTarget = ref<HTMLElement | string>('body')
const menuStyle = ref<Record<string, string>>({})
const selectedLabel = computed(
  () => props.options.find((option) => option.value === props.modelValue)?.label ??
    (props.options.length ? props.modelValue : props.emptyLabel || t('暂无选项')),
)
const selectedTone = computed(
  () => props.options.find((option) => option.value === props.modelValue)?.tone ?? 'default',
)
const resolvedSearchPlaceholder = computed(() => props.searchPlaceholder || t('搜索'))
const groupedOptions = computed(() => {
  const groups: Array<{ label: string | null; options: UiSelectOption[] }> = []
  const query = searchQuery.value.trim().toLocaleLowerCase()
  const options = query
    ? props.options.filter(option => `${option.label} ${option.group ?? ''}`.toLocaleLowerCase().includes(query))
    : props.options
  for (const option of options) {
    const label = option.group ?? null
    let group = groups.find(candidate => candidate.label === label)
    if (!group) {
      group = { label, options: [] }
      groups.push(group)
    }
    group.options.push(option)
  }
  return groups
})

function toggle() {
  if (props.disabled) return
  if (!open.value) {
    teleportTarget.value = root.value?.closest<HTMLElement>(
      '.ui-dialog-content',
    ) ?? document.body
  }
  open.value = !open.value
}

function select(value: string) {
  emit('update:modelValue', value)
  open.value = false
}

function closeFromOutside(event: PointerEvent) {
  const target = event.target as Node
  if (!root.value?.contains(target) && !menu.value?.contains(target)) open.value = false
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') open.value = false
}

function updateMenuPosition() {
  if (!open.value || !root.value || !menu.value) return
  const trigger = root.value.getBoundingClientRect()
  const viewportWidth = window.innerWidth
  const viewportHeight = window.innerHeight
  const margin = 8
  const gap = 4
  const availableBelow = Math.max(0, viewportHeight - trigger.bottom - gap - margin)
  const availableAbove = Math.max(0, trigger.top - gap - margin)
  const desiredHeight = Math.min(320, (menu.value.scrollHeight || 318) + 2)
  const minimumUsefulHeight = Math.min(160, desiredHeight)
  const useTop = props.placement === 'top'
    ? availableAbove >= minimumUsefulHeight || availableAbove >= availableBelow
    : availableBelow < minimumUsefulHeight && availableAbove > availableBelow
  const availableHeight = useTop ? availableAbove : availableBelow
  const height = Math.min(desiredHeight, Math.max(72, availableHeight))
  const maximumHeight = Math.min(320, Math.max(72, availableHeight))
  const menuWidth = Math.min(
    Math.max(trigger.width, menu.value.offsetWidth),
    Math.max(0, viewportWidth - margin * 2),
  )
  const preferredLeft = props.align === 'end' ? trigger.right - menuWidth : trigger.left
  const left = Math.min(
    Math.max(margin, preferredLeft),
    Math.max(margin, viewportWidth - menuWidth - margin),
  )
  const top = useTop ? Math.max(margin, trigger.top - gap - height) : trigger.bottom + gap
  menuStyle.value = {
    top: `${Math.round(top)}px`,
    left: `${Math.round(left)}px`,
    minWidth: `${Math.round(trigger.width)}px`,
    maxWidth: `${Math.max(0, viewportWidth - margin * 2)}px`,
    maxHeight: `${Math.round(maximumHeight)}px`,
  }
}

function updateOpenMenuPosition() {
  if (open.value) updateMenuPosition()
}

watch(open, async (isOpen) => {
  if (!isOpen) {
    searchQuery.value = ''
    menuStyle.value = {}
    return
  }
  await nextTick()
  updateMenuPosition()
  if (props.searchable) searchInput.value?.focus()
})

watch(() => props.disabled, (disabled) => {
  if (disabled) open.value = false
})

onMounted(() => {
  window.addEventListener('pointerdown', closeFromOutside)
  window.addEventListener('keydown', handleKeydown)
  window.addEventListener('resize', updateOpenMenuPosition)
  window.addEventListener('scroll', updateOpenMenuPosition, true)
})

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', closeFromOutside)
  window.removeEventListener('keydown', handleKeydown)
  window.removeEventListener('resize', updateOpenMenuPosition)
  window.removeEventListener('scroll', updateOpenMenuPosition, true)
})
</script>

<template>
  <div
    ref="root"
    class="app-select"
    :class="{ open, 'opens-top': placement === 'top', 'align-end': align === 'end', 'danger-selected': selectedTone === 'danger' }"
  >
    <button
      class="app-select-trigger"
      type="button"
      :disabled="disabled"
      :aria-label="ariaLabelText"
      aria-haspopup="listbox"
      :aria-expanded="open"
      @click="toggle"
    >
      <span>{{ selectedLabel }}</span>
      <svg viewBox="0 0 12 12" aria-hidden="true"><path d="m3 4.5 3 3 3-3" /></svg>
    </button>
    <Teleport :to="teleportTarget">
      <div v-if="open" ref="menu" class="app-select-menu" :class="{ searchable }" :style="menuStyle" role="listbox" :aria-label="ariaLabelText">
        <input
          v-if="searchable"
          ref="searchInput"
          v-model="searchQuery"
          class="app-select-search"
          type="search"
          :placeholder="resolvedSearchPlaceholder"
          :aria-label="resolvedSearchPlaceholder"
        />
        <div class="app-select-options">
          <section v-for="group in groupedOptions" :key="group.label ?? '__ungrouped'" class="app-select-group">
            <div v-if="group.label" class="app-select-group-label">{{ group.label }}</div>
            <button
              v-for="option in group.options"
              :key="option.value"
              class="app-select-option"
              :class="{ selected: option.value === modelValue, danger: option.tone === 'danger' }"
              type="button"
              role="option"
              :title="option.tooltip"
              :aria-selected="option.value === modelValue"
              @click="select(option.value)"
            >
              <span>{{ option.label }}</span>
              <svg v-if="option.value === modelValue" viewBox="0 0 12 12" aria-hidden="true"><path d="m2.5 6 2.2 2.2L9.5 3.5" /></svg>
            </button>
          </section>
          <div v-if="!groupedOptions.length" class="app-select-empty">{{ t('没有匹配项') }}</div>
        </div>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.app-select { position: relative; min-width: 0; }
.app-select-trigger {
  border: 1px solid var(--color-border-subtle); width: 100%; min-height: 29px; color: var(--color-text-secondary);
  cursor: pointer; background: var(--color-tone-2); border-radius: 6px; outline: 0;
  display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 5px 13px 5px 8px;
  font-size: var(--font-size-body-sm); font-weight: var(--font-weight-regular);
}
.app-select-trigger:hover, .app-select-trigger:focus-visible, .app-select.open .app-select-trigger { color: var(--color-text-primary); border-color: var(--color-tone-9); }
.app-select.danger-selected .app-select-trigger { border-color: var(--color-danger-border); color: var(--color-danger-text-strong); }
.app-select.danger-selected .app-select-trigger:hover, .app-select.danger-selected .app-select-trigger:focus-visible, .app-select.danger-selected.open .app-select-trigger {
  border-color: var(--color-danger-border-strong); color: var(--color-danger-text-strong);
}
.app-select-trigger:disabled { color: var(--color-text-tertiary); cursor: default; opacity: .7; }
.app-select-trigger > span { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.app-select-trigger svg {
  fill: none; stroke: currentColor; stroke-width: 1.4px; stroke-linecap: round; stroke-linejoin: round;
  flex: none; width: 12px; height: 12px; transition: transform .12s;
}
.app-select.open .app-select-trigger svg { transform: rotate(180deg); }
.app-select-menu {
  position: fixed; z-index: 1600; width: max-content;
  border: 1px solid var(--color-border-default); background: var(--color-bg-surface); border-radius: 7px; padding: 4px;
  box-shadow: 0 12px 28px var(--color-overlay); display: flex; max-height: min(320px, calc(100vh - 24px)); flex-direction: column;
}
.app-select-menu.searchable { min-width: min(220px, calc(100vw - 24px)); }
.app-select-search {
  box-sizing: border-box; width: calc(100% - 8px); flex: none; margin: 4px 4px 5px; padding: 7px 9px;
  border: 1px solid var(--color-border-subtle); border-radius: 5px; outline: 0; background: var(--color-tone-2); color: var(--color-text-primary); font-size: var(--font-size-body-sm);
}
.app-select-search:focus { border-color: var(--color-tone-9); }
.app-select-search::-webkit-search-cancel-button { filter: invert(.6); }
.app-select-options { min-height: 0; overflow-x: hidden; overflow-y: auto; scrollbar-color: var(--color-tone-8) transparent; }
.app-select-group + .app-select-group { margin-top: 4px; padding-top: 4px; border-top: 1px solid var(--color-border-subtle); }
.app-select-group-label { padding: 5px 8px 4px; color: var(--color-tone-12); font-size: var(--font-size-caption); font-weight: var(--font-weight-semibold); letter-spacing: .025em; }
.app-select-option {
  border: 0; width: 100%; min-width: 94px; color: var(--color-text-secondary); cursor: pointer; text-align: left;
  background: transparent; border-radius: 5px; display: flex; align-items: center; justify-content: space-between;
  gap: 12px; padding: 6px 8px; font-size: var(--font-size-body-sm);
}
.app-select-option:hover, .app-select-option.selected { color: var(--color-text-primary); background: var(--color-bg-hover); }
.app-select-option.danger { color: var(--color-danger-text); }
.app-select-option.danger:hover, .app-select-option.danger.selected { color: var(--color-danger-text-strong); background: var(--color-danger-surface); }
.app-select-empty { padding: 12px 8px; color: var(--color-text-tertiary); font-size: var(--font-size-caption); text-align: center; }
.app-select-option svg {
  fill: none; stroke: currentColor; stroke-width: 1.6px; stroke-linecap: round; stroke-linejoin: round;
  flex: none; width: 12px; height: 12px;
}
</style>
