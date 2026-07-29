import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { createSkillsPreview } from '@/preview'
import type { WorkspaceHistoryEntry } from '@/types/bridge'
import { UiSelect } from '@/components/ui'
import SkillsView from './SkillsView.vue'

const workspace: WorkspaceHistoryEntry = {
  id: 'preview-workspace',
  name: 'pi-companion',
  workingDirectory: 'D:\\Dev\\pi-companion',
  createdAt: '2026-07-27T00:00:00.000Z',
  updatedAt: '2026-07-27T00:00:00.000Z',
  taskCount: 1,
  hasActiveTask: false,
}

describe('SkillsView management acceptance', () => {
  let wrapper: ReturnType<typeof mount> | null = null

  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
  })

  it('keeps cards compact and moves metadata and locations into details', async () => {
    const snapshot = createSkillsPreview()

    wrapper = mount(SkillsView, {
      props: {
        snapshot,
        loading: false,
        error: null,
        sidebarCollapsed: false,
      },
    })

    expect(wrapper.findAll('.skill-card')).toHaveLength(3)
    const globalSkill = wrapper.findAll('.skill-card')
      .find(card => card.text().includes('find-skills'))!
    expect(globalSkill.find('.skill-origin').exists()).toBe(false)
    expect(globalSkill.findAll('.skill-card-footer button')).toHaveLength(1)
    expect(globalSkill.get('.skill-card-footer button').text()).toBe('查看详情')
    expect(globalSkill.text()).toContain('1 处安装')
    expect(globalSkill.text()).toContain('1 种内容')

    await globalSkill.get('.skill-card-footer button').trigger('click')
    const dialog = wrapper.get('.skill-detail-dialog')
    expect(dialog.text()).toContain('1.2.0')
    expect(dialog.text()).toContain('MIT')
    expect(dialog.text()).toContain('4 个 · 8.0 KB')
    expect(dialog.findAll('.skill-extra-metadata dt').map(node => node.text()))
      .toEqual(['作者', '兼容性'])
    expect(dialog.findAll('.skill-extra-metadata dd').map(node => node.text()))
      .toEqual(['openai/find-skills', 'Pi and compatible agent skill runtimes.'])
    expect(dialog.text()).toContain('全局')
    expect(dialog.get('.source-agent').text()).toBe('Agent')
    expect(dialog.text()).toContain('.agents\\skills\\find-skills')
    expect(dialog.find('.skill-remove-button').exists()).toBe(false)
    expect(dialog.text()).not.toContain('只有 Pi 专属目录中的技能可以卸载')
    expect(dialog.text()).not.toContain('当前生效')
    expect(dialog.find('.skill-detail-summary').exists()).toBe(false)
    expect(dialog.text()).not.toContain('SHA-256')

    await wrapper.get('.skill-detail-header > button').trigger('click')
    await wrapper.get('input[type="search"]').setValue('release-notes')
    expect(wrapper.findAll('.skill-card')).toHaveLength(1)
  })

  it('groups by exact name and separates content variants by hash', async () => {
    const snapshot = createSkillsPreview()
    const skill = snapshot.skills.find(candidate => candidate.name === 'release-notes')!
    const firstVariant = skill.variants[0]!
    const sameContentLocation = structuredClone(firstVariant.installations[0]!)
    sameContentLocation.id = 'release-notes-global-pi'
    sameContentLocation.installPath = 'C:\\Users\\you\\.pi\\agent\\skills\\release-notes'
    sameContentLocation.filePath = `${sameContentLocation.installPath}\\SKILL.md`
    sameContentLocation.canonicalPath = sameContentLocation.filePath
    sameContentLocation.isGloballyEffective = true
    sameContentLocation.origins = [{
      scope: 'global',
      source: 'pi',
      rootPath: 'C:\\Users\\you\\.pi\\agent\\skills',
      workspaceId: null,
      workspaceName: null,
      workspacePath: null,
      inherited: false,
      installPath: 'C:\\Users\\you\\.pi\\agent\\skills\\release-notes',
      isCompatibilityLink: false,
      linkTarget: null,
    }]
    firstVariant.installations.push(sameContentLocation)

    const differentContent = structuredClone(firstVariant)
    differentContent.id = '4444444444444444444444444444444444444444444444444444444444444444'
    differentContent.contentHash = differentContent.id
    differentContent.version = '3.0.0'
    differentContent.metadata.version = '3.0.0'
    differentContent.installations = [differentContent.installations[0]!]
    differentContent.installations[0]!.id = 'release-notes-workspace-pi-v3'
    differentContent.installations[0]!.installPath =
      'D:\\Dev\\pi-companion\\.pi\\skills\\release-notes-v3'
    skill.variants.push(differentContent)

    wrapper = mount(SkillsView, {
      props: {
        snapshot,
        loading: false,
        error: null,
        sidebarCollapsed: false,
      },
    })

    const card = wrapper.findAll('.skill-card')
      .find(candidate => candidate.text().includes('release-notes'))!
    expect(card.text()).toContain('3 处安装')
    expect(card.text()).toContain('2 种内容')
    expect(card.text()).toContain('内容不同')

    await card.get('.skill-card-footer button').trigger('click')
    expect(wrapper.findAll('.skill-variant')).toHaveLength(2)
    expect(wrapper.findAll('.skill-installation')).toHaveLength(3)
    expect(wrapper.get('.skill-detail-dialog').text()).toContain('同名内容不一致')
  })

  it('explains a Pi compatibility link without presenting it as a broken installation', async () => {
    const snapshot = createSkillsPreview()
    const skill = snapshot.skills.find(candidate => candidate.name === 'find-skills')!
    const installation = skill.variants[0]!.installations[0]!
    installation.origins.push({
      scope: 'global',
      source: 'pi',
      rootPath: 'C:\\Users\\you\\.pi\\agent\\skills',
      workspaceId: null,
      workspaceName: null,
      workspacePath: null,
      inherited: false,
      installPath: 'C:\\Users\\you\\.pi\\agent\\skills\\find-skills',
      isCompatibilityLink: true,
      linkTarget: installation.installPath,
    })

    wrapper = mount(SkillsView, {
      props: {
        snapshot,
        loading: false,
        error: null,
        sidebarCollapsed: false,
      },
    })

    const card = wrapper.findAll('.skill-card')
      .find(candidate => candidate.text().includes('find-skills'))!
    expect(card.text()).toContain('1 处安装')
    await card.get('.skill-card-footer button').trigger('click')

    const dialog = wrapper.get('.skill-detail-dialog')
    expect(dialog.findAll('.skill-installation-heading')).toHaveLength(0)
    expect(dialog.findAll('.skill-installation-context')).toHaveLength(1)
    expect(dialog.findAll('.skill-installation-path')).toHaveLength(2)
    expect(dialog.text()).toContain('全局')
    expect(dialog.text()).toContain('Agent')
    expect(dialog.text()).toContain('真实目录')
    expect(dialog.text()).toContain('C:\\Users\\you\\.agents\\skills\\find-skills')
    expect(dialog.text()).toContain('Pi')
    expect(dialog.text()).toContain('兼容入口')
    expect(dialog.text()).toContain('C:\\Users\\you\\.pi\\agent\\skills\\find-skills')
    expect(dialog.text()).not.toContain('内容检查失败')
    expect(dialog.find('.skill-remove-button').exists()).toBe(false)
  })

  it('confirms and emits removal only for a Pi installation', async () => {
    wrapper = mount(SkillsView, {
      props: {
        snapshot: createSkillsPreview(),
        loading: false,
        error: null,
        sidebarCollapsed: false,
      },
    })

    const releaseCard = wrapper.findAll('.skill-card')
      .find(card => card.text().includes('release-notes'))!
    await releaseCard.get('.skill-card-footer button').trigger('click')
    await wrapper.get('.skill-remove-button').trigger('click')
    expect(wrapper.get('.skill-removal-confirm-backdrop').text())
      .toContain('原内容将移到技能根目录中的可恢复位置')
    expect(wrapper.get('.skill-removal-confirm-backdrop code').text())
      .toContain('.pi\\skills\\release-notes')

    await wrapper.get('.skill-removal-confirm-backdrop .danger').trigger('click')
    expect(wrapper.emitted('removeInstallation')).toEqual([[
      {
        installationId: 'release-notes-workspace-pi',
        expectedContentHash:
          '2222222222222222222222222222222222222222222222222222222222222222',
      },
    ]])
  })

  it('uses the same grouped list for a workspace context', async () => {
    const snapshot = createSkillsPreview()
    const unrelated = structuredClone(snapshot.skills[1]!)
    unrelated.id = 'other-workspace-skill'
    unrelated.name = 'other-workspace-skill'
    const installation = unrelated.variants[0]!.installations[0]!
    installation.id = 'other-workspace-installation'
    installation.effectiveWorkspaceIds = ['other-workspace']
    installation.origins[0]!.workspaceId = 'other-workspace'
    installation.origins[0]!.workspaceName = 'Other'
    snapshot.skills.push(unrelated)

    wrapper = mount(SkillsView, {
      props: {
        snapshot,
        loading: false,
        error: null,
        sidebarCollapsed: false,
        contextWorkspace: workspace,
      },
    })

    expect(wrapper.get('.management-location strong').text()).toBe('pi-companion 的技能')
    expect(wrapper.text()).not.toContain('other-workspace-skill')
    expect(wrapper.text()).toContain('release-notes')
    expect(wrapper.find('.skills-import').exists()).toBe(false)
    await wrapper.get('.skills-context-clear').trigger('click')
    expect(wrapper.emitted('clearContext')).toHaveLength(1)
  })

  it('marks project skills unavailable until their workspace is trusted', async () => {
    const snapshot = createSkillsPreview()
    snapshot.workspaceTrust[0]!.status = 'undecided'
    const releaseNotes = snapshot.skills.find(skill => skill.name === 'release-notes')!
    const installation = releaseNotes.variants[0]!.installations[0]!
    installation.effectiveWorkspaceIds = []
    const diagnostic = {
      code: 'workspace-untrusted',
      severity: 'warning' as const,
      message: '工作区“pi-companion”尚未受 Pi 信任；该工作区中的技能不会被加载。',
      path: installation.filePath,
      winnerPath: null,
      workspaceId: workspace.id,
      workspaceName: workspace.name,
    }
    installation.diagnostics.push(diagnostic)
    releaseNotes.diagnostics.push(diagnostic)

    wrapper = mount(SkillsView, {
      props: {
        snapshot,
        loading: false,
        error: null,
        sidebarCollapsed: false,
      },
    })

    const card = wrapper.findAll('.skill-card')
      .find(candidate => candidate.text().includes('release-notes'))!
    expect(card.get('.skill-status').text()).toBe('未信任工作区')
    expect(card.get('.skill-trust-button').text()).toBe('信任工作区')

    await card.get('.skill-trust-button').trigger('click')
    expect(wrapper.emitted('trustWorkspace')).toEqual([[workspace.id]])

    await card.get('.skill-card-footer button:last-child').trigger('click')
    expect(wrapper.get('.skill-detail-dialog').text()).toContain('未信任工作区')
    expect(wrapper.get('.skill-diagnostics').text()).toContain('该工作区中的技能不会被加载')
  })

  it('shows only globally relevant skill groups for Direct Chat', () => {
    wrapper = mount(SkillsView, {
      props: {
        snapshot: createSkillsPreview(),
        loading: false,
        error: null,
        sidebarCollapsed: false,
        globalOnly: true,
      },
    })

    expect(wrapper.get('.management-location strong').text()).toBe('全局技能')
    expect(wrapper.findAll('.skill-card')).toHaveLength(1)
    expect(wrapper.text()).toContain('find-skills')
    expect(wrapper.text()).not.toContain('release-notes')
    expect(wrapper.find('.skills-import').exists()).toBe(false)
  })

  it('imports only from the full page and requires an explicit destination', async () => {
    wrapper = mount(SkillsView, {
      props: {
        snapshot: createSkillsPreview(),
        loading: false,
        error: null,
        sidebarCollapsed: false,
        workspaces: [workspace],
      },
    })

    expect(wrapper.findAll('.skills-import').map(button => button.text()))
      .toEqual(['导入技能'])
    await wrapper.get('.skills-import').trigger('click')
    expect(wrapper.get('.skill-import-dialog').text()).toContain('选择导入位置和本地来源')
    expect(wrapper.find('.skill-import-section-heading > span').exists()).toBe(false)
    expect(wrapper.get('.skill-import-close').attributes('aria-label')).toBe('关闭')
    expect(wrapper.get('.skill-import-dialog .primary').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).not.toContain('不会根据当前任务自动推断')
    expect(wrapper.findAll('.skill-import-source-empty button').map(button => button.text()))
      .toEqual(['选择文件夹', '选择 ZIP'])
    expect(wrapper.emitted('openImport')).toHaveLength(1)

    const destinationSelects = wrapper.findAllComponents(UiSelect)
    expect(destinationSelects).toHaveLength(2)
    expect(destinationSelects[1]!.props('disabled')).toBe(true)
    destinationSelects[0]!.vm.$emit('update:modelValue', 'workspace')
    await wrapper.vm.$nextTick()
    expect(destinationSelects[1]!.props('disabled')).toBe(false)
    expect(wrapper.get('.skill-import-dialog .primary').attributes('disabled')).toBeDefined()
    destinationSelects[1]!.vm.$emit('update:modelValue', 'preview-workspace')
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.skill-import-dialog .primary').attributes('disabled')).toBeDefined()
    await wrapper.findAll('.skill-import-source-empty button')[0]!.trigger('click')
    expect(wrapper.emitted('beginImport')).toEqual([['folder']])

    const source = {
      token: 'source-1',
      name: 'scripted',
      description: 'Runs a local script.',
      sourceKind: 'folder' as const,
      contentHash: 'source-hash',
      fileCount: 3,
      totalBytes: 3072,
      files: [
        { relativePath: 'SKILL.md', size: 1024, kind: 'file' as const },
        { relativePath: 'scripts/run.ps1', size: 1536, kind: 'script' as const },
        { relativePath: 'references/usage.md', size: 512, kind: 'file' as const },
      ],
      scriptFiles: ['scripts/run.ps1'],
      executableFiles: [],
    }
    await wrapper.setProps({ importSource: source })
    await wrapper.vm.$nextTick()
    expect(wrapper.emitted('prepareImport')?.at(-1)).toEqual([{
      targetScope: 'workspace',
      workspaceId: 'preview-workspace',
    }])
    expect(wrapper.get('.skill-import-file-list').text()).toContain('references/usage.md')
    expect(wrapper.get('.skill-import-source-summary').text()).toContain('含 1 个脚本')
    expect(wrapper.find('.skill-import-warning').exists()).toBe(false)
    expect(wrapper.findAll('.skill-import-source-actions button')).toHaveLength(1)
    expect(wrapper.get('.skill-import-source-actions button').text()).toContain('重新选择')
    expect(wrapper.get('.skill-import-dialog .primary').attributes('disabled')).toBeDefined()

    await wrapper.setProps({
      importPreparation: {
        token: 'prepared-1',
        sourceToken: 'source-1',
        name: 'scripted',
        description: 'Runs a local script.',
        targetScope: 'workspace',
        workspaceId: workspace.id,
        workspaceName: workspace.name,
        targetPath: `${workspace.workingDirectory}\\.pi\\skills\\scripted`,
        sourceKind: 'folder',
        contentHash: 'source-hash',
        fileCount: 3,
        totalBytes: 3072,
        files: source.files,
        scriptFiles: ['scripts/run.ps1'],
        executableFiles: [],
        requiresProjectTrust: true,
        trustStatus: 'undecided',
      },
    })
    const dialog = wrapper.get('.skill-import-dialog')
    expect(dialog.text()).toContain('需要信任此 Pi 项目')
    expect(dialog.get('.skill-import-location').text()).toContain('需要信任此 Pi 项目')
    expect(dialog.get('.skill-import-source').text()).not.toContain('需要信任此 Pi 项目')
    expect(dialog.text()).toContain('scripts/run.ps1')
    expect(dialog.text()).toContain('信任并导入')
    expect(dialog.get('.primary').attributes('disabled')).toBeUndefined()
    await dialog.get('.primary').trigger('click')
    expect(wrapper.emitted('confirmImport')).toHaveLength(1)
  })
})
