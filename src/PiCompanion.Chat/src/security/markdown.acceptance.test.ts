import { describe, expect, it } from 'vitest'
import { normalizeExternalUrl, renderSafeMarkdown } from './markdown'

describe('safe Markdown acceptance', () => {
  it('renders headings, fenced code and safe external links', () => {
    const html = renderSafeMarkdown([
      '# 标题',
      '',
      '```html',
      '<script>alert(1)</script>',
      '```',
      '',
      '[文档](https://example.com/docs)',
    ].join('\n'))

    expect(html).toContain('<h1>标题</h1>')
    expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;')
    expect(html).toContain('href="https://example.com/docs"')
    expect(html).toContain('rel="noopener noreferrer"')
  })

  it('does not pass raw HTML, executable URLs, images or event handlers', () => {
    const html = renderSafeMarkdown([
      '<script>alert(1)</script>',
      '<img src=x onerror="alert(2)">',
      '[危险链接](javascript:alert(3))',
      '![跟踪图](https://tracker.example/pixel.png)',
    ].join('\n\n'))

    expect(html).not.toMatch(/<script|<img|href=["']?javascript:/i)
    expect(html).toContain('&lt;script&gt;')
    expect(html).toContain('&lt;img')
    expect(html).not.toContain('tracker.example')
    expect(html).toContain('危险链接')
    expect(html).toContain('跟踪图')
  })

  it('only accepts explicit http, https and mailto URLs', () => {
    expect(normalizeExternalUrl('https://example.com/a')).toBe('https://example.com/a')
    expect(normalizeExternalUrl('mailto:test@example.com')).toBe('mailto:test@example.com')
    expect(normalizeExternalUrl('file:///C:/secret.txt')).toBeNull()
    expect(normalizeExternalUrl('data:text/html,<script>1</script>')).toBeNull()
    expect(normalizeExternalUrl('/relative/path')).toBeNull()
  })
})
