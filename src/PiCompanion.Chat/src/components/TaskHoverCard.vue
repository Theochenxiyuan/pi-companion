<script setup lang="ts">
import { computed } from 'vue'
import WorkspaceIcon from '@/components/WorkspaceIcon.vue'
import type { TaskHistoryEntry, WorkspaceHistoryEntry } from '@/types/bridge'
import { t } from '@/i18n'
import { taskStatusTone } from '@/utils/taskStatus'

const props = defineProps<{
  task: TaskHistoryEntry
  workspace?: WorkspaceHistoryEntry | null
  left: number
  top: number
}>()

const generalChat = computed(() => props.task.scopeKind === 'GeneralChat')
const workspaceName = computed(() => {
  if (generalChat.value) return t('直接对话')
  if (props.workspace?.name) return props.workspace.name
  const segments = props.task.workingDirectory.split(/[\\/]/).filter(Boolean)
  return segments.at(-1) ?? props.task.workingDirectory
})
const path = computed(() =>
  generalChat.value ? t('由 Pi Companion 管理的隔离空间') : props.task.workingDirectory)
</script>

<template>
  <aside
    id="recent-task-hover-card"
    class="recent-task-hover-card"
    role="tooltip"
    :style="{ left: `${left}px`, top: `${top}px` }"
  >
    <header>
      <WorkspaceIcon
        :icon-key="generalChat ? 'app' : workspace?.iconKey ?? 'folder'"
        :color-key="generalChat ? 'indigo' : workspace?.colorKey ?? 'blue'"
      />
      <span>
        <strong>{{ workspaceName }}</strong>
        <code :title="path">{{ path }}</code>
      </span>
    </header>
    <div class="recent-task-hover-summary">
      <div class="recent-task-hover-progress">
        <small>{{ t('最新进度') }}</small>
        <span class="history-state">
          <span class="history-status" :class="taskStatusTone(task.status)"></span>
          <small>{{ t(task.statusText) }}</small>
        </span>
      </div>
      <p>{{ task.summary || t('暂无任务总结') }}</p>
    </div>
  </aside>
</template>
