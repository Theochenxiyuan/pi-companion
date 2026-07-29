import { bridgeProtocolVersion, type BridgeEnvelope } from '@/types/bridge'
import { t } from '@/i18n'

let messageListener: ((event: WebViewMessageEvent) => void) | undefined

export function postBridgeMessage(type: string, payload: Record<string, unknown> = {}) {
  const host = window.chrome?.webview
  if (!host) return false

  host.postMessage({
    protocolVersion: bridgeProtocolVersion,
    type,
    payload,
  })
  return true
}

export function postBridgeMessageWithAdditionalObjects(
  type: string,
  payload: Record<string, unknown>,
  additionalObjects: object[],
) {
  const host = window.chrome?.webview
  if (!host?.postMessageWithAdditionalObjects) return false

  host.postMessageWithAdditionalObjects({
    protocolVersion: bridgeProtocolVersion,
    type,
    payload,
  }, additionalObjects)
  return true
}

export function connectBridge(consume: (message: BridgeEnvelope) => void) {
  if (!window.chrome?.webview) {
    consume({
      protocolVersion: bridgeProtocolVersion,
      type: 'BridgeError',
      payload: { message: t('当前页面不在 WebView2 中；仅展示静态预览。') },
    })
    return () => undefined
  }

  messageListener = (event) => consume(event.data as BridgeEnvelope)
  window.chrome.webview.addEventListener('message', messageListener)
  postBridgeMessage('BridgeReady', { supportedProtocolVersions: [bridgeProtocolVersion] })

  return () => {
    if (messageListener) {
      window.chrome?.webview?.removeEventListener('message', messageListener)
    }
  }
}
