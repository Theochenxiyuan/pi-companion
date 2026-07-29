<script setup lang="ts">
import { SwitchRoot, SwitchThumb } from 'reka-ui'
import { computed, useAttrs } from 'vue'

defineOptions({ inheritAttrs: false })

withDefaults(defineProps<{
  modelValue: boolean
  size?: 'sm' | 'md'
}>(), {
  size: 'md',
})

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()

const attrs = useAttrs()
const controlAttrs = computed(() => Object.fromEntries(
  Object.entries(attrs).filter(([name]) => name !== 'class' && name !== 'style'),
))
</script>

<template>
  <span class="ui-switch-field" :class="$attrs.class" :style="$attrs.style">
    <SwitchRoot
      v-bind="controlAttrs"
      :model-value="modelValue"
      class="ui-switch"
      :class="`ui-switch--${size}`"
      @update:model-value="emit('update:modelValue', $event)"
    >
      <SwitchThumb class="ui-switch-thumb" />
    </SwitchRoot>
    <span v-if="$slots.default" class="ui-switch-label"><slot /></span>
  </span>
</template>
