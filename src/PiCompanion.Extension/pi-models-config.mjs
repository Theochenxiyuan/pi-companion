import { createHash } from 'node:crypto'

const supportedApis = new Set([
  'openai-completions',
  'openai-responses',
  'anthropic-messages',
  'google-generative-ai',
])

const providerIdPattern = /^[a-z0-9][a-z0-9._-]{0,63}$/u

export function computeModelsConfigRevision(content) {
  return content == null ? null : createHash('sha256').update(content, 'utf8').digest('hex')
}

export function normalizeCustomProvider(value) {
  if (!isObject(value)) throw new Error('自定义 Provider 配置无效。')

  const id = requireTrimmedString(value.id, 'Provider ID', 64).toLowerCase()
  if (!providerIdPattern.test(id)) {
    throw new Error('Provider ID 只能包含小写字母、数字、点、短横线和下划线，且必须以字母或数字开头。')
  }

  const name = requireTrimmedString(value.name, 'Provider 名称', 80)
  const baseUrl = normalizeBaseUrl(value.baseUrl)
  const api = requireTrimmedString(value.api, 'API 类型', 64)
  if (!supportedApis.has(api)) throw new Error(`不支持的自定义 Provider API 类型：${api}`)

  const credentialMode = value.credentialMode === 'local' ? 'local' : 'api-key'
  if (!Array.isArray(value.models) || value.models.length === 0) {
    throw new Error('自定义 Provider 至少需要一个模型。')
  }
  if (value.models.length > 50) throw new Error('单个 Provider 最多可配置 50 个模型。')

  const modelIds = new Set()
  const models = value.models.map((model, index) => {
    if (!isObject(model)) throw new Error(`第 ${index + 1} 个模型配置无效。`)
    const modelId = requireTrimmedString(model.id, `第 ${index + 1} 个模型 ID`, 200)
    if (/\s/u.test(modelId)) throw new Error(`模型 ID “${modelId}”不能包含空白字符。`)
    if (modelIds.has(modelId)) throw new Error(`模型 ID “${modelId}”重复。`)
    modelIds.add(modelId)

    const contextWindow = requireInteger(model.contextWindow, `模型 ${modelId} 的上下文窗口`, 1024, 10_000_000)
    const maxTokens = requireInteger(model.maxTokens, `模型 ${modelId} 的最大输出 Token`, 1, contextWindow)
    return {
      id: modelId,
      name: optionalTrimmedString(model.name, 120) ?? modelId,
      reasoning: Boolean(model.reasoning),
      imageInput: Boolean(model.imageInput),
      contextWindow,
      maxTokens,
      supportsDeveloperRole: typeof model.supportsDeveloperRole === 'boolean'
        ? model.supportsDeveloperRole
        : undefined,
    }
  })

  return { id, name, baseUrl, api, credentialMode, models }
}

export function toModelsJsonProvider(provider) {
  const config = {
    name: provider.name,
    baseUrl: provider.baseUrl,
    api: provider.api,
    models: provider.models.map(model => {
      const config = {
        id: model.id,
        name: model.name,
        reasoning: model.reasoning,
        input: model.imageInput ? ['text', 'image'] : ['text'],
        contextWindow: model.contextWindow,
        maxTokens: model.maxTokens,
      }
      if (typeof model.supportsDeveloperRole === 'boolean') {
        config.compat = { supportsDeveloperRole: model.supportsDeveloperRole }
      }
      return config
    }),
  }
  if (provider.credentialMode === 'local') config.apiKey = 'local'
  return config
}

export function toCustomProviderInfo(providerId, config) {
  if (!isObject(config) || !Array.isArray(config.models)) return null
  const models = config.models
    .filter(isObject)
    .filter(model => typeof model.id === 'string' && model.id.trim().length > 0)
    .map(model => ({
      id: model.id.trim(),
      name: optionalTrimmedString(model.name, 120) ?? model.id.trim(),
      reasoning: Boolean(model.reasoning),
      imageInput: Array.isArray(model.input) && model.input.includes('image'),
      contextWindow: positiveIntegerOr(model.contextWindow, 128_000),
      maxTokens: positiveIntegerOr(model.maxTokens, 16_384),
      supportsDeveloperRole: typeof model.compat?.supportsDeveloperRole === 'boolean'
        ? model.compat.supportsDeveloperRole
        : undefined,
    }))
  return {
    id: providerId,
    name: optionalTrimmedString(config.name, 80) ?? providerId,
    baseUrl: optionalTrimmedString(config.baseUrl, 2048) ?? '',
    api: optionalTrimmedString(config.api, 64) ?? '',
    credentialMode: config.apiKey === 'local' ? 'local' : 'api-key',
    models,
  }
}

export function applyDeveloperRoleCapabilities(provider, catalog) {
  if (!isOpenAiCompatibleApi(provider.api)) return provider

  const fallback = defaultSupportsDeveloperRole(provider.baseUrl)
  const catalogModels = Array.isArray(catalog)
    ? catalog
    : Array.isArray(catalog?.data)
      ? catalog.data
      : []
  const capabilities = new Map(catalogModels
    .filter(isObject)
    .filter(model => typeof model.id === 'string')
    .map(model => [model.id, readDeveloperRoleCapability(model)]))

  return {
    ...provider,
    models: provider.models.map(model => ({
      ...model,
      supportsDeveloperRole: capabilities.get(model.id) ?? fallback,
    })),
  }
}

export function mergeDeveloperRoleCapabilities(config, provider) {
  if (!isObject(config) || !Array.isArray(config.models)) return config
  const supportByModel = new Map(provider.models
    .filter(model => typeof model.supportsDeveloperRole === 'boolean')
    .map(model => [model.id, model.supportsDeveloperRole]))
  let changed = false
  const models = config.models.map(model => {
    if (!isObject(model) || typeof model.id !== 'string' || !supportByModel.has(model.id)) return model
    const supportsDeveloperRole = supportByModel.get(model.id)
    if (model.compat?.supportsDeveloperRole === supportsDeveloperRole) return model
    changed = true
    return {
      ...model,
      compat: {
        ...(isObject(model.compat) ? model.compat : {}),
        supportsDeveloperRole,
      },
    }
  })
  return changed ? { ...config, models } : config
}

export function defaultSupportsDeveloperRole(baseUrl) {
  let hostname
  try {
    hostname = new URL(baseUrl).hostname.toLowerCase()
  } catch {
    return false
  }
  return hostname === 'api.openai.com'
    || hostname.endsWith('.openai.azure.com')
    || hostname.endsWith('.services.ai.azure.com')
}

function isOpenAiCompatibleApi(api) {
  return api === 'openai-completions' || api === 'openai-responses'
}

function readDeveloperRoleCapability(model) {
  const capability = model.metadata?.capabilities?.developer_role
    ?? model.capabilities?.developer_role
  return typeof capability === 'boolean' ? capability : undefined
}

// Pi accepts JSONC in models.json. This inserts only one property into the
// providers object, preserving the rest of the user's formatting and comments.
export function insertProviderIntoModelsJson(source, providerId, providerConfig) {
  if (source == null || source.trim().length === 0) {
    return `${JSON.stringify({ providers: { [providerId]: providerConfig } }, null, 2)}\n`
  }

  const rootStart = skipTrivia(source, 0)
  if (source[rootStart] !== '{') throw new Error('models.json 根节点必须是对象。')
  const root = scanObject(source, rootStart)
  const providersProperty = root.properties.find(property => property.key === 'providers')
  const eol = source.includes('\r\n') ? '\r\n' : '\n'
  const indentUnit = detectIndentUnit(source, root)

  if (!providersProperty) {
    const rootIndent = lineIndentAt(source, rootStart)
    const childIndent = root.properties.length > 0
      ? lineIndentAt(source, root.properties[0].keyStart)
      : rootIndent + indentUnit
    const serializedProviders = JSON.stringify({ [providerId]: providerConfig }, null, indentUnit)
      .split('\n')
      .join(`${eol}${childIndent}`)
    return insertObjectEntry(
      source,
      root,
      `${JSON.stringify('providers')}: ${serializedProviders}`,
      childIndent,
      rootIndent,
      eol,
    )
  }
  if (source[providersProperty.valueStart] !== '{') {
    throw new Error('models.json 的 providers 必须是对象。')
  }

  const providers = scanObject(source, providersProperty.valueStart)
  if (providers.properties.some(property => property.key === providerId)) {
    throw new Error(`Provider ID “${providerId}”已经存在。`)
  }

  const providersIndent = lineIndentAt(source, providersProperty.keyStart)
  const childIndent = providers.properties.length > 0
    ? lineIndentAt(source, providers.properties[0].keyStart)
    : providersIndent + indentUnit
  const serialized = JSON.stringify(providerConfig, null, indentUnit)
    .split('\n')
    .join(`${eol}${childIndent}`)
  const entry = `${JSON.stringify(providerId)}: ${serialized}`
  return insertObjectEntry(source, providers, entry, childIndent, providersIndent, eol)
}

// Replaces only the selected provider value so comments, trailing commas, and
// unrelated settings elsewhere in models.json remain untouched.
export function replaceProviderInModelsJson(source, providerId, providerConfig) {
  if (source == null || source.trim().length === 0) {
    throw new Error('models.json 中不存在要编辑的 Provider。')
  }

  const rootStart = skipTrivia(source, 0)
  if (source[rootStart] !== '{') throw new Error('models.json 根节点必须是对象。')
  const root = scanObject(source, rootStart)
  const providersProperty = root.properties.find(property => property.key === 'providers')
  if (!providersProperty || source[providersProperty.valueStart] !== '{') {
    throw new Error('models.json 的 providers 必须是对象。')
  }

  const providers = scanObject(source, providersProperty.valueStart)
  const providerProperty = providers.properties.find(property => property.key === providerId)
  if (!providerProperty) throw new Error(`Provider ID “${providerId}”不存在。`)

  const eol = source.includes('\r\n') ? '\r\n' : '\n'
  const indentUnit = detectIndentUnit(source, root)
  const childIndent = lineIndentAt(source, providerProperty.keyStart)
  const serialized = JSON.stringify(providerConfig, null, indentUnit)
    .split('\n')
    .join(`${eol}${childIndent}`)
  return source.slice(0, providerProperty.valueStart) + serialized + source.slice(providerProperty.valueEnd)
}

// Removes only the selected provider property while preserving unrelated JSONC
// settings, formatting, and comments.
export function removeProviderFromModelsJson(source, providerId) {
  if (source == null || source.trim().length === 0) {
    throw new Error('models.json 中不存在要删除的 Provider。')
  }

  const rootStart = skipTrivia(source, 0)
  if (source[rootStart] !== '{') throw new Error('models.json 根节点必须是对象。')
  const root = scanObject(source, rootStart)
  const providersProperty = root.properties.find(property => property.key === 'providers')
  if (!providersProperty || source[providersProperty.valueStart] !== '{') {
    throw new Error('models.json 的 providers 必须是对象。')
  }

  const providers = scanObject(source, providersProperty.valueStart)
  const providerIndex = providers.properties.findIndex(property => property.key === providerId)
  if (providerIndex < 0) throw new Error(`Provider ID “${providerId}”不存在。`)

  const providerProperty = providers.properties[providerIndex]
  if (providerProperty.hasComma) {
    return source.slice(0, providerProperty.keyStart)
      + source.slice(providerProperty.commaIndex + 1)
  }

  const previousProperty = providers.properties[providerIndex - 1]
  if (previousProperty?.hasComma) {
    return source.slice(0, previousProperty.commaIndex)
      + source.slice(providerProperty.valueEnd)
  }

  return source.slice(0, providerProperty.keyStart)
    + source.slice(providerProperty.valueEnd)
}

function insertObjectEntry(source, object, entry, childIndent, closingIndent, eol) {
  const tailStart = whitespaceStartBefore(source, object.closeIndex)
  const needsClosingIndent = tailStart === object.closeIndex
  const insertion = `${eol}${childIndent}${entry}${needsClosingIndent ? `${eol}${closingIndent}` : ''}`

  if (object.properties.length === 0) {
    return source.slice(0, tailStart) + insertion + source.slice(tailStart)
  }

  const last = object.properties[object.properties.length - 1]
  const beforeTail = source.slice(0, last.valueEnd)
    + (last.hasComma ? '' : ',')
    + source.slice(last.valueEnd, tailStart)
  return beforeTail + insertion + source.slice(tailStart)
}

function scanObject(source, openIndex) {
  const properties = []
  let index = openIndex + 1
  while (true) {
    index = skipTrivia(source, index)
    if (source[index] === '}') return { properties, closeIndex: index }
    const keyStart = index
    const keyToken = readString(source, index)
    index = skipTrivia(source, keyToken.end)
    if (source[index] !== ':') throw new Error('models.json 对象属性缺少冒号。')
    index = skipTrivia(source, index + 1)
    const valueStart = index
    const valueEnd = scanValue(source, valueStart)
    index = skipTrivia(source, valueEnd)
    const commaIndex = source[index] === ',' ? index : -1
    const hasComma = commaIndex >= 0
    if (hasComma) index += 1
    properties.push({ key: keyToken.value, keyStart, valueStart, valueEnd, hasComma, commaIndex })
    index = skipTrivia(source, index)
    if (source[index] === '}') return { properties, closeIndex: index }
    if (!hasComma) throw new Error('models.json 对象属性之间缺少逗号。')
  }
}

function scanValue(source, start) {
  const first = source[start]
  if (first === '"') return readString(source, start).end
  if (first !== '{' && first !== '[') {
    let index = start
    while (index < source.length && !/[\s,}\]]/u.test(source[index])) index += 1
    if (index === start) throw new Error('models.json 包含无效值。')
    return index
  }

  const stack = [first]
  let index = start + 1
  while (index < source.length && stack.length > 0) {
    const character = source[index]
    if (character === '"') {
      index = readString(source, index).end
      continue
    }
    if (character === '/' && source[index + 1] === '/') {
      index = skipLineComment(source, index + 2)
      continue
    }
    if (character === '/' && source[index + 1] === '*') {
      index = skipBlockComment(source, index + 2)
      continue
    }
    if (character === '{' || character === '[') stack.push(character)
    if (character === '}' || character === ']') {
      const expected = character === '}' ? '{' : '['
      if (stack.pop() !== expected) throw new Error('models.json 的括号不匹配。')
    }
    index += 1
  }
  if (stack.length > 0) throw new Error('models.json 包含未闭合的对象或数组。')
  return index
}

function readString(source, start) {
  if (source[start] !== '"') throw new Error('models.json 对象属性名必须是字符串。')
  let index = start + 1
  while (index < source.length) {
    if (source[index] === '\\') {
      index += 2
      continue
    }
    if (source[index] === '"') {
      const raw = source.slice(start, index + 1)
      return { value: JSON.parse(raw), end: index + 1 }
    }
    index += 1
  }
  throw new Error('models.json 包含未闭合的字符串。')
}

function skipTrivia(source, start) {
  let index = start
  while (index < source.length) {
    if (/\s/u.test(source[index])) {
      index += 1
      continue
    }
    if (source[index] === '/' && source[index + 1] === '/') {
      index = skipLineComment(source, index + 2)
      continue
    }
    if (source[index] === '/' && source[index + 1] === '*') {
      index = skipBlockComment(source, index + 2)
      continue
    }
    break
  }
  return index
}

function skipLineComment(source, start) {
  let index = start
  while (index < source.length && source[index] !== '\n') index += 1
  return index
}

function skipBlockComment(source, start) {
  const end = source.indexOf('*/', start)
  if (end < 0) throw new Error('models.json 包含未闭合的注释。')
  return end + 2
}

function detectIndentUnit(source, root) {
  const first = root.properties[0]
  if (!first) return '  '
  const indent = lineIndentAt(source, first.keyStart)
  if (indent.includes('\t')) return '\t'
  return indent.length > 0 ? ' '.repeat(Math.min(indent.length, 8)) : '  '
}

function lineIndentAt(source, index) {
  const lineStart = Math.max(source.lastIndexOf('\n', index - 1) + 1, 0)
  const prefix = source.slice(lineStart, index)
  return prefix.match(/^[\t ]*/u)?.[0] ?? ''
}

function whitespaceStartBefore(source, index) {
  let result = index
  while (result > 0 && /\s/u.test(source[result - 1])) result -= 1
  return result
}

function normalizeBaseUrl(value) {
  const raw = requireTrimmedString(value, 'Base URL', 2048)
  let parsed
  try {
    parsed = new URL(raw)
  } catch {
    throw new Error('Base URL 不是有效网址。')
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error('Base URL 只支持 http:// 或 https://。')
  }
  return raw.replace(/\/+$/u, '')
}

function requireTrimmedString(value, name, maxLength) {
  const normalized = optionalTrimmedString(value, maxLength)
  if (!normalized) throw new Error(`${name}不能为空。`)
  return normalized
}

function optionalTrimmedString(value, maxLength) {
  if (typeof value !== 'string') return null
  const normalized = value.trim()
  if (!normalized) return null
  if (normalized.length > maxLength) throw new Error(`配置文本不能超过 ${maxLength} 个字符。`)
  return normalized
}

function requireInteger(value, name, minimum, maximum) {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`${name}必须是 ${minimum.toLocaleString('en-US')} 到 ${maximum.toLocaleString('en-US')} 之间的整数。`)
  }
  return value
}

function positiveIntegerOr(value, fallback) {
  return Number.isInteger(value) && value > 0 ? value : fallback
}

function isObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
