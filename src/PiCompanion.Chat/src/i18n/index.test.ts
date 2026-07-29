import { afterEach, describe, expect, it } from 'vitest'
import { localeCode, setLocale, t } from '@/i18n'

describe('i18n', () => {
  afterEach(() => setLocale('zh-CN'))

  it('switches between Chinese and English at runtime', () => {
    setLocale('en-US')

    expect(localeCode()).toBe('en-US')
    expect(document.documentElement.lang).toBe('en-US')
    expect(t('删除于 {date}', { date: '7/22/2026' })).toBe('Deleted on 7/22/2026')
    expect(t('完成')).toBe('Done')
    expect(t('AI 标题与总结')).toBe('AI titles and summaries')
    expect(t('清理界面缓存')).toBe('Clear interface cache')
    expect(t('新任务默认权限')).toBe('Default permission for new tasks')

    setLocale('zh-CN')
    expect(t('删除于 {date}', { date: '2026/07/22' })).toBe('删除于 2026/07/22')
  })

  it('falls back to source text for generated or unknown content', () => {
    setLocale('en-US')
    expect(t('Runtime supplied content')).toBe('Runtime supplied content')
  })
})
