import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const source = (relativePath: string) => readFileSync(
  fileURLToPath(new URL(relativePath, import.meta.url)),
  'utf8',
)

const tokens = source('./color-tokens.css')
const implementation = [
  source('./styles.css'),
  source('./components/ui/UiSelect.vue'),
  source('./components/ContextSessionPanel.vue'),
  source('./components/SettingsModal.vue'),
].join('\n')
const nativeTokens = source('../../PiCompanion.Desktop/Design/DesignTokens.xaml')
const nativeApp = source('../../PiCompanion.Desktop/App.xaml')
const tokenSource = source('../../../design/design-tokens.json')
const tokenGenerator = source('../../../scripts/generate-design-tokens.mjs')
const nativeThemeManager = source('../../PiCompanion.Desktop/Design/ThemeManager.cs')
const nativeMonitorCode = source('../../PiCompanion.Desktop/Monitor/MonitorWindow.xaml.cs')
const nativeImplementation = [
  source('../../PiCompanion.Desktop/MainWindow.xaml'),
  source('../../PiCompanion.Desktop/Monitor/MonitorWindow.xaml'),
  source('../../PiCompanion.Desktop/PromptComposer/PromptComposerWindow.xaml'),
].join('\n')

describe('color design tokens', () => {
  it('defines a consolidated palette and semantic application roles', () => {
    expect(tokens).toContain('--color-tone-1: #0b0b0b')
    expect(tokens).toContain('--color-tone-15: #ededed')
    expect(tokens).toContain('--color-bg-canvas: var(--color-tone-1)')
    expect(tokens).toContain('--color-text-primary: var(--color-tone-15)')
    expect(tokens).toContain('--color-success:')
    expect(tokens).toContain('--color-warning:')
    expect(tokens).toContain('--color-danger:')
    expect(tokens).toContain('--color-running: var(--color-info)')
    expect(tokens).toContain(':root[data-theme="light"]')
    expect(tokens).toContain('--color-tone-1: #f7f7f7')
    expect(tokens).toContain('--color-tone-15: #202020')
  })

  it('keeps implementation styles free of local color literals', () => {
    expect(implementation).not.toMatch(/#[\da-f]{3,8}\b/iu)
    expect(implementation).not.toMatch(/\brgba?\s*\(/iu)
    expect(implementation).not.toMatch(/\bhsla?\s*\(/iu)
  })

  it('loads color tokens before component styles', () => {
    const entry = source('./main.ts')
    expect(entry.indexOf("import './color-tokens.css'"))
      .toBeLessThan(entry.indexOf("import './styles.css'"))
  })

  it('keeps native windows aligned with the same tokenized palette', () => {
    expect(tokenSource).toContain('"$schemaVersion": 1')
    expect(tokenGenerator).toContain('GeneratedDesignTokens.cs')
    expect(nativeTokens).toContain('<Color x:Key="ColorNeutral1000">#0B0B0B</Color>')
    expect(nativeTokens).toContain('<Color x:Key="ColorNeutral100">#EDEDED</Color>')
    expect(nativeTokens).toContain('<SolidColorBrush x:Key="WindowBrush"')
    expect(nativeTokens).toContain('<Color x:Key="ColorRunning">#91A5B8</Color>')
    expect(nativeTokens).toContain('<SolidColorBrush x:Key="RunningBrush"')
    expect(nativeTokens).toContain('<SolidColorBrush x:Key="RunningSurfaceBrush"')
    expect(nativeTokens).toContain('Color="{DynamicResource ColorNeutral1000}"')
    expect(nativeApp).toContain('Value="{DynamicResource WindowBrush}"')
    expect(nativeImplementation).not.toMatch(/\{StaticResource [A-Za-z][A-Za-z0-9]*Brush\}/u)
    expect(nativeThemeManager).toContain('SetBrush("WindowBrush"')
    expect(nativeThemeManager).toContain('SetBrush("RunningBrush"')
    expect(nativeThemeManager).toContain('GeneratedDesignTokens.For(theme)')
    expect(nativeMonitorCode).toContain('RunStatus.WaitingForAnswer => "RunningBrush"')
    expect(nativeThemeManager).not.toContain('brush.Color = color')
    expect(nativeImplementation).not.toMatch(/#[\da-f]{3,8}\b/iu)
  })
})
