import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import McpSettings from './McpSettings.vue'
import UiSelect from './ui/UiSelect.vue'
import type { McpSnapshot, WorkspaceHistoryEntry } from '@/types/bridge'

const workspace: WorkspaceHistoryEntry = { id: 'workspace', name: 'Project', workingDirectory: 'D:\\Project', createdAt: '', updatedAt: '', taskCount: 0, hasActiveTask: false, trustStatus: 'trusted' }
const snapshot: McpSnapshot = {
  servers: [{ name: 'docs', scope: 'global', config: { url: 'https://example.com/mcp', exposure: 'deferred', headers: { 'X-Token': 'secret-value' } }, state: 'needs-auth', tools: [], error: null }],
  globalRevision: 'global-revision', projectRevision: 'project-revision', projectTrusted: true,
}
async function ready() {
  const wrapper = mount(McpSettings, { props: { workspaces: [workspace], activeWorkspaceId: workspace.id } })
  const request = wrapper.emitted('request')![0]![0] as { requestId: string }
  await wrapper.setProps({ result: { requestId: request.requestId, workspaceId: workspace.id, action: 'list', succeeded: true, snapshot, message: '' } })
  return wrapper
}

describe('MCP settings', () => {
  it('uses service details for sign-in and cancels when the panel closes', async () => {
    const wrapper = await ready()
    expect(wrapper.text()).toContain('需要登录')
    await wrapper.findAll('button').find(button => button.text() === '登录')!.trigger('click')
    const request = wrapper.emitted('request')!.at(-1)![0] as { requestId: string; action: string; scope: string; name: string }
    expect(request).toMatchObject({ action: 'login', scope: 'global', name: 'docs' })
    await wrapper.setProps({ loginProgress: { requestId: request.requestId, phase: 'waiting' } })
    await wrapper.get('input[aria-label="回调地址"]').setValue('http://127.0.0.1:1234/callback?code=test')
    await wrapper.findAll('button').find(button => button.text() === '继续')!.trigger('click')
    expect(wrapper.emitted('submitRedirect')![0]).toEqual([request.requestId, 'http://127.0.0.1:1234/callback?code=test'])
    wrapper.unmount()
    expect(wrapper.emitted('cancelLogin')![0]).toEqual([request.requestId])
  })
  it('keeps credentials masked and preserves imported settings while editing', async () => {
    const wrapper = await ready()
    await wrapper.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    expect(wrapper.get('input[aria-label="值"]').attributes('type')).toBe('password')
    await wrapper.get('form').trigger('submit')
    const request = wrapper.emitted('request')!.at(-1)![0] as Record<string, unknown>
    expect(request).toMatchObject({ action: 'save', scope: 'global', originalName: 'docs', revision: 'global-revision', config: { headers: { 'X-Token': 'secret-value' }, exposure: 'deferred' } })
    wrapper.unmount()
  })
  it('previews imports before writing and keeps a failed operation editable', async () => {
    const wrapper = await ready()
    await wrapper.findAll('button').find(button => button.text() === '导入')!.trigger('click')
    await wrapper.get('textarea').setValue('{"mcpServers":{"local":{"command":"node","args":["server.mjs"]}}}')
    await wrapper.findAll('button').find(button => button.text() === '下一步')!.trigger('click')
    expect(wrapper.text()).toContain('local')
    expect(wrapper.emitted('request')!.length).toBe(1)
    await wrapper.findAll('button').find(button => button.text() === '导入 1')!.trigger('click')
    const request = wrapper.emitted('request')!.at(-1)![0] as { requestId: string }
    await wrapper.setProps({ result: { requestId: request.requestId, workspaceId: workspace.id, action: 'import', succeeded: false, message: '已存在同名服务。' } })
    expect(wrapper.text()).toContain('已存在同名服务。')
    expect(wrapper.text()).toContain('导入 1')
    wrapper.unmount()
  })
  it('saves only workspace choices for an inherited personal server', async () => {
    const wrapper = await ready()
    const scope = wrapper.findAllComponents(UiSelect).find(select => select.props('ariaLabelText') === '保存位置')!
    scope.vm.$emit('update:modelValue', 'project')
    await wrapper.vm.$nextTick()
    const load = wrapper.emitted('request')!.at(-1)![0] as { requestId: string }
    const inherited: McpSnapshot = { ...snapshot, servers: [{ ...snapshot.servers[0]!, scope: 'project', isOverride: true, inherited: true, overrideConfig: {} }] }
    await wrapper.setProps({ result: { requestId: load.requestId, workspaceId: workspace.id, action: 'list', succeeded: true, snapshot: inherited, message: '' } })
    await wrapper.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    expect(wrapper.find('input[placeholder="https://example.com/mcp"]').exists()).toBe(false)
    expect(wrapper.find('input[aria-label="值"]').exists()).toBe(false)
    await wrapper.get('[role="switch"]').trigger('click')
    const exposure = wrapper.findAllComponents(UiSelect).find(select => select.props('ariaLabelText') === '调用方式')!
    exposure.vm.$emit('update:modelValue', 'direct')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('request')!.at(-1)![0]).toMatchObject({ action: 'save', scope: 'project', originalName: 'docs', config: { enabled: false, exposure: 'direct' } })
    const request = wrapper.emitted('request')!.at(-1)![0] as { config: object }
    expect(Object.keys(request.config).sort()).toEqual(['enabled', 'exposure'])
    wrapper.unmount()
  })
})
