<script setup lang="ts">
import { computed } from 'vue'
import { UiButton, UiDialog } from '@/components/ui'
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

const systemTemplates = computed(() => props.templates.filter(template => template.isBuiltIn))
const userTemplates = computed(() => props.templates.filter(template => !template.isBuiltIn))
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
