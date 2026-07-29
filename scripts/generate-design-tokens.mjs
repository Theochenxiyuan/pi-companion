import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const scriptDirectory = dirname(fileURLToPath(import.meta.url))
const repositoryRoot = resolve(scriptDirectory, '..')
const sourcePath = resolve(repositoryRoot, 'design/design-tokens.json')
const checkOnly = process.argv.includes('--check')
const source = JSON.parse(await readFile(sourcePath, 'utf8'))

const outputPaths = {
  colors: resolve(repositoryRoot, 'src/PiCompanion.Chat/src/color-tokens.css'),
  typography: resolve(repositoryRoot, 'src/PiCompanion.Chat/src/typography.css'),
  components: resolve(repositoryRoot, 'src/PiCompanion.Chat/src/component-tokens.css'),
  wpf: resolve(repositoryRoot, 'src/PiCompanion.Desktop/Design/DesignTokens.xaml'),
  csharp: resolve(repositoryRoot, 'src/PiCompanion.Desktop/Design/GeneratedDesignTokens.cs'),
}

const kebab = value => value.replace(/[A-Z]/g, character => `-${character.toLowerCase()}`)
const pascal = value => value[0].toUpperCase() + value.slice(1)
const generatedNotice = 'Generated from design/design-tokens.json by scripts/generate-design-tokens.mjs.'

function themeCss(theme, selector) {
  const lines = [
    `${selector} {`,
    `  color-scheme: ${theme.colorScheme};`,
    '',
  ]

  theme.tones.forEach((color, index) => lines.push(`  --color-tone-${index + 1}: ${color};`))
  lines.push('')

  for (const [statusName, palette] of Object.entries(theme.status)) {
    for (const [role, color] of Object.entries(palette)) {
      const suffix = role === 'default' ? '' : `-${kebab(role)}`
      lines.push(`  --color-${statusName}${suffix}: ${color};`)
    }
    lines.push('')
  }

  for (const [name, color] of Object.entries(theme.workspace)) {
    lines.push(`  --color-workspace-${kebab(name)}: ${color};`)
  }
  lines.push('')

  for (const [name, color] of Object.entries(theme.effects)) {
    lines.push(`  --color-${kebab(name)}: ${color};`)
  }

  if (selector === ':root') {
    lines.push('', '  --color-running: var(--color-info);', '')
    for (const [role, target] of Object.entries(source.semanticRoles)) {
      lines.push(`  --color-${kebab(role)}: var(--color-${target});`)
    }
    lines.push('  --color-scrollbar-track: transparent;')
  }

  lines.push('}')
  return lines.join('\n')
}

function generateColors() {
  return [
    `/* ${generatedNotice} Do not edit by hand. */`,
    themeCss(source.themes.dark, ':root'),
    '',
    themeCss(source.themes.light, ':root[data-theme="light"]'),
    '',
  ].join('\n')
}

function generateTypography() {
  const { families, sizes, weights, lineHeights } = source.typography
  const lines = [
    `/* ${generatedNotice} Do not edit by hand. */`,
    ':root {',
  ]
  for (const [name, family] of Object.entries(families)) {
    lines.push(`  --font-family-${kebab(name)}: ${family.css};`)
  }
  lines.push('')
  for (const [name, size] of Object.entries(sizes)) {
    lines.push(`  --font-size-${kebab(name)}: ${size}px;`)
  }
  lines.push('')
  for (const [name, weight] of Object.entries(weights)) {
    lines.push(`  --font-weight-${kebab(name)}: ${weight};`)
  }
  lines.push('')
  for (const [name, height] of Object.entries(lineHeights)) {
    lines.push(`  --line-height-${kebab(name)}: ${height};`)
  }
  lines.push('}', '')
  return lines.join('\n')
}

function generateComponents() {
  const { spacing, radii, controlHeights, focusRing, motionDurations, zIndices } = source.dimensions
  const lines = [
    `/* ${generatedNotice} Do not edit by hand. */`,
    ':root {',
  ]
  for (const [name, value] of Object.entries(spacing)) {
    lines.push(`  --space-${name}: ${value}px;`)
  }
  lines.push('')
  for (const [name, value] of Object.entries(radii)) {
    lines.push(`  --radius-${kebab(name)}: ${value}px;`)
  }
  lines.push('')
  for (const [name, value] of Object.entries(controlHeights)) {
    lines.push(`  --control-height-${kebab(name)}: ${value}px;`)
  }
  lines.push(
    '',
    `  --focus-ring-width: ${focusRing.width}px;`,
    `  --focus-ring-offset: ${focusRing.offset}px;`,
    '',
  )
  for (const [name, value] of Object.entries(motionDurations)) {
    lines.push(`  --motion-duration-${kebab(name)}: ${value}ms;`)
  }
  lines.push('')
  for (const [name, value] of Object.entries(zIndices)) {
    lines.push(`  --z-index-${kebab(name)}: ${value};`)
  }
  lines.push('}', '')
  return lines.join('\n')
}

function normalizeHex(value) {
  const hex = value.slice(1)
  if (hex.length === 3) {
    return `#${[...hex].map(character => character.repeat(2)).join('')}`.toUpperCase()
  }
  return value.toUpperCase()
}

function colorExpression(value) {
  const hex = normalizeHex(value).slice(1)
  const bytes = []
  for (let index = 0; index < hex.length; index += 2) {
    bytes.push(`0x${hex.slice(index, index + 2)}`)
  }
  return hex.length === 8
    ? `Color.FromArgb(${bytes.join(', ')})`
    : `Color.FromRgb(${bytes.join(', ')})`
}

const neutralResourceNames = [
  '1000', '950', '900', '850', '800', '750', '700', '650',
  '600', '500', '400', '350', '300', '200', '100', '50',
]

function xamlColor(key, value) {
  return `    <Color x:Key="${key}">${normalizeHex(value)}</Color>`
}

function generateWpf() {
  const { typography, dimensions, themes } = source
  const dark = themes.dark
  const bodyLineHeight = Math.round(typography.sizes.bodySm * typography.lineHeights.body)
  const lines = [
    `<!-- ${generatedNotice} Do not edit by hand. -->`,
    '<ResourceDictionary',
    '    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
    '    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
    '    xmlns:sys="clr-namespace:System;assembly=System.Runtime">',
    '',
    `    <FontFamily x:Key="TypographyFontSans">${typography.families.sans.wpf}</FontFamily>`,
    `    <FontFamily x:Key="TypographyFontMono">${typography.families.mono.wpf}</FontFamily>`,
    `    <FontFamily x:Key="TypographyFontBrand">${typography.families.brand.wpf}</FontFamily>`,
    `    <FontFamily x:Key="TypographyFontSymbol">${typography.families.symbol.wpf}</FontFamily>`,
    '',
  ]

  const wpfSizeNames = {
    micro: 'Micro',
    caption: 'Caption',
    bodySm: 'BodySmall',
    body: 'Body',
    bodyLg: 'BodyLarge',
    titleSm: 'TitleSmall',
    titleMd: 'TitleMedium',
    titleLg: 'TitleLarge',
    display: 'Display',
    glyphSm: 'GlyphSmall',
    glyphMd: 'GlyphMedium',
    glyphLg: 'GlyphLarge',
    brandLg: 'BrandLarge',
  }
  for (const [name, value] of Object.entries(typography.sizes)) {
    lines.push(`    <sys:Double x:Key="TypographySize${wpfSizeNames[name]}">${value}</sys:Double>`)
  }
  lines.push(`    <sys:Double x:Key="TypographyLineHeightBody">${bodyLineHeight}</sys:Double>`, '')

  const wpfWeights = {
    light: 'Light',
    regular: 'Normal',
    medium: 'Medium',
    semibold: 'SemiBold',
    bold: 'Bold',
  }
  for (const [name, value] of Object.entries(wpfWeights)) {
    lines.push(`    <FontWeight x:Key="TypographyWeight${pascal(name)}">${value}</FontWeight>`)
  }
  lines.push('')

  for (const [name, value] of Object.entries(dimensions.spacing)) {
    lines.push(`    <sys:Double x:Key="Spacing${name}">${value}</sys:Double>`)
  }
  for (const [name, value] of Object.entries(dimensions.radii)) {
    lines.push(`    <CornerRadius x:Key="Radius${pascal(name)}">${value}</CornerRadius>`)
  }
  for (const [name, value] of Object.entries(dimensions.controlHeights)) {
    lines.push(`    <sys:Double x:Key="ControlHeight${pascal(name)}">${value}</sys:Double>`)
  }
  lines.push('')

  dark.tones.forEach((color, index) =>
    lines.push(xamlColor(`ColorNeutral${neutralResourceNames[index]}`, color)))
  lines.push(
    '',
    xamlColor('ColorRunning', dark.status.info.default),
    xamlColor('ColorRunningSurface', dark.status.info.surface),
    xamlColor('ColorSuccessSurface', dark.status.success.surfaceEmphasis),
    xamlColor('ColorSuccess', dark.status.success.default),
    xamlColor('ColorWarningSurface', dark.status.warning.surfaceEmphasis),
    xamlColor('ColorWarning', dark.status.warning.default),
    xamlColor('ColorDangerSurface', dark.status.danger.surfaceEmphasis),
    xamlColor('ColorDanger', dark.status.danger.default),
    '',
    xamlColor('ColorShadow', dark.desktop.shadow),
    xamlColor('ColorGlassWindow', dark.desktop.glassWindow),
    xamlColor('ColorGlassPanel', dark.desktop.glassPanel),
    xamlColor('ColorAccentHalo', dark.desktop.accentHalo),
    xamlColor('ColorWarningTint', dark.desktop.warningTint),
    '',
    '    <SolidColorBrush x:Key="WindowBrush" Color="{DynamicResource ColorNeutral1000}" />',
    '    <SolidColorBrush x:Key="SurfaceBrush" Color="{DynamicResource ColorNeutral950}" />',
    '    <SolidColorBrush x:Key="RaisedBrush" Color="{DynamicResource ColorNeutral900}" />',
    '    <SolidColorBrush x:Key="ElevatedBrush" Color="{DynamicResource ColorNeutral850}" />',
    '    <SolidColorBrush x:Key="HoverBrush" Color="{DynamicResource ColorNeutral800}" />',
    '    <SolidColorBrush x:Key="SelectionBrush" Color="{DynamicResource ColorNeutral750}" />',
    '    <SolidColorBrush x:Key="SelectionStrongBrush" Color="{DynamicResource ColorNeutral700}" />',
    '    <SolidColorBrush x:Key="StrokeBrush" Color="{DynamicResource ColorNeutral750}" />',
    '    <SolidColorBrush x:Key="StrokeStrongBrush" Color="{DynamicResource ColorNeutral650}" />',
    '    <SolidColorBrush x:Key="FocusBrush" Color="{DynamicResource ColorNeutral600}" />',
    '    <SolidColorBrush x:Key="TextPrimaryBrush" Color="{DynamicResource ColorNeutral100}" />',
    '    <SolidColorBrush x:Key="TextSecondaryBrush" Color="{DynamicResource ColorNeutral350}" />',
    '    <SolidColorBrush x:Key="TextMutedBrush" Color="{DynamicResource ColorNeutral400}" />',
    '    <SolidColorBrush x:Key="TextInverseBrush" Color="{DynamicResource ColorNeutral1000}" />',
    '    <SolidColorBrush x:Key="AccentBrush" Color="{DynamicResource ColorNeutral50}" />',
    '    <SolidColorBrush x:Key="AccentHoverBrush" Color="{DynamicResource ColorNeutral200}" />',
    '    <SolidColorBrush x:Key="RunningBrush" Color="{DynamicResource ColorRunning}" />',
    '    <SolidColorBrush x:Key="RunningSurfaceBrush" Color="{DynamicResource ColorRunningSurface}" />',
    '    <SolidColorBrush x:Key="SuccessBrush" Color="{DynamicResource ColorSuccess}" />',
    '    <SolidColorBrush x:Key="SuccessSurfaceBrush" Color="{DynamicResource ColorSuccessSurface}" />',
    '    <SolidColorBrush x:Key="WarningBrush" Color="{DynamicResource ColorWarning}" />',
    '    <SolidColorBrush x:Key="WarningSurfaceBrush" Color="{DynamicResource ColorWarningSurface}" />',
    '    <SolidColorBrush x:Key="DangerBrush" Color="{DynamicResource ColorDanger}" />',
    '    <SolidColorBrush x:Key="DangerSurfaceBrush" Color="{DynamicResource ColorDangerSurface}" />',
    '    <SolidColorBrush x:Key="GlassWindowBrush" Color="{DynamicResource ColorGlassWindow}" />',
    '    <SolidColorBrush x:Key="GlassPanelBrush" Color="{DynamicResource ColorGlassPanel}" />',
    '    <SolidColorBrush x:Key="AccentHaloBrush" Color="{DynamicResource ColorAccentHalo}" />',
    '    <SolidColorBrush x:Key="WarningTintBrush" Color="{DynamicResource ColorWarningTint}" />',
    '</ResourceDictionary>',
    '',
  )
  return lines.join('\n')
}

function themePaletteExpression(theme) {
  const values = [
    `new[] { ${theme.tones.map(colorExpression).join(', ')} }`,
    colorExpression(theme.status.info.default),
    colorExpression(theme.status.info.surface),
    colorExpression(theme.status.success.default),
    colorExpression(theme.status.success.surfaceEmphasis),
    colorExpression(theme.status.warning.default),
    colorExpression(theme.status.warning.surfaceEmphasis),
    colorExpression(theme.status.danger.default),
    colorExpression(theme.status.danger.surfaceEmphasis),
    colorExpression(theme.desktop.shadow),
    colorExpression(theme.desktop.glassWindow),
    colorExpression(theme.desktop.glassPanel),
    colorExpression(theme.desktop.accentHalo),
    colorExpression(theme.desktop.warningTint),
  ]
  return `new(\n        ${values.join(',\n        ')})`
}

function generateCsharp() {
  return `// ${generatedNotice} Do not edit by hand.
using Color = System.Windows.Media.Color;

namespace PiCompanion.Desktop.Design;

internal sealed record GeneratedThemePalette(
    Color[] Tones,
    Color Running,
    Color RunningSurface,
    Color Success,
    Color SuccessSurface,
    Color Warning,
    Color WarningSurface,
    Color Danger,
    Color DangerSurface,
    Color Shadow,
    Color GlassWindow,
    Color GlassPanel,
    Color AccentHalo,
    Color WarningTint);

internal static class GeneratedDesignTokens
{
    public static GeneratedThemePalette For(AppTheme theme) =>
        theme == AppTheme.Light ? Light : Dark;

    public static GeneratedThemePalette Dark { get; } = ${themePaletteExpression(source.themes.dark)};

    public static GeneratedThemePalette Light { get; } = ${themePaletteExpression(source.themes.light)};
}
`
}

const generated = {
  [outputPaths.colors]: generateColors(),
  [outputPaths.typography]: generateTypography(),
  [outputPaths.components]: generateComponents(),
  [outputPaths.wpf]: generateWpf(),
  [outputPaths.csharp]: generateCsharp(),
}

let stale = false
for (const [path, content] of Object.entries(generated)) {
  if (checkOnly) {
    const current = await readFile(path, 'utf8').catch(() => '')
    if (current.replaceAll('\r\n', '\n') !== content.replaceAll('\r\n', '\n')) {
      console.error(`Design token output is stale: ${path}`)
      stale = true
    }
    continue
  }

  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, content, 'utf8')
  console.log(`Generated ${path}`)
}

if (stale) {
  console.error('Run: node scripts/generate-design-tokens.mjs')
  process.exitCode = 1
}
