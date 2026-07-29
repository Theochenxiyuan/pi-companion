<script setup lang="ts">
import { DropdownMenuItem } from 'reka-ui'

defineOptions({ inheritAttrs: false })

withDefaults(defineProps<{
  disabled?: boolean
  danger?: boolean
}>(), {
  disabled: false,
  danger: false,
})

const emit = defineEmits<{
  select: [event: Event]
}>()
</script>

<template>
  <DropdownMenuItem
    as-child
    :disabled="disabled"
    @select="emit('select', $event)"
  >
    <button
      v-bind="$attrs"
      type="button"
      class="ui-button ui-button--plain ui-button--inherit ui-menu-item"
      :class="{ 'ui-menu-item--danger': danger }"
      :disabled="disabled"
    >
      <slot />
    </button>
  </DropdownMenuItem>
</template>
