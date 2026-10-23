const MIMO_WEB_SEARCH_MODELS = new Set([
  'mimo-v2.6-flash',
  'mimo-v2.6-pro',
  'mimo-v2.6-pro-ultraspeed',
])

export const WEB_SEARCH_PROVIDERS = [
  'openai',
  'openai-codex',
  'azure',
  'google',
  'anthropic',
  'xai',
  'github-copilot',
  'opencode',
  'opencode-go',
  'xiaomi',
  'deepseek',
]

export function getWebSearchIntegration(model) {
  if (!model) return 'none'
  if (model.provider === 'xiaomi' &&
      model.api === 'openai-completions' &&
      MIMO_WEB_SEARCH_MODELS.has(model.id)) return 'provider-request'
  if (model.provider === 'openai' && model.api === 'openai-responses') return 'companion-tool'
  if (model.provider === 'azure' && model.api === 'azure-openai-responses') return 'companion-tool'
  if (model.provider === 'google' && model.api === 'google-generative-ai') return 'companion-tool'
  if (model.provider === 'anthropic' && model.api === 'anthropic-messages') return 'companion-tool'
  if (model.provider === 'openai-codex' && model.api === 'openai-codex-responses') return 'companion-tool'
  if (model.provider === 'xai' && model.api === 'openai-responses') return 'companion-tool'
  if (model.provider === 'github-copilot' && model.api === 'openai-responses') return 'companion-tool'
  if ((model.provider === 'opencode' || model.provider === 'opencode-go') &&
      model.api === 'openai-responses') return 'companion-tool'
  if (model.provider === 'deepseek' && model.api === 'openai-completions') return 'companion-tool'
  return 'none'
}
