import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import WorkspaceGitPanel from './WorkspaceGitPanel.vue'
import type { WorkspaceGitHistorySnapshot, WorkspaceGitSnapshot } from '@/types/bridge'

describe('WorkspaceGitPanel', () => {
  it('shows the current branch and opens a read-only diff from the changed file list', async () => {
    const update: WorkspaceGitSnapshot = {
      requestId: 'git-1',
      workingDirectory: 'D:\\work',
      isRepository: true,
      repositoryRoot: 'D:\\work',
      branch: 'main',
      isDetached: false,
      entries: [{
        relativePath: 'src/App.vue',
        originalRelativePath: null,
        status: ' M',
        indexStatus: ' ',
        workingTreeStatus: 'M',
        kind: 'Modified',
        isStaged: false,
        isUnstaged: true,
        isUntracked: false,
        isBinary: false,
        addedLines: 8,
        deletedLines: 3,
      }],
      error: null,
    }
    const wrapper = mount(WorkspaceGitPanel, {
      props: {
        workingDirectory: 'D:\\work',
        update,
        historyUpdate: null,
        actionResult: null,
        pendingAction: null,
      },
    })

    expect(wrapper.get('.git-branch-row').text()).toContain('main')
    expect(wrapper.find('.git-branch-chevron').exists()).toBe(true)
    await wrapper.get('.git-current-branch').trigger('click')
    expect(wrapper.get('.git-create-branch-option').text()).toContain('创建新分支…')
    expect(wrapper.get('.git-branch-option.current').text()).toContain('main')
    expect(wrapper.get('.git-change-section').text()).toContain('更改')
    expect(wrapper.get('.git-change-file').text()).toContain('App.vue')
    expect(wrapper.get('.git-change-file').text()).toContain('+8')
    expect(wrapper.get('.git-change-file').text()).toContain('-3')
    expect(wrapper.get('.git-change-directory > span').text()).toBe('src')
    expect(wrapper.get('.git-change-directory > i').text()).toBe('/')

    await wrapper.get('.git-change-file').trigger('click')
    expect(wrapper.emitted('openDiff')).toBeUndefined()
    await wrapper.get('.git-change-main').trigger('click')
    expect(wrapper.emitted('openDiff')?.[0]?.[0]).toEqual(update.entries[0])
    await wrapper.get('button[aria-label="刷新 Git 变更"]').trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
  })

  it('stages files, commits manual messages, and replaces PR with local commit history', async () => {
    const update: WorkspaceGitSnapshot = {
      requestId: 'git-2',
      workingDirectory: 'D:\\work',
      isRepository: true,
      repositoryRoot: 'D:\\work',
      branch: 'main',
      isDetached: false,
      branches: [
        { name: 'feature', shortHash: '1234567', subject: 'feature', isCurrent: false },
        { name: 'main', shortHash: '7654321', subject: 'main', isCurrent: true },
      ],
      operationState: 'None',
      canManageBranches: true,
      entries: [{
        relativePath: 'README.md',
        originalRelativePath: null,
        status: ' M',
        indexStatus: ' ',
        workingTreeStatus: 'M',
        kind: 'Modified',
        isStaged: false,
        isUnstaged: true,
        isUntracked: false,
        isBinary: false,
        addedLines: 2,
        deletedLines: 1,
      }],
      error: null,
    }
    const wrapper = mount(WorkspaceGitPanel, {
      props: {
        workingDirectory: 'D:\\work',
        update,
        historyUpdate: null,
        actionResult: null,
        pendingAction: null,
      },
    })

    expect(wrapper.findAll('.git-mode-tabs button').map(button => button.text())).toEqual([
      '提交',
      '更新',
      '提交历史',
    ])
    expect(wrapper.find('.git-mode-tabs svg').exists()).toBe(false)
    expect(wrapper.text().toLocaleLowerCase()).not.toContain('pr')
    expect(wrapper.get('.git-ai-generate').attributes('disabled')).toBeDefined()

    await wrapper.get('.git-current-branch').trigger('click')
    expect(wrapper.get('.git-branch-menu-heading').text()).toBe('本地分支')
    expect(wrapper.get('.git-branch-search input').attributes('placeholder')).toBe('搜索分支…')
    expect(wrapper.get('.git-create-branch-option').text()).toContain('创建新分支…')
    expect(wrapper.get('.git-create-branch-option').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('.git-branch-option')).toHaveLength(2)
    expect(wrapper.get('.git-branch-option.current').text()).toContain('当前')
    await wrapper.get('.git-branch-option.current').trigger('click')

    await wrapper.setProps({ update: { ...update, entries: [] } })
    await wrapper.get('.git-current-branch').trigger('click')
    await wrapper.findAll('.git-branch-option')[0]!.trigger('click')
    expect(wrapper.emitted('switchBranch')?.[0]).toEqual(['feature'])

    await wrapper.get('.git-current-branch').trigger('click')
    await wrapper.get('.git-create-branch-option').trigger('click')
    expect(wrapper.find('.git-new-branch').exists()).toBe(true)

    await wrapper.setProps({ update })
    await wrapper.get('.git-change-action').trigger('click')
    expect(wrapper.emitted('stage')?.[0]).toEqual([['README.md']])

    await wrapper.setProps({
      update: {
        ...update,
        entries: [{ ...update.entries[0]!, status: 'M ', indexStatus: 'M', workingTreeStatus: ' ', isStaged: true, isUnstaged: false }],
      },
      actionResult: {
        requestId: 'stage-1',
        workingDirectory: 'D:\\work',
        action: 'stage',
        succeeded: true,
        message: '已暂存所选文件。',
        detail: null,
      },
    })
    expect(wrapper.find('.git-action-notice').exists()).toBe(false)
    expect(wrapper.get('.git-change-group-toggle strong').text()).toBe('已暂存')
    await wrapper.get('.git-change-group-toggle').trigger('click')
    expect(wrapper.find('.git-change-files').exists()).toBe(false)
    await wrapper.get('.git-change-group-toggle').trigger('click')
    await wrapper.get('.git-change-group-batch').trigger('click')
    expect(wrapper.emitted('unstage')?.[0]).toEqual([['README.md']])
    await wrapper.get('.git-change-action').trigger('click')
    expect(wrapper.emitted('unstage')?.[1]).toEqual([['README.md']])
    const commitInput = wrapper.get('.git-commit-form textarea')
    Object.defineProperty(commitInput.element, 'scrollHeight', { configurable: true, value: 88 })
    await commitInput.setValue('docs: update readme')
    expect((commitInput.element as HTMLTextAreaElement).style.height).toBe('88px')
    expect((commitInput.element as HTMLTextAreaElement).style.overflowY).toBe('hidden')
    Object.defineProperty(commitInput.element, 'scrollHeight', { configurable: true, value: 240 })
    await commitInput.trigger('input')
    expect((commitInput.element as HTMLTextAreaElement).style.height).toBe('120px')
    expect((commitInput.element as HTMLTextAreaElement).style.overflowY).toBe('auto')
    await wrapper.get('.git-commit-form').trigger('submit')
    expect(wrapper.emitted('commit')?.[0]).toEqual(['docs: update readme'])

    const historyTab = wrapper.findAll('.git-mode-tabs button')[2]!
    await historyTab.trigger('click')
    expect(wrapper.emitted('refreshHistory')).toHaveLength(1)
    const historyUpdate: WorkspaceGitHistorySnapshot = {
      requestId: 'history-1',
      workingDirectory: 'D:\\work',
      offset: 0,
      hasMore: true,
      entries: [{
        hash: '0123456789abcdef',
        shortHash: '0123456',
        subject: 'docs: update readme',
        authorName: 'Pi User',
        authorEmail: 'pi@example.test',
        timestamp: '2026-07-24T12:00:00+08:00',
        parents: ['fedcba9876543210'],
      }],
      error: null,
    }
    await wrapper.setProps({ historyUpdate })
    expect(wrapper.get('.git-history-list').text()).toContain('docs: update readme')
    await wrapper.get('.git-history-list button').trigger('click')
    expect(wrapper.emitted('openCommit')?.[0]?.[0]).toEqual(historyUpdate.entries[0])
    await wrapper.get('.git-history-load-more button').trigger('click')
    expect(wrapper.emitted('refreshHistory')?.[1]).toEqual([true])
    await wrapper.setProps({ historyLoading: true })
    expect(wrapper.get('.git-history-load-more button').attributes('disabled')).toBeDefined()
  })

  it('generates from staged changes and rejects a result for an outdated index', async () => {
    const update: WorkspaceGitSnapshot = {
      requestId: 'git-ai-1',
      workingDirectory: 'D:\\work',
      isRepository: true,
      repositoryRoot: 'D:\\work',
      branch: 'main',
      isDetached: false,
      stagedFingerprint: 'fingerprint-1',
      entries: [{
        relativePath: 'src/App.vue',
        originalRelativePath: null,
        status: 'M ',
        indexStatus: 'M',
        workingTreeStatus: ' ',
        kind: 'Modified',
        isStaged: true,
        isUnstaged: false,
        isUntracked: false,
        isBinary: false,
        addedLines: 12,
        deletedLines: 3,
      }],
      error: null,
    }
    const wrapper = mount(WorkspaceGitPanel, {
      props: {
        workingDirectory: 'D:\\work',
        update,
        historyUpdate: null,
        actionResult: null,
        pendingAction: null,
        commitMessageResult: null,
        commitMessageLoading: false,
      },
    })

    expect(wrapper.get('.git-ai-generate').text()).toBe('生成')
    await wrapper.get('.git-ai-generate').trigger('click')
    expect(wrapper.emitted('generateCommitMessage')).toHaveLength(1)

    await wrapper.setProps({
      commitMessageResult: {
        requestId: 'message-1',
        workingDirectory: 'D:\\work',
        succeeded: true,
        message: 'feat: customize workspaces',
        stagedFingerprint: 'fingerprint-1',
        truncatedInput: false,
        error: null,
      },
    })
    expect((wrapper.get('.git-commit-form textarea').element as HTMLTextAreaElement).value)
      .toBe('feat: customize workspaces')
    expect(wrapper.get('.git-ai-generate').text()).toBe('生成')

    await wrapper.get('.git-commit-form textarea').setValue('user-edited message')
    await wrapper.setProps({
      update: { ...update, stagedFingerprint: 'fingerprint-2' },
      commitMessageResult: {
        requestId: 'message-2',
        workingDirectory: 'D:\\work',
        succeeded: true,
        message: 'feat: stale generated message',
        stagedFingerprint: 'fingerprint-1',
        truncatedInput: false,
        error: null,
      },
    })

    expect((wrapper.get('.git-commit-form textarea').element as HTMLTextAreaElement).value)
      .toBe('user-edited message')
    expect(wrapper.get('.git-commit-message-notice.warning').text()).toContain('暂存内容已变化')
  })
})
