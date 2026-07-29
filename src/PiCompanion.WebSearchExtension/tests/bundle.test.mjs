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
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'google',
    api: 'google-generative-ai',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'anthropic',
    api: 'anthropic-messages',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'openai-codex',
    api: 'openai-codex-responses',
  }), 'native')
  assert.equal(extension.getPiCompanionWebSearchSupport({
    provider: 'company-proxy',
    api: 'openai-responses',
  }), 'none')
})
