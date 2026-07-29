import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const source = (relativePath: string) => readFileSync(
  fileURLToPath(new URL(relativePath, import.meta.url)),
  'utf8',
)

const activationClient = source('../../PiCompanion.ExplorerCommand/ActivationClient.cpp')
const composerCode = source('../../PiCompanion.Desktop/PromptComposer/PromptComposerWindow.xaml.cs')
const desktopStyles = source('../../PiCompanion.Desktop/App.xaml')
const composerView = source('../../PiCompanion.Desktop/PromptComposer/PromptComposerWindow.xaml')

describe('Prompt Composer foreground focus', () => {
  it('transfers foreground permission from Explorer to the resident desktop host', () => {
    expect(activationClient).toContain('GetNamedPipeServerProcessId')
    expect(activationClient).toContain('AllowSetForegroundWindow(serverProcessId)')
    expect(activationClient).toContain('AllowSetForegroundWindow(processInfo.dwProcessId)')
  })

  it('places the window before acquiring real keyboard focus', () => {
    expect(composerCode).toContain('ShowPlaceAndFocus')
    expect(composerCode).toContain('placeWindow();')
    expect(composerCode).toContain('SetForegroundWindow(handle)')
    expect(composerCode).toContain('if (!IsActive)')
    expect(composerCode).toContain('Keyboard.Focus(PromptTextBox)')
    expect(composerCode.indexOf('placeWindow();')).toBeLessThan(
      composerCode.indexOf('Keyboard.Focus(PromptTextBox)'),
    )
  })

  it('focuses model search when the dropdown opens instead of styling focus from hover', () => {
    const searchStyle = desktopStyles.match(
      /<Style x:Key="ComboBoxSearchTextBox"[\s\S]*?<\/Style>/u,
    )?.[0]

    expect(desktopStyles).toContain('Style="{StaticResource ComboBoxSearchTextBox}"')
    expect(searchStyle).toContain('<Setter Property="BorderBrush" Value="{DynamicResource FocusBrush}" />')
    expect(searchStyle).not.toContain('Property="IsKeyboardFocusWithin"')
    expect(searchStyle).not.toContain('Property="IsMouseOver"')
    expect(composerView).toContain('<Setter Property="Focusable" Value="False" />')
    expect(composerCode).toContain('DispatcherPriority.ContextIdle')
    expect(composerCode).toContain('Keyboard.Focus(searchBox)')
    expect(composerCode).toContain('searchBox.CaretIndex = 0')
    expect(searchStyle).not.toContain('Padding="{TemplateBinding Padding}"')
    expect(composerCode).toContain('searchBox.LostKeyboardFocus += OnModelSearchLostKeyboardFocus')
    expect(composerCode).toContain('ModelComboBox.IsDropDownOpen')
    expect(composerCode).toContain('Mouse.LeftButton == MouseButtonState.Released')
    expect(composerCode).toContain('e.Key is Key.Tab or Key.Escape')
  })
})
