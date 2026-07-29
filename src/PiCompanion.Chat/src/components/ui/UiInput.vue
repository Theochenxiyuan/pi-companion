<script setup lang="ts">
import { computed, ref, useAttrs } from 'vue'

defineOptions({ inheritAttrs: false })

type UiInputValue = string | number | null | undefined

const props = defineProps<{
  modelValue?: UiInputValue
  modelModifiers?: {
    number?: boolean
    trim?: boolean
  }
}>()

const emit = defineEmits<{
  'update:modelValue': [value: UiInputValue]
  input: [event: Event]
}>()

const attrs = useAttrs()
const element = ref<HTMLInputElement | null>(null)
const resolvedValue = computed(() => props.modelValue === undefined ? attrs.value as UiInputValue : props.modelValue)

function handleInput(event: Event) {
  const target = event.target as HTMLInputElement
  let value: UiInputValue = target.value
  if (props.modelModifiers?.trim) value = target.value.trim()
  if (props.modelModifiers?.number) {
    const numericValue = target.valueAsNumber
    value = Number.isNaN(numericValue) ? target.value : numericValue
  }
  emit('update:modelValue', value)
  emit('input', event)
}

function focus(options?: FocusOptions) {
  element.value?.focus(options)
}

function select() {
  element.value?.select()
}

defineExpose({ element, focus, select })
</script>

<template>
  <input
    v-bind="$attrs"
    ref="element"
    class="ui-input"
    :value="resolvedValue ?? ''"
    @input="handleInput"
  />
</template>
