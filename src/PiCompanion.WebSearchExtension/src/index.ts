import type { Api, Model } from '@earendil-works/pi-ai'
import type { ExtensionAPI } from '@earendil-works/pi-coding-agent'
import webSearchExtension from 'pi-web-search'

const WEB_SEARCH_TOOL = 'web_search'
const URL_CONTEXT_TOOL = 'url_context'

export type PiCompanionWebSearchSupport = 'none' | 'native'

export function getPiCompanionWebSearchSupport(
  model: Pick<Model<Api>, 'provider' | 'api'> | undefined,
): PiCompanionWebSearchSupport {
  if (!model) return 'none'
  if (model.provider === 'openai' && model.api === 'openai-responses') return 'native'
  if (model.provider === 'google' && model.api === 'google-generative-ai') return 'native'
  if (model.provider === 'anthropic' && model.api === 'anthropic-messages') return 'native'
  if (model.provider === 'openai-codex' && model.api === 'openai-codex-responses') return 'native'
  return 'none'
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
    if (getPiCompanionWebSearchSupport(model) === 'none') {
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

  // Register after upstream so the application-owned provider policy is the
  // final authority for all model lifecycle events.
  const toolManager = createPiCompanionToolManager(pi)
  pi.on('session_start', (_event, context) => toolManager.sync(context.model))
  pi.on('session_tree', (_event, context) => toolManager.sync(context.model))
  pi.on('model_select', event => toolManager.sync(event.model))
}
