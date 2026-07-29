/// <reference types="vite/client" />

declare global {
  interface WebViewMessageEvent extends Event {
    data: unknown
  }

  interface WebViewHost {
    postMessage(message: unknown): void
    postMessageWithAdditionalObjects?(message: unknown, additionalObjects: object[]): void
    addEventListener(type: 'message', listener: (event: WebViewMessageEvent) => void): void
    removeEventListener(type: 'message', listener: (event: WebViewMessageEvent) => void): void
  }

  interface Window {
    chrome?: {
      webview?: WebViewHost
    }
  }
}

export {}
