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
    const contentWidthRules = styles.slice(
      styles.indexOf('@container (max-width: 740px)'),
      styles.indexOf('@container (max-width: 500px)'),
    )
    expect(contentWidthRules).toContain(
      '.skills-grid { grid-template-columns: minmax(0, 1fr); }',
    )
    expect(contentWidthRules).toContain(
      '.skills-controls { grid-template-columns: repeat(2, minmax(0, 1fr)); }',
    )

    const narrowViewportRules = styles.slice(styles.indexOf('@media (max-width: 520px)'))
    expect(narrowViewportRules).not.toContain('.skills-grid')
  })
})
