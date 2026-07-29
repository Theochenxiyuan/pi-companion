<script setup lang="ts">
import { UiButton, UiDialog, UiInput } from '@/components/ui'
import type { TaskConfirmation, TaskContextMenu } from '@/composables/useTaskManagement'
import type { TaskHistoryEntry } from '@/types/bridge'
import { activeTaskStatuses } from '@/utils/taskStatus'
import { t } from '@/i18n'

defineProps<{
  contextMenu: TaskContextMenu | null
  renameTarget: TaskHistoryEntry | null
  confirmation: TaskConfirmation | null
  confirmTitle: string
  confirmDescription: string
}>()

const renameTitle = defineModel<string>('renameTitle', { required: true })

defineEmits<{
  openRename: []
  requestAction: [kind: 'recycle' | 'delete-permanently', task: TaskHistoryEntry]
  restoreTask: [task: TaskHistoryEntry]
  dismissRename: []
  submitRename: []
  dismissConfirmation: []
  confirmAction: []
}>()
</script>

<template>
  <div
    v-if="contextMenu"
    class="task-context-menu"
    role="menu"
    :aria-label="t('任务操作')"
    :style="{ left: `${contextMenu.x}px`, top: `${contextMenu.y}px` }"
    @click.stop
  >
    <template v-if="!contextMenu.recycled">
      <UiButton type="button" role="menuitem" @click="$emit('openRename')">{{ t('重命名操作') }}</UiButton>
      <UiButton
        class="danger"
        type="button"
        role="menuitem"
        :disabled="activeTaskStatuses.includes(contextMenu.task.status)"
        @click="$emit('requestAction', 'recycle', contextMenu.task)"
      >{{ t('移入回收站') }}</UiButton>
    </template>
    <template v-else>
      <UiButton type="button" role="menuitem" @click="$emit('restoreTask', contextMenu.task)">{{ t('恢复') }}</UiButton>
      <UiButton class="danger" type="button" role="menuitem" @click="$emit('requestAction', 'delete-permanently', contextMenu.task)">{{ t('永久删除') }}</UiButton>
    </template>
  </div>

  <UiDialog
    v-if="renameTarget"
    as="form"
    :title="t('重命名任务')"
    overlay-class="dialog-backdrop"
    content-class="task-dialog"
    @close="$emit('dismissRename')"
    @submit.prevent="$emit('submitRename')"
  >
      <h2>{{ t('重命名任务') }}</h2>
      <UiInput v-model="renameTitle" maxlength="120" autofocus :aria-label="t('任务名称')" />
      <div class="dialog-actions">
        <UiButton type="button" @click="$emit('dismissRename')">{{ t('取消') }}</UiButton>
        <UiButton class="primary" type="submit" :disabled="!renameTitle.trim()">{{ t('保存') }}</UiButton>
      </div>
  </UiDialog>

  <UiDialog
    v-if="confirmation"
    :title="confirmTitle"
    :description="confirmDescription"
    overlay-class="dialog-backdrop"
    content-class="task-dialog"
    alert
    @close="$emit('dismissConfirmation')"
  >
      <h2>{{ confirmTitle }}</h2>
      <p>{{ confirmDescription }}</p>
      <div class="dialog-actions">
        <UiButton type="button" @click="$emit('dismissConfirmation')">{{ t('取消') }}</UiButton>
        <UiButton class="danger-action" type="button" @click="$emit('confirmAction')">
          {{ t(confirmation.kind === 'recycle' ? '移入回收站' : '永久删除') }}
        </UiButton>
      </div>
  </UiDialog>
</template>
