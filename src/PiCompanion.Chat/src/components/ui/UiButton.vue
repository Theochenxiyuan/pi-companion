<script setup lang="ts">
import { computed, ref, useAttrs } from 'vue'

defineOptions({ inheritAttrs: false })

type UiButtonVariant = 'plain' | 'primary' | 'secondary' | 'ghost' | 'danger'
type UiButtonSize = 'inherit' | 'sm' | 'md' | 'lg' | 'icon'

const props = withDefaults(defineProps<{
  variant?: UiButtonVariant
  size?: UiButtonSize
  loading?: boolean
}>(), {
  variant: 'plain',
  size: 'inherit',
  loading: false,
})

const attrs = useAttrs()
const element = ref<HTMLButtonElement | null>(null)
const classes = computed(() => [
  'ui-button',
  `ui-button--${props.variant}`,
  `ui-button--${props.size}`,
])
const disabled = computed(() => props.loading || attrs.disabled === true || attrs.disabled === '')
const ariaBusy = computed(() =>
  props.loading || attrs['aria-busy'] === true || attrs['aria-busy'] === 'true')

function focus(options?: FocusOptions) {
  element.value?.focus(options)
}

defineExpose({ element, focus })
</script>

<template>
  <button
    v-bind="$attrs"
    ref="element"
    :class="classes"
    :aria-busy="ariaBusy"
    :disabled="disabled"
  >
    <span v-if="loading" class="ui-button-spinner" aria-hidden="true"></span>
    <slot />
  </button>
</template>
