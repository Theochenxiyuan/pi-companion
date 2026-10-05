import { appendFileSync } from 'node:fs'
import { createInterface } from 'node:readline'

const log = process.argv[2]
const tools = ['read_probe', 'read_other', 'write_probe'].map(name => ({
  name, description: name,
  inputSchema: { type: 'object', properties: { value: { type: 'string' } }, required: ['value'], additionalProperties: false },
  annotations: { readOnlyHint: name !== 'write_probe', destructiveHint: name === 'write_probe' },
}))
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })
lines.on('line', line => {
  const request = JSON.parse(line)
  if (!Object.hasOwn(request, 'id')) return
  let result
  switch (request.method) {
    case 'initialize': result = { protocolVersion: request.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'companion-test', version: '1.0' } }; break
    case 'ping': result = {}; break
    case 'tools/list': result = { tools }; break
    case 'tools/call':
      if (log) appendFileSync(log, `${JSON.stringify(request.params)}\n`)
      result = { content: [{ type: 'text', text: `${request.params.name}:${request.params.arguments.value}` }] }
      break
    default: process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32601, message: 'Method not found' } })}\n`); return
  }
  process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, result })}\n`)
})
