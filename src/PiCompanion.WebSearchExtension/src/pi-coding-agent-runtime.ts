import { homedir } from 'node:os'
import { join } from 'node:path'

// pi-web-search imports these runtime exports from the broad
// pi-coding-agent entry point. Keeping the compatible implementation here
// prevents the entire interactive coding agent from being duplicated inside
// Pi Companion's private search bundle.
export const DEFAULT_MAX_LINES = 2000
export const DEFAULT_MAX_BYTES = 50 * 1024

export function getAgentDir() {
  const configured = process.env.PI_CODING_AGENT_DIR?.trim()
  if (!configured) return join(homedir(), '.pi', 'agent')
  if (configured === '~') return homedir()
  if (configured.startsWith('~/') || configured.startsWith('~\\')) {
    return join(homedir(), configured.slice(2))
  }
  return configured
}

export function truncateHead(
  content: string,
  options: { maxLines?: number; maxBytes?: number } = {},
) {
  const maxLines = options.maxLines ?? DEFAULT_MAX_LINES
  const maxBytes = options.maxBytes ?? DEFAULT_MAX_BYTES
  const totalBytes = Buffer.byteLength(content, 'utf-8')
  const lines = content.length === 0 ? [] : content.split('\n')
  if (content.endsWith('\n')) lines.pop()
  const totalLines = lines.length

  if (totalLines <= maxLines && totalBytes <= maxBytes) {
    return { content, truncated: false }
  }

  if (lines.length > 0 && Buffer.byteLength(lines[0], 'utf-8') > maxBytes) {
    return { content: '', truncated: true }
  }

  const outputLines: string[] = []
  let outputBytes = 0
  for (let index = 0; index < lines.length && index < maxLines; index += 1) {
    const line = lines[index]!
    const lineBytes = Buffer.byteLength(line, 'utf-8') + (index > 0 ? 1 : 0)
    if (outputBytes + lineBytes > maxBytes) break
    outputLines.push(line)
    outputBytes += lineBytes
  }

  return { content: outputLines.join('\n'), truncated: true }
}
