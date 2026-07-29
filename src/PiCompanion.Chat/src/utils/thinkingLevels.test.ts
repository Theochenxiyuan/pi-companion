import { describe, expect, it } from 'vitest'
import { coerceThinkingLevel } from './thinkingLevels'

describe('coerceThinkingLevel', () => {
  it('keeps a supported level', () => {
    expect(coerceThinkingLevel('high', ['off', 'medium', 'high'])).toBe('high')
  })

  it('falls back to the closest lower supported level', () => {
    expect(coerceThinkingLevel('xhigh', ['off', 'low', 'high'])).toBe('high')
    expect(coerceThinkingLevel('high', ['off', 'medium'])).toBe('medium')
  })

  it('falls forward when no lower level is supported', () => {
    expect(coerceThinkingLevel('off', ['low', 'high'])).toBe('low')
  })

  it('uses off for a model without reasoning', () => {
    expect(coerceThinkingLevel('max', ['off'])).toBe('off')
  })

  it('returns null when the model exposes no recognized level', () => {
    expect(coerceThinkingLevel('high', [])).toBeNull()
  })
})
