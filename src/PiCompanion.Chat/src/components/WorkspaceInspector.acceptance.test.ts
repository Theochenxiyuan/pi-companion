import { mount } from '@vue/test-utils'
import { BaseTree } from '@he-tree/vue'
import { describe, expect, it, vi } from 'vitest'
import WorkspaceInspector from './WorkspaceInspector.vue'
import type { WorkspaceDirectoryListing, WorkspaceFileEntry, WorkspaceGitSnapshot } from '@/types/bridge'

function fileEntry(
  entry: Omit<WorkspaceFileEntry, 'isIgnored' | 'ignoreSource'> &
    Partial<Pick<WorkspaceFileEntry, 'isIgnored' | 'ignoreSource'>>,
): WorkspaceFileEntry {
  return { isIgnored: false, ignoreSource: null, ...entry }
}

const BaseTreeStub = {
  props: ['modelValue', 'defaultOpen', 'statHandler'],
  template: `
    <div class="base-tree-stub">
      <div v-for="node in modelValue" :key="node.relativePath">
        <slot :node="node" :stat="{ open: false }" />
      </div>
    </div>
  `,
}

describe('WorkspaceInspector', () => {
  it('restores only explicitly expanded folders after an async tree rebuild', async () => {
    const wrapper = mount(WorkspaceInspector, {
      props: {
        workingDirectory: 'D:\\work',
        directoryUpdate: null,
        searchUpdate: null,
        gitUpdate: null,
        taskId: null,
        taskTitle: null,
        selectedModel: null,
        selectedModelReference: '',
        sessionModelReference: null,
        sessionUpdate: null,
        sessionLoading: false,
        sessionManualLoadAvailable: false,
        activeTab: 'files',
        width: 300,
      },
      global: { stubs: { BaseTree: BaseTreeStub } },
    })

    const rootRequest = wrapper.emitted('loadDirectory')?.[0]
    await wrapper.setProps({
      directoryUpdate: {
        requestId: rootRequest?.[0] as string,
        workingDirectory: 'D:\\work',
        relativePath: '',
        entries: [
          fileEntry({ name: 'alpha', relativePath: 'alpha', isDirectory: true, hasChildren: true, isReparsePoint: false }),
          fileEntry({ name: 'beta', relativePath: 'beta', isDirectory: true, hasChildren: true, isReparsePoint: false }),
        ],
        inaccessibleEntries: 0,
        error: null,
      } satisfies WorkspaceDirectoryListing,
    })

    const tree = wrapper.getComponent(BaseTree)
    const restore = tree.props('statHandler') as unknown as (stat: {
      data: WorkspaceFileEntry
      open: boolean
    }) => { open: boolean }
    expect(tree.props('defaultOpen')).toBe(false)
    expect(restore({ data: fileEntry({ name: 'beta', relativePath: 'beta', isDirectory: true, hasChildren: true, isReparsePoint: false }), open: true }).open).toBe(false)

    await wrapper.findAll('.file-tree-row')[0]!.trigger('click')

    expect(restore({ data: fileEntry({ name: 'alpha', relativePath: 'alpha', isDirectory: true, hasChildren: true, isReparsePoint: false }), open: false }).open).toBe(true)
    expect(restore({ data: fileEntry({ name: 'beta', relativePath: 'beta', isDirectory: true, hasChildren: true, isReparsePoint: false }), open: true }).open).toBe(false)
    wrapper.unmount()
  })

  it('loads, searches and reveals workspace entries', async () => {
    vi.useFakeTimers()
    const wrapper = mount(WorkspaceInspector, {
      props: {
        workingDirectory: 'D:\\work',
        directoryUpdate: null,
        searchUpdate: null,
        gitUpdate: null,
        taskId: null,
        taskTitle: null,
        selectedModel: null,
        selectedModelReference: '',
        sessionModelReference: null,
        sessionUpdate: null,
        sessionLoading: false,
        sessionManualLoadAvailable: false,
        activeTab: 'files',
        width: 300,
      },
      global: { stubs: { BaseTree: BaseTreeStub } },
      attachTo: document.body,
    })

    const rootRequest = wrapper.emitted('loadDirectory')?.[0]
    expect(rootRequest?.[1]).toBe('')
    const listing: WorkspaceDirectoryListing = {
      requestId: rootRequest?.[0] as string,
      workingDirectory: 'D:\\work',
      relativePath: '',
      entries: [
        fileEntry({ name: 'src', relativePath: 'src', isDirectory: true, hasChildren: true, isReparsePoint: false }),
      ],
      inaccessibleEntries: 0,
      error: null,
    }
    await wrapper.setProps({ directoryUpdate: listing })

    expect(wrapper.text()).toContain('src')
    await wrapper.findAll('.file-tree-row')[0]!.trigger('click')
    expect(wrapper.emitted('loadDirectory')?.[1]?.[1]).toBe('src')

    await wrapper.setProps({
      directoryUpdate: {
        ...listing,
        entries: [
          fileEntry({ name: 'README.md', relativePath: 'README.md', isDirectory: false, hasChildren: false, isReparsePoint: false }),
        ],
      },
    })
    expect(wrapper.text()).toContain('README.md')

    await wrapper.findAll('.file-tree-row')[0]!.trigger('dblclick')
    expect(wrapper.emitted('reveal')).toBeUndefined()

    await wrapper.get('.file-search input').setValue('readme')
    await vi.advanceTimersByTimeAsync(260)
    expect(wrapper.emitted('search')?.[0]?.[1]).toBe('readme')
    expect(wrapper.emitted('search')?.[0]?.[2]).toBe(false)

    await wrapper.findAll('.file-tree-row')[0]!.trigger('contextmenu', { clientX: 100, clientY: 100 })
    const menuButton = document.body.querySelector<HTMLButtonElement>('.workspace-file-context-menu button')
    expect(menuButton?.textContent).toContain('在资源管理器中显示')
    menuButton?.click()
    await wrapper.vm.$nextTick()
    expect(wrapper.emitted('reveal')?.[0]?.[0]).toMatchObject({ relativePath: 'README.md' })

    await wrapper.get('button[aria-label="刷新文件和 Git 状态"]').trigger('click')
    expect(wrapper.emitted('refreshGit')).toHaveLength(1)

    wrapper.unmount()
    vi.useRealTimers()
  })

  it('dims ignored entries, explains their source, and opts them into search explicitly', async () => {
    vi.useFakeTimers()
    const wrapper = mount(WorkspaceInspector, {
      props: {
        workingDirectory: 'D:\\work',
        directoryUpdate: null,
        searchUpdate: null,
        gitUpdate: null,
        taskId: null,
        taskTitle: null,
        selectedModel: null,
        selectedModelReference: '',
        sessionModelReference: null,
        sessionUpdate: null,
        sessionLoading: false,
        sessionManualLoadAvailable: false,
        activeTab: 'files',
        width: 300,
      },
      global: { stubs: { BaseTree: BaseTreeStub } },
    })

    const rootRequest = wrapper.emitted('loadDirectory')?.[0]
    await wrapper.setProps({
      directoryUpdate: {
        requestId: rootRequest?.[0] as string,
        workingDirectory: 'D:\\work',
        relativePath: '',
        entries: [
          fileEntry({
            name: 'node_modules',
            relativePath: 'node_modules',
            isDirectory: true,
            hasChildren: true,
            isReparsePoint: false,
            isIgnored: true,
            ignoreSource: 'built-in',
          }),
        ],
        inaccessibleEntries: 0,
        error: null,
      } satisfies WorkspaceDirectoryListing,
    })

    const ignoredRow = wrapper.get('.file-tree-row')
    expect(ignoredRow.classes()).toContain('ignored')
    expect(ignoredRow.get('.file-ignore-decoration').text()).toBe('忽略')
    expect(ignoredRow.attributes('title')).toContain('已忽略 · 内置规则')

    await wrapper.get('.file-search input').setValue('dependency')
    await vi.advanceTimersByTimeAsync(260)
    expect(wrapper.emitted('search')?.at(-1)).toEqual([
      expect.any(String),
      'dependency',
      false,
    ])

    await wrapper.get('button[aria-label="包含忽略文件"]').trigger('click')
    await vi.advanceTimersByTimeAsync(260)
    expect(wrapper.get('button[aria-label="包含忽略文件"]').attributes('aria-pressed')).toBe('true')
    expect(wrapper.emitted('search')?.at(-1)).toEqual([
      expect.any(String),
      'dependency',
      true,
    ])

    wrapper.unmount()
    vi.useRealTimers()
  })

  it('shows VS Code-style file badges and propagates deep Git changes to ancestor folders', async () => {
    const wrapper = mount(WorkspaceInspector, {
      props: {
        workingDirectory: 'D:\\work',
        directoryUpdate: null,
        searchUpdate: null,
        gitUpdate: null,
        taskId: null,
        taskTitle: null,
        selectedModel: null,
        selectedModelReference: '',
        sessionModelReference: null,
        sessionUpdate: null,
        sessionLoading: false,
        sessionManualLoadAvailable: false,
        activeTab: 'files',
        width: 300,
      },
      global: { stubs: { BaseTree: BaseTreeStub } },
    })

    const rootRequest = wrapper.emitted('loadDirectory')?.[0]
    await wrapper.setProps({
      directoryUpdate: {
        requestId: rootRequest?.[0] as string,
        workingDirectory: 'D:\\work',
        relativePath: '',
        entries: [
          fileEntry({ name: 'src', relativePath: 'src', isDirectory: true, hasChildren: true, isReparsePoint: false }),
        ],
        inaccessibleEntries: 0,
        error: null,
      } satisfies WorkspaceDirectoryListing,
      gitUpdate: {
        requestId: 'git-1',
        workingDirectory: 'D:\\work',
        isRepository: true,
        repositoryRoot: 'D:\\work',
        branch: 'main',
        isDetached: false,
        entries: [
          {
            relativePath: 'README.md', originalRelativePath: null, status: 'MM',
            indexStatus: 'M', workingTreeStatus: 'M', kind: 'Modified',
            isStaged: true, isUnstaged: true, isUntracked: false,
            isBinary: false, addedLines: 2, deletedLines: 1,
          },
          {
            relativePath: 'src/deep/Button.vue', originalRelativePath: null, status: ' M',
            indexStatus: ' ', workingTreeStatus: 'M', kind: 'Modified',
            isStaged: false, isUnstaged: true, isUntracked: false,
            isBinary: false, addedLines: 3, deletedLines: 1,
          },
          {
            relativePath: 'src/new.ts', originalRelativePath: null, status: '??',
            indexStatus: '?', workingTreeStatus: '?', kind: 'Added',
            isStaged: false, isUnstaged: true, isUntracked: true,
            isBinary: false, addedLines: 4, deletedLines: 0,
          },
          {
            relativePath: 'src/conflict.ts', originalRelativePath: null, status: 'UU',
            indexStatus: 'U', workingTreeStatus: 'U', kind: 'Unmerged',
            isStaged: true, isUnstaged: true, isUntracked: false,
            isBinary: false, addedLines: 0, deletedLines: 0,
          },
          {
            relativePath: 'renamed/new.ts', originalRelativePath: 'legacy/old.ts', status: 'R ',
            indexStatus: 'R', workingTreeStatus: ' ', kind: 'Renamed',
            isStaged: true, isUnstaged: false, isUntracked: false,
            isBinary: false, addedLines: 0, deletedLines: 0,
          },
        ],
        error: null,
      } satisfies WorkspaceGitSnapshot,
    })

    const srcRow = wrapper.get('.file-tree-row')

    expect(srcRow.classes()).toContain('git-conflict')
    expect(srcRow.find('.file-git-decoration').exists()).toBe(false)
    expect(srcRow.get('.folder-git-decoration').classes()).toContain('conflict')
    expect(srcRow.attributes('title')).toContain('包含 3 个 Git 变更')
    expect(srcRow.attributes('title')).toContain('1 已修改')
    expect(srcRow.attributes('title')).toContain('1 未跟踪')
    expect(srcRow.attributes('title')).toContain('1 有冲突')

    await wrapper.setProps({
      directoryUpdate: {
        requestId: rootRequest?.[0] as string,
        workingDirectory: 'D:\\work',
        relativePath: '',
        entries: [
          fileEntry({ name: 'legacy', relativePath: 'legacy', isDirectory: true, hasChildren: true, isReparsePoint: false }),
        ],
        inaccessibleEntries: 0,
        error: null,
      } satisfies WorkspaceDirectoryListing,
    })
    const legacyRow = wrapper.get('.file-tree-row')
    expect(legacyRow.classes()).toContain('git-renamed')
    expect(legacyRow.attributes('title')).toContain('包含 1 个 Git 变更')

    await wrapper.setProps({
      directoryUpdate: {
        requestId: rootRequest?.[0] as string,
        workingDirectory: 'D:\\work',
        relativePath: '',
        entries: [
          fileEntry({ name: 'README.md', relativePath: 'README.md', isDirectory: false, hasChildren: false, isReparsePoint: false }),
        ],
        inaccessibleEntries: 0,
        error: null,
      } satisfies WorkspaceDirectoryListing,
    })
    const readmeRow = wrapper.get('.file-tree-row')
    expect(readmeRow.get('.file-git-decoration').text()).toBe('M')
    expect(readmeRow.get('.file-git-decoration').classes()).toContain('modified')
    expect(readmeRow.attributes('title')).toContain('Git：已修改 · 已暂存和未暂存')
  })

  it('exposes the same resize handle contract as the left sidebar', async () => {
    const wrapper = mount(WorkspaceInspector, {
      props: {
        workingDirectory: null,
        directoryUpdate: null,
        searchUpdate: null,
        gitUpdate: null,
        taskId: null,
        taskTitle: null,
        selectedModel: null,
        selectedModelReference: '',
        sessionModelReference: null,
        sessionUpdate: null,
        sessionLoading: false,
        sessionManualLoadAvailable: false,
        activeTab: 'files',
        width: 300,
      },
      global: { stubs: { BaseTree: BaseTreeStub } },
    })

    const resizer = wrapper.get('.inspector-resizer')
    expect(resizer.attributes('aria-valuemin')).toBe('250')
    expect(resizer.attributes('aria-valuemax')).toBe('400')
    expect(resizer.attributes('aria-valuenow')).toBe('300')
    await resizer.trigger('dblclick')
    await resizer.trigger('keydown', { key: 'ArrowLeft' })

    expect(wrapper.emitted('setWidth')).toEqual([[300], [312]])
  })
})
