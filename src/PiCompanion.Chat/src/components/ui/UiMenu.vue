<script setup lang="ts">
import {
  DropdownMenuContent,
  DropdownMenuRoot,
  DropdownMenuTrigger,
} from 'reka-ui'
import type { HTMLAttributes } from 'vue'

defineOptions({ inheritAttrs: false })

withDefaults(defineProps<{
  modelValue: boolean
  contentClass?: HTMLAttributes['class']
  ariaLabel?: string
  align?: 'start' | 'center' | 'end'
  side?: 'top' | 'right' | 'bottom' | 'left'
  sideOffset?: number
}>(), {
  contentClass: '',
  ariaLabel: '',
  align: 'start',
  side: 'bottom',
  sideOffset: 4,
})

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()
</script>

<template>
  <div v-bind="$attrs" class="ui-menu">
    <DropdownMenuRoot
      :open="modelValue"
      @update:open="emit('update:modelValue', $event)"
    >
      <DropdownMenuTrigger as-child>
        <slot name="trigger" :open="modelValue" />
      </DropdownMenuTrigger>
      <DropdownMenuContent
        class="ui-menu-content"
        :class="contentClass"
        :aria-label="ariaLabel || undefined"
        :align="align"
        :side="side"
        :side-offset="sideOffset"
      >
        <slot />
      </DropdownMenuContent>
    </DropdownMenuRoot>
  </div>
</template>
