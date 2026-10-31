<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { UiButton, UiDialog, UiInput, UiSelect, UiSwitch, UiTextarea } from '@/components/ui'
import { useI18n } from '@/i18n'
import { formatMcpCommand, parseMcpImport, splitMcpCommand } from '@/utils/mcp'
import type { McpConfig, McpLoginProgress, McpResult, McpScope, McpServer, McpSnapshot, WorkspaceHistoryEntry } from '@/types/bridge'

const props = defineProps<{
  workspaces: WorkspaceHistoryEntry[]
  activeWorkspaceId?: string
  result?: McpResult | null
  loginProgress?: McpLoginProgress | null
}>()
const emit = defineEmits<{
  request: [payload: Record<string, unknown>]
  cancelLogin: [requestId: string]
  submitRedirect: [requestId: string, redirectUrl: string]
  trustWorkspace: [workspaceId: string]
}>()
const { t } = useI18n()
const snapshot = ref<McpSnapshot | null>(null)
const workspaceId = ref(props.activeWorkspaceId || props.workspaces[0]?.id || '')
const scope = ref<McpScope>('global')
const search = ref('')
const selected = ref('')
const pending = ref<{ id: string; action: string } | null>(null)
const message = ref('')
const editing = ref(false)
const original = ref<McpServer | null>(null)
const name = ref('')
const kind = ref('remote')
const command = ref('')
const url = ref('')
const directory = ref('')
const enabled = ref(true)
const timeout = ref(60)
const exposure = ref<NonNullable<McpConfig['exposure']>>('deferred')
const pairs = ref<Array<{ key: string; value: string; revealed: boolean }>>([])
const oauthClientId = ref('')
const oauthClientSecret = ref('')
const oauthCallbackUrl = ref('')
const importing = ref(false)
const importText = ref('')
const importPreview = ref<Record<string, McpConfig> | null>(null)
const removeTarget = ref<McpServer | null>(null)
const redirectUrl = ref('')
const workspace = computed(() => props.workspaces.find(value => value.id === workspaceId.value))
const trusted = computed(() => workspace.value?.trustStatus === 'trusted')
const workspaceOptions = computed(() =>
  props.workspaces.map(value => ({ value: value.id, label: value.displayName || value.name })))
const servers = computed(() => (snapshot.value?.servers ?? []).filter(server =>
  server.scope === scope.value && server.name.toLowerCase().includes(search.value.trim().toLowerCase())))
const current = computed(() => servers.value.find(server => server.name === selected.value) ?? servers.value[0])
const canSignIn = computed(() => current.value?.state === 'needs-auth')
const signedIn = computed(() => current.value?.authenticated === true && current.value.state !== 'needs-auth')
const waiting = computed(() => pending.value?.action === 'login' && props.loginProgress?.requestId === pending.value.id)
const canChange = computed(() => !pending.value && !!snapshot.value && (scope.value === 'global' || trusted.value))
const statusLabels: Record<McpServer['state'], string> = {
  unchecked: '未检查', connected: '可用', disabled: '已停用', untrusted: '需要信任',
  overridden: '工作区优先', 'needs-auth': '需要登录', failed: '连接失败',
}

function request(action: string, values: Record<string, unknown> = {}) {
  if (pending.value) return
  const requestId = crypto.randomUUID()
  pending.value = { id: requestId, action }
  message.value = ''
  emit('request', {
    requestId, action, scope: scope.value, workspaceId: workspaceId.value || null,
    revision: scope.value === 'global' ? snapshot.value?.globalRevision : snapshot.value?.projectRevision,
    ...values,
  })
}

function load(check = false) { request('list', { check }) }
function selectScope(next: McpScope) {
  if (pending.value || next === scope.value) return
  if (next === 'project' && !workspaceId.value) workspaceId.value = props.workspaces[0]?.id ?? ''
  scope.value = next
}
function selectServer(server: McpServer) {
  if (pending.value) return
  selected.value = server.name
  editing.value = false
}
function changeKind(value: string) { if (kind.value !== value) { kind.value = value; pairs.value = [] } }
function beginEdit(server?: McpServer) {
  if (!canChange.value) return
  original.value = server ?? null
  name.value = server?.name ?? ''
  const config = server?.config ?? {}
  kind.value = config.command ? 'local' : 'remote'
  command.value = config.command ? formatMcpCommand(config) : ''
  url.value = config.url ?? ''
  directory.value = config.cwd ?? ''
  enabled.value = config.enabled !== false
  timeout.value = config.timeout ?? 60
  exposure.value = config.exposure ?? (server ? 'codemode' : 'deferred')
  pairs.value = Object.entries(config.command ? config.env ?? {} : config.headers ?? {}).map(([key, value]) => ({ key, value, revealed: false }))
  oauthClientId.value = config.oauth?.clientId ?? ''
  oauthClientSecret.value = config.oauth?.clientSecret ?? ''
  oauthCallbackUrl.value = config.oauth?.callbackUrl ?? ''
  editing.value = true
  message.value = ''
}

function save() {
  try {
    if (!/^[a-zA-Z0-9_-]+$/u.test(name.value)) throw new Error(t('名称只能包含字母、数字、短横线和下划线。'))
    if (original.value?.isOverride) {
      request('save', { name: name.value, originalName: original.value.name,
        config: { ...original.value.overrideConfig, enabled: enabled.value, exposure: exposure.value }, check: true })
      return
    }
    const values: Record<string, string> = Object.create(null)
    for (const pair of pairs.value) {
      if (!pair.key.trim() && !pair.value) continue
      if (!pair.key.trim() || Object.hasOwn(values, pair.key.trim())) throw new Error(t('请检查重复或空白的名称。'))
      values[pair.key.trim()] = pair.value
    }
    const config: McpConfig = { ...original.value?.config, enabled: enabled.value, timeout: Number(timeout.value), exposure: exposure.value }
    delete config.type
    if (kind.value === 'local') {
      const args = splitMcpCommand(command.value)
      if (!args[0]) throw new Error(t('请输入启动命令。'))
      config.command = args[0]; config.args = args.slice(1); config.env = values
      if (directory.value.trim()) config.cwd = directory.value.trim(); else delete config.cwd
      delete config.url; delete config.headers; delete config.oauth
    } else {
      const address = new URL(url.value.trim())
      if (!['https:', 'http:'].includes(address.protocol)) throw new Error(t('请输入有效的服务地址。'))
      config.url = url.value.trim(); config.headers = values
      delete config.command; delete config.args; delete config.cwd; delete config.env
      const oauth = { ...config.oauth }
      if (oauthClientId.value.trim()) oauth.clientId = oauthClientId.value.trim(); else delete oauth.clientId
      if (oauthClientSecret.value) oauth.clientSecret = oauthClientSecret.value; else delete oauth.clientSecret
      if (oauthCallbackUrl.value.trim()) oauth.callbackUrl = oauthCallbackUrl.value.trim(); else delete oauth.callbackUrl
      if (Object.keys(oauth).length) config.oauth = oauth; else delete config.oauth
    }
    request('save', { name: name.value, originalName: original.value?.name ?? null, config, check: true })
  } catch (error) { message.value = error instanceof Error ? error.message : t('请检查连接设置。') }
}

function reviewImport() {
  try { importPreview.value = parseMcpImport(importText.value, scope.value === 'project'); message.value = '' }
  catch (error) { message.value = error instanceof Error ? error.message : t('请检查服务配置。') }
}

function startImport() { importing.value = true; importText.value = ''; importPreview.value = null; editing.value = false; message.value = '' }

// A snapshot only probes the servers the request asked about; the others come
// back as unchecked. Keep the results already on screen instead of dropping
// them, unless the server's configuration changed since it was checked.
const probedStates = new Set<McpServer['state']>(['connected', 'failed', 'needs-auth'])
function keepCheckedResults(previous: McpSnapshot | null, next: McpSnapshot): McpSnapshot {
  if (!previous) return next
  const checked = new Map(previous.servers.map(server => [`${server.scope}\u0000${server.name}`, server]))
  return {
    ...next,
    servers: next.servers.map(server => {
      if (server.state !== 'unchecked') return server
      const last = checked.get(`${server.scope}\u0000${server.name}`)
      if (!last || !probedStates.has(last.state)) return server
      if (JSON.stringify(last.config) !== JSON.stringify(server.config)) return server
      return { ...server, state: last.state, tools: last.tools, error: last.error }
    }),
  }
}

watch(() => props.result, result => {
  if (!result || result.requestId !== pending.value?.id) return
  pending.value = null
  redirectUrl.value = ''
  if (result.succeeded && result.snapshot) {
    snapshot.value = keepCheckedResults(snapshot.value, result.snapshot)
    if (result.action === 'save' || result.action === 'import') {
      selected.value = result.action === 'import' ? Object.keys(importPreview.value ?? {})[0] ?? '' : name.value
      editing.value = false; importing.value = false
    }
    removeTarget.value = null
  } else if (!result.canceled) message.value = result.message
})
watch([workspaceId, scope], () => {
  editing.value = false; importing.value = false; selected.value = ''; snapshot.value = null; load()
})
watch(trusted, (value, previous) => { if (value && !previous && !pending.value) load() })
onMounted(() => load())
onBeforeUnmount(() => { if (pending.value?.action === 'login') emit('cancelLogin', pending.value.id) })
</script>

<template>
  <div class="mcp-settings">
    <header class="mcp-toolbar">
      <div v-if="workspaces.length" class="mcp-segments" role="group" :aria-label="t('保存位置')">
        <UiButton variant="ghost" class="mcp-segment" :class="{ active: scope === 'global' }" :aria-pressed="scope === 'global'" :disabled="!!pending" @click="selectScope('global')">{{ t('个人') }}</UiButton>
        <UiButton variant="ghost" class="mcp-segment" :class="{ active: scope === 'project' }" :aria-pressed="scope === 'project'" :disabled="!!pending" @click="selectScope('project')">{{ t('工作区') }}</UiButton>
      </div>
      <div v-if="scope === 'project'" class="mcp-workspace">
        <UiSelect v-model="workspaceId" :options="workspaceOptions" :disabled="!!pending" :ariaLabelText="t('工作区')" />
      </div>
      <span class="mcp-toolbar-spacer" />
      <UiButton variant="ghost" :disabled="!canChange" @click="startImport">{{ t('导入') }}</UiButton>
      <UiButton variant="primary" :disabled="!canChange" @click="beginEdit()">{{ t('添加服务') }}</UiButton>
    </header>

    <div v-if="scope === 'project' && workspace && !trusted" class="mcp-trust">
      <span>{{ t('信任工作区后可使用这里的服务。') }}</span>
      <UiButton @click="emit('trustWorkspace', workspaceId)">{{ t('信任工作区') }}</UiButton>
    </div>
    <p v-if="message" class="mcp-error" role="alert">{{ t(message) }}</p>
    <div v-if="pending" class="mcp-pending" role="status"><i class="ui-spinner" aria-hidden="true" />{{ t(pending.action === 'login' ? waiting ? '等待浏览器授权' : '正在登录' : '处理中') }}</div>
    <div v-if="pending?.action === 'login'" class="mcp-login">
      <details v-if="waiting"><summary>{{ t('浏览器未能返回？') }}</summary><div class="mcp-redirect"><UiInput v-model="redirectUrl" :placeholder="t('粘贴完整回调地址')" :aria-label="t('回调地址')" /><UiButton :disabled="!redirectUrl.trim()" @click="emit('submitRedirect', pending.id, redirectUrl)">{{ t('继续') }}</UiButton></div></details>
      <UiButton variant="ghost" @click="emit('cancelLogin', pending.id)">{{ t('取消登录') }}</UiButton>
    </div>

    <div class="mcp-layout" :class="{ 'mcp-layout-single': !snapshot?.servers.length }">
      <aside v-if="snapshot?.servers.length" class="mcp-list">
        <div class="mcp-list-toolbar">
          <label class="mcp-search"><UiInput v-model="search" type="search" :placeholder="t('搜索服务')" :aria-label="t('搜索服务')" /></label>
          <UiButton class="mcp-icon" variant="ghost" :disabled="!!pending" :aria-label="t('检查全部连接')" :title="t('检查全部连接')" @click="load(true)">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M19 8a7 7 0 1 0 1 6" /><path d="M19 4v4h-4" /></svg>
          </UiButton>
        </div>
        <div class="mcp-items">
          <UiButton
            v-for="server in servers"
            :key="server.name"
            type="button"
            variant="ghost"
            class="mcp-server"
            :class="{ selected: current?.name === server.name && (!editing || original?.name === server.name) }"
            :aria-pressed="current?.name === server.name && (!editing || original?.name === server.name)"
            :disabled="!!pending"
            @click="selectServer(server)"
          >
            <span class="mcp-server-text">
              <strong>{{ server.name }}</strong>
              <small>{{ t(statusLabels[server.state]) }}</small>
            </span>
            <i class="mcp-dot" :class="server.state" aria-hidden="true" />
          </UiButton>
        </div>
      </aside>

      <div class="mcp-pane">
        <section v-if="importing" class="mcp-editor">
          <h2>{{ t('导入服务') }}</h2>
          <template v-if="!importPreview">
            <UiTextarea v-model="importText" rows="10" :aria-label="t('服务配置')" placeholder='{ "mcpServers": { … } }' />
            <div class="mcp-actions"><UiButton variant="ghost" @click="importing = false">{{ t('取消') }}</UiButton><UiButton variant="primary" :disabled="!importText.trim()" @click="reviewImport">{{ t('下一步') }}</UiButton></div>
          </template>
          <template v-else>
            <ul class="mcp-import-list"><li v-for="(config, serverName) in importPreview" :key="serverName"><strong>{{ serverName }}</strong><span>{{ config.url || config.command }}</span></li></ul>
            <div class="mcp-actions"><UiButton variant="ghost" :disabled="!!pending" @click="importPreview = null">{{ t('返回') }}</UiButton><UiButton variant="primary" :disabled="!canChange" @click="request('import', { servers: importPreview, check: true })">{{ t('导入') }} {{ Object.keys(importPreview).length }}</UiButton></div>
          </template>
        </section>

        <form v-else-if="editing" class="mcp-editor" @submit.prevent="save">
          <h2>{{ t(original ? '编辑服务' : '添加服务') }}</h2>
          <div class="mcp-field"><span class="mcp-label">{{ t('名称') }}</span><UiInput v-model="name" :disabled="!!original || !!pending" autocomplete="off" placeholder="my-service" /></div>
          <template v-if="!original?.isOverride">
            <div class="mcp-kind" role="group" :aria-label="t('连接方式')"><UiButton type="button" :class="{ active: kind === 'remote' }" :disabled="!!pending" @click="changeKind('remote')">{{ t('远程') }}</UiButton><UiButton type="button" :class="{ active: kind === 'local' }" :disabled="!!pending" @click="changeKind('local')">{{ t('本地') }}</UiButton></div>
            <div v-if="kind === 'remote'" class="mcp-field"><span class="mcp-label">{{ t('服务地址') }}</span><UiInput v-model="url" :disabled="!!pending" placeholder="https://example.com/mcp" /></div>
            <div v-else class="mcp-field"><span class="mcp-label">{{ t('启动命令') }}</span><UiInput v-model="command" :disabled="!!pending" placeholder="npx -y @example/mcp-server" /></div>
          </template>
          <p v-else class="mcp-address">{{ t('个人服务') }} · {{ url || command }}</p>
          <div class="mcp-enabled"><span>{{ t('启用') }}</span><UiSwitch v-model="enabled" :disabled="!!pending" :aria-label="t('启用服务')" /></div>
          <details class="mcp-advanced"><summary>{{ t('高级设置') }}</summary>
            <div class="mcp-advanced-content">
            <template v-if="!original?.isOverride">
            <div v-if="kind === 'local'" class="mcp-field"><span class="mcp-label">{{ t('运行目录') }}</span><UiInput v-model="directory" :disabled="!!pending" :placeholder="t('当前工作区')" /></div>
            <div class="mcp-pairs"><span class="mcp-label">{{ t(kind === 'local' ? '环境变量' : '请求头') }}</span><div v-for="(pair, index) in pairs" :key="index" class="mcp-pair"><UiInput v-model="pair.key" :disabled="!!pending" :aria-label="t('名称')" :placeholder="t('名称')" /><UiInput v-model="pair.value" :disabled="!!pending" :type="pair.revealed ? 'text' : 'password'" :aria-label="t('值')" :placeholder="t('值')" autocomplete="off" /><UiButton type="button" :aria-label="t(pair.revealed ? '隐藏' : '显示')" @click="pair.revealed = !pair.revealed">{{ pair.revealed ? '◉' : '○' }}</UiButton><UiButton type="button" :disabled="!!pending" :aria-label="t('移除')" @click="pairs.splice(index, 1)">×</UiButton></div><UiButton type="button" :disabled="!!pending" @click="pairs.push({ key: '', value: '', revealed: false })">{{ t('添加一项') }}</UiButton></div>
            </template>
            <div class="mcp-field"><span class="mcp-label">{{ t('调用方式') }}</span><UiSelect :ariaLabelText="t('调用方式')" v-model="exposure" :disabled="!!pending" :options="[{ value: 'deferred', label: t('按需加载') }, { value: 'direct', label: t('直接调用') }, { value: 'codemode', label: t('批量调用') }, { value: 'hidden', label: t('隐藏工具') }]" /></div>
            <template v-if="!original?.isOverride">
            <div class="mcp-field"><span class="mcp-label">{{ t('等待时间（秒）') }}</span><UiInput v-model.number="timeout" type="number" min="1" max="3600" :disabled="!!pending" /></div>
            <template v-if="kind === 'remote'"><div class="mcp-field"><span class="mcp-label">{{ t('登录客户端 ID') }}</span><UiInput v-model="oauthClientId" :disabled="!!pending" autocomplete="off" /></div><div class="mcp-field"><span class="mcp-label">{{ t('登录客户端密钥') }}</span><UiInput v-model="oauthClientSecret" type="password" :disabled="!!pending" autocomplete="off" /></div><div class="mcp-field"><span class="mcp-label">{{ t('登录回调地址') }}</span><UiInput v-model="oauthCallbackUrl" :disabled="!!pending" placeholder="http://127.0.0.1:8080/callback" /></div></template>
            </template>
            </div>
          </details>
          <div class="mcp-actions"><UiButton variant="ghost" type="button" :disabled="!!pending" @click="editing = false">{{ t('取消') }}</UiButton><UiButton type="submit" variant="primary" :disabled="!canChange">{{ t('保存') }}</UiButton></div>
        </form>

        <section v-else-if="current" class="mcp-detail">
          <header class="mcp-detail-head">
            <div class="mcp-detail-title">
              <h2>{{ current.name }}</h2>
              <span class="mcp-status" :class="current.state"><i aria-hidden="true" />{{ t(statusLabels[current.state]) }}</span>
            </div>
            <UiSwitch :model-value="current.config.enabled !== false" :disabled="!canChange || current.state === 'overridden'" :aria-label="t('启用服务')" @update:model-value="request('toggle', { name: current.name, enabled: $event, check: $event })" />
          </header>
          <p class="mcp-address">{{ current.config.url || formatMcpCommand(current.config) }}</p>
          <div class="mcp-actions mcp-detail-actions">
            <UiButton :variant="current.state === 'connected' ? 'secondary' : 'primary'" :disabled="!!pending || current.state === 'untrusted' || current.state === 'overridden' || current.config.enabled === false" @click="request('list', { name: current.name, check: true })">{{ t('检查连接') }}</UiButton>
            <UiButton variant="ghost" :disabled="!canChange" @click="beginEdit(current)">{{ t('编辑') }}</UiButton>
            <UiButton v-if="canSignIn" variant="primary" :disabled="!canChange || current.config.enabled === false" @click="request('login', { name: current.name })">{{ t('登录') }}</UiButton>
            <UiButton v-if="signedIn" variant="ghost" :disabled="!canChange" @click="request('logout', { name: current.name })">{{ t('退出登录') }}</UiButton>
            <UiButton variant="ghost" class="mcp-remove" :disabled="!canChange || current.inherited" @click="removeTarget = current">{{ t(current.isOverride ? '恢复个人设置' : '移除') }}</UiButton>
          </div>
          <section v-if="current.tools.length" class="mcp-section">
            <h3>{{ t('可用工具') }}<span>{{ current.tools.length }}</span></h3>
            <ul class="mcp-tool-list"><li v-for="tool in current.tools" :key="tool.name"><strong>{{ tool.name }}</strong><p v-if="tool.description">{{ tool.description }}</p></li></ul>
          </section>
          <section v-if="current.error" class="mcp-section">
            <h3>{{ t('连接详情') }}</h3>
            <pre class="mcp-pre">{{ current.error }}</pre>
          </section>
        </section>

        <div v-else-if="!pending" class="mcp-empty">
          <p>{{ t(search ? '没有匹配的服务' : '暂无服务') }}</p>
          <UiButton variant="primary" :disabled="!canChange" @click="beginEdit()">{{ t('添加服务') }}</UiButton>
        </div>
      </div>
    </div>

    <UiDialog v-if="removeTarget" :title="t(removeTarget.isOverride ? '恢复个人设置' : '移除服务')" content-class="mcp-confirm" @close="removeTarget = null"><h2>{{ t(removeTarget.isOverride ? '恢复个人设置' : '移除服务') }}</h2><p>{{ removeTarget.name }}</p><div class="mcp-actions"><UiButton variant="ghost" :disabled="!!pending" @click="removeTarget = null">{{ t('取消') }}</UiButton><UiButton variant="danger" :disabled="!!pending" @click="request('delete', { name: removeTarget.name })">{{ t(removeTarget.isOverride ? '恢复' : '移除') }}</UiButton></div></UiDialog>
  </div>
</template>

<style scoped>
.mcp-settings { display: flex; flex-direction: column; gap: 16px; height: 100%; min-height: 0; min-width: 0; }
.mcp-settings :deep(.ui-button) { justify-content: center; gap: 6px; min-height: 34px; padding: 6px 12px; border-radius: 7px; font-size: var(--font-size-body-sm); line-height: 1.4; }
.mcp-settings :deep(.ui-input), .mcp-settings :deep(.ui-textarea) { box-sizing: border-box; width: 100%; min-height: 34px; padding: 7px 10px; border: 1px solid var(--color-border-default); border-radius: 7px; outline: 0; background: var(--color-bg-surface); color: var(--color-text-primary); font: inherit; font-size: var(--font-size-body-sm); line-height: 1.5; }
.mcp-settings :deep(.ui-input:focus-visible), .mcp-settings :deep(.ui-textarea:focus-visible) { border-color: var(--color-tone-9); }
.mcp-settings :deep(.ui-input::placeholder), .mcp-settings :deep(.ui-textarea::placeholder) { color: var(--color-text-tertiary); }
.mcp-settings :deep(.ui-textarea) { min-height: 0; resize: vertical; }

.mcp-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.mcp-toolbar-spacer { flex: 1 1 auto; min-width: 8px; }
.mcp-segments { display: inline-flex; padding: 2px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-tone-2); }
.mcp-settings :deep(.mcp-segment) { min-height: 28px; padding: 0 12px; border: 0; border-radius: 6px; background: transparent; color: var(--color-text-tertiary); font-size: var(--font-size-body-sm); }
.mcp-settings :deep(.mcp-segment:hover:not(:disabled)) { background: transparent; color: var(--color-text-primary); }
.mcp-settings :deep(.mcp-segment.active) { background: var(--color-bg-active); color: var(--color-text-primary); }
.mcp-workspace { width: min(220px, 100%); }

.mcp-layout { display: grid; grid-template-columns: 252px minmax(0, 1fr); gap: 28px; flex: 1 1 auto; min-height: 0; }
.mcp-layout-single { grid-template-columns: minmax(0, 1fr); }
.mcp-list { display: flex; flex-direction: column; gap: 10px; min-height: 0; min-width: 0; padding-right: 14px; border-right: 1px solid var(--color-border-subtle); }
.mcp-list-toolbar { display: grid; grid-template-columns: minmax(0, 1fr) 34px; gap: 4px; }
.mcp-search { display: flex; min-width: 0; }
.mcp-settings :deep(.mcp-icon) { width: 34px; min-width: 34px; padding: 0; color: var(--color-text-secondary); }
.mcp-settings :deep(.mcp-icon svg) { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.7; }
.mcp-items { display: flex; flex-direction: column; gap: 2px; min-height: 0; overflow-y: auto; overscroll-behavior: contain; }
.mcp-settings :deep(.mcp-server) { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 10px; width: 100%; min-height: 46px; padding: 7px 9px; border: 1px solid transparent; border-radius: 7px; background: transparent; color: var(--color-text-secondary); cursor: pointer; text-align: left; }
.mcp-settings :deep(.mcp-server:hover) { background: var(--color-bg-hover); color: var(--color-text-primary); }
.mcp-settings :deep(.mcp-server.selected) { background: var(--color-bg-active); color: var(--color-text-primary); }
.mcp-server-text { display: grid; min-width: 0; gap: 2px; }
.mcp-server strong { overflow: hidden; font-size: var(--font-size-body); font-weight: var(--font-weight-medium); text-overflow: ellipsis; white-space: nowrap; }
.mcp-server small { overflow: hidden; color: var(--color-text-tertiary); font-size: var(--font-size-caption); text-overflow: ellipsis; white-space: nowrap; }
.mcp-dot { width: 6px; height: 6px; border-radius: 50%; background: var(--color-tone-9); }
.mcp-dot.connected { background: var(--color-success); box-shadow: 0 0 0 3px var(--color-success-halo); }
.mcp-dot.failed, .mcp-dot.needs-auth { background: var(--color-danger); }

.mcp-pane { min-width: 0; min-height: 0; overflow-y: auto; padding-right: 4px; }
.mcp-detail, .mcp-editor { display: flex; flex-direction: column; gap: 18px; max-width: 680px; min-width: 0; }
.mcp-detail-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; }
.mcp-detail-title { display: flex; min-width: 0; flex-wrap: wrap; align-items: center; gap: 8px 10px; }
.mcp-settings h2 { margin: 0; color: var(--color-text-primary); font-size: var(--font-size-title-md); font-weight: var(--font-weight-semibold); line-height: 1.4; overflow-wrap: anywhere; }
.mcp-status { display: inline-flex; align-items: center; gap: 6px; padding: 3px 9px; border-radius: 99px; background: var(--color-bg-hover); color: var(--color-text-secondary); font-size: var(--font-size-caption); white-space: nowrap; }
.mcp-status > i { width: 6px; height: 6px; border-radius: 50%; background: currentColor; opacity: .8; }
.mcp-status.connected { background: var(--color-success-surface-emphasis); color: var(--color-success-emphasis); }
.mcp-status.failed, .mcp-status.needs-auth { background: var(--color-danger-surface-emphasis); color: var(--color-danger-text); }
.mcp-status.disabled, .mcp-status.unchecked { color: var(--color-text-tertiary); }
.mcp-address { margin: -6px 0 0; color: var(--color-text-tertiary); font-family: var(--font-family-mono); font-size: var(--font-size-caption); line-height: 1.6; overflow-wrap: anywhere; }

.mcp-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 6px; }
.mcp-detail-actions { justify-content: flex-start; margin-top: -4px; }
.mcp-settings :deep(.mcp-remove) { margin-left: auto; color: var(--color-danger); }
.mcp-settings :deep(.mcp-remove:hover:not(:disabled)) { background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }

.mcp-section { display: flex; flex-direction: column; gap: 10px; padding-top: 18px; border-top: 1px solid var(--color-border-subtle); }
.mcp-section h3 { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--color-text-secondary); font-size: var(--font-size-body); font-weight: var(--font-weight-semibold); }
.mcp-section h3 span { padding: 1px 7px; border-radius: 99px; background: var(--color-bg-hover); color: var(--color-text-tertiary); font-size: var(--font-size-micro); font-weight: var(--font-weight-regular); }
.mcp-tool-list, .mcp-import-list { display: flex; flex-direction: column; gap: 14px; max-height: 360px; margin: 0; padding: 0; overflow-y: auto; list-style: none; }
.mcp-tool-list strong { display: block; color: var(--color-text-primary); font-family: var(--font-family-mono); font-size: var(--font-size-body-sm); font-weight: var(--font-weight-medium); overflow-wrap: anywhere; }
.mcp-tool-list p, .mcp-import-list span, .mcp-pre { margin: 4px 0 0; color: var(--color-text-tertiary); font-size: var(--font-size-caption); line-height: 1.6; white-space: pre-wrap; overflow-wrap: anywhere; }
.mcp-pre { margin: 0; font-family: var(--font-family-mono); }

.mcp-editor .mcp-field, .mcp-pairs { display: flex; flex-direction: column; gap: 7px; }
.mcp-label { color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.mcp-kind { display: inline-flex; align-self: flex-start; padding: 2px; border: 1px solid var(--color-border-subtle); border-radius: 8px; background: var(--color-tone-2); }
.mcp-settings :deep(.mcp-kind button) { min-height: 30px; padding: 0 14px; border: 0; border-radius: 6px; background: transparent; color: var(--color-text-tertiary); }
.mcp-settings :deep(.mcp-kind button:hover) { color: var(--color-text-primary); }
.mcp-settings :deep(.mcp-kind button.active) { background: var(--color-bg-active); color: var(--color-text-primary); }
.mcp-enabled { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 0; border-top: 1px solid var(--color-border-subtle); border-bottom: 1px solid var(--color-border-subtle); color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.mcp-advanced summary, .mcp-login summary { color: var(--color-text-secondary); cursor: pointer; font-size: var(--font-size-body-sm); }
.mcp-advanced-content { display: flex; flex-direction: column; gap: 16px; padding-top: 14px; }
.mcp-pair { display: grid; grid-template-columns: minmax(70px, 1fr) minmax(70px, 1.3fr) auto auto; gap: 6px; }
.mcp-pair .ui-input { min-width: 0; }
.mcp-pairs > .ui-button { align-self: flex-start; }
.mcp-editor .mcp-actions { padding-top: 6px; }

.mcp-pending { display: flex; align-items: center; gap: 8px; color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.mcp-error { margin: 0; color: var(--color-danger-text); font-size: var(--font-size-body-sm); line-height: 1.6; overflow-wrap: anywhere; }
.mcp-trust { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 12px; border: 1px solid var(--color-border-subtle); border-radius: 8px; color: var(--color-text-secondary); font-size: var(--font-size-body-sm); }
.mcp-login { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; }
.mcp-redirect { display: flex; gap: 8px; margin-top: 10px; }
.mcp-empty { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; min-height: 100%; color: var(--color-text-tertiary); }
.mcp-empty p { margin: 0; font-size: var(--font-size-body-sm); }
:global(.mcp-confirm) { width: min(420px, calc(100vw - 32px)); padding: 24px; border: 1px solid var(--color-border-default); border-radius: 12px; background: var(--color-bg-elevated); color: var(--color-text-primary); }
@media (max-width: 760px) { .mcp-layout { grid-template-columns: 1fr; } .mcp-list { max-height: 210px; border-right: 0; border-bottom: 1px solid var(--color-border-subtle); padding: 0 0 12px; } }
</style>
