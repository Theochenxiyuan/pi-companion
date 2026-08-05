<script setup lang="ts">
import { UiButton } from '@/components/ui'
import { t } from '@/i18n'

defineProps<{
  view: 'scheduled'
  sidebarCollapsed: boolean
}>()

defineEmits<{
  toggleSidebar: []
}>()

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
          <strong>{{ t('定时任务') }}</strong>
        </div>
      </div>
    </header>

    <section class="feature-placeholder-content">
      <div class="feature-placeholder-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></svg>
      </div>
      <h1>{{ t('定时任务') }}</h1>
      <span>{{ t('暂未开放') }}</span>
    </section>
  </main>
</template>
