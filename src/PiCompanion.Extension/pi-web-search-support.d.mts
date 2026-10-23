export type WebSearchIntegration = 'none' | 'companion-tool' | 'provider-request'

export declare const WEB_SEARCH_PROVIDERS: readonly string[]

export declare function getWebSearchIntegration(
  model: { provider: string; api: string; id: string } | undefined,
): WebSearchIntegration
