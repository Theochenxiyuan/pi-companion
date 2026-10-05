import { readFile, writeFile } from 'node:fs/promises'

// Apply the pinned provider compatibility patch without a dependency on a
// general-purpose patching package. Validate every hunk before writing files.
const extensionRoot = new URL('../', import.meta.url)
const packageRoot = new URL('node_modules/pi-web-search/', extensionRoot)
const manifest = JSON.parse(await readFile(new URL('package.json', packageRoot), 'utf8'))
if (manifest.version !== '1.6.0') {
  throw new Error(`Provider compatibility patch requires pi-web-search 1.6.0, received ${manifest.version}.`)
}

const patch = await readFile(new URL('patches/pi-web-search+1.6.0.patch', extensionRoot), 'utf8')
const files = new Map()
let hunks
let hunk
for (const line of patch.replace(/\r\n/g, '\n').split('\n')) {
  if (line.startsWith('diff --git ')) {
    hunks = undefined
    hunk = undefined
  } else if (line.startsWith('+++ ')) {
    const match = /^\+\+\+ b\/node_modules\/pi-web-search\/(src\/providers\/(?:anthropic|auth|google|openai)\.ts)$/u.exec(line)
    if (!match || files.has(match[1])) throw new Error(`Unsupported provider patch target: ${line}`)
    hunks = []
    files.set(match[1], hunks)
  } else if (line.startsWith('@@ ')) {
    const match = /^@@ -\d+,(\d+) \+\d+,(\d+) @@/u.exec(line)
    if (!hunks || !match) throw new Error(`Unsupported provider patch hunk: ${line}`)
    hunk = { before: [], after: [], beforeCount: Number(match[1]), afterCount: Number(match[2]) }
    hunks.push(hunk)
  } else if (hunk && /^[ +\-]/u.test(line)) {
    if (line[0] !== '+') hunk.before.push(line.slice(1))
    if (line[0] !== '-') hunk.after.push(line.slice(1))
  } else if (line !== '' && !line.startsWith('index ') && !line.startsWith('--- ')) {
    throw new Error(`Unsupported provider patch line: ${line}`)
  }
}
if (files.size !== 4) throw new Error('Provider compatibility patch must cover all four provider files.')

const updates = []
for (const [path, fileHunks] of files) {
  const url = new URL(path, packageRoot)
  const original = await readFile(url, 'utf8')
  let source = original.replace(/\r\n/g, '\n')
  for (const { before, after, beforeCount, afterCount } of fileHunks) {
    if (before.length !== beforeCount || after.length !== afterCount) {
      throw new Error(`Invalid provider patch hunk in ${path}.`)
    }
    const beforeText = `${before.join('\n')}\n`
    const afterText = `${after.join('\n')}\n`
    const matches = source.split(beforeText).length - 1
    if (matches === 1) source = source.replace(beforeText, () => afterText)
    else if (matches !== 0 || source.split(afterText).length - 1 !== 1) {
      throw new Error(`Provider source changed; review the compatibility patch for ${path}.`)
    }
  }
  if (original.includes('\r\n')) source = source.replace(/\n/g, '\r\n')
  if (source !== original) updates.push({ url, source })
}
for (const { url, source } of updates) await writeFile(url, source, 'utf8')
console.log(`pi-web-search provider compatibility ready (${updates.length} files updated).`)
