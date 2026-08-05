<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton, UiDialog, UiInput } from '@/components/ui'
import type { TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { taskTemplateTargetLabel } from '@/utils/taskTemplates'
import { useI18n } from '@/i18n'

const { t } = useI18n()
const props = defineProps<{
  templates: TaskTemplate[]
  workspaces: WorkspaceHistoryEntry[]
}>()

defineEmits<{
  apply: [template: TaskTemplate]
  manage: []
  close: []
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
</script>

<template>
  <UiDialog
    :title="t('任务模板')"
    :description="t('选择模板后会填入输入框，不会立即运行。')"
    overlay-class="dialog-backdrop"
    content-class="task-template-picker"
    @close="$emit('close')"
  >
    <header class="task-template-dialog-header">
      <div>
        <h2>{{ t('任务模板') }}</h2>
        <p>{{ t('选择模板后会填入输入框，不会立即运行。') }}</p>
      </div>
      <UiButton type="button" :aria-label="t('关闭')" @click="$emit('close')">×</UiButton>
    </header>

    <div class="task-template-picker-body">
      <label class="management-search task-template-picker-search">
        <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5.5" /><path d="m13 13 4 4" /></svg>
        <UiInput v-model="search" autofocus type="search" :placeholder="t('搜索任务模板')" :aria-label="t('搜索任务模板')" />
      </label>

      <p v-if="!hasSearchResults" class="task-template-empty">{{ t('未找到匹配的模板。') }}</p>

      <section v-if="systemTemplates.length">
        <h3>{{ t('系统模板') }}</h3>
        <div class="task-template-list">
          <article v-for="template in systemTemplates" :key="template.id" class="task-template-row">
            <UiButton class="task-template-main" type="button" @click="$emit('apply', template)">
              <span>
                <strong>{{ template.name }}</strong>
                <small>{{ template.prompt }}</small>
              </span>
              <em>{{ taskTemplateTargetLabel(template, workspaces, t) }}</em>
            </UiButton>
          </article>
        </div>
      </section>

      <section v-if="userTemplates.length">
        <div class="task-template-section-heading">
          <h3>{{ t('我的模板') }}</h3>
        </div>
        <div class="task-template-list">
          <article v-for="template in userTemplates" :key="template.id" class="task-template-row">
            <UiButton class="task-template-main" type="button" @click="$emit('apply', template)">
              <span>
                <strong><b v-if="template.isPinned" aria-hidden="true">★</b>{{ template.name }}</strong>
                <small>{{ template.prompt }}</small>
              </span>
              <em>{{ taskTemplateTargetLabel(template, workspaces, t) }}</em>
            </UiButton>
          </article>
        </div>
      </section>
    </div>
    <footer class="task-template-picker-footer">
      <UiButton type="button" @click="$emit('manage')">{{ t('管理任务模板') }}</UiButton>
    </footer>
  </UiDialog>
</template>
