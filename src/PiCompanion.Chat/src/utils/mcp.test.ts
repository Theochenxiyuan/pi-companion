import { describe, expect, it } from 'vitest'
import { formatMcpCommand, parseMcpImport, splitMcpCommand } from './mcp'

describe('MCP configuration input', () => {
  it('splits commands without interpreting shell operators or expanding variables', () => {
    expect(splitMcpCommand('npx -y @example/server "two words"')).toEqual(['npx', '-y', '@example/server', 'two words'])
    expect(splitMcpCommand('"C:\\Program Files\\nodejs\\node.exe" C:\\tools\\server.mjs')).toEqual(['C:\\Program Files\\nodejs\\node.exe', 'C:\\tools\\server.mjs'])
    expect(splitMcpCommand('node "" "$TOKEN"')).toEqual(['node', '', '$TOKEN'])
    expect(() => splitMcpCommand('node "unfinished')).toThrow()
  })
  it('round-trips arguments with spaces, quotes and Windows paths', () => {
    const config = { command: 'C:\\Program Files\\node.exe', args: ['C:\\hello world\\script.mjs', 'a"b', ''] }
    expect(splitMcpCommand(formatMcpCommand(config))).toEqual([config.command, ...config.args])
  })
  it('imports multiple servers and retains advanced options', () => {
    const servers = { docs: { url: 'https://example.com/mcp', oauth: { clientId: 'test' } }, local: { command: 'node', args: ['server.mjs'], enabled: false } }
    expect(parseMcpImport(JSON.stringify({ mcpServers: servers }))).toEqual(servers)
    expect(parseMcpImport(JSON.stringify(servers))).toEqual(servers)
    expect(() => parseMcpImport('{"mcpServers":[]}')).toThrow()
    expect(() => parseMcpImport('{"mcpServers":{"bad name":{"url":"test"}}}')).toThrow()
  })
  it('accepts workspace overrides only in workspace imports', () => {
    const source = '{"mcpServers":{"docs":{"enabled":false,"toolExposure":{"read_*":"direct"}}}}'
    expect(() => parseMcpImport(source)).toThrow()
    expect(parseMcpImport(source, true)).toEqual({ docs: { enabled: false, toolExposure: { 'read_*': 'direct' } } })
    expect(() => parseMcpImport('{"docs":{"enabled":true,"headers":{"Authorization":"secret"}}}', true)).toThrow()
  })
})
