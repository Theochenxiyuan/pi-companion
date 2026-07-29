<script setup lang="ts">
import { ref } from 'vue'
import { UiButton, UiMenu, UiMenuItem } from '@/components/ui'
import { t } from '@/i18n'

defineProps<{
  path: string
}>()

const emit = defineEmits<{
  select: [action: 'terminal' | 'explorer' | 'copy']
}>()

const open = ref(false)

function show() {
  open.value = true
}

function select(action: 'terminal' | 'explorer' | 'copy') {
  open.value = false
  emit('select', action)
}
</script>

<template>
  <UiMenu
    v-model="open"
    class="workspace-location-menu"
    content-class="workspace-location-popover"
    :aria-label="t('工作区操作')"
    @contextmenu.stop
  >
    <template #trigger>
      <UiButton
        class="location-path workspace-location-trigger"
        type="button"
        :title="path"
        @contextmenu.prevent="show"
      >
        <span>{{ path }}</span>
        <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m5 6 3 3 3-3" /></svg>
      </UiButton>
    </template>
      <UiMenuItem @select="select('terminal')">
        <svg viewBox="0 0 20 20" aria-hidden="true"><rect x="2.5" y="3.5" width="15" height="13" rx="2" /><path d="m6 8 2 2-2 2M10.5 12h3.5" /></svg>
        <span>{{ t('在终端中打开') }}</span>
      </UiMenuItem>
      <UiMenuItem @select="select('explorer')">
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M2.5 6.5h5l1.7-2h8.3v11h-15z" /></svg>
        <span>{{ t('在文件资源管理器中打开') }}</span>
      </UiMenuItem>
      <UiMenuItem @select="select('copy')">
        <svg viewBox="0 0 20 20" aria-hidden="true"><rect x="6.5" y="5.5" width="9" height="10" rx="1.5" /><path d="M4.5 12.5h-1v-9h8v1" /></svg>
        <span>{{ t('复制路径') }}</span>
      </UiMenuItem>
  </UiMenu>
</template>
