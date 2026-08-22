import type { Api, Model } from '@earendil-works/pi-ai'
import type { ExtensionAPI } from '@earendil-works/pi-coding-agent'
import webSearchExtension from 'pi-web-search'

const WEB_SEARCH_TOOL = 'web_search'
const URL_CONTEXT_TOOL = 'url_context'
const MIMO_WEB_SEARCH_MODELS = new Set(['mimo-v2.5', 'mimo-v2.5-pro'])
const MIMO_WEB_SEARCH_TOOL = {
  type: WEB_SEARCH_TOOL,
  max_keyword: 3,
  force_search: false,
} as const

export type PiCompanionWebSearchSupport = 'none' | 'native'
type PiCompanionWebSearchIntegration = 'none' | 'companion-tool' | 'provider-request'

type WebSearchModel = Pick<Model<Api>, 'provider' | 'api' | 'id'>

function getPiCompanionWebSearchIntegration(
  model: WebSearchModel | undefined,
): PiCompanionWebSearchIntegration {
  if (!model) return 'none'
  if (model.provider === 'xiaomi' &&
      model.api === 'openai-completions' &&
      MIMO_WEB_SEARCH_MODELS.has(model.id)) {
    return 'provider-request'
  }
  if (model.provider === 'openai' && model.api === 'openai-responses') return 'companion-tool'
  if (model.provider === 'google' && model.api === 'google-generative-ai') return 'companion-tool'
  if (model.provider === 'anthropic' && model.api === 'anthropic-messages') return 'companion-tool'
  if (model.provider === 'openai-codex' && model.api === 'openai-codex-responses') return 'companion-tool'
  return 'none'
}

export function getPiCompanionWebSearchSupport(
  model: WebSearchModel | undefined,
): PiCompanionWebSearchSupport {
  return getPiCompanionWebSearchIntegration(model) === 'none' ? 'none' : 'native'
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function injectMimoWebSearch(payload: unknown, model: WebSearchModel | undefined) {
  if (getPiCompanionWebSearchIntegration(model) !== 'provider-request' || !isRecord(payload)) {
    return undefined
  }

  if (payload.tools !== undefined && !Array.isArray(payload.tools)) return undefined
  const tools = Array.isArray(payload.tools) ? payload.tools : []
  if (tools.some(tool => isRecord(tool) && tool.type === WEB_SEARCH_TOOL)) return undefined

  return {
    ...payload,
    tools: [...tools, MIMO_WEB_SEARCH_TOOL],
  }
}

function createPiCompanionToolManager(
  pi: Pick<ExtensionAPI, 'getActiveTools' | 'setActiveTools'>,
) {
  let webSearchWasRequested = false

  const sync = (model: Model<Api> | undefined) => {
    const current = new Set(pi.getActiveTools())
    webSearchWasRequested ||= current.has(WEB_SEARCH_TOOL)

    // Pi Companion launches with web_search only for an eligible official
    // provider. Keep that boundary intact after an in-session model switch.
    // URL Context is intentionally outside this release.
    current.delete(URL_CONTEXT_TOOL)
    if (getPiCompanionWebSearchIntegration(model) !== 'companion-tool') {
      current.delete(WEB_SEARCH_TOOL)
    } else if (webSearchWasRequested) {
      current.add(WEB_SEARCH_TOOL)
    }
    pi.setActiveTools(Array.from(current))
  }

  return { sync }
}

export default function piCompanionWebSearchExtension(pi: ExtensionAPI) {
  webSearchExtension(pi)

  pi.on('before_provider_request', (event, context) =>
    injectMimoWebSearch(event.payload, context.model))

  // Register after upstream so the application-owned provider policy is the
  // final authority for all model lifecycle events.
  const toolManager = createPiCompanionToolManager(pi)
  pi.on('session_start', (_event, context) => toolManager.sync(context.model))
  pi.on('session_tree', (_event, context) => toolManager.sync(context.model))
  pi.on('model_select', event => toolManager.sync(event.model))
}
