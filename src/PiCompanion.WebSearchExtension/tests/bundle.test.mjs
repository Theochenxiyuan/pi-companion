import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import test from 'node:test'

test('the private web search extension bundle is self-contained', () => {
  const bundlePath = resolve('dist/pi-web-search.mjs')
  assert.equal(existsSync(bundlePath), true)

  const bundle = readFileSync(bundlePath, 'utf8')
  const legalPath = resolve('dist/pi-web-search.mjs.LEGAL.txt')
  assert.equal(existsSync(legalPath), true)
  assert.match(bundle, /Web Search/u)
  assert.match(bundle, /web_search/u)
  assert.doesNotMatch(bundle, /from\s+["'](?:pi-web-search|typebox|@earendil-works\/pi-ai|@earendil-works\/pi-coding-agent)/u)
  assert.match(readFileSync(legalPath, 'utf8'), /pi-web-search 1\.3\.1/u)
})

test('only approved official providers advertise bundled native search', async () => {
  const extension = await import(pathToFileURL(resolve('dist/pi-web-search.mjs')))

  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'openai',
    api: 'openai-responses',
    id: 'gpt-5.4',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'google',
    api: 'google-generative-ai',
    id: 'gemini-2.5-pro',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'anthropic',
    api: 'anthropic-messages',
    id: 'claude-sonnet-4',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'openai-codex',
    api: 'openai-codex-responses',
    id: 'gpt-5.6',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'xiaomi',
    api: 'openai-completions',
    id: 'mimo-v2.5',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'xiaomi',
    api: 'openai-completions',
    id: 'mimo-v2.5-pro',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'company-proxy',
    api: 'openai-responses',
    id: 'gpt-5.4',
  }), 'none')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'xiaomi',
    api: 'openai-completions',
    id: 'mimo-v2.5-pro-ultraspeed',
  }), 'none')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'xiaomi-token-plan-cn',
    api: 'openai-completions',
    id: 'mimo-v2.5-pro',
  }), 'none')
})

test('MiMo search is injected into provider requests without exposing the companion tool', async () => {
  const extension = await import(pathToFileURL(resolve('dist/pi-web-search.mjs')))
  const handlers = new Map()
  let activeTools = ['read', 'web_search']
  const pi = {
    registerTool() {},
    on(event, handler) {
      const registered = handlers.get(event) ?? []
      registered.push(handler)
      handlers.set(event, registered)
    },
    getActiveTools() {
      return activeTools
    },
    setActiveTools(tools) {
      activeTools = tools
    },
  }
  extension.default(pi)

  const model = {
    provider: 'xiaomi',
    api: 'openai-completions',
    id: 'mimo-v2.5-pro',
  }
  const originalPayload = {
    model: model.id,
    messages: [{ role: 'user', content: '今天有什么新闻？' }],
    tools: [{ type: 'function', function: { name: 'read' } }],
  }
  const requestHandler = handlers.get('before_provider_request').at(-1)
  const injected = await requestHandler(
    { type: 'before_provider_request', payload: originalPayload },
    { model },
  )

  assert.notEqual(injected, originalPayload)
  assert.equal(originalPayload.tools.length, 1)
  assert.deepEqual(injected.tools, [
    originalPayload.tools[0],
    { type: 'web_search', max_keyword: 3, force_search: false },
  ])
  assert.equal(await requestHandler(
    { type: 'before_provider_request', payload: injected },
    { model },
  ), undefined)

  const sessionHandler = handlers.get('session_start').at(-1)
  await sessionHandler({ type: 'session_start' }, { model })
  assert.deepEqual(activeTools, ['read'])
})

test('MiMo search injection rejects unsupported Xiaomi models', async () => {
  const extension = await import(pathToFileURL(resolve('dist/pi-web-search.mjs')))
  const handlers = new Map()
  const pi = {
    registerTool() {},
    on(event, handler) {
      const registered = handlers.get(event) ?? []
      registered.push(handler)
      handlers.set(event, registered)
    },
    getActiveTools: () => [],
    setActiveTools() {},
  }
  extension.default(pi)

  const requestHandler = handlers.get('before_provider_request').at(-1)
  const result = await requestHandler(
    { type: 'before_provider_request', payload: { model: 'mimo-v2.5-pro-ultraspeed' } },
    {
      model: {
        provider: 'xiaomi',
        api: 'openai-completions',
        id: 'mimo-v2.5-pro-ultraspeed',
      },
    },
  )
  assert.equal(result, undefined)
})

test('Pi 0.84.2 provider header deletion markers are applied before native search requests', async () => {
  const extension = await import(pathToFileURL(resolve('dist/pi-web-search.mjs')))
  const tools = []
  const pi = {
    registerTool(tool) {
      tools.push(tool)
    },
    on() {},
    getActiveTools() {
      return ['web_search']
    },
    setActiveTools() {},
  }
  extension.default(pi)
  const webSearch = tools.find(tool => tool.name === 'web_search')
  assert.ok(webSearch)

  const model = {
    provider: 'openai',
    api: 'openai-responses',
    id: 'test-model',
    name: 'Test model',
    baseUrl: 'https://example.invalid/v1',
    reasoning: false,
    input: ['text'],
    contextWindow: 128_000,
    maxTokens: 8_192,
    headers: {
      'X-Model': 'present',
      'X-Remove': 'must-not-be-sent',
    },
  }
  let requestHeaders
  const originalFetch = globalThis.fetch
  const originalConfig = process.env.PI_WEB_SEARCH_CONFIG
  process.env.PI_WEB_SEARCH_CONFIG = resolve('.missing-web-search-test-config.json')
  globalThis.fetch = async (_url, init) => {
    requestHeaders = new Headers(init.headers)
    return new Response(
      'data: {"type":"response.completed","response":{"output":[]}}\n\n',
      { status: 200, headers: { 'Content-Type': 'text/event-stream' } },
    )
  }

  try {
    const result = await webSearch.execute(
      'tool-1',
      { query: 'test query' },
      new AbortController().signal,
      undefined,
      {
        model,
        modelRegistry: {
          find: () => undefined,
          getAvailable: () => [model],
          getApiKeyAndHeaders: async () => ({
            ok: true,
            apiKey: 'test-api-key',
            headers: {
              'x-remove': null,
              'X-Auth': 'present',
            },
          }),
        },
      },
    )

    assert.equal(result.details.error, undefined)
    assert.equal(requestHeaders.get('x-model'), 'present')
    assert.equal(requestHeaders.get('x-auth'), 'present')
    assert.equal(requestHeaders.has('x-remove'), false)
    assert.equal(requestHeaders.get('authorization'), 'Bearer test-api-key')
  } finally {
    globalThis.fetch = originalFetch
    if (originalConfig === undefined) delete process.env.PI_WEB_SEARCH_CONFIG
    else process.env.PI_WEB_SEARCH_CONFIG = originalConfig
  }
})
