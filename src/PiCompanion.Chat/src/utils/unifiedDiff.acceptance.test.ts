import { describe, expect, it } from 'vitest'
import { getDiffStats, parseUnifiedDiff } from './unifiedDiff'

describe('unified diff presentation', () => {
  it('keeps file headers out of change counts and assigns old/new line numbers', () => {
    const lines = parseUnifiedDiff(
      '--- a/file.txt\r\n+++ b/file.txt\r\n@@ -2,3 +2,3 @@ section\r\n same\r\n-old\r\n+new\r\n\\ No newline at end of file\r\n',
    )

    expect(lines.map((line) => line.kind)).toEqual([
      'header', 'header', 'hunk', 'context', 'removed', 'added', 'meta',
    ])
    expect(lines[3]).toMatchObject({ oldLine: 2, newLine: 2, content: 'same' })
    expect(lines[4]).toMatchObject({ oldLine: 3, newLine: null, content: 'old' })
    expect(lines[5]).toMatchObject({ oldLine: null, newLine: 3, content: 'new' })
    expect(getDiffStats(lines)).toEqual({ added: 1, removed: 1 })
  })

  it('resets counters for each hunk and handles empty input', () => {
    const lines = parseUnifiedDiff('@@ -1 +1 @@\n-a\n+b\n@@ -20,0 +21,2 @@\n+x\n+y')
    const additions = lines.filter((line) => line.kind === 'added')
    const removals = lines.filter((line) => line.kind === 'removed')

    expect(additions.map((line) => line.newLine)).toEqual([1, 21, 22])
    expect(removals.map((line) => line.oldLine)).toEqual([1])
    expect(parseUnifiedDiff('')).toEqual([])
  })
})
