import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import SkillManagementModal from './SkillManagementModal.vue'
import { createSkillsPreview } from '@/preview'
import { setLocale } from '@/i18n'

afterEach(() => setLocale('zh-CN'))

const workspace = {
  id: 'preview-workspace',
  name: 'pi-companion',
  workingDirectory: 'D:\\Dev\\pi-companion',
  createdAt: '2026-07-27T00:00:00.000Z',
  updatedAt: '2026-07-27T00:00:00.000Z',
  taskCount: 1,
  hasActiveTask: false,
}

describe('skill management modal', () => {
  it('keeps global and Agent skills read-only and removes a project Pi skill', async () => {
    const wrapper = mount(SkillManagementModal, {
      props: {
        snapshot: createSkillsPreview(),
        loading: false,
        error: null,
        workspace,
      },
    })

    expect(wrapper.findAll('.skill-manager-section-heading h2').map(node => node.text()))
      .toEqual(['工作区技能', '全局技能'])
    expect(wrapper.findAll('.source-pi').map(node => node.text())).toEqual(['Pi'])
    expect(wrapper.findAll('.source-agent').map(node => node.text())).toEqual(['Agent', 'Agent'])
    expect(wrapper.text()).not.toContain('处安装')
    expect(wrapper.text()).not.toContain('D:\\Dev\\pi-companion')
    expect(wrapper.text()).not.toContain('C:\\Users\\you')
    expect(wrapper.text()).toContain('同名时，工作区技能优先于全局技能')
    expect(wrapper.findAll('.skill-manager-readonly')).toHaveLength(2)
    expect(wrapper.findAll('.skill-manager-remove')).toHaveLength(1)

    await wrapper.get('.skill-manager-remove').trigger('click')
    expect(wrapper.get('[role="alertdialog"]').text()).toContain('卸载 release-notes？')
    await wrapper.get('.skill-manager-confirm .danger').trigger('click')

    expect(wrapper.emitted('removeInstallation')).toEqual([[
      {
        installationId: 'release-notes-workspace-pi',
        expectedContentHash:
          '2222222222222222222222222222222222222222222222222222222222222222',
        workspaceId: 'preview-workspace',
      },
    ]])
  })

  it('lists only global installations in Direct Chat and never exposes removal', () => {
    const wrapper = mount(SkillManagementModal, {
      props: {
        snapshot: createSkillsPreview(),
        loading: false,
        error: null,
        globalOnly: true,
      },
    })

    expect(wrapper.text()).toContain('Direct Chat 技能')
    expect(wrapper.findAll('.skill-manager-section-heading h2').map(node => node.text()))
      .toEqual(['全局技能'])
    expect(wrapper.text()).toContain('find-skills')
    expect(wrapper.text()).not.toContain('release-notes')
    expect(wrapper.text()).not.toContain('draft')
    expect(wrapper.findAll('.skill-manager-remove')).toHaveLength(0)
    expect(wrapper.findAll('.skill-manager-readonly')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('C:\\Users\\you')
  })

  it('explains that project skills are inactive and can trust the workspace', async () => {
    const snapshot = createSkillsPreview()
    snapshot.workspaceTrust[0]!.status = 'undecided'

    const wrapper = mount(SkillManagementModal, {
      props: {
        snapshot,
        loading: false,
        error: null,
        workspace,
      },
    })

    expect(wrapper.get('.skill-manager-trust').text())
      .toContain('Pi 不会加载此工作区中的项目技能')
    expect(wrapper.findAll('.skill-manager-untrusted').map(node => node.text()))
      .toEqual(['未信任工作区', '未信任工作区'])

    await wrapper.get('.skill-manager-trust button').trigger('click')
    expect(wrapper.emitted('trustWorkspace')).toEqual([[workspace.id]])
  })

  it('shows workspace priority only when the same skill also exists globally', () => {
    const snapshot = createSkillsPreview()
    const releaseNotes = snapshot.skills.find(skill => skill.name === 'release-notes')!
    const globalInstallation = structuredClone(releaseNotes.variants[0]!.installations[0]!)
    globalInstallation.id = 'release-notes-global-pi'
    globalInstallation.installPath = 'C:\\Users\\you\\.pi\\agent\\skills\\release-notes'
    globalInstallation.origins = [{
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
    releaseNotes.variants[0]!.installations.push(globalInstallation)

    const wrapper = mount(SkillManagementModal, {
      props: {
        snapshot,
        loading: false,
        error: null,
        workspace,
      },
    })

    expect(wrapper.findAll('.skill-manager-priority').map(node => node.text()))
      .toEqual(['覆盖全局', '已被工作区覆盖'])
  })
})
