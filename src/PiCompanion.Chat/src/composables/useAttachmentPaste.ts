import { onBeforeUnmount, onMounted } from 'vue'
import { postBridgeMessage } from '@/bridge'
import { t } from '@/i18n'

interface AttachmentPastePayload extends Record<string, unknown> {
  workingDirectory?: string
  prompt: string
  model: string
  thinkingLevel: string
}

interface AttachmentPasteOptions {
  isTaskActive: () => boolean
  isChatView: () => boolean
  getPayload: () => AttachmentPastePayload
  reportError: (message: string) => void
}

const supportedImageTypes = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp'])
const maximumImageBytes = 10 * 1024 * 1024

function readBase64(file: File) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onerror = () => reject(reader.error)
    reader.onload = () => {
      const result = typeof reader.result === 'string' ? reader.result : ''
      const separator = result.indexOf(',')
      if (separator < 0) reject(new Error('Invalid image data URL'))
      else resolve(result.slice(separator + 1))
    }
    reader.readAsDataURL(file)
  })
}

export function useAttachmentPaste(options: AttachmentPasteOptions) {
  async function handlePaste(event: ClipboardEvent) {
    const target = event.target
    if (!(target instanceof HTMLTextAreaElement) || !target.closest('.composer')) return

    const images = Array.from(event.clipboardData?.items ?? [])
      .filter(item => item.kind === 'file' && supportedImageTypes.has(item.type.toLowerCase()))
      .map(item => item.getAsFile())
      .filter((file): file is File => file !== null)
    if (images.length === 0) return

    event.preventDefault()
    if (options.isTaskActive()) {
      options.reportError(t('任务运行时暂不能添加附件。'))
      return
    }
    if (!options.isChatView()) {
      options.reportError(t('请先回到智能体对话再添加附件。'))
      return
    }

    for (const image of images) {
      if (image.size <= 0 || image.size > maximumImageBytes) {
        options.reportError(t('粘贴的图片不能超过 10 MB。'))
        continue
      }

      try {
        const data = await readBase64(image)
        const posted = postBridgeMessage('AddClipboardImageAttachment', {
          ...options.getPayload(),
          fileName: image.name,
          mimeType: image.type.toLowerCase(),
          data,
        })
        if (!posted) options.reportError(t('当前页面不支持粘贴图片附件。'))
      } catch {
        options.reportError(t('读取剪贴板图片失败。'))
      }
    }
  }

  onMounted(() => window.addEventListener('paste', handlePaste))
  onBeforeUnmount(() => window.removeEventListener('paste', handlePaste))
}
