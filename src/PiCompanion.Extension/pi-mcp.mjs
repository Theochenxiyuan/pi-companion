import { createHash, randomUUID } from 'node:crypto'
import { existsSync } from 'node:fs'
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { basename, dirname, join } from 'node:path'
import { createInterface } from 'node:readline'
import { pathToFileURL } from 'node:url'

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
const iterator = lines[Symbol.asyncIterator]()
const input = JSON.parse((await iterator.next()).value.replace(/^\uFEFF/u, ''))
const entryDirectory = dirname(input.piEntry)
const dist = basename(entryDirectory) === 'bundle' ? dirname(entryDirectory) : entryDirectory
const moduleAt = relative => import(pathToFileURL(join(dist, relative)).href)
const { getAgentDir } = await moduleAt('config.js')
const { mcpNamespace, validateMcpServerConfig } = await moduleAt('core/mcp-servers.js')
const { loadMcpConfig } = await moduleAt('extensions/mcp/config.js')
const agentDir = input.agentDir ?? getAgentDir()
const cwd = input.cwd ?? process.cwd()
const paths = { global: join(agentDir, 'mcp.json'), project: join(cwd, '.pi', 'mcp.json') }
const send = value => process.stdout.write(`${JSON.stringify(value)}\n`)
const connections = []
let runtime
let modelRuntime
const overrideKeys = ['enabled', 'exposure', 'toolExposure']
const isRecord = value => !!value && typeof value === 'object' && !Array.isArray(value)

function resolveConfig(name, raw, scope, globals) {
  const isOverride = scope === 'project' && isRecord(raw) && raw.command === undefined && raw.url === undefined && raw.type === undefined
  let value = raw
  if (isOverride) {
    if (Object.keys(raw).some(key => !overrideKeys.includes(key))) return { error: '工作区覆盖只能修改启用状态和调用方式。' }
    const base = validateMcpServerConfig(name, globals[name])
    if (typeof base === 'string') return { error: `找不到可用的个人服务：${name}` }
    value = { ...base, ...raw }
  }
  const config = validateMcpServerConfig(name, value)
  if (typeof config === 'string') return { error: config }
  if (scope === 'project' && !isOverride && config.auth) return { error: '服务账号只能在个人配置中使用。' }
  const storedConfig = isOverride ? Object.fromEntries(Object.keys(raw).map(key => [key, config[key]])) : config
  return { config, storedConfig, isOverride }
}

async function readConfig(scope) {
  const text = existsSync(paths[scope]) ? await readFile(paths[scope], 'utf8') : ''
  const data = text ? JSON.parse(text.replace(/^\uFEFF/u, '')) : {}
  if (!data || typeof data !== 'object' || Array.isArray(data) ||
    (data.mcpServers !== undefined && (!data.mcpServers || typeof data.mcpServers !== 'object' || Array.isArray(data.mcpServers)))) {
    throw new Error('服务配置无法读取，请检查配置文件。')
  }
  return { data, text, revision: createHash('sha256').update(text).digest('hex') }
}

async function mutate() {
  if (!['global', 'project'].includes(input.scope)) throw new Error('请选择保存位置。')
  if (input.scope === 'project' && !input.projectTrusted) throw new Error('请先信任工作区。')
  const current = await readConfig(input.scope)
  if (input.revision !== current.revision) throw new Error('配置已在其他位置更改，请刷新后重试。')
  const servers = current.data.mcpServers ?? Object.create(null)
  const globals = input.scope === 'global' ? servers : (await readConfig('global')).data.mcpServers ?? {}
  if (input.action === 'save' || input.action === 'import') {
    const changes = input.action === 'save' ? { [input.name]: input.config } : input.servers
    if (!changes || typeof changes !== 'object' || Array.isArray(changes) || !Object.keys(changes).length) {
      throw new Error('请提供至少一个服务。')
    }
    for (const [name, config] of Object.entries(changes)) {
      const resolved = resolveConfig(name, config, input.scope, globals)
      if (resolved.error) throw new Error(resolved.error)
      if (Object.hasOwn(servers, name) && (input.action === 'import' || input.originalName !== name)) {
        throw new Error(`已存在同名服务：${name}`)
      }
      if (input.action === 'save' && input.originalName && input.originalName !== name) throw new Error('服务名称不可更改。')
      if (input.action === 'save' && input.originalName && !Object.hasOwn(servers, input.originalName) && !resolved.isOverride) {
        throw new Error('服务已被移除，请刷新后重试。')
      }
    }
    for (const [name, config] of Object.entries(changes)) Object.defineProperty(servers, name, { value: resolveConfig(name, config, input.scope, globals).storedConfig, writable: true, enumerable: true, configurable: true })
  } else {
    if (input.action === 'toggle' && input.scope === 'project' && !Object.hasOwn(servers, input.name) && Object.hasOwn(globals, input.name)) {
      Object.defineProperty(servers, input.name, { value: {}, writable: true, enumerable: true, configurable: true })
    }
    if (!Object.hasOwn(servers, input.name)) throw new Error('服务已被移除，请刷新后重试。')
    if (input.action === 'delete') delete servers[input.name]
    else if (input.action === 'toggle') {
      const resolved = resolveConfig(input.name, { ...servers[input.name], enabled: input.enabled === true }, input.scope, globals)
      if (resolved.error) throw new Error(resolved.error)
      servers[input.name] = resolved.storedConfig
    }
    else throw new Error('未知的服务操作。')
  }
  const namespaces = new Map()
  for (const [name, config] of Object.entries(input.scope === 'project' ? { ...globals, ...servers } : servers)) {
    const resolved = resolveConfig(name, config, input.scope === 'project' && !Object.hasOwn(servers, name) ? 'global' : input.scope, globals)
    if (resolved.error) continue
    const namespace = mcpNamespace(name)
    if (namespaces.has(namespace)) throw new Error(`服务名称冲突：${name}、${namespaces.get(namespace)}`)
    namespaces.set(namespace, name)
  }
  current.data.mcpServers = servers
  await mkdir(dirname(paths[input.scope]), { recursive: true })
  const temporary = `${paths[input.scope]}.${randomUUID()}.tmp`
  try {
    await writeFile(temporary, `${JSON.stringify(current.data, null, '  ')}\n`, { mode: 0o600 })
    await rename(temporary, paths[input.scope])
  } finally {
    await rm(temporary, { force: true })
  }
}

async function createConnection(entry) {
  runtime ??= await moduleAt('extensions/mcp/runtime.js')
  const { FileAuthStorageBackend } = await moduleAt('core/auth-storage.js')
  const credentials = new runtime.McpOAuthCredentialStore(new FileAuthStorageBackend(join(agentDir, 'mcp-auth.json')), agentDir)
  const connection = new runtime.McpServerConnection({
    entry, cwd, credentials, createTransport: runtime.createDefaultTransport, onTools() {},
    async providerToken(provider) {
      const { ModelRuntime } = await moduleAt('core/model-runtime.js')
      modelRuntime ??= ModelRuntime.create({ authPath: join(agentDir, 'auth.json'), modelsPath: join(agentDir, 'models.json'), allowModelNetwork: false })
      return (await (await modelRuntime).getAuth(provider))?.auth.apiKey
    },
  })
  connections.push(connection)
  return { connection, credentials }
}

async function probe(entry) {
  const { connection } = await createConnection(entry)
  let timer
  try {
    await Promise.race([
      connection.getClient(),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('连接超时。')), 12_000) }),
    ])
    return { state: 'connected', tools: connection.tools.map(tool => ({ name: tool.name, description: tool.description ?? '' })), error: null }
  } catch (error) {
    return { state: connection.state === 'needs-auth' ? 'needs-auth' : 'failed', tools: [], error: error.message }
  } finally {
    clearTimeout(timer)
    await connection.close()
  }
}

async function snapshot() {
  const global = await readConfig('global')
  const project = input.workspaceSelected ? await readConfig('project') : null
  const servers = []
  const checks = []
  const probes = new Map()
  const globals = global.data.mcpServers ?? {}
  const projects = project?.data.mcpServers ?? {}
  const effective = new Map(loadMcpConfig({ agentDir, cwd, projectTrusted: !!input.workspaceSelected && !!input.projectTrusted }).servers.map(entry => [entry.name, entry]))
  for (const [scope, file] of [['global', global], ['project', project]]) {
    if (!file) continue
    const entries = scope === 'project' ? { ...Object.fromEntries(Object.keys(globals).filter(name => !Object.hasOwn(projects, name)).map(name => [name, {}])), ...projects } : globals
    for (const [name, raw] of Object.entries(entries)) {
      const resolved = resolveConfig(name, raw, scope, globals)
      const config = resolved.config ?? (isRecord(raw) ? raw : {})
      const entry = effective.get(name)
      const shadowed = scope === 'global' && entry && (entry.scope === 'project' || entry.override)
      const inherited = scope === 'project' && !Object.hasOwn(projects, name)
      let status = { state: 'unchecked', tools: [], error: null }
      if (resolved.error) status = { state: 'failed', tools: [], error: resolved.error }
      else if (scope === 'project' && !input.projectTrusted) status.state = 'untrusted'
      else if (shadowed) status.state = 'overridden'
      else if (!entry) status = { state: 'failed', tools: [], error: '服务名称冲突，请修改名称。' }
      else if (config.enabled === false) status.state = 'disabled'
      const server = { name, scope, config, isOverride: !!resolved.isOverride, inherited, overrideConfig: resolved.isOverride ? resolved.storedConfig : null, ...status }
      servers.push(server)
      if (status.state === 'unchecked' && input.check && (!input.name || input.name === name && input.scope === scope)) {
        checks.push(async () => {
          if (!probes.has(entry)) probes.set(entry, probe(entry))
          Object.assign(server, await probes.get(entry))
        })
      }
    }
  }
  // Bound simultaneous local processes while letting slow services finish independently.
  let next = 0
  await Promise.all(Array.from({ length: Math.min(4, checks.length) }, async () => {
    while (next < checks.length) await checks[next++]()
  }))
  return { servers, globalRevision: global.revision, projectRevision: project?.revision ?? null, projectTrusted: !!input.projectTrusted }
}

try {
  if (['save', 'import', 'delete', 'toggle'].includes(input.action)) await mutate()
  else if (input.action === 'login' || input.action === 'logout') {
    if (!['global', 'project'].includes(input.scope) || input.scope === 'project' && !input.projectTrusted) throw new Error('请先信任工作区。')
    const file = await readConfig(input.scope)
    const globals = (await readConfig('global')).data.mcpServers ?? {}
    const raw = file.data.mcpServers?.[input.name] ?? (input.scope === 'project' && Object.hasOwn(globals, input.name) ? {} : undefined)
    const resolved = resolveConfig(input.name, raw, input.scope, globals)
    if (resolved.error) throw new Error(resolved.error)
    const entry = input.scope === 'global' ? { name: input.name, config: resolved.config, scope: 'global', source: paths.global } :
      loadMcpConfig({ agentDir, cwd, projectTrusted: !!input.workspaceSelected && !!input.projectTrusted }).servers.find(entry => entry.name === input.name)
    if (!entry) throw new Error('找不到可用的服务。')
    const { connection, credentials } = await createConnection(entry)
    if (!connection.oauthUrl) throw new Error('该服务无需浏览器登录。')
    if (input.action === 'logout') credentials.remove(input.name, connection.oauthUrl)
    else {
      try { await connection.getClient() } catch { /* Sign-in uses the server's OAuth challenge. */ }
      await runtime.signInMcpServer({
        serverUrl: connection.oauthUrl, store: credentials.forServer(input.name, connection.oauthUrl),
        settings: connection.oauthSettings(), challenge: connection.challenge,
        prompt: {
          showAuthorizationUrl(url) { send({ kind: 'event', url: url.toString() }) },
          async promptForRedirectUrl(signal) {
            const redirected = iterator.next().then(line => line.done ? undefined : JSON.parse(line.value).redirectUrl)
            const aborted = new Promise(resolve => signal.addEventListener('abort', () => resolve(undefined), { once: true }))
            return signal.aborted ? undefined : Promise.race([redirected, aborted])
          },
        },
      })
      input.check = true
    }
    await connection.close()
  } else if (input.action !== 'list') throw new Error('未知的服务操作。')
  send({ kind: 'result', snapshot: await snapshot() })
} catch (error) {
  send({ kind: 'error', message: error.message })
  process.exitCode = 1
} finally {
  await Promise.allSettled(connections.map(connection => connection.close()))
  lines.close()
  process.stdin.destroy()
}
