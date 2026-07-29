import { onBeforeUnmount, onMounted, ref } from 'vue'
import { postBridgeMessageWithAdditionalObjects } from '@/bridge'
import { t } from '@/i18n'

interface AttachmentDropPayload extends Record<string, unknown> {
  workingDirectory?: string
  prompt: string
  model: string
  thinkingLevel: string
}

interface AttachmentDropOptions {
  isTaskActive: () => boolean
  isChatView: () => boolean
  getPayload: () => AttachmentDropPayload
  reportError: (message: string) => void
}

export function useAttachmentDrop(options: AttachmentDropOptions) {
  const isAttachmentDragActive = ref(false)
  let dragDepth = 0

  function isFileDrag(event: DragEvent) {
    return Array.from(event.dataTransfer?.types ?? []).includes('Files')
  }

  function reset() {
    dragDepth = 0
    isAttachmentDragActive.value = false
  }

  function handleDragEnter(event: DragEvent) {
    if (!isFileDrag(event)) return
    event.preventDefault()
    dragDepth += 1
    isAttachmentDragActive.value = true
  }

  function handleDragOver(event: DragEvent) {
    if (!isFileDrag(event)) return
    event.preventDefault()
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = options.isTaskActive() || !options.isChatView() ? 'none' : 'copy'
    }
  }

  function handleDragLeave(event: DragEvent) {
    if (!isFileDrag(event)) return
    dragDepth = Math.max(0, dragDepth - 1)
    if (dragDepth === 0) isAttachmentDragActive.value = false
  }

  function handleDrop(event: DragEvent) {
    if (!isFileDrag(event)) return
    event.preventDefault()
    const files = Array.from(event.dataTransfer?.files ?? [])
    reset()

    if (options.isTaskActive()) {
      options.reportError(t('任务运行时暂不能添加附件。'))
      return
    }
    if (!options.isChatView()) {
      options.reportError(t('请先回到智能体对话再添加附件。'))
      return
    }
    if (files.length === 0) {
      options.reportError(t('未能读取拖放的文件。'))
      return
    }

    try {
      const posted = postBridgeMessageWithAdditionalObjects('AddDroppedAttachments', options.getPayload(), files)
      if (!posted) options.reportError(t('当前 WebView2 版本不支持拖放附件。'))
    } catch {
      options.reportError(t('拖放附件失败，请使用附件按钮选择文件。'))
    }
  }

  onMounted(() => {
    window.addEventListener('dragenter', handleDragEnter)
    window.addEventListener('dragover', handleDragOver)
    window.addEventListener('dragleave', handleDragLeave)
    window.addEventListener('drop', handleDrop)
  })

  onBeforeUnmount(() => {
    window.removeEventListener('dragenter', handleDragEnter)
    window.removeEventListener('dragover', handleDragOver)
    window.removeEventListener('dragleave', handleDragLeave)
    window.removeEventListener('drop', handleDrop)
  })

  return { isAttachmentDragActive }
}
