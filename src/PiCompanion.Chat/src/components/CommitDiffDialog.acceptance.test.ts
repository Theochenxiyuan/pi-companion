import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import CommitDiffDialog from './CommitDiffDialog.vue'
import type { WorkspaceGitCommitDiff } from '@/types/bridge'

const commitDiff: WorkspaceGitCommitDiff = {
  workingDirectory: 'D:\\work',
  hash: '0123456789abcdef',
  shortHash: '0123456',
  subject: 'feat: group commit diff by file',
  truncated: true,
  files: [
    {
      relativePath: 'src/App.vue',
      originalRelativePath: null,
      status: 'Modified',
      addedLines: 2,
      deletedLines: 1,
      diffText: 'diff --git a/src/App.vue b/src/App.vue\n--- a/src/App.vue\n+++ b/src/App.vue\n@@ -1 +1,2 @@\n-old\n+new\n+next\n',
      isBinary: false,
      truncated: false,
    },
    {
      relativePath: 'assets/logo.bin',
      originalRelativePath: null,
      status: 'Added',
      addedLines: null,
      deletedLines: null,
      diffText: null,
      isBinary: true,
      truncated: false,
    },
    {
      relativePath: 'src/New Name.vue',
      originalRelativePath: 'src/Old Name.vue',
      status: 'Renamed',
      addedLines: 0,
      deletedLines: 0,
      diffText: 'diff --git a/src/Old Name.vue b/src/New Name.vue\nsimilarity index 100%\nrename from src/Old Name.vue\nrename to src/New Name.vue\n',
      isBinary: false,
      truncated: true,
    },
  ],
}

describe('CommitDiffDialog', () => {
  it('lists commit files and shows only the selected file diff', async () => {
    const wrapper = mount(CommitDiffDialog, { props: { diff: commitDiff } })

    expect(wrapper.get('.commit-diff-dialog').text()).toContain(commitDiff.subject)
    expect(wrapper.findAll('.commit-diff-sidebar nav > button')).toHaveLength(3)
    expect(wrapper.get('.commit-diff-content').text()).toContain('src/App.vue')
    expect(wrapper.get('.commit-unified-diff').text()).toContain('old')
    expect(wrapper.get('.commit-unified-diff').text()).not.toContain('Old Name.vue')
    expect(wrapper.get('.diff-meta').text()).toContain('+2')
    expect(wrapper.get('.diff-meta').text()).toContain('-1')

    await wrapper.findAll('.commit-diff-sidebar nav > button')[2]!.trigger('click')
    expect(wrapper.get('.commit-diff-file-header').text()).toContain('src/Old Name.vue')
    expect(wrapper.get('.commit-diff-file-header').text()).toContain('src/New Name.vue')

    await wrapper.get('button[aria-label="上一个文件"]').trigger('click')
    expect(wrapper.get('.commit-diff-content').text()).toContain('assets/logo.bin')
    expect(wrapper.find('.binary-diff').exists()).toBe(true)

    await wrapper.get('input[aria-label="搜索提交文件"]').setValue('App.vue')
    expect(wrapper.findAll('.commit-diff-sidebar nav > button')).toHaveLength(1)
    expect(wrapper.get('.commit-diff-content').text()).toContain('src/App.vue')

    await wrapper.get('button[aria-label="关闭 Diff"]').trigger('click')
    expect(wrapper.emitted('close')).toHaveLength(1)
  })
})
