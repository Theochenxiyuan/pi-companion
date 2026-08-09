<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { UiButton, UiDialog, UiInput, UiNativeSelect, UiSwitch, UiTextarea } from '@/components/ui'
import type { PiThinkingLevel, ScheduledTask, ScheduledTaskFrequency, TaskTemplate, WorkspaceHistoryEntry } from '@/types/bridge'
import { useI18n } from '@/i18n'
import { coerceThinkingLevel, thinkingLevelLabel } from '@/utils/thinkingLevels'

interface ModelOption { value: string; label: string; group?: string; thinkingLevels?: string[] }

const { t } = useI18n()
const props = defineProps<{
  scheduledTask: ScheduledTask | null
  templates: TaskTemplate[]
  workspaces: WorkspaceHistoryEntry[]
  modelOptions: ModelOption[]
}>()
const emit = defineEmits<{ save: [scheduledTask: ScheduledTask]; cancel: [] }>()

const mode = ref<'custom' | 'linked'>(props.scheduledTask?.templateId ? 'linked' : 'custom')
const name = ref(props.scheduledTask?.name ?? '')
const prompt = ref(props.scheduledTask?.prompt ?? '')
const target = ref(toTargetValue(props.scheduledTask))
const model = ref(props.scheduledTask?.model ?? '')
const thinkingLevel = ref(props.scheduledTask?.thinkingLevel ?? '')
const permissionMode = ref(props.scheduledTask?.permissionMode ?? 'read-only')
const templateId = ref(props.scheduledTask?.templateId ?? '')
const fillTemplateId = ref('')
const frequency = ref<ScheduledTaskFrequency>(props.scheduledTask?.frequency ?? 'Daily')
const localStartAt = ref(toLocalInputValue(props.scheduledTask?.localStartAt))
const daysOfWeek = ref(props.scheduledTask?.daysOfWeek || 1)
const timeZoneId = props.scheduledTask?.timeZoneId ?? (Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC')
const isEnabled = ref(props.scheduledTask?.isEnabled ?? true)

const linkableTemplates = computed(() => props.templates.filter(template =>
  !template.isBuiltIn && (template.targetKind === 'GeneralChat' ||
    template.targetKind === 'Workspace' && Boolean(template.workspaceId))))
const selectedLinkedTemplate = computed(() => props.templates.find(template => template.id === templateId.value) ?? null)
const availableThinkingLevels = computed(() => {
  if (!model.value) return []
  return props.modelOptions.find(option => option.value === model.value)?.thinkingLevels ?? ['low', 'medium', 'high']
})
const canSave = computed(() => Boolean(
  name.value.trim() && localStartAt.value &&
  (mode.value === 'linked' ? templateId.value : prompt.value.trim() && target.value) &&
  (frequency.value !== 'Weekly' || daysOfWeek.value)))

watch(model, selected => { if (!selected) thinkingLevel.value = '' })
watch(availableThinkingLevels, levels => {
  if (thinkingLevel.value) thinkingLevel.value = coerceThinkingLevel(thinkingLevel.value as PiThinkingLevel, levels) ?? ''
})
watch(mode, (nextMode, previousMode) => {
  if (nextMode !== 'custom' || previousMode !== 'linked' || !selectedLinkedTemplate.value) return
  fillTemplateId.value = selectedLinkedTemplate.value.id
  fillFromTemplate()
})

function toTargetValue(scheduledTask: ScheduledTask | null) {
  if (!scheduledTask || scheduledTask.targetKind === 'GeneralChat') return 'GeneralChat'
  return scheduledTask.workspaceId ? `Workspace:${scheduledTask.workspaceId}` : ''
}

function toLocalInputValue(value?: string) {
  if (value) return value.slice(0, 16)
  const date = new Date(Date.now() + 60 * 60 * 1000)
  date.setSeconds(0, 0)
  const offset = date.getTimezoneOffset() * 60_000
  return new Date(date.getTime() - offset).toISOString().slice(0, 16)
}

function fillFromTemplate() {
  const template = props.templates.find(candidate => candidate.id === fillTemplateId.value)
  if (!template) return
  prompt.value = template.prompt
  model.value = template.model ?? ''
  thinkingLevel.value = template.thinkingLevel ?? ''
  permissionMode.value = template.permissionMode ?? 'read-only'
  if (template.targetKind === 'GeneralChat') target.value = 'GeneralChat'
  else if (template.targetKind === 'Workspace' && template.workspaceId) target.value = `Workspace:${template.workspaceId}`
}

function detachTemplate() {
  const template = selectedLinkedTemplate.value
  if (template) {
    fillTemplateId.value = template.id
    mode.value = 'custom'
    fillFromTemplate()
  } else mode.value = 'custom'
  templateId.value = ''
}

function toggleDay(bit: number) {
  daysOfWeek.value = daysOfWeek.value & bit ? daysOfWeek.value & ~bit : daysOfWeek.value | bit
}

function save() {
  if (!canSave.value) return
  const now = new Date().toISOString()
  const custom = mode.value === 'custom'
  const workspaceId = custom && target.value.startsWith('Workspace:')
    ? target.value.slice('Workspace:'.length)
    : null
  emit('save', {
    id: props.scheduledTask?.id ?? '',
    name: name.value.trim(),
    isEnabled: isEnabled.value,
    templateId: custom ? null : templateId.value,
    prompt: custom ? prompt.value.trim() : null,
    targetKind: custom ? (target.value === 'GeneralChat' ? 'GeneralChat' : 'Workspace') : null,
    workspaceId,
    model: custom ? model.value || null : null,
    thinkingLevel: custom ? thinkingLevel.value || null : null,
    permissionMode: custom && target.value !== 'GeneralChat'
      ? permissionMode.value === 'standard' ? 'standard' : 'read-only'
      : null,
    frequency: frequency.value,
    localStartAt: localStartAt.value,
    daysOfWeek: frequency.value === 'Weekly' ? daysOfWeek.value : 0,
    timeZoneId,
    nextRunAt: null,
    createdAt: props.scheduledTask?.createdAt ?? now,
    updatedAt: now,
    lastOccurrence: props.scheduledTask?.lastOccurrence ?? null,
  })
}
</script>

<template>
  <UiDialog :title="t(scheduledTask ? '编辑定时任务' : '新建定时任务')" overlay-class="dialog-backdrop" content-class="task-template-editor scheduled-task-editor" @close="$emit('cancel')">
    <form class="scheduled-task-editor-form" @submit.prevent="save">
      <header class="task-template-dialog-header">
        <h2>{{ t(scheduledTask ? '编辑定时任务' : '新建定时任务') }}</h2>
        <UiButton variant="ghost" size="icon" type="button" :aria-label="t('关闭')" @click="$emit('cancel')">×</UiButton>
      </header>
      <div class="task-template-editor-body scheduled-task-editor-body">
        <label><span>{{ t('名称') }}</span><UiInput v-model="name" maxlength="80" autofocus /></label>

        <section class="scheduled-editor-section" :aria-label="t('任务来源')">
          <h3>{{ t('任务来源') }}</h3>
          <div class="scheduled-source-tabs" role="group" :aria-label="t('任务来源')">
            <UiButton variant="ghost" size="sm" type="button" :class="{ selected: mode === 'custom' }" :aria-pressed="mode === 'custom'" @click="mode = 'custom'">{{ t('自定义') }}</UiButton>
            <UiButton variant="ghost" size="sm" type="button" :class="{ selected: mode === 'linked' }" :aria-pressed="mode === 'linked'" @click="mode = 'linked'">{{ t('关联模板') }}</UiButton>
          </div>

          <div v-if="mode === 'custom'" class="scheduled-custom-fields">
            <div class="scheduled-template-fill">
              <UiNativeSelect v-model="fillTemplateId"><option value="">{{ t('选择模板快速填入') }}</option><option v-for="template in templates" :key="template.id" :value="template.id">{{ template.name }}</option></UiNativeSelect>
              <UiButton variant="secondary" size="md" type="button" :disabled="!fillTemplateId" @click="fillFromTemplate">{{ t('填入') }}</UiButton>
            </div>
            <label><span>{{ t('任务内容') }}</span><UiTextarea v-model="prompt" rows="6" maxlength="100000" /></label>
            <div class="scheduled-preference-grid">
              <label><span>{{ t('运行位置') }}</span><UiNativeSelect v-model="target"><option value="GeneralChat">{{ t('直接对话') }}</option><option v-for="workspace in workspaces" :key="workspace.id" :value="`Workspace:${workspace.id}`">{{ workspace.name }}</option></UiNativeSelect></label>
              <label><span>{{ t('权限') }}</span><UiNativeSelect v-model="permissionMode" :disabled="target === 'GeneralChat'"><option value="read-only">{{ t('只读') }}</option><option value="standard">{{ t('标准访问') }}</option></UiNativeSelect></label>
              <label><span>{{ t('模型') }}</span><UiNativeSelect v-model="model"><option value="">{{ t('使用默认值') }}</option><option v-for="option in modelOptions" :key="option.value" :value="option.value">{{ option.group ? `${option.group} · ` : '' }}{{ option.label }}</option></UiNativeSelect></label>
              <label><span>{{ t('推理等级') }}</span><UiNativeSelect v-model="thinkingLevel" :disabled="!model"><option value="">{{ t('使用默认值') }}</option><option v-for="level in availableThinkingLevels" :key="level" :value="level">{{ thinkingLevelLabel(level) }}</option></UiNativeSelect></label>
            </div>
          </div>

          <div v-else class="scheduled-linked-fields">
            <label><span>{{ t('任务模板') }}</span><UiNativeSelect v-model="templateId"><option value="">{{ t('选择具有固定运行位置的模板') }}</option><option v-for="template in linkableTemplates" :key="template.id" :value="template.id">{{ template.name }}</option></UiNativeSelect></label>
            <div v-if="selectedLinkedTemplate" class="scheduled-template-preview"><strong>{{ selectedLinkedTemplate.name }}</strong><p>{{ selectedLinkedTemplate.prompt }}</p><span>{{ t('未来每次运行都使用该模板的最新版本。') }}</span><UiButton v-if="scheduledTask?.templateId" variant="secondary" size="sm" type="button" @click="detachTemplate">{{ t('解除关联并保留当前内容') }}</UiButton></div>
            <p v-else class="scheduled-link-note">{{ t('只有绑定具体工作区或直接对话的已保存模板可以关联。系统模板仍可在自定义模式中快速填入。') }}</p>
          </div>
        </section>

        <section class="scheduled-editor-section" :aria-label="t('运行计划')">
          <h3>{{ t('运行计划') }}</h3>
          <div class="scheduled-time-grid">
            <label><span>{{ t('频率') }}</span><UiNativeSelect v-model="frequency"><option value="Once">{{ t('一次') }}</option><option value="Daily">{{ t('每天') }}</option><option value="Weekdays">{{ t('工作日') }}</option><option value="Weekly">{{ t('每周') }}</option></UiNativeSelect></label>
            <label><span>{{ t(frequency === 'Once' ? '运行时间' : '开始日期和时间') }}</span><UiInput v-model="localStartAt" type="datetime-local" /></label>
          </div>
          <div v-if="frequency === 'Weekly'" class="scheduled-weekdays" role="group" :aria-label="t('选择星期')"><UiButton v-for="day in [[1, '周一'], [2, '周二'], [4, '周三'], [8, '周四'], [16, '周五'], [32, '周六'], [64, '周日']]" :key="day[0]" variant="ghost" size="sm" type="button" :class="{ selected: daysOfWeek & Number(day[0]) }" :aria-pressed="Boolean(daysOfWeek & Number(day[0]))" @click="toggleDay(Number(day[0]))">{{ t(String(day[1])) }}</UiButton></div>
          <p class="scheduled-timezone-note">{{ t('时区：{timeZone}（自动设置）', { timeZone: timeZoneId }) }}</p>
          <UiSwitch v-model="isEnabled">{{ t('启用定时任务') }}</UiSwitch>
        </section>
      </div>
      <footer class="dialog-actions"><UiButton type="button" @click="$emit('cancel')">{{ t('取消') }}</UiButton><UiButton class="primary" type="submit" :disabled="!canSave">{{ t('保存') }}</UiButton></footer>
    </form>
  </UiDialog>
</template>

<style scoped>
.scheduled-task-editor-body { gap: 18px; }
.scheduled-task-editor-body label { display: grid; gap: 6px; }
.scheduled-task-editor-body label > span { color: var(--color-text-secondary); font-size: var(--font-size-caption); }
.scheduled-editor-section { display: grid; min-width: 0; gap: 14px; padding-top: 17px; border-top: 1px solid var(--color-border-subtle); }
.scheduled-editor-section h3 { margin: 0; color: var(--color-text-primary); font-size: var(--font-size-body-sm); font-weight: var(--font-weight-semibold); }
.scheduled-source-tabs { display: inline-flex; width: fit-content; gap: 2px; padding: 3px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-tone-1); }
.scheduled-source-tabs .ui-button:hover:not(.selected), .scheduled-weekdays .ui-button:hover:not(.selected) { background: var(--color-highlight-subtle); color: var(--color-text-primary); }
.scheduled-source-tabs .selected, .scheduled-weekdays .selected { border-color: var(--color-border-default); background: var(--color-bg-surface); color: var(--color-text-primary); box-shadow: 0 1px 2px var(--color-overlay-subtle); }
.scheduled-source-tabs .selected:hover, .scheduled-weekdays .selected:hover { border-color: var(--color-border-strong); background: var(--color-bg-hover); }
.scheduled-custom-fields, .scheduled-linked-fields { display: grid; gap: 14px; }
.scheduled-template-fill { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 7px; }
.scheduled-template-fill .ui-button:not(:disabled):hover, .scheduled-template-preview .ui-button:hover { border-color: var(--color-border-strong); background: var(--color-bg-hover); color: var(--color-text-primary); }
.scheduled-template-fill .ui-button:disabled { cursor: not-allowed; opacity: .45; }
.scheduled-preference-grid, .scheduled-time-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.scheduled-timezone-note { margin: -2px 0 0; color: var(--color-text-tertiary); font-size: var(--font-size-caption); line-height: var(--line-height-body); }
.scheduled-template-preview { display: grid; gap: 8px; padding: 12px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-tone-2); }
.scheduled-template-preview p { display: -webkit-box; max-height: 110px; overflow: hidden; margin: 0; color: var(--color-text-secondary); line-height: var(--line-height-body); white-space: pre-wrap; -webkit-box-orient: vertical; -webkit-line-clamp: 4; }
.scheduled-template-preview .ui-button { width: fit-content; }
.scheduled-template-preview span, .scheduled-link-note { color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.scheduled-link-note { margin: 0; padding: 11px 12px; border: 1px dashed var(--color-border-subtle); border-radius: 8px; line-height: var(--line-height-body); }
.scheduled-weekdays { display: flex; gap: 5px; flex-wrap: wrap; }

@media (max-width: 620px) {
  .scheduled-preference-grid, .scheduled-time-grid { grid-template-columns: 1fr; }
}
</style>
