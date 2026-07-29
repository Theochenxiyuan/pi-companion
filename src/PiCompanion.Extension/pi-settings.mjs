import { copyFile, mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { randomUUID } from 'node:crypto'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'
import {
  applyDeveloperRoleCapabilities,
  computeModelsConfigRevision,
  insertProviderIntoModelsJson,
  mergeDeveloperRoleCapabilities,
  normalizeCustomProvider,
  removeProviderFromModelsJson,
  replaceProviderInModelsJson,
  toCustomProviderInfo,
  toModelsJsonProvider,
} from './pi-models-config.mjs'

const input = JSON.parse((await readStdin()).replace(/^\uFEFF/u, ''))
const piEntry = input.piEntry
if (typeof piEntry !== 'string' || piEntry.length === 0) {
  throw new Error('Missing Pi entry path.')
}

const distDirectory = dirname(piEntry)
const pi = await import(pathToFileURL(join(distDirectory, 'index.js')).href)
const { AuthStorage } = await import(pathToFileURL(join(distDirectory, 'core', 'auth-storage.js')).href)
const { ModelConfig } = await import(pathToFileURL(join(distDirectory, 'core', 'model-config.js')).href)
const { FileSettingsStorage } = await import(pathToFileURL(join(distDirectory, 'core', 'settings-manager.js')).href)
const { findInitialModel, resolveModelScope } = await import(pathToFileURL(join(distDirectory, 'core', 'model-resolver.js')).href)
const agentDir = optionalString(input.agentDir) ?? pi.getAgentDir()
const authPath = join(agentDir, 'auth.json')
const modelsPath = join(agentDir, 'models.json')
let settingsManager = pi.SettingsManager.create(process.cwd(), agentDir)

let streamedResult = false
if (input.action === 'login-oauth') {
  const providerId = requireString(input.providerId, 'providerId')
  const runtime = await createRuntime(true)
  const provider = runtime.getProvider(providerId)
  if (!provider?.auth?.oauth) throw new Error(`Provider does not support OAuth authentication: ${providerId}`)
  await runtime.login(providerId, 'oauth', {
    notify(event) {
      process.stdout.write(`${JSON.stringify({ kind: 'event', event })}\n`)
    },
    prompt: prompt => handleGuiOAuthPrompt(providerId, prompt),
  })
  process.stdout.write(`${JSON.stringify({ kind: 'result', snapshot: await createSnapshot() })}\n`)
  streamedResult = true
} else if (input.action === 'save-api-key') {
  const providerId = requireString(input.providerId, 'providerId')
  const apiKey = requireString(input.apiKey, 'apiKey').trim()
  if (apiKey.length === 0) throw new Error('API Key cannot be empty.')
  const runtime = await createRuntime(false)
  const provider = runtime.getProvider(providerId)
  if (!provider?.auth?.apiKey) throw new Error(`Provider does not support API key authentication: ${providerId}`)
  const auth = AuthStorage.create(authPath)
  await auth.modify(providerId, async () => ({ type: 'api_key', key: apiKey }))
} else if (input.action === 'logout') {
  const providerId = requireString(input.providerId, 'providerId')
  const auth = AuthStorage.create(authPath)
  await auth.delete(providerId)
} else if (input.action === 'add-custom-provider') {
  await addCustomProvider()
} else if (input.action === 'update-custom-provider') {
  await updateCustomProvider()
} else if (input.action === 'delete-custom-provider') {
  await deleteCustomProvider()
} else if (input.action === 'save-agent-defaults') {
  const defaultModel = requireString(input.defaultModel, 'defaultModel')
  const separator = defaultModel.indexOf('/')
  if (separator <= 0 || separator === defaultModel.length - 1) {
    throw new Error(`Invalid Pi model reference: ${defaultModel}`)
  }
  const providerId = defaultModel.slice(0, separator)
  const modelId = defaultModel.slice(separator + 1)
  const runtime = await createRuntime(false)
  if (!runtime.getModel(providerId, modelId)) {
    throw new Error(`Pi model is not available: ${defaultModel}`)
  }
  settingsManager.setDefaultModelAndProvider(providerId, modelId)
  settingsManager.setDefaultThinkingLevel(requireThinkingLevel(input.defaultThinkingLevel))
  settingsManager.setCompactionEnabled(Boolean(input.autoCompact))
  settingsManager.setRetryEnabled(Boolean(input.autoRetry))
  settingsManager.setSteeringMode('one-at-a-time')
  settingsManager.setFollowUpMode('one-at-a-time')
  await settingsManager.flush()
  const errors = settingsManager.drainErrors()
  if (errors.length > 0) throw errors[0].error ?? new Error(errors[0].message ?? 'Pi settings write failed.')
  updateAdvancedAgentSettings({
    compactionReserveTokens: requireInteger(input.compactionReserveTokens, 'compactionReserveTokens', 1024, 262144),
    compactionKeepRecentTokens: requireInteger(input.compactionKeepRecentTokens, 'compactionKeepRecentTokens', 1024, 262144),
    retryMaxRetries: requireInteger(input.retryMaxRetries, 'retryMaxRetries', 0, 20),
    retryBaseDelayMilliseconds: requireInteger(input.retryBaseDelayMilliseconds, 'retryBaseDelayMilliseconds', 100, 300000),
    retryMaxDelayMilliseconds: requireInteger(input.retryMaxDelayMilliseconds, 'retryMaxDelayMilliseconds', 0, 3600000),
  })
  settingsManager = pi.SettingsManager.create(process.cwd(), agentDir)
} else if (input.action === 'save-enabled-models') {
  if (input.enabledModels !== null && !Array.isArray(input.enabledModels)) {
    throw new Error('enabledModels must be an array or null.')
  }
  const runtime = await createRuntime(false)
  const available = new Set(runtime.getAvailableSnapshot().map(model => `${model.provider}/${model.id}`))
  const enabledModels = input.enabledModels === null
    ? undefined
    : [...new Set(input.enabledModels.map(value => requireString(value, 'enabledModel')))]
  if (enabledModels?.some(model => !available.has(model))) {
    throw new Error('One or more enabled Pi models are not available.')
  }
  settingsManager.setEnabledModels(enabledModels?.length === available.size ? undefined : enabledModels)
  await settingsManager.flush()
  const errors = settingsManager.drainErrors()
  if (errors.length > 0) throw errors[0].error ?? new Error(errors[0].message ?? 'Pi model scope write failed.')
} else if (input.action !== 'snapshot') {
  throw new Error(`Unsupported action: ${String(input.action)}`)
}

if (!streamedResult) {
  const refreshModels = input.action === 'snapshot' && input.refreshModels === true
  const snapshot = await createSnapshot(refreshModels)
  process.stdout.write(`${JSON.stringify(snapshot)}\n`)
}

function getWebSearchSupport(model, builtInProviderIds) {
  if (!builtInProviderIds.has(model.provider)) return 'none'
  if (model.provider === 'openai' && model.api === 'openai-responses') return 'native'
  if (model.provider === 'google' && model.api === 'google-generative-ai') return 'native'
  if (model.provider === 'anthropic' && model.api === 'anthropic-messages') return 'native'
  if (model.provider === 'openai-codex' && model.api === 'openai-codex-responses') return 'native'
  return 'none'
}

function getProviderCapabilities(providerId, builtInProviderIds) {
  if (!builtInProviderIds.has(providerId)) return []
  return ['openai', 'openai-codex', 'google', 'anthropic'].includes(providerId)
    ? ['web-search']
    : []
}

async function createSnapshot(refreshModels = false) {
  const runtime = await createRuntime(false)
  if (refreshModels) await refreshModelCatalog(runtime)
  const baseRuntime = await pi.ModelRuntime.create({
    authPath,
    modelsPath: null,
    allowModelNetwork: false,
  })
  const modelsSource = await readModelsSource()
  const modelConfig = await ModelConfig.load(modelsPath)
  const builtInProviderIds = new Set(baseRuntime.getProviders().map(provider => provider.id))
  const customProviders = modelConfig.getProviderIds()
    .filter(providerId => !builtInProviderIds.has(providerId))
    .map(providerId => toCustomProviderInfo(providerId, modelConfig.getProvider(providerId)))
    .filter(Boolean)
    .sort((left, right) => left.name.localeCompare(right.name, 'en'))
  const globalSettings = pi.SettingsManager.inMemory(settingsManager.getGlobalSettings())
  const enabledPatterns = globalSettings.getEnabledModels()
  const enabledModels = enabledPatterns?.length
    ? (await resolveModelScope(enabledPatterns, runtime)).map(item => `${item.model.provider}/${item.model.id}`)
    : null
  const initialModel = await findInitialModel({
    scopedModels: [],
    isContinuing: false,
    defaultProvider: globalSettings.getDefaultProvider(),
    defaultModelId: globalSettings.getDefaultModel(),
    defaultThinkingLevel: globalSettings.getDefaultThinkingLevel(),
    modelRuntime: runtime,
  })
  const credentials = new Map((await runtime.listCredentials()).map(item => [item.providerId, item.type]))
  const providers = runtime.getProviders()
    .map(provider => {
      const status = runtime.getProviderAuthStatus(provider.id)
      return {
        id: provider.id,
        name: provider.name,
        configured: status.configured,
        authType: credentials.get(provider.id) ?? configuredAuthType(status),
        authSource: status.label ?? status.source ?? null,
        supportsApiKey: Boolean(provider.auth?.apiKey),
        supportsOAuth: Boolean(provider.auth?.oauth),
        capabilities: getProviderCapabilities(provider.id, builtInProviderIds),
      }
    })
    .sort((left, right) => left.name.localeCompare(right.name, 'en'))

  const models = runtime.getAvailableSnapshot()
    .map(model => ({
      provider: model.provider,
      id: model.id,
      name: model.name,
      reasoning: model.reasoning,
      contextWindow: model.contextWindow,
      input: model.input,
      thinkingLevels: getThinkingLevels(model),
      api: model.api,
      webSearchSupport: getWebSearchSupport(model, builtInProviderIds),
    }))
    .sort((left, right) => left.provider.localeCompare(right.provider, 'en') || left.name.localeCompare(right.name, 'en'))

  return {
    available: true,
    version: pi.VERSION,
    defaultModel: initialModel.model ? `${initialModel.model.provider}/${initialModel.model.id}` : null,
    defaultThinkingLevel: globalSettings.getDefaultThinkingLevel() ?? initialModel.thinkingLevel,
    autoCompact: globalSettings.getCompactionEnabled(),
    autoRetry: globalSettings.getRetryEnabled(),
    compactionReserveTokens: globalSettings.getCompactionReserveTokens(),
    compactionKeepRecentTokens: globalSettings.getCompactionKeepRecentTokens(),
    retryMaxRetries: globalSettings.getRetrySettings().maxRetries,
    retryBaseDelayMilliseconds: globalSettings.getRetrySettings().baseDelayMs,
    retryMaxDelayMilliseconds: globalSettings.getProviderRetrySettings().maxRetryDelayMs,
    steeringMode: globalSettings.getSteeringMode(),
    followUpMode: globalSettings.getFollowUpMode(),
    providers,
    models,
    enabledModels,
    customProviders,
    modelsConfigRevision: computeModelsConfigRevision(modelsSource),
    error: runtime.getError() ?? null,
  }
}

function updateAdvancedAgentSettings(values) {
  const storage = new FileSettingsStorage(process.cwd(), agentDir)
  storage.withLock('global', current => {
    const settings = current ? JSON.parse(current) : {}
    settings.compaction = {
      ...(settings.compaction ?? {}),
      reserveTokens: values.compactionReserveTokens,
      keepRecentTokens: values.compactionKeepRecentTokens,
    }
    settings.retry = {
      ...(settings.retry ?? {}),
      maxRetries: values.retryMaxRetries,
      baseDelayMs: values.retryBaseDelayMilliseconds,
      provider: {
        ...(settings.retry?.provider ?? {}),
        maxRetryDelayMs: values.retryMaxDelayMilliseconds,
      },
    }
    return JSON.stringify(settings, null, 2)
  })
}

async function refreshModelCatalog(runtime) {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), 15_000)
  try {
    const result = await runtime.refresh({
      allowNetwork: true,
      force: true,
      signal: controller.signal,
    })
    if (result.aborted) throw new Error('Pi model catalog refresh timed out.')
    await refreshCustomProviderCapabilities()
    const errors = [...result.errors.entries()]
    if (errors.length > 0) {
      throw new Error(errors
        .map(([providerId, error]) => `${providerId}: ${error instanceof Error ? error.message : String(error)}`)
        .join('\n'))
    }
  } finally {
    clearTimeout(timeout)
  }
}

async function addCustomProvider() {
  let provider = normalizeCustomProvider(input.provider)
  const apiKey = provider.credentialMode === 'api-key'
    ? requireString(input.apiKey, 'apiKey').trim()
    : null
  if (provider.credentialMode === 'api-key' && !apiKey) throw new Error('API Key 不能为空。')
  provider = await resolveDeveloperRoleCapabilities(provider, apiKey)
  const expectedRevision = optionalRevision(input.modelsConfigRevision)
  const currentSource = await readModelsSource()
  if (computeModelsConfigRevision(currentSource) !== expectedRevision) {
    throw new Error('models.json 已被其他程序修改，请刷新 Provider 状态后重试。')
  }

  const currentConfig = await ModelConfig.load(modelsPath)
  if (currentConfig.getError()) throw new Error(currentConfig.getError())
  if (currentConfig.getProvider(provider.id)) throw new Error(`Provider ID “${provider.id}”已经存在。`)

  const baseRuntime = await pi.ModelRuntime.create({
    authPath,
    modelsPath: null,
    allowModelNetwork: false,
  })
  if (baseRuntime.getProvider(provider.id)) {
    throw new Error(`Provider ID “${provider.id}”与 Pi 内置 Provider 冲突。`)
  }

  const providerConfig = toModelsJsonProvider(provider)
  const candidate = insertProviderIntoModelsJson(currentSource, provider.id, providerConfig)
  await mkdir(agentDir, { recursive: true })
  const temporaryPath = `${modelsPath}.${process.pid}.${randomUUID()}.tmp`
  const temporaryStorePath = `${temporaryPath}.store`
  await writeFile(temporaryPath, candidate, 'utf8')

  try {
    const validationRuntime = await pi.ModelRuntime.create({
      authPath,
      modelsPath: temporaryPath,
      modelsStorePath: temporaryStorePath,
      allowModelNetwork: false,
    })
    const validationError = validationRuntime.getError()
    if (validationError) throw new Error(validationError)
    if (!validationRuntime.getProvider(provider.id)) {
      throw new Error('Pi 未能加载新建的自定义 Provider。')
    }
    for (const model of provider.models) {
      if (!validationRuntime.getModel(provider.id, model.id)) {
        throw new Error(`Pi 未能加载模型 ${provider.id}/${model.id}。`)
      }
    }

    const latestSource = await readModelsSource()
    if (computeModelsConfigRevision(latestSource) !== expectedRevision) {
      throw new Error('models.json 已被其他程序修改，请刷新 Provider 状态后重试。')
    }

    if (latestSource !== null) await copyFile(modelsPath, `${modelsPath}.pi-companion.bak`)
    await rename(temporaryPath, modelsPath)

    if (provider.credentialMode === 'api-key') {
      try {
        const auth = AuthStorage.create(authPath)
        await auth.modify(provider.id, async () => ({ type: 'api_key', key: apiKey }))
      } catch (error) {
        await restoreModelsSource(currentSource)
        throw error
      }
    }
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => {})
    await rm(temporaryStorePath, { force: true }).catch(() => {})
  }
}

async function updateCustomProvider() {
  let provider = normalizeCustomProvider(input.provider)
  const apiKey = optionalString(input.apiKey) ?? ''
  const expectedRevision = optionalRevision(input.modelsConfigRevision)
  const currentSource = await readModelsSource()
  if (computeModelsConfigRevision(currentSource) !== expectedRevision) {
    throw new Error('models.json 已被其他程序修改，请刷新 Provider 状态后重试。')
  }

  const currentConfig = await ModelConfig.load(modelsPath)
  if (currentConfig.getError()) throw new Error(currentConfig.getError())
  const existingConfig = currentConfig.getProvider(provider.id)
  if (!existingConfig) throw new Error(`Provider ID “${provider.id}”不存在。`)

  const baseRuntime = await pi.ModelRuntime.create({
    authPath,
    modelsPath: null,
    allowModelNetwork: false,
  })
  if (baseRuntime.getProvider(provider.id)) {
    throw new Error('Pi 内置 Provider 不能通过自定义 Provider 编辑器修改。')
  }

  const existingProvider = toCustomProviderInfo(provider.id, existingConfig)
  if (!existingProvider) throw new Error('现有自定义 Provider 配置无效。')
  if (provider.credentialMode === 'api-key' && !apiKey && existingProvider.credentialMode !== 'api-key') {
    throw new Error('API Key 不能为空。')
  }

  const currentApiKey = apiKey || await readStoredApiKey(provider.id)
  provider = await resolveDeveloperRoleCapabilities(provider, currentApiKey)
  const providerConfig = toModelsJsonProvider(provider)
  const candidate = replaceProviderInModelsJson(currentSource, provider.id, providerConfig)
  await mkdir(agentDir, { recursive: true })
  const temporaryPath = `${modelsPath}.${process.pid}.${randomUUID()}.tmp`
  const temporaryStorePath = `${temporaryPath}.store`
  await writeFile(temporaryPath, candidate, 'utf8')

  try {
    const validationRuntime = await pi.ModelRuntime.create({
      authPath,
      modelsPath: temporaryPath,
      modelsStorePath: temporaryStorePath,
      allowModelNetwork: false,
    })
    const validationError = validationRuntime.getError()
    if (validationError) throw new Error(validationError)
    if (!validationRuntime.getProvider(provider.id)) {
      throw new Error('Pi 未能加载更新后的自定义 Provider。')
    }
    for (const model of provider.models) {
      if (!validationRuntime.getModel(provider.id, model.id)) {
        throw new Error(`Pi 未能加载模型 ${provider.id}/${model.id}。`)
      }
    }

    const latestSource = await readModelsSource()
    if (computeModelsConfigRevision(latestSource) !== expectedRevision) {
      throw new Error('models.json 已被其他程序修改，请刷新 Provider 状态后重试。')
    }

    await copyFile(modelsPath, `${modelsPath}.pi-companion.bak`)
    await rename(temporaryPath, modelsPath)

    try {
      const auth = AuthStorage.create(authPath)
      if (provider.credentialMode === 'local') {
        await auth.delete(provider.id)
      } else if (apiKey) {
        await auth.modify(provider.id, async () => ({ type: 'api_key', key: apiKey }))
      }
    } catch (error) {
      await restoreModelsSource(currentSource)
      throw error
    }
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => {})
    await rm(temporaryStorePath, { force: true }).catch(() => {})
  }
}

async function refreshCustomProviderCapabilities() {
  const currentSource = await readModelsSource()
  if (currentSource === null) return
  const currentRevision = computeModelsConfigRevision(currentSource)
  const currentConfig = await ModelConfig.load(modelsPath)
  if (currentConfig.getError()) throw new Error(currentConfig.getError())
  const baseRuntime = await pi.ModelRuntime.create({
    authPath,
    modelsPath: null,
    allowModelNetwork: false,
  })
  const builtInProviderIds = new Set(baseRuntime.getProviders().map(provider => provider.id))
  const providers = currentConfig.getProviderIds()
    .filter(providerId => !builtInProviderIds.has(providerId))
    .map(providerId => ({
      id: providerId,
      config: currentConfig.getProvider(providerId),
    }))
    .map(item => ({
      ...item,
      provider: toCustomProviderInfo(item.id, item.config),
    }))
    .filter(item => item.provider)

  const resolved = await Promise.all(providers.map(async item => ({
    ...item,
    provider: await resolveDeveloperRoleCapabilities(
      item.provider,
      await readStoredApiKey(item.id),
    ),
  })))

  let candidate = currentSource
  for (const item of resolved) {
    const mergedConfig = mergeDeveloperRoleCapabilities(item.config, item.provider)
    if (mergedConfig !== item.config) {
      candidate = replaceProviderInModelsJson(candidate, item.id, mergedConfig)
    }
  }
  if (candidate === currentSource) return

  const latestSource = await readModelsSource()
  if (computeModelsConfigRevision(latestSource) !== currentRevision) {
    throw new Error('models.json was modified while the model catalog was refreshing. Please retry.')
  }

  await mkdir(agentDir, { recursive: true })
  const temporaryPath = `${modelsPath}.${process.pid}.${randomUUID()}.tmp`
  const temporaryStorePath = `${temporaryPath}.store`
  await writeFile(temporaryPath, candidate, 'utf8')
  try {
    const validationRuntime = await pi.ModelRuntime.create({
      authPath,
      modelsPath: temporaryPath,
      modelsStorePath: temporaryStorePath,
      allowModelNetwork: false,
    })
    const validationError = validationRuntime.getError()
    if (validationError) throw new Error(validationError)
    await copyFile(modelsPath, `${modelsPath}.pi-companion.bak`)
    await rename(temporaryPath, modelsPath)
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => {})
    await rm(temporaryStorePath, { force: true }).catch(() => {})
  }
}

async function resolveDeveloperRoleCapabilities(provider, apiKey) {
  const fallback = applyDeveloperRoleCapabilities(provider, null)
  if (provider.api !== 'openai-completions' && provider.api !== 'openai-responses') return fallback

  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), 5_000)
  try {
    const headers = { Accept: 'application/json' }
    if (apiKey) headers.Authorization = `Bearer ${apiKey}`
    const response = await fetch(`${provider.baseUrl.replace(/\/+$/u, '')}/models`, {
      headers,
      signal: controller.signal,
    })
    if (!response.ok) return fallback
    return applyDeveloperRoleCapabilities(provider, await response.json())
  } catch {
    return fallback
  } finally {
    clearTimeout(timeout)
  }
}

async function readStoredApiKey(providerId) {
  try {
    const credential = await AuthStorage.create(authPath).read(providerId)
    return credential?.type === 'api_key' && typeof credential.key === 'string'
      ? credential.key
      : ''
  } catch {
    return ''
  }
}

async function deleteCustomProvider() {
  const providerId = requireString(input.providerId, 'providerId').trim().toLowerCase()
  const expectedRevision = optionalRevision(input.modelsConfigRevision)
  const currentSource = await readModelsSource()
  if (computeModelsConfigRevision(currentSource) !== expectedRevision) {
    throw new Error('models.json 已被其他程序修改，请刷新 Provider 状态后重试。')
  }

  const currentConfig = await ModelConfig.load(modelsPath)
  if (currentConfig.getError()) throw new Error(currentConfig.getError())
  if (!currentConfig.getProvider(providerId)) throw new Error(`Provider ID “${providerId}”不存在。`)

  const baseRuntime = await pi.ModelRuntime.create({
    authPath,
    modelsPath: null,
    allowModelNetwork: false,
  })
  if (baseRuntime.getProvider(providerId)) {
    throw new Error('Pi 内置 Provider 不能删除。')
  }

  const candidate = removeProviderFromModelsJson(currentSource, providerId)
  await mkdir(agentDir, { recursive: true })
  const temporaryPath = `${modelsPath}.${process.pid}.${randomUUID()}.tmp`
  await writeFile(temporaryPath, candidate, 'utf8')

  try {
    const latestSource = await readModelsSource()
    if (computeModelsConfigRevision(latestSource) !== expectedRevision) {
      throw new Error('models.json 已被其他程序修改，请刷新 Provider 状态后重试。')
    }

    await copyFile(modelsPath, `${modelsPath}.pi-companion.bak`)
    await rename(temporaryPath, modelsPath)

    try {
      const auth = AuthStorage.create(authPath)
      await auth.delete(providerId)
    } catch (error) {
      await restoreModelsSource(currentSource)
      throw error
    }
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => {})
  }
}

function createRuntime(allowModelNetwork) {
  return pi.ModelRuntime.create({ authPath, modelsPath, allowModelNetwork })
}

async function readModelsSource() {
  try {
    return await readFile(modelsPath, 'utf8')
  } catch (error) {
    if (error?.code === 'ENOENT') return null
    throw error
  }
}

async function restoreModelsSource(source) {
  if (source === null) {
    await rm(modelsPath, { force: true })
    return
  }
  const restorePath = `${modelsPath}.${process.pid}.${randomUUID()}.restore`
  await writeFile(restorePath, source, 'utf8')
  await rename(restorePath, modelsPath)
}

function configuredAuthType(status) {
  if (!status.configured) return null
  if (status.source === 'models_json_key' || status.source === 'models_json_command' || status.source === 'fallback') {
    return 'configuration'
  }
  return 'environment'
}

async function handleGuiOAuthPrompt(providerId, prompt) {
  if (providerId === 'github-copilot' && prompt.type === 'text') {
    return ''
  }
  if (prompt.type === 'select') {
    const firstOption = prompt.options?.[0]?.id
    if (typeof firstOption === 'string') return firstOption
  }
  if (prompt.type === 'manual_code' && prompt.signal) {
    return new Promise((resolve, reject) => {
      const signal = prompt.signal
      if (signal?.aborted) {
        reject(new Error('Browser login completed.'))
        return
      }
      signal?.addEventListener('abort', () => reject(new Error('Browser login completed.')), { once: true })
    })
  }
  throw new Error(`OAuth login requires interactive input that is not supported in the GUI yet: ${prompt.message}`)
}

function getThinkingLevels(model) {
  if (!model.reasoning) return ['off']
  const levels = ['off', 'minimal', 'low', 'medium', 'high']
  for (const level of ['xhigh', 'max']) {
    if (model.thinkingLevelMap?.[level] != null) levels.push(level)
  }
  return levels.filter(level => model.thinkingLevelMap?.[level] !== null)
}

function requireString(value, name) {
  if (typeof value !== 'string') throw new Error(`Missing ${name}.`)
  return value
}

function optionalString(value) {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : undefined
}

function optionalRevision(value) {
  if (value === null || value === undefined) return null
  return requireString(value, 'modelsConfigRevision').trim()
}

function requireThinkingLevel(value) {
  const level = requireString(value, 'defaultThinkingLevel').trim().toLowerCase()
  if (!['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].includes(level)) {
    throw new Error(`Invalid Pi thinking level: ${level}`)
  }
  return level
}

function requireInteger(value, name, minimum, maximum) {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`Invalid ${name}: ${String(value)}`)
  }
  return value
}

async function readStdin() {
  let content = ''
  for await (const chunk of process.stdin) content += chunk
  return content
}
