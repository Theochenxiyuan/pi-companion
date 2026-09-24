<script setup lang="ts">
import { DropdownMenuRadioItem } from 'reka-ui'

defineOptions({ inheritAttrs: false })

defineProps<{
  value: string
}>()
</script>

<template>
  <DropdownMenuRadioItem :value="value" as-child>
    <button
      v-bind="$attrs"
      type="button"
      class="ui-button ui-button--plain ui-button--inherit ui-menu-radio-item"
    >
      <slot />
    </button>
  </DropdownMenuRadioItem>
</template>
