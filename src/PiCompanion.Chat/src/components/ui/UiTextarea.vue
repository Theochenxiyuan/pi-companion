<script setup lang="ts">
import { computed, ref, useAttrs } from 'vue'

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  modelValue?: string | null
  modelModifiers?: {
    trim?: boolean
  }
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  input: [event: Event]
}>()

const attrs = useAttrs()
const element = ref<HTMLTextAreaElement | null>(null)
const resolvedValue = computed(() => props.modelValue === undefined ? String(attrs.value ?? '') : props.modelValue ?? '')

function handleInput(event: Event) {
  const target = event.target as HTMLTextAreaElement
  const value = props.modelModifiers?.trim ? target.value.trim() : target.value
  emit('update:modelValue', value)
  emit('input', event)
}

function focus(options?: FocusOptions) {
  element.value?.focus(options)
}

function select() {
  element.value?.select()
}

function setSelectionRange(start: number | null, end: number | null, direction?: 'forward' | 'backward' | 'none') {
  element.value?.setSelectionRange(start, end, direction)
}

function resizeToContent(maxHeight: number) {
  if (!element.value) return
  element.value.style.height = 'auto'
  const contentHeight = element.value.scrollHeight
  element.value.style.height = `${Math.min(contentHeight, maxHeight)}px`
  element.value.style.overflowY = contentHeight > maxHeight ? 'auto' : 'hidden'
}

defineExpose({ element, focus, resizeToContent, select, setSelectionRange })
</script>

<template>
  <textarea
    v-bind="$attrs"
    ref="element"
    class="ui-textarea"
    :value="resolvedValue"
    @input="handleInput"
  ></textarea>
</template>
