<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton, UiDialog, UiInput } from '@/components/ui'
import WorkspaceIcon from '@/components/WorkspaceIcon.vue'
import type {
  WorkspaceColorKey,
  WorkspaceHistoryEntry,
  WorkspaceIconKey,
} from '@/types/bridge'
import { t } from '@/i18n'

const props = defineProps<{
  workspace: WorkspaceHistoryEntry
}>()

const emit = defineEmits<{
  dismiss: []
  save: [payload: {
    workspaceId: string
    displayName: string | null
    iconKey: WorkspaceIconKey
    colorKey: WorkspaceColorKey
  }]
}>()

const displayName = ref(props.workspace.displayName ?? '')
const iconKey = ref<WorkspaceIconKey>(props.workspace.iconKey ?? 'folder')
const colorKey = ref<WorkspaceColorKey>(props.workspace.colorKey ?? 'blue')

const iconOptions: { value: WorkspaceIconKey; label: string }[] = [
  { value: 'folder', label: t('文件夹') },
  { value: 'code', label: t('代码') },
  { value: 'terminal', label: t('终端') },
  { value: 'book', label: t('文档') },
  { value: 'globe', label: t('网站') },
  { value: 'flask', label: t('实验') },
  { value: 'database', label: t('数据库') },
  { value: 'app', label: t('应用') },
]

const colorOptions: WorkspaceColorKey[] = [
  'blue',
  'indigo',
  'violet',
  'pink',
  'red',
  'orange',
  'green',
  'teal',
]

const fallbackName = computed(() => {
  const segments = props.workspace.workingDirectory.split(/[\\/]/).filter(Boolean)
  return segments.at(-1) ?? props.workspace.workingDirectory
})

function save() {
  const normalizedName = displayName.value.trim().replace(/\s+/gu, ' ')
  emit('save', {
    workspaceId: props.workspace.id,
    displayName: normalizedName || null,
    iconKey: iconKey.value,
    colorKey: colorKey.value,
  })
}
</script>

<template>
  <UiDialog
    as="form"
    :title="t('编辑工作区')"
    overlay-class="dialog-backdrop"
    content-class="task-dialog workspace-presentation-dialog"
    @close="$emit('dismiss')"
    @submit.prevent="save"
  >
      <div class="workspace-presentation-heading">
        <WorkspaceIcon :icon-key="iconKey" :color-key="colorKey" />
        <div>
          <h2>{{ t('编辑工作区') }}</h2>
          <p>{{ t('只修改显示信息，不会更改实际目录名称。') }}</p>
        </div>
      </div>

      <label class="workspace-presentation-field">
        <span>{{ t('显示名称') }}</span>
        <UiInput
          v-model="displayName"
          maxlength="60"
          autofocus
          :placeholder="fallbackName"
          :aria-label="t('工作区显示名称')"
        />
        <small>{{ t('留空时使用目录名称。') }}</small>
      </label>

      <fieldset class="workspace-presentation-options">
        <legend>{{ t('图标') }}</legend>
        <div class="workspace-icon-options">
          <UiButton
            v-for="option in iconOptions"
            :key="option.value"
            type="button"
            :class="{ selected: iconKey === option.value }"
            :title="option.label"
            :aria-label="option.label"
            :aria-pressed="iconKey === option.value"
            @click="iconKey = option.value"
          >
            <WorkspaceIcon :icon-key="option.value" :color-key="colorKey" />
          </UiButton>
        </div>
      </fieldset>

      <fieldset class="workspace-presentation-options">
        <legend>{{ t('图标颜色') }}</legend>
        <div class="workspace-color-options">
          <UiButton
            v-for="color in colorOptions"
            :key="color"
            type="button"
            :class="[`workspace-color-option-${color}`, { selected: colorKey === color }]"
            :aria-label="t('{color} 色', { color })"
            :aria-pressed="colorKey === color"
            @click="colorKey = color"
          ><span></span></UiButton>
        </div>
      </fieldset>

      <label class="workspace-presentation-field">
        <span>{{ t('实际路径') }}</span>
        <UiInput :value="workspace.workingDirectory" readonly />
      </label>

      <div class="dialog-actions">
        <UiButton type="button" @click="$emit('dismiss')">{{ t('取消') }}</UiButton>
        <UiButton class="primary" type="submit">{{ t('保存') }}</UiButton>
      </div>
  </UiDialog>
</template>
