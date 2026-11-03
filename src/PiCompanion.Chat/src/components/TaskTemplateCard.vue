<script setup lang="ts">
import { ref } from 'vue'
import { UiButton, UiMenu, UiMenuItem } from '@/components/ui'
import type { TaskTemplate } from '@/types/bridge'
import { useI18n } from '@/i18n'

const props = defineProps<{
  template: TaskTemplate
  targetLabel: string
  linkedCount?: number
}>()

const emit = defineEmits<{
  edit: [template: TaskTemplate]
  duplicate: [template: TaskTemplate]
  delete: [template: TaskTemplate]
  apply: [template: TaskTemplate]
}>()

const { t } = useI18n()
const menuOpen = ref(false)
</script>

<template>
  <article
    class="surface-card task-template-management-card"
    :class="{ system: props.template.isBuiltIn }"
  >
    <header>
      <div class="task-template-card-heading">
        <strong :title="props.template.name">
          <b
            v-if="props.template.isPinned"
            :title="t('已固定到新任务首页')"
            :aria-label="t('已固定到新任务首页')"
          >★</b>
          {{ props.template.name }}
        </strong>
        <span class="task-template-target" :title="props.targetLabel">{{ props.targetLabel }}</span>
        <small v-if="props.linkedCount" class="task-template-linked">
          {{ t('被 {count} 个定时任务关联', { count: props.linkedCount }) }}
        </small>
      </div>
      <em v-if="props.template.origin === 'Agent'" class="task-template-origin">{{ t('AI创建') }}</em>
    </header>
    <p>{{ props.template.prompt }}</p>
    <footer>
      <UiMenu
        v-model="menuOpen"
        content-class="app-more-popover"
        :aria-label="t('更多操作')"
        align="start"
      >
        <template #trigger="{ open }">
          <UiButton
            class="task-template-more"
            variant="ghost"
            size="icon"
            type="button"
            :aria-label="t('更多操作')"
            :aria-expanded="open"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1.7" /><circle cx="12" cy="12" r="1.7" /><circle cx="19" cy="12" r="1.7" /></svg>
          </UiButton>
        </template>
        <template v-if="props.template.isBuiltIn">
          <UiMenuItem @select="emit('duplicate', props.template)">{{ t('复制为我的模板') }}</UiMenuItem>
        </template>
        <template v-else>
          <UiMenuItem @select="emit('edit', props.template)">{{ t('编辑') }}</UiMenuItem>
          <UiMenuItem @select="emit('duplicate', props.template)">{{ t('复制') }}</UiMenuItem>
          <UiMenuItem danger @select="emit('delete', props.template)">{{ t('删除') }}</UiMenuItem>
        </template>
      </UiMenu>
      <UiButton
        class="task-template-apply-button"
        variant="secondary"
        size="sm"
        type="button"
        @click="emit('apply', props.template)"
      >{{ t('使用模板') }}</UiButton>
    </footer>
  </article>
</template>
