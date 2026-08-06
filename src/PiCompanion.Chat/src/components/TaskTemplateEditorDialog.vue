<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { UiButton, UiDialog, UiInput, UiNativeSelect, UiSwitch, UiTextarea } from '@/components/ui'
import type { PiThinkingLevel, TaskTemplate, TaskTemplateTargetKind, WorkspaceHistoryEntry } from '@/types/bridge'
import { useI18n } from '@/i18n'
import { coerceThinkingLevel, thinkingLevelLabel } from '@/utils/thinkingLevels'

interface ModelOption {
  value: string
  label: string
  group?: string
  thinkingLevels?: string[]
}

const { t } = useI18n()
const props = defineProps<{
  template: TaskTemplate | null
  workspaces: WorkspaceHistoryEntry[]
  modelOptions: ModelOption[]
  currentModel: string
}>()

const emit = defineEmits<{
  save: [template: TaskTemplate]
  cancel: []
}>()

const name = ref(props.template?.name ?? '')
const prompt = ref(props.template?.prompt ?? '')
const target = ref(toTargetValue(props.template))
const model = ref(props.template?.model ?? '')
const thinkingLevel = ref(props.template?.thinkingLevel ?? '')
const permissionMode = ref(props.template?.permissionMode ?? '')
const isPinned = ref(props.template?.isPinned ?? false)
const advancedOpen = ref(Boolean(props.template && (
  props.template.targetKind !== 'CurrentContext' ||
  props.template.model ||
  props.template.thinkingLevel ||
  props.template.permissionMode ||
  props.template.isPinned
)))

const canSave = computed(() => name.value.trim().length > 0 && prompt.value.trim().length > 0)
const availableThinkingLevels = computed(() => {
  if (!model.value) return []
  const selected = props.modelOptions.find(option => option.value === model.value)
  return selected?.thinkingLevels?.length ? selected.thinkingLevels : ['low', 'medium', 'high']
})

watch(model, selectedModel => {
  if (!selectedModel) thinkingLevel.value = ''
}, { immediate: true })

watch(availableThinkingLevels, levels => {
  if (!thinkingLevel.value) return
  thinkingLevel.value = coerceThinkingLevel(thinkingLevel.value as PiThinkingLevel, levels) ?? ''
}, { immediate: true })

function toTargetValue(template: TaskTemplate | null) {
  if (!template || template.targetKind === 'CurrentContext') return 'CurrentContext'
  if (template.targetKind === 'GeneralChat') return 'GeneralChat'
  return template.workspaceId ? `Workspace:${template.workspaceId}` : 'Workspace'
}

function save() {
  if (!canSave.value) return
  let targetKind: TaskTemplateTargetKind = 'CurrentContext'
  let workspaceId: string | null = null
  if (target.value === 'GeneralChat') targetKind = 'GeneralChat'
  else if (target.value === 'Workspace' || target.value.startsWith('Workspace:')) {
    targetKind = 'Workspace'
    workspaceId = target.value.startsWith('Workspace:') ? target.value.slice('Workspace:'.length) : null
  }
  const now = new Date().toISOString()
  emit('save', {
    id: props.template?.isBuiltIn ? '' : props.template?.id ?? '',
    name: name.value.trim(),
    prompt: prompt.value.trim(),
    targetKind,
    workspaceId,
    model: model.value || null,
    thinkingLevel: thinkingLevel.value || null,
    permissionMode: permissionMode.value === 'read-only' || permissionMode.value === 'standard'
      ? permissionMode.value
      : null,
    isPinned: isPinned.value,
    createdAt: props.template?.createdAt ?? now,
    updatedAt: now,
  })
}
</script>

<template>
  <UiDialog
    :title="t(template ? '编辑任务模板' : '新建任务模板')"
    overlay-class="dialog-backdrop"
    content-class="task-template-editor"
    @close="$emit('cancel')"
  >
    <form @submit.prevent="save">
      <header class="task-template-dialog-header">
        <h2>{{ t(template ? '编辑任务模板' : '新建任务模板') }}</h2>
        <UiButton type="button" :aria-label="t('关闭')" @click="$emit('cancel')">×</UiButton>
      </header>

      <div class="task-template-editor-body">
        <label>
          <span>{{ t('模板名称') }}</span>
          <UiInput v-model="name" maxlength="80" autofocus />
        </label>
        <label>
          <span>{{ t('任务内容') }}</span>
          <UiTextarea v-model="prompt" rows="8" maxlength="100000" :placeholder="t('描述要完成的任务')" />
        </label>

        <UiButton class="task-template-advanced-toggle" type="button" :aria-expanded="advancedOpen" @click="advancedOpen = !advancedOpen">
          <span>{{ t('执行偏好') }}</span><b>{{ advancedOpen ? '−' : '+' }}</b>
        </UiButton>
        <div v-if="advancedOpen" class="task-template-advanced">
          <label class="task-template-target-field">
            <span>{{ t('任务环境') }}</span>
            <UiNativeSelect v-model="target">
              <option value="CurrentContext">{{ t('不指定') }}</option>
              <option value="Workspace">{{ t('工作区（使用时选择）') }}</option>
              <option value="GeneralChat">{{ t('直接对话') }}</option>
              <optgroup v-if="workspaces.length" :label="t('固定工作区')">
                <option v-for="workspace in workspaces" :key="workspace.id" :value="`Workspace:${workspace.id}`">{{ workspace.name }}</option>
              </optgroup>
            </UiNativeSelect>
          </label>
          <label class="task-template-permission-field">
            <span>{{ t('权限') }}</span>
            <UiNativeSelect v-model="permissionMode" :disabled="target === 'GeneralChat'">
              <option value="">{{ t('不指定') }}</option>
              <option value="read-only">{{ t('只读') }}</option>
              <option value="standard">{{ t('标准访问') }}</option>
            </UiNativeSelect>
          </label>
          <label class="task-template-model-field">
            <span>{{ t('模型') }}</span>
            <UiNativeSelect v-model="model">
              <option value="">{{ t('不指定') }}</option>
              <option v-for="option in modelOptions" :key="option.value" :value="option.value">{{ option.group ? `${option.group} · ` : '' }}{{ option.label }}</option>
            </UiNativeSelect>
          </label>
          <label class="task-template-thinking-field">
            <span>{{ t('推理等级') }}</span>
            <UiNativeSelect v-model="thinkingLevel" :disabled="!model">
              <option value="">{{ t('不指定') }}</option>
              <option v-for="level in availableThinkingLevels" :key="level" :value="level">{{ thinkingLevelLabel(level) }}</option>
            </UiNativeSelect>
          </label>
          <UiSwitch v-model="isPinned">{{ t('固定到新任务首页') }}</UiSwitch>
        </div>
      </div>

      <footer class="dialog-actions">
        <UiButton type="button" @click="$emit('cancel')">{{ t('取消') }}</UiButton>
        <UiButton class="primary" type="submit" :disabled="!canSave">{{ t('保存') }}</UiButton>
      </footer>
    </form>
  </UiDialog>
</template>
