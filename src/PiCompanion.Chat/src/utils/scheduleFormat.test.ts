import { describe, expect, it } from 'vitest'
import { formatNextRun } from './scheduleFormat'

const locale = 'en-US'
const t = (key: string, params: Record<string, string | number> = {}) =>
  key.replace(/\{([^}]+)\}/gu, (_, name: string) => String(params[name] ?? `{${name}}`))

const time = (date: Date) =>
  new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(date)
const weekday = (date: Date) =>
  new Intl.DateTimeFormat(locale, { weekday: 'short' }).format(date)
const shortDate = (date: Date) =>
  new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' }).format(date)
const fullDate = (date: Date) =>
  new Intl.DateTimeFormat(locale, { year: 'numeric', month: 'short', day: 'numeric' }).format(date)

const now = new Date(2026, 10, 3, 10, 0)
const format = (value: Date) => formatNextRun(value, { now, locale, t })

describe('formatNextRun', () => {
  it('labels a run later today with today', () => {
    const value = new Date(2026, 10, 3, 18, 5)
    expect(format(value)).toBe(`今天 ${time(value)}`)
  })

  it('labels tomorrow with tomorrow', () => {
    const value = new Date(2026, 10, 4, 8, 30)
    expect(format(value)).toBe(`明天 ${time(value)}`)
  })

  it('uses a short weekday within the next seven days', () => {
    const value = new Date(2026, 10, 6, 9, 0)
    expect(format(value)).toBe(`${weekday(value)} ${time(value)}`)
  })

  it('uses a short date later in the same year', () => {
    const value = new Date(2026, 11, 24, 7, 15)
    expect(format(value)).toBe(`${shortDate(value)} ${time(value)}`)
  })

  it('includes the year for a run in another year', () => {
    const value = new Date(2027, 0, 5, 7, 15)
    expect(format(value)).toBe(`${fullDate(value)} ${time(value)}`)
  })

  it('returns the original text for an invalid date', () => {
    expect(formatNextRun('not-a-date', { now, locale, t })).toBe('not-a-date')
  })
})
