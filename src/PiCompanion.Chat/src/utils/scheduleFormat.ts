type Translate = (key: string, params?: Record<string, string | number>) => string

export interface FormatNextRunOptions {
  now?: Date
  locale: string
  t: Translate
}

function timeText(date: Date, locale: string) {
  return new Intl.DateTimeFormat(locale, {
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

function dayStart(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate())
}

function dayDistance(from: Date, to: Date) {
  return Math.round((dayStart(to).getTime() - dayStart(from).getTime()) / 86_400_000)
}

export function formatNextRun(value: string | Date, options: FormatNextRunOptions): string {
  const date = value instanceof Date ? value : new Date(value)
  if (Number.isNaN(date.getTime())) return typeof value === 'string' ? value : ''

  const now = options.now ?? new Date()
  const locale = options.locale
  const time = timeText(date, locale)
  const distance = dayDistance(now, date)

  if (distance === 0) return options.t('今天 {time}', { time })
  if (distance === 1) return options.t('明天 {time}', { time })
  if (distance > 1 && distance < 7) {
    return `${new Intl.DateTimeFormat(locale, { weekday: 'short' }).format(date)} ${time}`
  }
  if (date.getFullYear() === now.getFullYear()) {
    return `${new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' }).format(date)} ${time}`
  }
  return `${new Intl.DateTimeFormat(locale, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  }).format(date)} ${time}`
}
