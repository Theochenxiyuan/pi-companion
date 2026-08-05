<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton, UiInput } from '@/components/ui'
import type { TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { taskTemplateTargetLabel } from '@/utils/taskTemplates'
import { useI18n } from '@/i18n'

const { t } = useI18n()
const props = defineProps<{
  templates: TaskTemplate[]
  workspaces: WorkspaceHistoryEntry[]
  sidebarCollapsed: boolean
}>()

defineEmits<{
  toggleSidebar: []
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

function badgeLabels(template: TaskTemplate) {
  const labels: string[] = [taskTemplateTargetLabel(template, props.workspaces, t)]
  if (template.permissionMode) {
    labels.push(t('权限：{mode}', {
      mode: t(template.permissionMode === 'read-only' ? '只读' : '标准访问'),
    }))
  }
  if (template.model) labels.push(t('模型：{model}', { model: template.model }))
  if (template.thinkingLevel) labels.push(t('推理：{level}', { level: template.thinkingLevel }))
  return labels
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
          <span>{{ t('管理可复用的任务草稿，使用模板会新建任务。') }}</span>
        </div>
      </div>
      <UiButton class="task-template-create" variant="secondary" size="md" type="button" @click="$emit('create')">
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
        {{ t('新建模板') }}
      </UiButton>
    </header>

    <section class="management-content task-template-management-content">
      <label class="management-search task-template-management-search">
        <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" /><path d="m13 13 4 4" /></svg>
        <UiInput v-model="search" type="search" :placeholder="t('搜索任务模板')" :aria-label="t('搜索任务模板')" />
      </label>

      <p v-if="!hasSearchResults" class="task-template-empty">{{ t('未找到匹配的模板。') }}</p>

      <section v-if="systemTemplates.length" class="task-template-management-section">
        <div class="task-template-management-heading">
          <h2>{{ t('系统模板') }}</h2>
          <span>{{ systemTemplates.length }}</span>
        </div>
        <div class="task-template-management-grid">
          <article v-for="template in systemTemplates" :key="template.id" class="task-template-management-card system">
            <header>
              <strong>{{ template.name }}</strong>
            </header>
            <p>{{ template.prompt }}</p>
            <div class="task-template-preferences">
              <span v-for="label in badgeLabels(template)" :key="label">{{ label }}</span>
            </div>
            <footer>
              <UiButton variant="secondary" type="button" @click="$emit('duplicate', template)">{{ t('复制为我的模板') }}</UiButton>
              <UiButton class="task-template-apply-button" variant="secondary" type="button" @click="$emit('apply', template)">{{ t('使用模板') }}</UiButton>
            </footer>
          </article>
        </div>
      </section>

      <section v-if="userTemplates.length || !normalizedSearch" class="task-template-management-section">
        <div class="task-template-management-heading">
          <h2>{{ t('我的模板') }}</h2>
          <span>{{ userTemplates.length }}</span>
        </div>
        <div v-if="userTemplates.length" class="task-template-management-grid">
          <article v-for="template in userTemplates" :key="template.id" class="task-template-management-card">
            <header>
              <strong><b v-if="template.isPinned" :title="t('已固定到新任务首页')" aria-label="t('已固定到新任务首页')">★</b>{{ template.name }}</strong>
            </header>
            <p>{{ template.prompt }}</p>
            <div class="task-template-preferences">
              <span v-for="label in badgeLabels(template)" :key="label">{{ label }}</span>
            </div>
            <footer>
              <UiButton variant="secondary" type="button" @click="$emit('edit', template)">{{ t('编辑') }}</UiButton>
              <UiButton variant="secondary" type="button" @click="$emit('duplicate', template)">{{ t('复制模板') }}</UiButton>
              <UiButton class="danger-action" variant="ghost" type="button" @click="$emit('delete', template)">{{ t('删除模板') }}</UiButton>
              <UiButton class="task-template-apply-button" variant="secondary" type="button" @click="$emit('apply', template)">{{ t('使用模板') }}</UiButton>
            </footer>
          </article>
        </div>
        <div v-else-if="!normalizedSearch" class="task-template-management-empty">
          <p>{{ t('还没有我的模板。') }}</p>
          <UiButton class="task-template-create" variant="secondary" size="md" type="button" @click="$emit('create')">{{ t('新建模板') }}</UiButton>
        </div>
      </section>
    </section>
  </main>
</template>
