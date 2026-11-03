<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton, UiInput } from '@/components/ui'
import AppMoreMenu from '@/components/AppMoreMenu.vue'
import TaskTemplateCard from '@/components/TaskTemplateCard.vue'
import type { ScheduledTask, TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { taskTemplateTargetLabel } from '@/utils/taskTemplates'
import { useI18n } from '@/i18n'

const { t } = useI18n()
const props = defineProps<{
  templates: TaskTemplate[]
  scheduledTasks?: ScheduledTask[]
  workspaces: WorkspaceHistoryEntry[]
  sidebarCollapsed: boolean
  detailLevel?: 'summary' | 'normal' | 'verbose'
  loading?: boolean
}>()

defineEmits<{
  toggleSidebar: []
  toggleMonitor: []
  setDetail: [level: 'summary' | 'normal' | 'verbose']
  exit: []
  apply: [template: TaskTemplate]
  create: []
  edit: [template: TaskTemplate]
  duplicate: [template: TaskTemplate]
  delete: [template: TaskTemplate]
}>()

const search = ref('')
const normalizedSearch = computed(() => search.value.trim().toLocaleLowerCase())
const visibleTemplates = computed(() => props.templates.filter((template) => {
  const query = normalizedSearch.value
  if (!query) return true
  return [
    template.name,
    template.prompt,
    taskTemplateTargetLabel(template, props.workspaces, t),
    template.model ?? '',
    template.thinkingLevel ?? '',
    template.permissionMode ?? '',
  ].some(value => value.toLocaleLowerCase().includes(query))
}))
const systemTemplates = computed(() => visibleTemplates.value.filter(template => template.isBuiltIn))
const userTemplates = computed(() => visibleTemplates.value.filter(template => !template.isBuiltIn))
const hasSearchResults = computed(() => visibleTemplates.value.length > 0)
const systemCount = computed(() => props.templates.filter(template => template.isBuiltIn).length)
const userCount = computed(() => props.templates.filter(template => !template.isBuiltIn).length)
const subtitle = computed(() => t('{mine} 个我的模板 · {system} 个系统模板', {
  mine: userCount.value,
  system: systemCount.value,
}))
const showLoading = computed(() => Boolean(props.loading) && props.templates.length === 0)

function targetLabel(template: TaskTemplate) {
  return taskTemplateTargetLabel(template, props.workspaces, t)
}

function linkedScheduleCount(templateId: string) {
  return props.scheduledTasks?.filter(task => task.templateId === templateId).length ?? 0
}
</script>

<template>
  <main class="management-main task-templates-view">
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
          <strong>{{ t('任务模板') }}</strong>
          <small>{{ subtitle }}</small>
        </div>
      </div>
      <div class="topbar-actions">
        <UiButton class="task-template-create" variant="secondary" size="md" type="button" @click="$emit('create')">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
          <span>{{ t('新建模板') }}</span>
        </UiButton>
        <AppMoreMenu
          :detail-level="detailLevel ?? 'normal'"
          @toggle-monitor="$emit('toggleMonitor')"
          @set-detail="$emit('setDetail', $event)"
          @exit="$emit('exit')"
        />
      </div>
    </header>

    <section class="management-content task-template-management-content">
      <div class="task-template-management-search-row">
        <label class="management-search task-template-management-search">
          <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" /><path d="m13 13 4 4" /></svg>
          <UiInput v-model="search" type="search" :placeholder="t('搜索任务模板')" :aria-label="t('搜索任务模板')" />
        </label>
      </div>

      <div
        v-if="showLoading"
        class="loading-skeleton management-loading-skeleton"
        role="status"
        :aria-label="t('正在读取任务模板…')"
      >
        <span v-for="index in 4" :key="index" class="loading-skeleton-row"></span>
      </div>

      <template v-else>
        <p v-if="!hasSearchResults" class="task-template-empty">{{ t('未找到匹配的模板。') }}</p>

        <section v-if="userTemplates.length || !normalizedSearch" class="task-template-management-section">
          <div class="task-template-management-heading">
            <h2>{{ t('我的模板') }}</h2>
            <span>{{ userTemplates.length }}</span>
          </div>
          <div v-if="userTemplates.length" class="task-template-management-grid">
            <TaskTemplateCard
              v-for="template in userTemplates"
              :key="template.id"
              :template="template"
              :target-label="targetLabel(template)"
              :linked-count="linkedScheduleCount(template.id)"
              @edit="$emit('edit', $event)"
              @duplicate="$emit('duplicate', $event)"
              @delete="$emit('delete', $event)"
              @apply="$emit('apply', $event)"
            />
          </div>
          <div v-else class="task-template-management-empty">
            <p>{{ t('还没有我的模板。') }}</p>
            <UiButton variant="secondary" size="sm" type="button" @click="$emit('create')">{{ t('新建模板') }}</UiButton>
          </div>
        </section>

        <section v-if="systemTemplates.length" class="task-template-management-section">
          <div class="task-template-management-heading">
            <h2>{{ t('系统模板') }}</h2>
            <span>{{ systemTemplates.length }}</span>
          </div>
          <div class="task-template-management-grid">
            <TaskTemplateCard
              v-for="template in systemTemplates"
              :key="template.id"
              :template="template"
              :target-label="targetLabel(template)"
              @duplicate="$emit('duplicate', $event)"
              @apply="$emit('apply', $event)"
            />
          </div>
        </section>
      </template>
    </section>
  </main>
</template>
