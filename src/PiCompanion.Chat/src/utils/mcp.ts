import type { McpConfig } from '@/types/bridge'

export function splitMcpCommand(value: string): string[] {
  const args: string[] = []
  let current = ''
  let quote = ''
  let started = false
  for (let index = 0; index < value.length; index++) {
    const character = value[index]!
    if (quote) {
      if (character === quote) quote = ''
      else if (quote === '"' && character === '\\' && ['"', '\\'].includes(value[index + 1] ?? '')) current += value[++index]
      else current += character
    } else if (character === '"' || character === "'") { quote = character; started = true }
    else if (/\s/u.test(character)) {
      if (started) { args.push(current); current = ''; started = false }
    } else { current += character; started = true }
  }
  if (quote) throw new Error('命令中的引号未闭合。')
  if (started) args.push(current)
  return args
}

export function formatMcpCommand(config: McpConfig): string {
  return [config.command ?? '', ...(config.args ?? [])].map(value =>
    /\s|["']/u.test(value) || !value ? JSON.stringify(value) : value).join(' ')
}

export function parseMcpImport(text: string, allowOverrides = false): Record<string, McpConfig> {
  const parsed: unknown = JSON.parse(text)
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('请粘贴服务配置。')
  const servers = (parsed as { mcpServers?: unknown }).mcpServers ?? parsed
  if (!servers || typeof servers !== 'object' || Array.isArray(servers) || !Object.keys(servers).length) throw new Error('配置中没有服务。')
  for (const [name, config] of Object.entries(servers)) {
    if (!/^[a-zA-Z0-9_-]+$/u.test(name)) throw new Error('名称只能包含字母、数字、短横线和下划线。')
    if (!config || typeof config !== 'object' || Array.isArray(config)) throw new Error(`服务配置不完整：${name}`)
    if (!('command' in config) && !('url' in config) &&
      !(allowOverrides && Object.keys(config).every(key => ['enabled', 'exposure', 'toolExposure'].includes(key)))) throw new Error(`服务配置不完整：${name}`)
  }
  return servers as Record<string, McpConfig>
}
