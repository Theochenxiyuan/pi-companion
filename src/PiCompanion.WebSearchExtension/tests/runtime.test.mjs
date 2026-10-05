import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { existsSync } from 'node:fs'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import test from 'node:test'
import { getSupportedThinkingLevels } from '@earendil-works/pi-ai'

const piEntry = resolve('node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js')
const settingsEntry = resolve('../PiCompanion.Extension/pi-settings.mjs')
const companionEntry = resolve('../PiCompanion.Extension/pi-companion.mjs')
const searchEntry = resolve('dist/pi-web-search.mjs')

async function runSettings(agentDir, values) {
  const child = spawn(process.execPath, [settingsEntry], { cwd: agentDir, stdio: ['pipe', 'pipe', 'pipe'] })
  let output = ''
  let errors = ''
  child.stdout.on('data', chunk => { output += chunk })
  child.stderr.on('data', chunk => { errors += chunk })
  const exited = once(child, 'exit')
  child.stdin.end(JSON.stringify({ piEntry, agentDir, ...values }))
  const [code] = await exited
  assert.equal(code, 0, errors)
  return JSON.parse(output)
}

test('settings use the installed Pi model and settings contracts', { timeout: 60_000 }, async () => {
  const agentDir = await mkdtemp(join(tmpdir(), 'pi-companion-settings-'))
  try {
    await writeFile(join(agentDir, 'models.json'), JSON.stringify({ providers: {
      azure: {
        api: 'azure-openai-responses', baseUrl: 'http://127.0.0.1:1', apiKey: 'test-azure-key',
        models: [{ id: 'gpt-probe', name: 'Responses probe', reasoning: false },
          { id: 'deepseek-probe', name: 'Foundry probe', api: 'openai-completions', reasoning: false }],
      },
      'companion-test': {
        api: 'openai-completions', baseUrl: 'http://127.0.0.1:1/v1', apiKey: 'test-key',
        models: [{ id: 'test-model', name: 'Test', reasoning: true,
          thinkingLevelMap: { off: null, minimal: null, xhigh: 'xhigh', max: 'max' } }],
      },
    } }))
    const snapshot = await runSettings(agentDir, { action: 'snapshot' })
    assert.equal(snapshot.version, '1.0.3')
    assert.equal(snapshot.error, null)
    const openai = snapshot.providers.find(provider => provider.id === 'openai')
    assert.equal(openai.supportsApiKey, true)
    assert.equal(openai.supportsOAuth, true)
    assert.ok(snapshot.providers.some(provider => provider.id === 'azure'))
    assert.equal(snapshot.models.find(model => model.provider === 'azure' && model.id === 'deepseek-probe').webSearchSupport, 'none')
    assert.equal(snapshot.models.find(model => model.provider === 'azure' && model.id === 'gpt-probe').webSearchSupport, 'native')
    const model = snapshot.models.find(model => model.provider === 'companion-test')
    assert.deepEqual(model.thinkingLevels, getSupportedThinkingLevels({
      reasoning: true, thinkingLevelMap: { off: null, minimal: null, xhigh: 'xhigh', max: 'max' },
    }))

    const saved = await runSettings(agentDir, {
      action: 'save-agent-defaults', defaultModel: 'companion-test/test-model',
      defaultThinkingLevel: 'max', autoCompact: false, autoRetry: false, cacheWarming: 'off',
      compactionReserveTokens: 8192, compactionKeepRecentTokens: 4096,
      retryMaxRetries: 2, retryBaseDelayMilliseconds: 2000, retryMaxDelayMilliseconds: 5000,
    })
    assert.equal(saved.defaultModel, 'companion-test/test-model')
    assert.equal(saved.defaultThinkingLevel, 'max')
    assert.equal(saved.autoCompact, false)
    assert.equal(saved.autoRetry, false)
    assert.equal(saved.compactionReserveTokens, 8192)
    assert.equal(saved.compactionKeepRecentTokens, 4096)
    assert.equal(saved.retryMaxRetries, 2)
    assert.equal(saved.retryBaseDelayMilliseconds, 2000)
    assert.equal(saved.retryMaxDelayMilliseconds, 5000)
    assert.equal(saved.steeringMode, 'one-at-a-time')
    assert.equal(saved.followUpMode, 'one-at-a-time')
  } finally {
    await rm(agentDir, { recursive: true, force: true })
  }
})

test('OpenAI browser login persists its device ID across cancellation and handles callback errors', { timeout: 60_000 }, async () => {
  const agentDir = await mkdtemp(join(tmpdir(), 'pi-companion-oauth-'))
  let deviceId
  try {
    for (const outcome of ['cancel', 'denied']) {
      const child = spawn(process.execPath, [settingsEntry], {
        cwd: agentDir,
        env: { ...process.env, PI_OAUTH_CALLBACK_HOST: '127.0.0.2' },
        stdio: ['pipe', 'pipe', 'pipe'],
      })
      let errors = ''
      child.stderr.on('data', chunk => { errors += chunk })
      const closed = once(child, 'close')
      try {
        const authorization = new Promise((resolve, reject) => {
          let buffer = ''
          const timer = setTimeout(() => reject(new Error('OAuth startup timed out')), 30_000)
          child.once('error', error => { clearTimeout(timer); reject(error) })
          child.once('close', () => {
            clearTimeout(timer)
            reject(new Error(`OAuth exited before browser authorization: ${errors}`))
          })
          child.stdout.setEncoding('utf8')
          child.stdout.on('data', chunk => {
            buffer += chunk
            let boundary
            while ((boundary = buffer.indexOf('\n')) !== -1) {
              const frame = JSON.parse(buffer.slice(0, boundary))
              buffer = buffer.slice(boundary + 1)
              if (frame.kind === 'event' && frame.event.type === 'auth_url') {
                clearTimeout(timer)
                resolve(new URL(frame.event.url))
              }
            }
          })
        })
        child.stdin.end(JSON.stringify({ piEntry, agentDir, action: 'login-oauth', providerId: 'openai' }))
        const url = await authorization
        assert.equal(url.origin, 'https://auth.openai.com')
        assert.equal(url.searchParams.get('client_id'), 'dynamic_agent_client')
        assert.equal(url.searchParams.get('redirect_uri'), 'http://127.0.0.1:1455/auth/callback')
        const settings = JSON.parse(await readFile(join(agentDir, 'settings.json'), 'utf8'))
        assert.match(settings.deviceId, /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu)
        assert.equal(url.searchParams.get('ext_agent_host_id'), `urn:uuid:${settings.deviceId}`)
        if (deviceId) assert.equal(settings.deviceId, deviceId)
        deviceId = settings.deviceId
        if (outcome === 'cancel') {
          child.kill()
          await closed
        } else {
          const response = await fetch('http://127.0.0.2:1455/auth/callback?error=access_denied')
          assert.equal(response.status, 400)
          await response.text()
          const [code] = await closed
          assert.equal(code, 1)
          assert.match(errors, /ChatGPT authorization failed: access_denied/u)
        }
        const auth = existsSync(join(agentDir, 'auth.json'))
          ? JSON.parse(await readFile(join(agentDir, 'auth.json'), 'utf8')) : {}
        assert.equal(auth.openai, undefined)
      } finally {
        if (child.exitCode === null && child.signalCode === null) child.kill()
        await closed
      }
    }
  } finally {
    await rm(agentDir, { recursive: true, force: true })
  }
})

test('Pi RPC runs Companion tools with permissions and extension UI', { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'pi-companion-rpc-'))
  const agentDir = join(root, 'agent')
  const events = []
  let requestCount = 0
  let toolResults = []
  let exposedTools = []
  const tools = [
    { name: 'read', arguments: { path: 'sample.txt' } },
    { name: 'write', arguments: { path: 'blocked.txt', content: 'blocked' } },
    { name: 'ask_user', arguments: { question: '继续？', choices: ['继续', '停止'], allowOther: false, placeholder: '' } },
  ]
  const server = createServer(async (request, response) => {
    let body = ''
    for await (const chunk of request) body += chunk
    const payload = JSON.parse(body)
    requestCount += 1
    if (requestCount === 1) exposedTools = payload.tools.map(tool => tool.function.name)
    if (requestCount > 1) toolResults = payload.messages.filter(message => message.role === 'tool')
    response.writeHead(200, { 'Content-Type': 'text/event-stream' })
    const send = (delta, finish_reason = null) => response.write(`data: ${JSON.stringify({
      id: 'test-response', object: 'chat.completion.chunk', created: 1, model: 'test-model',
      choices: [{ index: 0, delta, finish_reason }],
    })}\n\n`)
    if (requestCount === 1) {
      send({ role: 'assistant', tool_calls: tools.map((tool, index) => ({
        index, id: `tool-${index}`, type: 'function',
        function: { name: tool.name, arguments: JSON.stringify(tool.arguments) },
      })) })
      send({}, 'tool_calls')
    } else {
      send({ role: 'assistant', content: '完成' })
      send({}, 'stop')
    }
    response.end('data: [DONE]\n\n')
  })
  let child
  try {
    await mkdir(agentDir)
    await writeFile(join(root, 'sample.txt'), 'workspace content\u2028next line\u2029next paragraph')
    server.listen(0, '127.0.0.1')
    await once(server, 'listening')
    await writeFile(join(agentDir, 'models.json'), JSON.stringify({ providers: {
      'companion-test': {
        api: 'openai-completions', baseUrl: `http://127.0.0.1:${server.address().port}/v1`, apiKey: 'test-key',
        models: [{ id: 'test-model', name: 'Test', reasoning: false }],
      },
    } }))
    const sentinel = join(root, 'mcp-started.txt')
    await writeFile(join(agentDir, 'mcp.json'), JSON.stringify({ mcpServers: {
      test: { command: process.execPath, args: ['-e', `require('fs').writeFileSync(${JSON.stringify(sentinel)}, 'started')`] },
    } }))
    const inputExtension = join(root, 'handled.mjs')
    await writeFile(inputExtension, `export default pi => pi.on('input', event =>
      event.text === 'handled-input' ? { action: 'handled' } : undefined)`)
    child = spawn(process.execPath, [piEntry,
      '--mode', 'rpc', '--no-extensions', '--extension', companionEntry, '--extension', searchEntry,
      '--extension', inputExtension, '--no-prompt-templates', '--no-context-files', '--no-approve',
      '--tools', 'read,write,ask_user', '--thinking', 'off', '--model', 'companion-test/test-model',
      '--session-dir', join(root, 'sessions'),
    ], { cwd: root, env: { ...process.env,
      PI_CODING_AGENT_DIR: agentDir, PI_COMPANION_WORKING_DIRECTORY: root,
      PI_COMPANION_PERMISSION_MODE: 'read-only', PI_COMPANION_LANGUAGE: 'zh-CN',
      PI_COMPANION_CONTEXT_FILE: '', PI_COMPANION_RUN_ID_FILE: '',
    }, stdio: ['pipe', 'pipe', 'pipe'] })
    let errors = ''
    child.stderr.on('data', chunk => { errors += chunk })
    const pending = new Set()
    const onFrame = line => {
      const event = JSON.parse(line)
      events.push(event)
      for (const waiter of pending) if (waiter.matches(event)) {
        pending.delete(waiter)
        clearTimeout(waiter.timer)
        waiter.resolve(event)
      }
      if (event.type === 'extension_ui_request' && event.method === 'select') {
        child.stdin.write(`${JSON.stringify({ type: 'extension_ui_response', id: event.id, value: '继续' })}\n`)
      }
    }
    let buffer = ''
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', chunk => {
      buffer += chunk
      let boundary
      while ((boundary = buffer.indexOf('\n')) !== -1) {
        const frame = buffer.slice(0, boundary)
        buffer = buffer.slice(boundary + 1)
        if (frame.trim()) onFrame(frame)
      }
    })
    const waitFor = matches => {
      const existing = events.find(matches)
      if (existing) return Promise.resolve(existing)
      return new Promise((resolve, reject) => {
        const waiter = { matches, resolve, timer: setTimeout(() => {
          pending.delete(waiter)
          reject(new Error(`RPC timed out: ${errors}`))
        }, 30_000) }
        pending.add(waiter)
      })
    }
    const command = async (id, type, message) => {
      child.stdin.write(`${JSON.stringify({ id, type, message })}\n`)
      const response = await waitFor(event => event.id === id && event.type === 'response')
      assert.equal(response.success, true, JSON.stringify(response))
      return response
    }
    for (const type of ['prompt', 'steer', 'follow_up']) {
      const response = await command(type, type, 'handled-input')
      assert.equal(response.data.disposition, 'handled')
    }
    const response = await command('run', 'prompt', 'Read the file, try to write, and ask the user.')
    assert.equal(response.data.disposition, 'started')
    await waitFor(event => event.type === 'agent_settled')
    assert.equal(requestCount, 2)
    assert.deepEqual(exposedTools.sort(), ['ask_user', 'read', 'write'])
    assert.equal(toolResults.length, 3)
    assert.match(toolResults[0].content, /workspace content/u)
    assert.match(toolResults[0].content, /\u2028next line\u2029next paragraph/u)
    assert.match(toolResults[1].content, /只读/u)
    assert.match(toolResults[2].content, /继续/u)
    assert.equal(existsSync(join(root, 'blocked.txt')), false)
    assert.equal(existsSync(sentinel), false)
    assert.equal(events.filter(event => event.type === 'tool_execution_end').length, 3)
    assert.equal(events.some(event => event.type === 'extension_error'), false, errors)
    const state = await command('state', 'get_state')
    assert.equal(state.data.isStreaming, false)
    const stats = await command('stats', 'get_session_stats')
    assert.equal(stats.data.toolCalls, 3)
    const history = await command('history', 'get_entries')
    assert.ok(history.data.entries.length > 0)
  } finally {
    if (child) {
      const exited = once(child, 'close')
      child.kill()
      await exited
    }
    server.closeAllConnections()
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})
