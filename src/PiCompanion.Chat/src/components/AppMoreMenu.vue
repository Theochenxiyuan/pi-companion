<script setup lang="ts">
import { ref } from 'vue'
import { DropdownMenuRadioGroup } from 'reka-ui'
import { UiButton, UiMenu, UiMenuItem, UiMenuRadioItem } from '@/components/ui'
import { t } from '@/i18n'

defineProps<{
  detailLevel: 'summary' | 'normal' | 'verbose'
}>()

const emit = defineEmits<{
  toggleMonitor: []
  setDetail: [level: 'summary' | 'normal' | 'verbose']
  exit: []
}>()

const open = ref(false)

function setDetail(level: unknown) {
  if (level === 'summary' || level === 'normal' || level === 'verbose') {
    emit('setDetail', level)
  }
}
</script>

<template>
  <UiMenu v-model="open" class="app-more-menu" content-class="app-more-popover" :aria-label="t('更多')" align="end">
    <template #trigger="{ open: menuOpen }">
      <UiButton
        class="sidebar-toggle app-more-trigger"
        type="button"
        :aria-label="t('更多')"
        :title="t('更多')"
        :aria-expanded="menuOpen"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1" /><circle cx="12" cy="12" r="1" /><circle cx="19" cy="12" r="1" /></svg>
      </UiButton>
    </template>
    <UiMenuItem @select="emit('toggleMonitor')">{{ t('显示 / 隐藏任务监视器') }}</UiMenuItem>
    <div class="app-more-separator" role="separator" />
    <div class="app-more-section-label">{{ t('对话显示') }}</div>
    <DropdownMenuRadioGroup :model-value="detailLevel" @update:model-value="setDetail">
      <UiMenuRadioItem
        v-for="option in (['summary', 'normal', 'verbose'] as const)"
        :key="option"
        :value="option"
        class="app-more-radio"
      >
        <span class="app-more-check" aria-hidden="true">{{ detailLevel === option ? '✓' : '' }}</span>
        {{ t(option === 'summary' ? '摘要' : option === 'normal' ? '标准' : '详细') }}
      </UiMenuRadioItem>
    </DropdownMenuRadioGroup>
    <div class="app-more-separator" role="separator" />
    <UiMenuItem danger @select="emit('exit')">{{ t('退出 Pi Companion') }}</UiMenuItem>
  </UiMenu>
</template>
