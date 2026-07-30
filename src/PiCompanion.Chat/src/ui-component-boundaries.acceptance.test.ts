import { readdirSync, readFileSync } from 'node:fs'
import { join, relative, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

const sourceRoot = resolve(process.cwd(), 'src')
const uiRoot = resolve(sourceRoot, 'components/ui')

function vueFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name)
    return entry.isDirectory()
      ? vueFiles(path)
      : entry.name.endsWith('.vue')
        ? [path]
        : []
  })
}

const businessComponents = vueFiles(sourceRoot)
  .filter(path => !path.startsWith(uiRoot))

describe('UI component boundaries', () => {
  it('keeps native interactive elements inside the shared UI layer', () => {
    const violations = businessComponents.flatMap(path => {
      const matches = readFileSync(path, 'utf8')
        .match(/<(?:button|input|textarea|select)(?=[\s>])/giu)
      return matches ? [`${relative(sourceRoot, path)}: ${matches.join(', ')}`] : []
    })

    expect(violations).toEqual([])
  })

  it('routes modal semantics through UiDialog', () => {
    const violations = businessComponents.filter(path =>
      /role=["'](?:dialog|alertdialog)["']/u.test(readFileSync(path, 'utf8')))

    expect(violations.map(path => relative(sourceRoot, path))).toEqual([])
  })

  it('portals dialogs outside application layout containers and keeps their shell interactive', () => {
    const dialog = readFileSync(resolve(uiRoot, 'UiDialog.vue'), 'utf8')
    const styles = readFileSync(resolve(sourceRoot, 'ui-components.css'), 'utf8')
    const layerRule = styles.match(/\.ui-dialog-layer\s*\{(?<declarations>[^}]*)\}/u)

    expect(dialog).toContain('DialogPortal')
    expect(dialog).toContain('<DialogPortal')
    expect(layerRule?.groups?.declarations).toContain('pointer-events: auto')
  })

  it('keeps scoped dialog shell classes global across the UiDialog boundary', () => {
    const settings = readFileSync(resolve(sourceRoot, 'components/SettingsModal.vue'), 'utf8')
    const skillManager = readFileSync(resolve(sourceRoot, 'components/SkillManagementModal.vue'), 'utf8')
    const skills = readFileSync(resolve(sourceRoot, 'components/SkillsView.vue'), 'utf8')

    expect(settings).toContain(':global(.settings-backdrop)')
    expect(settings).toContain(':global(.settings-modal)')
    expect(skillManager).toContain(':global(.skill-manager-backdrop)')
    expect(skillManager).toContain(':global(.skill-manager)')
    expect(skills).toContain(':global(.skill-detail-backdrop)')
    expect(skills).toContain(':global(.skill-detail-dialog)')
  })

  it('keeps the skill manager shell vertically stable when status rows appear', () => {
    const skillManager = readFileSync(
      resolve(sourceRoot, 'components/SkillManagementModal.vue'),
      'utf8',
    )
    const shellRule = skillManager.match(
      /:global\(\.skill-manager\)\s*\{(?<declarations>[^}]*)\}/u,
    )

    expect(shellRule?.groups?.declarations).toContain('display: flex')
    expect(shellRule?.groups?.declarations).toContain('flex-direction: column')
    expect(shellRule?.groups?.declarations).not.toContain('grid-template-rows')
    expect(skillManager).toContain(':global(.skill-manager > header)')
    expect(skillManager).not.toContain(':global(.skill-manager) > header')
  })

  it('exports the complete UI foundation from one module and loads its tokens first', () => {
    const entry = readFileSync(resolve(sourceRoot, 'main.ts'), 'utf8')
    const registry = readFileSync(resolve(uiRoot, 'index.ts'), 'utf8')

    expect(entry).toContain("import './component-tokens.css'")
    expect(registry).toContain('UiButton')
    expect(registry).toContain('UiDialog')
    expect(registry).toContain('UiMenu')
    expect(registry).toContain('UiSelect')
    expect(registry).toContain('UiSwitch')
  })

  it('centers workspace inspector tab labels inside their equal-width buttons', () => {
    const styles = readFileSync(resolve(sourceRoot, 'styles.css'), 'utf8')
    const tabRule = styles.match(/\.inspector-tabs button\s*\{(?<declarations>[^}]*)\}/u)

    expect(tabRule?.groups?.declarations).toContain('justify-content: center')
    expect(tabRule?.groups?.declarations).toContain('text-align: center')
  })
})
