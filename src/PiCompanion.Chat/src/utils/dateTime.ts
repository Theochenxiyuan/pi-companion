const minute = 60_000
const hour = 60 * minute
const day = 24 * hour
const week = 7 * day
const month = 30 * day
const year = 365 * day

export function formatRelativeTimestamp(value: string, locale: string, now = Date.now(), justNow?: string) {
  const timestamp = Date.parse(value)
  if (!Number.isFinite(timestamp)) return ''
  const difference = timestamp - now
  const absoluteDifference = Math.abs(difference)
  if (absoluteDifference < minute) {
    return justNow ?? new Intl.RelativeTimeFormat(locale, { numeric: 'auto', style: 'narrow' }).format(0, 'second')
  }
  const [unit, unitMilliseconds] = absoluteDifference < hour
    ? ['minute', minute] as const
    : absoluteDifference < day
      ? ['hour', hour] as const
      : absoluteDifference < week
        ? ['day', day] as const
        : absoluteDifference < month
          ? ['week', week] as const
          : absoluteDifference < year
            ? ['month', month] as const
            : ['year', year] as const
  const magnitude = Math.max(1, Math.floor(absoluteDifference / unitMilliseconds))
  const amount = difference < 0 ? -magnitude : magnitude
  return new Intl.RelativeTimeFormat(locale, { numeric: 'always', style: 'narrow' }).format(amount, unit)
}

export function formatFullTimestamp(value: string, locale: string) {
  const timestamp = new Date(value)
  if (Number.isNaN(timestamp.getTime())) return ''
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(timestamp)
}

export function formatConversationTimestamp(value: string, locale: string, now = new Date()) {
  const timestamp = new Date(value)
  if (Number.isNaN(timestamp.getTime())) return ''
  const sameDay = timestamp.getFullYear() === now.getFullYear()
    && timestamp.getMonth() === now.getMonth()
    && timestamp.getDate() === now.getDate()
  return new Intl.DateTimeFormat(locale, sameDay
    ? { hour: '2-digit', minute: '2-digit' }
    : { dateStyle: 'medium', timeStyle: 'short' }).format(timestamp)
}
