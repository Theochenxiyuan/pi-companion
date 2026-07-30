<script setup lang="ts">
import {
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import type { HTMLAttributes } from 'vue'

defineOptions({ inheritAttrs: false })

const props = withDefaults(defineProps<{
  open?: boolean
  title: string
  description?: string
  overlayClass?: HTMLAttributes['class']
  contentClass?: HTMLAttributes['class']
  alert?: boolean
  closeOnBackdrop?: boolean
  closeOnEscape?: boolean
}>(), {
  open: true,
  description: '',
  overlayClass: '',
  contentClass: '',
  alert: false,
  closeOnBackdrop: true,
  closeOnEscape: true,
})

const emit = defineEmits<{
  close: []
  'update:open': [value: boolean]
}>()

function handleOpenChange(value: boolean) {
  emit('update:open', value)
  if (!value) emit('close')
}

function handlePointerDownOutside(event: Event) {
  if (!props.closeOnBackdrop) event.preventDefault()
}

function handleEscape(event: KeyboardEvent) {
  if (!props.closeOnEscape) event.preventDefault()
}

function handleBackdropMouseDown(event: MouseEvent) {
  if (event.target === event.currentTarget && props.closeOnBackdrop) {
    handleOpenChange(false)
  }
}
</script>

<template>
  <DialogRoot :open="open" :modal="true" @update:open="handleOpenChange">
    <DialogPortal>
      <div class="ui-dialog-layer" :class="overlayClass">
        <DialogOverlay class="ui-dialog-overlay" @mousedown="handleBackdropMouseDown" />
        <DialogContent
          v-bind="$attrs"
          class="ui-dialog-content"
          :class="contentClass"
          :role="alert ? 'alertdialog' : 'dialog'"
          aria-modal="true"
          @pointer-down-outside="handlePointerDownOutside"
          @escape-key-down="handleEscape"
        >
          <DialogTitle as="span" class="ui-visually-hidden">{{ title }}</DialogTitle>
          <DialogDescription as="span" class="ui-visually-hidden">
            {{ description }}
          </DialogDescription>
          <slot />
        </DialogContent>
      </div>
    </DialogPortal>
  </DialogRoot>
</template>
