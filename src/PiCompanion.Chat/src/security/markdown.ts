import DOMPurify from 'dompurify'
import { marked } from 'marked'

const allowedProtocols = new Set(['http:', 'https:', 'mailto:'])
const allowedTags = [
  'a', 'blockquote', 'br', 'code', 'del', 'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'hr',
  'li', 'ol', 'p', 'pre', 'strong', 'table', 'tbody', 'td', 'th', 'thead', 'tr', 'ul',
]

function escapeHtml(value: string) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;')
}

export function normalizeExternalUrl(value: string) {
  if (!value || value.length > 2048) return null
  try {
    const url = new URL(value)
    if (!allowedProtocols.has(url.protocol)) return null
    if ((url.protocol === 'http:' || url.protocol === 'https:') && !url.hostname) return null
    return url.href
  } catch {
    return null
  }
}

export function renderSafeMarkdown(content: string) {
  const renderer = new marked.Renderer()
  renderer.html = ({ text }) => escapeHtml(text)
  renderer.image = ({ text }) => escapeHtml(text)
  renderer.link = ({ href, title, tokens }) => {
    const label = renderer.parser.parseInline(tokens)
    const safeUrl = normalizeExternalUrl(href)
    if (!safeUrl) return label
    const safeTitle = title ? ` title="${escapeHtml(title)}"` : ''
    return `<a href="${escapeHtml(safeUrl)}" rel="noopener noreferrer"${safeTitle}>${label}</a>`
  }

  const parsed = marked.parse(content, {
    async: false,
    breaks: true,
    gfm: true,
    renderer,
  }) as string

  return DOMPurify.sanitize(parsed, {
    ALLOWED_TAGS: allowedTags,
    ALLOWED_ATTR: ['class', 'href', 'rel', 'title'],
    ALLOW_DATA_ATTR: false,
    ALLOW_ARIA_ATTR: false,
    ALLOW_UNKNOWN_PROTOCOLS: false,
  })
}
