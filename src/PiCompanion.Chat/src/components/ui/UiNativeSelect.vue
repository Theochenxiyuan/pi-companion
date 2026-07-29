<script setup lang="ts">
defineOptions({ inheritAttrs: false })

defineProps<{
  modelValue: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  change: [event: Event]
}>()

function handleChange(event: Event) {
  emit('update:modelValue', (event.target as HTMLSelectElement).value)
  emit('change', event)
}
</script>

<template>
  <select
    v-bind="$attrs"
    class="ui-native-select"
    :value="modelValue"
    @change="handleChange"
  >
    <slot />
  </select>
</template>
