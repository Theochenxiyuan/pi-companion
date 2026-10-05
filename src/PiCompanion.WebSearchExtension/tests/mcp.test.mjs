import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { existsSync } from 'node:fs'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import test from 'node:test'

const piEntry = resolve('node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js')
const helper = resolve('../PiCompanion.Extension/pi-mcp.mjs')
const extension = resolve('../PiCompanion.Extension/pi-companion.mjs')
const fixture = resolve('tests/fixtures/mcp-server.mjs')

async function manage(agentDir, cwd, values, success = true) {
  const child = spawn(process.execPath, [helper], { cwd, stdio: ['pipe', 'pipe', 'pipe'] })
  let output = ''
  let errors = ''
  child.stdout.on('data', chunk => { output += chunk })
  child.stderr.on('data', chunk => { errors += chunk })
  const closed = once(child, 'close')
  child.stdin.end(`${JSON.stringify({ piEntry, agentDir, cwd, workspaceSelected: true, projectTrusted: true, ...values })}\n`)
  const [code] = await closed
  assert.equal(code, success ? 0 : 1, errors || output)
  const frames = output.trim().split('\n').map(line => JSON.parse(line))
  return success ? frames.find(frame => frame.kind === 'result').snapshot : frames.find(frame => frame.kind === 'error').message
}

test('MCP management preserves scope, rejects stale writes and validates an entire import', { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'companion-mcp-management-'))
  const agentDir = join(root, 'agent')
  try {
    await mkdir(agentDir)
    await writeFile(join(agentDir, 'mcp.json'), JSON.stringify({ extra: { keep: true }, mcpServers: {} }))
    let snapshot = await manage(agentDir, root, { action: 'list' })
    const config = { command: process.execPath, args: [fixture], exposure: 'deferred', env: { TEST_VALUE: '${PATH}' } }
    snapshot = await manage(agentDir, root, { action: 'save', scope: 'global', name: 'docs', config, revision: snapshot.globalRevision, check: true })
    assert.equal(snapshot.servers[0].state, 'connected', snapshot.servers[0].error)
    assert.equal(snapshot.servers[0].tools.length, 3)
    assert.deepEqual(JSON.parse(await readFile(join(agentDir, 'mcp.json'), 'utf8')).extra, { keep: true })
    assert.match(await manage(agentDir, root, { action: 'delete', scope: 'global', name: 'docs', revision: 'stale' }, false), /刷新/u)
    const before = await readFile(join(agentDir, 'mcp.json'), 'utf8')
    assert.match(await manage(agentDir, root, { action: 'import', scope: 'global', revision: snapshot.globalRevision, servers: { good: config, bad: { url: 'file:///bad' } } }, false), /url/iu)
    assert.equal(await readFile(join(agentDir, 'mcp.json'), 'utf8'), before)
    assert.match(await manage(agentDir, root, { action: 'import', scope: 'global', revision: snapshot.globalRevision, servers: { docs: config } }, false), /同名/u)
    assert.match(await manage(agentDir, root, { action: 'save', scope: 'project', projectTrusted: false, name: 'local', config, revision: snapshot.projectRevision }, false), /信任/u)
    snapshot = await manage(agentDir, root, { action: 'save', scope: 'project', name: 'docs', config: { ...config, enabled: false }, revision: snapshot.projectRevision })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'disabled')
    assert.equal(snapshot.servers.find(server => server.scope === 'global').state, 'overridden')
    snapshot = await manage(agentDir, root, { action: 'toggle', scope: 'project', name: 'docs', enabled: true, revision: snapshot.projectRevision, check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'connected')
    snapshot = await manage(agentDir, root, { action: 'list', projectTrusted: false, check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'untrusted')
    assert.equal(snapshot.servers.find(server => server.scope === 'global').state, 'connected')
    snapshot = await manage(agentDir, root, { action: 'delete', scope: 'project', name: 'docs', revision: snapshot.projectRevision })
    assert.equal(snapshot.servers.filter(server => server.scope === 'global').length, 1)
    assert.equal(snapshot.servers[0].scope, 'global')
    assert.equal(snapshot.servers.find(server => server.scope === 'project').inherited, true)
  } finally { await rm(root, { recursive: true, force: true }) }
})

test('workspace overrides keep personal connection settings and reject conflicting namespaces', { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'companion-mcp-overrides-'))
  const agentDir = join(root, 'agent')
  try {
    await mkdir(agentDir)
    const config = { command: process.execPath, args: [fixture], exposure: 'deferred', env: { PRIVATE_TOKEN: 'personal-only' } }
    await writeFile(join(agentDir, 'mcp.json'), JSON.stringify({ mcpServers: { docs: config } }))
    const original = await readFile(join(agentDir, 'mcp.json'), 'utf8')
    let snapshot = await manage(agentDir, root, { action: 'list', check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').inherited, true)
    snapshot = await manage(agentDir, root, { action: 'toggle', scope: 'project', name: 'docs', enabled: false, revision: snapshot.projectRevision })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'disabled')
    assert.deepEqual(JSON.parse(await readFile(join(root, '.pi', 'mcp.json'), 'utf8')).mcpServers.docs, { enabled: false })
    snapshot = await manage(agentDir, root, { action: 'save', scope: 'project', name: 'docs', originalName: 'docs', config: { enabled: true, exposure: 'codemode', toolExposure: { read_probe: 'direct' } }, revision: snapshot.projectRevision, check: true })
    const project = snapshot.servers.find(server => server.scope === 'project')
    assert.equal(project.state, 'connected', project.error)
    assert.equal(project.config.command, process.execPath)
    assert.equal(project.config.env.PRIVATE_TOKEN, 'personal-only')
    assert.equal(project.isOverride, true)
    const saved = JSON.parse(await readFile(join(root, '.pi', 'mcp.json'), 'utf8')).mcpServers.docs
    assert.deepEqual(Object.keys(saved).sort(), ['enabled', 'exposure', 'toolExposure'])
    assert.equal(await readFile(join(agentDir, 'mcp.json'), 'utf8'), original)
    assert.match(await manage(agentDir, root, { action: 'import', scope: 'project', servers: { missing: { enabled: false } }, revision: snapshot.projectRevision }, false), /个人服务/u)
    assert.match(await manage(agentDir, root, { action: 'save', scope: 'project', name: 'docs', originalName: 'docs', config: { enabled: true, headers: { Authorization: 'secret' } }, revision: snapshot.projectRevision }, false), /覆盖/u)
    assert.match(await manage(agentDir, root, { action: 'save', scope: 'project', name: 'unsafe', config: { url: 'https://example.com/mcp', auth: { provider: 'openai' } }, revision: snapshot.projectRevision }, false), /个人配置/u)
    assert.match(await manage(agentDir, root, { action: 'import', scope: 'global', servers: { 'same-name': config, same_name: config }, revision: snapshot.globalRevision }, false), /名称冲突/u)
    snapshot = await manage(agentDir, root, { action: 'list', projectTrusted: false, check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'global').state, 'connected')
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'untrusted')
    snapshot = await manage(agentDir, root, { action: 'delete', scope: 'project', name: 'docs', revision: snapshot.projectRevision, check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').inherited, true)
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'connected')
    assert.equal(snapshot.servers.find(server => server.scope === 'project').config.exposure, 'deferred')
  } finally { await rm(root, { recursive: true, force: true }) }
})

test('inherited MCP servers reuse provider credentials without copying them into the project', { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'companion-mcp-provider-'))
  const agentDir = join(root, 'agent')
  const headers = []
  const server = createServer(async (request, response) => {
    headers.push(request.headers.authorization)
    let body = ''
    for await (const chunk of request) body += chunk
    if (request.headers.authorization !== 'Bearer owner-token') { response.writeHead(401); response.end(); return }
    if (request.method !== 'POST') { response.writeHead(request.method === 'DELETE' ? 200 : 405); response.end(); return }
    const frame = JSON.parse(body)
    if (!Object.hasOwn(frame, 'id')) { response.writeHead(202); response.end(); return }
    const result = frame.method === 'initialize'
      ? { protocolVersion: frame.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'test', version: '1' } }
      : { tools: [] }
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({ jsonrpc: '2.0', id: frame.id, result }))
  })
  try {
    await mkdir(agentDir)
    server.listen(0, '127.0.0.1'); await once(server, 'listening')
    const url = `http://127.0.0.1:${server.address().port}/mcp`
    await writeFile(join(agentDir, 'models.json'), JSON.stringify({ providers: { account: {
      api: 'openai-completions', baseUrl: 'http://127.0.0.1:1', apiKey: 'owner-token', models: [{ id: 'probe', name: 'Probe', reasoning: false }],
    } } }))
    await writeFile(join(agentDir, 'mcp.json'), JSON.stringify({ mcpServers: { docs: { url, auth: { provider: 'account' } } } }))
    let snapshot = await manage(agentDir, root, { action: 'list', check: true })
    assert.equal(snapshot.servers[0].state, 'connected', snapshot.servers[0].error)
    snapshot = await manage(agentDir, root, { action: 'save', scope: 'project', name: 'docs', config: { exposure: 'deferred' }, revision: snapshot.projectRevision, check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'project').state, 'connected')
    assert.deepEqual(JSON.parse(await readFile(join(root, '.pi', 'mcp.json'), 'utf8')).mcpServers.docs, { exposure: 'deferred' })
    assert.ok(headers.length > 0)
    assert.ok(headers.every(value => value === 'Bearer owner-token'))
  } finally {
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('remote MCP sign-in accepts a pasted callback and isolates accounts sharing a URL', { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'companion-mcp-oauth-'))
  let origin
  let tokenRequests = 0
  let child
  const server = createServer(async (request, response) => {
    let body = ''
    for await (const chunk of request) body += chunk
    const send = (value, status = 200, headers = {}) => {
      response.writeHead(status, { 'Content-Type': 'application/json', ...headers })
      response.end(JSON.stringify(value))
    }
    if (request.url.startsWith('/.well-known/oauth-protected-resource')) return send({ resource: `${origin}/mcp`, authorization_servers: [origin], scopes_supported: ['read'] })
    if (request.url.startsWith('/.well-known/oauth-authorization-server')) return send({
      issuer: origin, authorization_endpoint: `${origin}/authorize`, token_endpoint: `${origin}/token`, registration_endpoint: `${origin}/register`,
      response_types_supported: ['code'], grant_types_supported: ['authorization_code', 'refresh_token'], code_challenge_methods_supported: ['S256'],
    })
    if (request.url === '/register') return send({ client_id: 'test-client', ...JSON.parse(body) }, 201)
    if (request.url === '/token') {
      tokenRequests++
      assert.equal(new URLSearchParams(body).get('code'), 'test-code')
      return send({ access_token: 'test-access', refresh_token: 'test-refresh', token_type: 'Bearer', expires_in: 3600, scope: 'read' })
    }
    if (request.url === '/mcp') {
      if (request.headers.authorization !== 'Bearer test-access') return send({ error: 'unauthorized' }, 401, { 'WWW-Authenticate': `Bearer resource_metadata="${origin}/.well-known/oauth-protected-resource"` })
      if (request.method === 'GET') return send({}, 405, { Allow: 'POST' })
      if (request.method === 'DELETE') return send({})
      const frame = JSON.parse(body)
      if (!Object.hasOwn(frame, 'id')) { response.writeHead(202); response.end(); return }
      const result = frame.method === 'initialize'
        ? { protocolVersion: frame.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'test', version: '1' } }
        : { tools: [] }
      return send({ jsonrpc: '2.0', id: frame.id, result })
    }
    send({}, 404)
  })
  try {
    server.listen(0, '127.0.0.1'); await once(server, 'listening')
    origin = `http://127.0.0.1:${server.address().port}`
    await writeFile(join(root, 'mcp.json'), JSON.stringify({ mcpServers: {
      remote: { url: `${origin}/mcp`, exposure: 'deferred' }, second: { url: `${origin}/mcp`, exposure: 'deferred' },
    } }))
    let snapshot = await manage(root, root, { action: 'list', check: true })
    assert.equal(snapshot.servers[0].state, 'needs-auth')
    for (const name of ['remote', 'second']) {
    child = spawn(process.execPath, [helper], { cwd: root, stdio: ['pipe', 'pipe', 'pipe'] })
    const closed = once(child, 'close')
    let errors = ''
    let output = ''
    let buffer = ''
    let callback
    child.stderr.on('data', chunk => { errors += chunk })
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', chunk => {
      output += chunk; buffer += chunk
      let boundary
      while ((boundary = buffer.indexOf('\n')) !== -1) {
        const frame = JSON.parse(buffer.slice(0, boundary)); buffer = buffer.slice(boundary + 1)
        if (frame.kind === 'event') {
          const authorization = new URL(frame.url)
          assert.equal(authorization.origin, origin)
          callback = new URL(authorization.searchParams.get('redirect_uri'))
          callback.searchParams.set('code', 'test-code')
          callback.searchParams.set('state', authorization.searchParams.get('state'))
          child.stdin.write(`${JSON.stringify({ redirectUrl: callback.toString() })}\n`)
        }
      }
    })
    child.stdin.write(`${JSON.stringify({ piEntry, agentDir: root, cwd: root, action: 'login', scope: 'global', name })}\n`)
    const [code] = await closed
    assert.equal(code, 0, errors || output)
    assert.ok(callback)
    assert.equal(tokenRequests, name === 'remote' ? 1 : 2)
    snapshot = JSON.parse(output.trim().split('\n').at(-1)).snapshot
    assert.equal(snapshot.servers.find(server => server.scope === 'global' && server.name === name).state, 'connected')
    snapshot = await manage(root, root, { action: 'list', check: true })
    assert.equal(snapshot.servers.find(server => server.scope === 'global' && server.name === 'second').state, name === 'remote' ? 'needs-auth' : 'connected')
    }
    snapshot = await manage(root, root, { action: 'list', check: true })
    assert.equal(snapshot.servers[0].state, 'connected')
    assert.equal(tokenRequests, 2)
    await manage(root, root, { action: 'logout', scope: 'global', name: 'remote' })
    snapshot = await manage(root, root, { action: 'list', check: true })
    assert.equal(snapshot.servers[0].state, 'needs-auth')
    assert.equal(snapshot.servers.find(server => server.scope === 'global' && server.name === 'second').state, 'connected')
  } finally {
    if (child && child.exitCode === null && child.signalCode === null) { const closed = once(child, 'close'); child.kill(); await closed }
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

for (const scenario of ['codemode', 'deferred', 'cancel']) test(`MCP ${scenario} preserves discovery, approval and read-only restrictions`, { timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), 'companion-mcp-rpc-'))
  const agentDir = join(root, 'agent')
  const log = join(root, 'calls.jsonl')
  let child
  let requestCount = 0
  const script = `const results = await Promise.allSettled([
    tools.mcp__fixture__read_probe({value:'first'}),
    tools.mcp__fixture__read_other({value:'second'}),
    tools.mcp__fixture__write_probe({value:'blocked'})
  ]); for (const result of results) text(result.status === 'fulfilled' ? result.value : String(result.reason));`
  let results
  let toolNames
  let loadedToolNames
  const server = createServer(async (request, response) => {
    let body = ''
    for await (const chunk of request) body += chunk
    const payload = JSON.parse(body)
    requestCount++
    response.writeHead(200, { 'Content-Type': 'text/event-stream' })
    const send = (delta, finish_reason = null) => response.write(`data: ${JSON.stringify({
      id: 'test', object: 'chat.completion.chunk', created: 1, model: 'test', choices: [{ index: 0, delta, finish_reason }],
    })}\n\n`)
    if (requestCount === 1) {
      toolNames = payload.tools.map(tool => tool.function.name)
      const name = scenario === 'deferred' ? 'tool_search' : 'codemode'
      const args = scenario === 'deferred' ? { query: 'read_probe', limit: 1 } : { code: script }
      send({ role: 'assistant', tool_calls: [{ index: 0, id: 'batch', type: 'function', function: { name, arguments: JSON.stringify(args) } }] })
      send({}, 'tool_calls')
    } else if (scenario === 'deferred' && requestCount === 2) {
      loadedToolNames = payload.tools.map(tool => tool.function.name)
      send({ role: 'assistant', tool_calls: [{ index: 0, id: 'read', type: 'function', function: { name: 'mcp__fixture__read_probe', arguments: JSON.stringify({ value: 'discovered' }) } }] })
      send({}, 'tool_calls')
    } else {
      results = payload.messages.filter(message => message.role === 'tool')
      send({ role: 'assistant', content: 'done' }); send({}, 'stop')
    }
    response.end('data: [DONE]\n\n')
  })
  try {
    await mkdir(agentDir)
    server.listen(0, '127.0.0.1'); await once(server, 'listening')
    await writeFile(join(agentDir, 'models.json'), JSON.stringify({ providers: { test: {
      api: 'openai-completions', baseUrl: `http://127.0.0.1:${server.address().port}/v1`, apiKey: 'test',
      models: [{ id: 'test', name: 'test', reasoning: false }],
    } } }))
    await writeFile(join(agentDir, 'mcp.json'), JSON.stringify({ mcpServers: { fixture: { command: process.execPath, args: [fixture, log], ...(scenario === 'deferred' ? { exposure: 'deferred' } : {}) } } }))
    child = spawn(process.execPath, [piEntry, '--mode', 'rpc', '--no-extensions',
      '-e', 'builtin:mcp', '-e', 'builtin:codemode', '-e', 'builtin:tool-search', '-e', extension,
      '--no-prompt-templates', '--no-context-files', '--no-approve',
      '--thinking', 'off', '--model', 'test/test', '--session-dir', join(root, 'sessions'),
    ], { cwd: root, env: { ...process.env, PI_CODING_AGENT_DIR: agentDir,
      PI_COMPANION_WORKING_DIRECTORY: root, PI_COMPANION_PERMISSION_MODE: 'read-only',
      PI_COMPANION_CONTEXT_FILE: '', PI_COMPANION_RUN_ID_FILE: '', PI_COMPANION_LANGUAGE: 'zh-CN',
      PI_COMPANION_ACTIVE_TOOLS: 'read,write,ask_user',
    }, stdio: ['pipe', 'pipe', 'pipe'] })
    let buffer = ''
    let errors = ''
    let approvals = 0
    let activeApprovals = 0
    let maximumApprovals = 0
    const events = []
    child.stderr.on('data', chunk => { errors += chunk })
    const settled = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`RPC timeout: ${errors}`)), 30_000)
      child.once('close', () => { clearTimeout(timer); reject(new Error(`RPC exited: ${errors}`)) })
      child.stdout.setEncoding('utf8')
      child.stdout.on('data', chunk => {
        buffer += chunk
        let boundary
        while ((boundary = buffer.indexOf('\n')) !== -1) {
          const event = JSON.parse(buffer.slice(0, boundary)); buffer = buffer.slice(boundary + 1)
          events.push(event)
          if (event.type === 'extension_ui_request' && event.method === 'select') {
            approvals++; activeApprovals++; maximumApprovals = Math.max(maximumApprovals, activeApprovals)
            assert.match(event.title, /服务操作请求/u)
            if (scenario === 'cancel') {
              child.stdin.write(`${JSON.stringify({ id: 'cancel', type: 'abort' })}\n`)
              continue
            }
            setTimeout(() => {
              activeApprovals--
              child.stdin.write(`${JSON.stringify({ type: 'extension_ui_response', id: event.id, value: '允许一次' })}\n`)
            }, 50)
          }
          if (event.type === 'agent_settled') { clearTimeout(timer); resolve() }
        }
      })
    })
    child.stdin.write(`${JSON.stringify({ id: 'run', type: 'prompt', message: 'Run all probe tools in codemode.' })}\n`)
    await settled
    if (scenario === 'cancel') {
      assert.equal(approvals, 1, JSON.stringify(events.filter(event => event.type === 'extension_ui_request')))
      assert.equal(existsSync(log), false, 'Canceled calls must not reach the MCP server')
      return
    }
    if (scenario === 'deferred') {
      assert.ok(toolNames.includes('tool_search'))
      assert.equal(toolNames.includes('mcp__fixture__read_probe'), false)
      assert.ok(loadedToolNames.includes('mcp__fixture__read_probe'))
      assert.equal(approvals, 1)
      assert.equal(requestCount, 3)
      assert.match(results.at(-1).content, /read_probe:discovered/u)
      const calls = (await readFile(log, 'utf8')).trim().split('\n').map(line => JSON.parse(line))
      assert.deepEqual(calls.map(call => call.name), ['read_probe'])
      return
    }
    assert.ok(toolNames.includes('codemode'), JSON.stringify(events.filter(event => event.type === 'extension_ui_request' || event.type === 'extension_error')))
    assert.equal(requestCount, 2)
    assert.equal(approvals, 2, JSON.stringify(results))
    assert.equal(maximumApprovals, 1)
    assert.equal(events.some(event => event.type === 'extension_error'), false, errors)
    const nested = events.filter(event => event.type === 'tool_execution_end' && event.parentToolCallId === 'batch')
    assert.equal(nested.length, 3)
    assert.equal(nested.find(event => event.toolName.endsWith('write_probe')).isError, true)
    assert.match(results[0].content, /read_probe:first/u)
    assert.match(results[0].content, /read_other:second/u)
    assert.equal(existsSync(log), true)
    const calls = (await readFile(log, 'utf8')).trim().split('\n').map(line => JSON.parse(line))
    assert.deepEqual(calls.map(call => call.name).sort(), ['read_other', 'read_probe'])
  } finally {
    if (child) { const closed = once(child, 'close'); child.kill(); await closed }
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})
