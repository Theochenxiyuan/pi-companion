export type DiffLineKind = 'added' | 'removed' | 'context' | 'hunk' | 'header' | 'meta'

export interface DiffLine {
  key: number
  kind: DiffLineKind
  oldLine: number | null
  newLine: number | null
  marker: string
  content: string
}

export interface DiffStats {
  added: number
  removed: number
}

export function parseUnifiedDiff(diffText: string): DiffLine[] {
  if (!diffText) return []
  const sourceLines = diffText.replace(/\r\n?/g, '\n').split('\n')
  if (sourceLines.at(-1) === '') sourceLines.pop()

  let oldLine = 0
  let newLine = 0
  let insideHunk = false
  return sourceLines.map((line, key) => {
    const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line)
    if (hunk) {
      oldLine = Number.parseInt(hunk[1], 10)
      newLine = Number.parseInt(hunk[2], 10)
      insideHunk = true
      return { key, kind: 'hunk', oldLine: null, newLine: null, marker: '', content: line }
    }

    if (line.startsWith('diff --git ') || line.startsWith('index ') || line.startsWith('--- ') || line.startsWith('+++ ')) {
      return { key, kind: 'header', oldLine: null, newLine: null, marker: '', content: line }
    }
    if (line.startsWith('\\')) {
      return { key, kind: 'meta', oldLine: null, newLine: null, marker: '', content: line }
    }
    if (line.startsWith('+')) {
      const currentNewLine = insideHunk ? newLine++ : null
      return { key, kind: 'added', oldLine: null, newLine: currentNewLine, marker: '+', content: line.slice(1) }
    }
    if (line.startsWith('-')) {
      const currentOldLine = insideHunk ? oldLine++ : null
      return { key, kind: 'removed', oldLine: currentOldLine, newLine: null, marker: '-', content: line.slice(1) }
    }
    if (insideHunk && line.startsWith(' ')) {
      const currentOldLine = oldLine++
      const currentNewLine = newLine++
      return { key, kind: 'context', oldLine: currentOldLine, newLine: currentNewLine, marker: ' ', content: line.slice(1) }
    }

    return { key, kind: 'meta', oldLine: null, newLine: null, marker: '', content: line }
  })
}

export function getDiffStats(lines: DiffLine[]): DiffStats {
  return {
    added: lines.filter((line) => line.kind === 'added').length,
    removed: lines.filter((line) => line.kind === 'removed').length,
  }
}
