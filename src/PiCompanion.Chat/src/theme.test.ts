import { afterEach, describe, expect, it } from 'vitest'
import { applyTheme, clearTheme, resolveTheme } from './theme'

describe('application theme', () => {
  afterEach(() => clearTheme())

  it('resolves explicit and system theme preferences', () => {
    expect(resolveTheme('dark', true)).toBe('dark')
    expect(resolveTheme('light', false)).toBe('light')
    expect(resolveTheme('system', false)).toBe('dark')
    expect(resolveTheme('system', true)).toBe('light')
  })

  it('applies the resolved theme to the document root', () => {
    applyTheme('light')
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(document.documentElement.style.colorScheme).toBe('light')

    applyTheme('dark')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.style.colorScheme).toBe('dark')
  })
})
