import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const source = (relativePath: string) => readFileSync(
  fileURLToPath(new URL(relativePath, import.meta.url)),
  'utf8',
)
const typography = source('./typography.css')
const globalStyles = source('./styles.css')
const appSelect = source('./components/ui/UiSelect.vue')
const settingsModal = source('./components/SettingsModal.vue')

describe('typography design tokens', () => {
  it('defines the shared type scale, families, weights, and line heights', () => {
    expect(typography).toContain('--font-family-sans:')
    expect(typography).toContain('--font-family-mono:')
    expect(typography).toContain('--font-size-micro: 12px')
    expect(typography).toContain('--font-size-caption: 13px')
    expect(typography).toContain('--font-size-body: 15px')
    expect(typography).toContain('--font-size-title-lg: 21px')
    expect(typography).toContain('--font-weight-medium: 550')
    expect(typography).toContain('--font-weight-semibold: 600')
    expect(typography).toContain('--line-height-reading: 1.68')
  })

  it('does not define readable UI text below 12px', () => {
    const sizes = [...typography.matchAll(/--font-size-(?:micro|caption|body(?:-sm|-lg)?|title-(?:sm|md|lg)):\s*(\d+)px/gu)]
      .map(match => Number(match[1]))

    expect(sizes.length).toBeGreaterThan(0)
    expect(Math.min(...sizes)).toBeGreaterThanOrEqual(12)
  })

  it('keeps implementation styles on tokens instead of local numeric typography', () => {
    const implementation = [globalStyles, appSelect, settingsModal]
      .join('\n')
      .replaceAll('font-size: 0', '')

    expect(implementation).not.toMatch(/font-size:\s*\d/u)
    expect(implementation).not.toMatch(/font-weight:\s*\d/u)
    expect(implementation).not.toMatch(/font:\s*\d/u)
    expect(implementation).not.toContain('font-family: Georgia')
    expect(implementation).not.toContain('font-family: Consolas')
  })

  it('sets an explicit global base and select control typography', () => {
    expect(globalStyles).toContain('font-size: var(--font-size-body)')
    expect(globalStyles).toContain('font-weight: var(--font-weight-regular)')
    expect(appSelect).toContain('font-size: var(--font-size-body-sm)')
    expect(appSelect).toContain('font-weight: var(--font-weight-regular)')
  })

})
