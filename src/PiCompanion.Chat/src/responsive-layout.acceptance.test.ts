import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const styles = readFileSync(
  resolve(process.cwd(), 'src/styles.css'),
  'utf8',
)

describe('responsive management layouts', () => {
  it('collapses the skill grid from two columns based on available content width', () => {
    expect(styles).toContain('.management-main { container-type: inline-size;')
    expect(styles).toContain('@container (max-width: 740px)')
    expect(styles).toMatch(
      /@container \(max-width: 740px\)\s*\{\s*\.skills-grid\s*\{\s*grid-template-columns: minmax\(0, 1fr\);\s*\}\s*\}/u,
    )

    const narrowViewportRules = styles.slice(styles.indexOf('@media (max-width: 520px)'))
    expect(narrowViewportRules).not.toContain('.skills-grid')
  })
})
