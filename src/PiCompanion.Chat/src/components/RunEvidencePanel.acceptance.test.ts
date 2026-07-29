import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import RunEvidencePanel from './RunEvidencePanel.vue'
import type { TaskRunSnapshot } from '@/types/bridge'

describe('RunEvidencePanel', () => {
  it('collapses details by default and keeps actionable warnings when expanded', async () => {
    const run: TaskRunSnapshot = {
      id: 'run-1',
      prompt: '检查变更',
      model: 'provider/model',
      thinkingLevel: 'high',
      messageAttachments: [],
      status: 'Completed',
      statusText: '已完成',
      summary: '完成',
      assistantText: null,
      finalAnswer: '完成',
      lastSequence: 1,
      pendingSteering: [],
      pendingFollowUps: [],
      transcript: [],
      activities: [],
      evidence: {
        runId: 'run-1',
        finalized: true,
        isGitRepository: true,
        gitRoot: 'D:\\work',
        headBefore: null,
        headAfter: null,
        testStatus: 'NotRun',
        files: [],
        commands: [{
          id: 'command-1', toolCallId: 'bash-1', command: 'dotnet test', workingDirectory: 'D:\\work',
          startedAt: new Date().toISOString(), durationMilliseconds: 1200, exitCode: 0,
          cancelled: false, timedOut: false, outputSummary: '45 tests passed', fullOutputPath: null,
          isTest: true, detectedFramework: 'dotnet', status: 'Passed',
        }],
        tests: [],
        warnings: [
          { code: 'shell-coverage', message: 'Shell 可以绕过 edit/write。', createdAt: new Date().toISOString() },
          { code: 'watcher-overflow', message: '文件变化列表可能不完整。', createdAt: new Date().toISOString() },
        ],
      },
    }

    const wrapper = mount(RunEvidencePanel, { props: { run, taskActive: false } })

    expect(wrapper.text()).not.toContain('Shell 可以绕过 edit/write')
    expect(wrapper.text()).not.toContain('dotnet test')
    expect(wrapper.find('.evidence-run-details').exists()).toBe(false)
    expect(wrapper.find('.evidence-warnings').exists()).toBe(false)
    expect(wrapper.get('.evidence-toggle').attributes('aria-expanded')).toBe('false')

    await wrapper.get('.evidence-toggle').trigger('click')

    expect(wrapper.get('.evidence-toggle').attributes('aria-expanded')).toBe('true')
    expect(wrapper.text()).toContain('文件变化列表可能不完整')
  })

  it('omits the redundant observed-change label', async () => {
    const run: TaskRunSnapshot = {
      id: 'run-2', prompt: '修改文件', model: 'provider/model', thinkingLevel: 'high', messageAttachments: [],
      status: 'Completed', statusText: '已完成', summary: '完成', assistantText: null, finalAnswer: '完成',
      lastSequence: 1, pendingSteering: [], pendingFollowUps: [], transcript: [], activities: [],
      evidence: {
        runId: 'run-2', finalized: true, isGitRepository: false, gitRoot: null, headBefore: null, headAfter: null,
        testStatus: 'NotRun', commands: [], tests: [], warnings: [], files: [{
          id: 'change-1', path: 'D:\\work\\README.md', relativePath: 'README.md', kind: 'Modified',
          source: 'FileSystemWatcher', confidence: 'Observed', isBinary: false, hasDiff: false,
          beforeHash: null, afterHash: null, beforeSize: null, afterSize: null,
          addedLines: 0, deletedLines: 0, diffTruncated: false, recovery: 'Unavailable', recoveryMessage: null,
        }],
      },
    }
    const wrapper = mount(RunEvidencePanel, { props: { run, taskActive: false } })

    expect(wrapper.text()).toContain('1 个文件')
    expect(wrapper.find('.file-change-list').exists()).toBe(false)
    await wrapper.get('.evidence-toggle').trigger('click')
    expect(wrapper.text()).toContain('README.md')
    expect(wrapper.text()).not.toContain('检测到文件变化')
  })

  it('can expand file changes from the persisted default', () => {
    const run: TaskRunSnapshot = {
      id: 'run-expanded', prompt: '修改文件', model: 'provider/model', thinkingLevel: 'high', messageAttachments: [],
      status: 'Completed', statusText: '已完成', summary: '完成', assistantText: null, finalAnswer: '完成',
      lastSequence: 1, pendingSteering: [], pendingFollowUps: [], transcript: [], activities: [],
      evidence: {
        runId: 'run-expanded', finalized: true, isGitRepository: false, gitRoot: null, headBefore: null, headAfter: null,
        testStatus: 'NotRun', commands: [], tests: [], warnings: [], files: [{
          id: 'change-expanded', path: 'D:\\work\\README.md', relativePath: 'README.md', kind: 'Modified',
          source: 'FileSystemWatcher', confidence: 'Observed', isBinary: false, hasDiff: false,
          beforeHash: null, afterHash: null, beforeSize: null, afterSize: null,
          addedLines: 0, deletedLines: 0, diffTruncated: false, recovery: 'Unavailable', recoveryMessage: null,
        }],
      },
    }

    const wrapper = mount(RunEvidencePanel, {
      props: { run, taskActive: false, expandedByDefault: true },
    })

    expect(wrapper.get('.evidence-toggle').attributes('aria-expanded')).toBe('true')
    expect(wrapper.text()).toContain('README.md')
  })
})
