import { computed, ref } from 'vue'
import { postBridgeMessage } from '@/bridge'
import { useTaskStore } from '@/stores/task'
import type { TaskHistoryEntry } from '@/types/bridge'
import { t } from '@/i18n'

export type MainView = 'chat' | 'history' | 'skills' | 'presets' | 'scheduled'
export type ConfirmAction = 'recycle' | 'delete-permanently'

export interface TaskContextMenu {
  x: number
  y: number
  task: TaskHistoryEntry
  recycled: boolean
}

export interface TaskConfirmation {
  kind: ConfirmAction
  task?: TaskHistoryEntry
}

export function useTaskManagement() {
  const store = useTaskStore()
  const mainView = ref<MainView>('chat')
  const historySearch = ref('')
  const historyStatus = ref('all')
  const taskContextMenu = ref<TaskContextMenu | null>(null)
  const renameTaskTarget = ref<TaskHistoryEntry | null>(null)
  const renameTitle = ref('')
  const confirmAction = ref<TaskConfirmation | null>(null)

  const confirmDialogTitle = computed(() => {
    if (confirmAction.value?.kind === 'recycle') return t('移入回收站？')
    if (confirmAction.value?.kind === 'delete-permanently') return t('永久删除这项任务？')
    return ''
  })

  const confirmDialogDescription = computed(() => {
    if (confirmAction.value?.kind === 'recycle') return t('任务及完整会话会保留在回收站中，之后可以恢复。')
    if (confirmAction.value?.kind === 'delete-permanently') return t('任务、会话和运行记录将被永久删除，此操作无法撤销。')
    return ''
  })

  function openTaskContextMenu(event: MouseEvent, task: TaskHistoryEntry, recycled = false) {
    const menuWidth = 156
    const menuHeight = 84
    const viewportPadding = 8
    taskContextMenu.value = {
      x: Math.max(viewportPadding, Math.min(event.clientX, window.innerWidth - menuWidth - viewportPadding)),
      y: Math.max(viewportPadding, Math.min(event.clientY, window.innerHeight - menuHeight - viewportPadding)),
      task,
      recycled,
    }
  }

  function closeTaskContextMenu() {
    taskContextMenu.value = null
  }

  function showMainView(view: MainView) {
    mainView.value = view
    historySearch.value = ''
    historyStatus.value = 'all'
    closeTaskContextMenu()
  }

  function openRenameTask() {
    const task = taskContextMenu.value?.task
    if (!task) return
    renameTaskTarget.value = task
    renameTitle.value = task.title
    closeTaskContextMenu()
  }

  function submitRenameTask() {
    const task = renameTaskTarget.value
    const title = renameTitle.value.trim()
    if (!task || !title) return
    postBridgeMessage('RenameTask', { taskId: task.id, title })
    renameTaskTarget.value = null
  }

  function requestTaskAction(kind: ConfirmAction, task?: TaskHistoryEntry) {
    confirmAction.value = { kind, task }
    closeTaskContextMenu()
  }

  function confirmTaskManagementAction() {
    const action = confirmAction.value
    if (!action) return
    if (action.kind === 'recycle' && action.task) {
      postBridgeMessage('MoveTaskToRecycleBin', { taskId: action.task.id })
    } else if (action.kind === 'delete-permanently' && action.task) {
      postBridgeMessage('DeleteTaskPermanently', { taskId: action.task.id })
    }
    confirmAction.value = null
  }

  function restoreTask(task: TaskHistoryEntry) {
    postBridgeMessage('RestoreTaskFromRecycleBin', { taskId: task.id })
    closeTaskContextMenu()
  }

  function selectTask(taskId: string) {
    mainView.value = 'chat'
    if (store.currentTask?.id === taskId) return
    postBridgeMessage('SelectTask', { taskId })
  }

  return {
    mainView,
    historySearch,
    historyStatus,
    taskContextMenu,
    renameTaskTarget,
    renameTitle,
    confirmAction,
    confirmDialogTitle,
    confirmDialogDescription,
    openTaskContextMenu,
    closeTaskContextMenu,
    showMainView,
    openRenameTask,
    submitRenameTask,
    requestTaskAction,
    confirmTaskManagementAction,
    restoreTask,
    selectTask,
  }
}
