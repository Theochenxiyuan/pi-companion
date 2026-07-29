<script setup lang="ts">
import { computed } from 'vue'
import { UiButton } from '@/components/ui'
import type { MainView } from '@/composables/useTaskManagement'
import { t } from '@/i18n'

type PlaceholderView = Exclude<MainView, 'chat' | 'history' | 'skills'>

const props = defineProps<{
  view: PlaceholderView
  sidebarCollapsed: boolean
}>()

defineEmits<{
  toggleSidebar: []
}>()

const copy = computed(() => ({
  presets: {
    title: t('预置任务'),
  },
  scheduled: {
    title: t('定时任务'),
  },
})[props.view])
</script>

<template>
  <main class="management-main feature-placeholder-view" :class="`management-${view}`">
    <header class="topbar management-topbar">
      <div class="topbar-leading">
        <UiButton
          class="sidebar-toggle"
          type="button"
          :aria-label="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
          :title="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
          @click="$emit('toggleSidebar')"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3.5" y="4" width="17" height="16" rx="2" /><path d="M9 4v16" /></svg>
        </UiButton>
        <div class="location management-location">
          <strong>{{ copy.title }}</strong>
        </div>
      </div>
    </header>

    <section class="feature-placeholder-content">
      <div class="feature-placeholder-icon" aria-hidden="true">
        <svg v-if="view === 'presets'" viewBox="0 0 24 24"><rect x="5" y="3.5" width="14" height="17" rx="2" /><path d="M9 3.5v3h6v-3M9 11h6M9 15h4" /></svg>
        <svg v-else viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg>
      </div>
      <h1>{{ copy.title }}</h1>
      <span>{{ t('暂未开放') }}</span>
    </section>
  </main>
</template>
