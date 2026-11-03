<script setup lang="ts">
import { computed } from 'vue'
import WorkspaceIcon from '@/components/WorkspaceIcon.vue'
import type {
  TaskHistoryEntry,
  WorkspaceColorKey,
  WorkspaceIconKey,
} from '@/types/bridge'
import { useI18n } from '@/i18n'
import { formatFullTimestamp } from '@/utils/dateTime'
import { taskStatusTone } from '@/utils/taskStatus'

export type TaskHoverCardMode = 'task-with-workspace' | 'task-only' | 'workspace-only'

export interface HoverWorkspacePresentation {
  name?: string
  workingDirectory?: string
  iconKey?: WorkspaceIconKey
  colorKey?: WorkspaceColorKey
  generalChat?: boolean
}

const props = withDefaults(defineProps<{
  mode?: TaskHoverCardMode
  task?: TaskHistoryEntry | null
  workspace?: HoverWorkspacePresentation | null
  left: number
  top: number
}>(), {
  mode: 'task-with-workspace',
  task: null,
  workspace: null,
})

const { locale, t } = useI18n()

const generalChat = computed(() =>
  Boolean(props.workspace?.generalChat || props.task?.scopeKind === 'GeneralChat'))

const rawDirectory = computed(() =>
  props.task?.workingDirectory || props.workspace?.workingDirectory || '')

const workspaceName = computed(() => {
  if (generalChat.value) return t('直接对话')
  if (props.workspace?.name) return props.workspace.name
  const segments = rawDirectory.value.split(/[\\/]/u).filter(Boolean)
  return segments.at(-1) ?? rawDirectory.value
})

const path = computed(() =>
  generalChat.value ? t('由 Pi Companion 管理的隔离空间') : rawDirectory.value)

const fullTaskTime = computed(() =>
  props.task?.updatedAt ? formatFullTimestamp(props.task.updatedAt, locale.value) : '')
</script>

<template>
  <aside
    id="recent-task-hover-card"
    class="recent-task-hover-card"
    :class="`recent-task-hover-card--${mode}`"
    role="tooltip"
    :style="{ left: `${left}px`, top: `${top}px` }"
  >
    <header v-if="mode === 'task-only' && task" class="recent-task-hover-task-header">
      <span>
        <strong class="recent-task-hover-title">{{ task.title }}</strong>
        <time v-if="fullTaskTime" class="recent-task-hover-time" :datetime="task.updatedAt">{{ fullTaskTime }}</time>
      </span>
    </header>
    <header v-else>
      <WorkspaceIcon
        :icon-key="generalChat ? 'app' : workspace?.iconKey ?? 'folder'"
        :color-key="generalChat ? 'indigo' : workspace?.colorKey ?? 'blue'"
      />
      <span>
        <strong>{{ workspaceName }}</strong>
        <code :title="path">{{ path }}</code>
      </span>
    </header>
    <div v-if="mode !== 'workspace-only' && task" class="recent-task-hover-summary">
      <div class="recent-task-hover-progress">
        <small>{{ t('最新进度') }}</small>
        <span class="history-state">
          <span class="history-status" :class="taskStatusTone(task.status)"></span>
          <small>{{ t(task.statusText) }}</small>
        </span>
      </div>
      <p v-if="task.summary">{{ task.summary }}</p>
      <p v-else-if="task.aiSummaryStatus === 'Generating'" class="recent-task-hover-summary-loading" role="status">
        <span class="ui-spinner file-loading-spinner" aria-hidden="true"></span>
        {{ t('正在生成 AI 总结') }}
      </p>
      <p v-else>{{ t('暂无任务总结') }}</p>
    </div>
  </aside>
</template>
