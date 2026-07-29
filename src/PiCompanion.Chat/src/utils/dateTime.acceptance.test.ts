import { describe, expect, it } from 'vitest'
import { formatConversationTimestamp, formatRelativeTimestamp } from '@/utils/dateTime'

describe('localized timestamps', () => {
  const now = Date.UTC(2026, 6, 24, 12, 0)

  it('formats task activity as narrow localized relative time', () => {
    const sixDaysAgo = new Date(now - 6 * 24 * 60 * 60 * 1000).toISOString()
    const twoWeeksAgo = new Date(now - 14 * 24 * 60 * 60 * 1000).toISOString()

    expect(formatRelativeTimestamp(sixDaysAgo, 'en-US', now)).toBe('6d ago')
    expect(formatRelativeTimestamp(twoWeeksAgo, 'en-US', now)).toBe('2w ago')
    expect(formatRelativeTimestamp(sixDaysAgo, 'zh-CN', now)).toBe('6天前')
    expect(formatRelativeTimestamp(twoWeeksAgo, 'zh-CN', now)).toBe('2周前')
  })

  it('uses a localized just-now label for differences under one minute in either direction', () => {
    expect(formatRelativeTimestamp(new Date(now - 30_000).toISOString(), 'zh-CN', now, '刚刚')).toBe('刚刚')
    expect(formatRelativeTimestamp(new Date(now + 30_000).toISOString(), 'en-US', now, 'Just now')).toBe('Just now')
  })

  it('includes the full localized date for chat timestamps outside today', () => {
    const currentDate = new Date(2026, 6, 24, 20, 0)
    const today = new Date(2026, 6, 24, 18, 7).toISOString()
    const previousDay = new Date(2026, 6, 23, 18, 7).toISOString()

    expect(formatConversationTimestamp(today, 'zh-CN', currentDate)).not.toContain('2026')
    expect(formatConversationTimestamp(previousDay, 'zh-CN', currentDate)).toContain('2026')
    expect(formatConversationTimestamp(previousDay, 'en-US', currentDate)).toContain('2026')
  })
})
