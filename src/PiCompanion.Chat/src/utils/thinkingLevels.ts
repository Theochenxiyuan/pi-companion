import type { PiThinkingLevel } from '@/types/bridge'

export const thinkingLevelOrder: PiThinkingLevel[] = [
  'off',
  'minimal',
  'low',
  'medium',
  'high',
  'xhigh',
  'max',
]

export function thinkingLevelLabel(value: string) {
  return ({
    off: 'None',
    minimal: 'Minimal',
    low: 'Low',
    medium: 'Medium',
    high: 'High',
    xhigh: 'Xhigh',
    max: 'Max',
  } as Record<string, string>)[value] ?? value
}

export function coerceThinkingLevel(
  current: PiThinkingLevel,
  supported: readonly string[],
): PiThinkingLevel | null {
  const available = new Set(
    supported.filter((level): level is PiThinkingLevel => thinkingLevelOrder.includes(level as PiThinkingLevel)),
  )
  if (!available.size) return null
  if (available.has(current)) return current

  const currentIndex = thinkingLevelOrder.indexOf(current)
  for (let index = currentIndex; index >= 0; index -= 1) {
    const candidate = thinkingLevelOrder[index]!
    if (available.has(candidate)) return candidate
  }
  for (let index = currentIndex + 1; index < thinkingLevelOrder.length; index += 1) {
    const candidate = thinkingLevelOrder[index]!
    if (available.has(candidate)) return candidate
  }
  return null
}
