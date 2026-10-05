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
const workspaceId = ref(props.activeWorkspaceId ?? '')
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
const scopeOptions = computed(() => [
  { value: 'global', label: t('个人') },
  ...(workspace.value ? [{ value: 'project', label: t('工作区') }] : []),
])
const workspaceOptions = computed(() => [
  { value: '', label: t('选择工作区') },
  ...props.workspaces.map(value => ({ value: value.id, label: value.displayName || value.name })),
])
const servers = computed(() => (snapshot.value?.servers ?? []).filter(server =>
  server.scope === scope.value && server.name.toLowerCase().includes(search.value.trim().toLowerCase())))
const current = computed(() => servers.value.find(server => server.name === selected.value) ?? servers.value[0])
const canSignIn = computed(() => !!current.value?.config.url && !current.value.config.auth &&
  !Object.keys(current.value.config.headers ?? {}).some(key => key.toLowerCase() === 'authorization'))
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

watch(() => props.result, result => {
  if (!result || result.requestId !== pending.value?.id) return
  pending.value = null
  redirectUrl.value = ''
  if (result.succeeded && result.snapshot) {
    snapshot.value = result.snapshot
    if (result.action === 'save' || result.action === 'import') {
      selected.value = result.action === 'import' ? Object.keys(importPreview.value ?? {})[0] ?? '' : name.value
      editing.value = false; importing.value = false
    }
    removeTarget.value = null
  } else if (!result.canceled) message.value = result.message
})
watch([workspaceId, scope], () => {
  if (scope.value === 'project' && !workspace.value) { scope.value = 'global'; return }
  editing.value = false; importing.value = false; selected.value = ''; snapshot.value = null; load()
})
watch(trusted, (value, previous) => { if (value && !previous && !pending.value) load() })
onMounted(() => load())
onBeforeUnmount(() => { if (pending.value?.action === 'login') emit('cancelLogin', pending.value.id) })
</script>

<template>
  <div class="mcp-settings">
    <div class="mcp-toolbar">
      <UiSelect v-model="scope" :options="scopeOptions" :disabled="!!pending" :ariaLabelText="t('保存位置')" />
      <UiSelect v-model="workspaceId" :options="workspaceOptions" :disabled="!!pending" :ariaLabelText="t('工作区')" />
      <UiButton :disabled="!!pending" @click="load(true)">{{ t('检查连接') }}</UiButton>
      <UiButton :disabled="!canChange" @click="startImport">{{ t('导入') }}</UiButton>
      <UiButton variant="primary" :disabled="!canChange" @click="beginEdit()">{{ t('添加服务') }}</UiButton>
    </div>
    <div v-if="scope === 'project' && workspace && !trusted" class="mcp-trust">
      <span>{{ t('信任工作区后可使用这里的服务。') }}</span>
      <UiButton @click="emit('trustWorkspace', workspaceId)">{{ t('信任工作区') }}</UiButton>
    </div>
    <p v-if="message" class="mcp-error" role="alert">{{ t(message) }}</p>
    <div v-if="pending" class="mcp-pending" role="status"><i class="ui-spinner" aria-hidden="true" />{{ t(pending.action === 'login' ? waiting ? '等待浏览器授权' : '正在登录' : '处理中') }}</div>
    <div v-if="pending?.action === 'login'" class="mcp-login">
      <details v-if="waiting"><summary>{{ t('浏览器未能返回？') }}</summary><div class="mcp-redirect"><UiInput v-model="redirectUrl" :placeholder="t('粘贴完整回调地址')" :aria-label="t('回调地址')" /><UiButton :disabled="!redirectUrl.trim()" @click="emit('submitRedirect', pending.id, redirectUrl)">{{ t('继续') }}</UiButton></div></details>
      <UiButton @click="emit('cancelLogin', pending.id)">{{ t('取消登录') }}</UiButton>
    </div>
    <section v-if="importing" class="mcp-editor">
      <h2>{{ t('导入服务') }}</h2>
      <template v-if="!importPreview">
        <UiTextarea v-model="importText" rows="9" :aria-label="t('服务配置')" placeholder='{ "mcpServers": { … } }' />
        <div class="mcp-actions"><UiButton @click="importing = false">{{ t('取消') }}</UiButton><UiButton variant="primary" :disabled="!importText.trim()" @click="reviewImport">{{ t('下一步') }}</UiButton></div>
      </template>
      <template v-else>
        <ul class="mcp-import-list"><li v-for="(config, serverName) in importPreview" :key="serverName"><strong>{{ serverName }}</strong><span>{{ config.url || config.command }}</span></li></ul>
        <div class="mcp-actions"><UiButton :disabled="!!pending" @click="importPreview = null">{{ t('返回') }}</UiButton><UiButton variant="primary" :disabled="!canChange" @click="request('import', { servers: importPreview, check: true })">{{ t('导入') }} {{ Object.keys(importPreview).length }}</UiButton></div>
      </template>
    </section>
    <div v-else class="mcp-layout" :class="{ 'mcp-layout-single': !snapshot?.servers.length }">
      <aside v-if="snapshot?.servers.length" class="mcp-list">
        <UiInput v-model="search" :placeholder="t('搜索服务')" :aria-label="t('搜索服务')" />
        <UiButton v-for="server in servers" :key="server.name" class="mcp-server" :class="{ selected: current?.name === server.name && !editing }" :disabled="!!pending" @click="selected = server.name; editing = false">
          <strong>{{ server.name }}</strong><span class="mcp-state" :class="server.state">{{ t(statusLabels[server.state]) }}</span>
        </UiButton>
        <span v-if="!servers.length" class="mcp-muted">{{ t('暂无服务') }}</span>
      </aside>
      <form v-if="editing" class="mcp-editor" @submit.prevent="save">
        <h2>{{ t(original ? '编辑服务' : '添加服务') }}</h2>
        <label>{{ t('名称') }}<UiInput v-model="name" :disabled="!!original || !!pending" autocomplete="off" placeholder="my-service" /></label>
        <template v-if="!original?.isOverride">
          <div class="mcp-kind" role="group" :aria-label="t('连接方式')"><UiButton type="button" :class="{ active: kind === 'remote' }" :disabled="!!pending" @click="changeKind('remote')">{{ t('远程') }}</UiButton><UiButton type="button" :class="{ active: kind === 'local' }" :disabled="!!pending" @click="changeKind('local')">{{ t('本地') }}</UiButton></div>
          <label v-if="kind === 'remote'">{{ t('服务地址') }}<UiInput v-model="url" :disabled="!!pending" placeholder="https://example.com/mcp" /></label>
          <label v-else>{{ t('启动命令') }}<UiInput v-model="command" :disabled="!!pending" placeholder="npx -y @example/mcp-server" /></label>
        </template>
        <p v-else class="mcp-address">{{ t('个人服务') }} · {{ url || command }}</p>
        <div class="mcp-enabled"><span>{{ t('启用') }}</span><UiSwitch v-model="enabled" :disabled="!!pending" :aria-label="t('启用服务')" /></div>
        <details class="mcp-advanced"><summary>{{ t('高级设置') }}</summary>
          <div class="mcp-advanced-content">
          <template v-if="!original?.isOverride">
          <label v-if="kind === 'local'">{{ t('运行目录') }}<UiInput v-model="directory" :disabled="!!pending" :placeholder="t('当前工作区')" /></label>
          <div class="mcp-pairs"><span>{{ t(kind === 'local' ? '环境变量' : '请求头') }}</span><div v-for="(pair, index) in pairs" :key="index" class="mcp-pair"><UiInput v-model="pair.key" :disabled="!!pending" :aria-label="t('名称')" :placeholder="t('名称')" /><UiInput v-model="pair.value" :disabled="!!pending" :type="pair.revealed ? 'text' : 'password'" :aria-label="t('值')" :placeholder="t('值')" autocomplete="off" /><UiButton type="button" :aria-label="t(pair.revealed ? '隐藏' : '显示')" @click="pair.revealed = !pair.revealed">{{ pair.revealed ? '◉' : '○' }}</UiButton><UiButton type="button" :disabled="!!pending" :aria-label="t('移除')" @click="pairs.splice(index, 1)">×</UiButton></div><UiButton type="button" :disabled="!!pending" @click="pairs.push({ key: '', value: '', revealed: false })">{{ t('添加一项') }}</UiButton></div>
          </template>
          <label>{{ t('调用方式') }}<UiSelect :ariaLabelText="t('调用方式')" v-model="exposure" :disabled="!!pending" :options="[{ value: 'deferred', label: t('按需加载') }, { value: 'direct', label: t('直接调用') }, { value: 'codemode', label: t('批量调用') }, { value: 'hidden', label: t('隐藏工具') }]" /></label>
          <template v-if="!original?.isOverride">
          <label>{{ t('等待时间（秒）') }}<UiInput v-model.number="timeout" type="number" min="1" max="3600" :disabled="!!pending" /></label>
          <template v-if="kind === 'remote'"><label>{{ t('登录客户端 ID') }}<UiInput v-model="oauthClientId" :disabled="!!pending" autocomplete="off" /></label><label>{{ t('登录客户端密钥') }}<UiInput v-model="oauthClientSecret" type="password" :disabled="!!pending" autocomplete="off" /></label><label>{{ t('登录回调地址') }}<UiInput v-model="oauthCallbackUrl" :disabled="!!pending" placeholder="http://127.0.0.1:8080/callback" /></label></template>
          </template>
          </div>
        </details>
        <div class="mcp-actions"><UiButton type="button" :disabled="!!pending" @click="editing = false">{{ t('取消') }}</UiButton><UiButton type="submit" variant="primary" :disabled="!canChange">{{ t('保存') }}</UiButton></div>
      </form>
      <section v-else-if="current" class="mcp-detail">
        <div class="mcp-detail-heading"><h2>{{ current.name }}</h2><UiSwitch :model-value="current.config.enabled !== false" :disabled="!canChange || current.state === 'overridden'" :aria-label="t('启用服务')" @update:model-value="request('toggle', { name: current.name, enabled: $event, check: $event })" /></div>
        <span class="mcp-state" :class="current.state">{{ t(statusLabels[current.state]) }}</span>
        <p class="mcp-address">{{ current.config.url || formatMcpCommand(current.config) }}</p>
        <div class="mcp-actions mcp-detail-actions"><UiButton :disabled="!canChange" @click="beginEdit(current)">{{ t('编辑') }}</UiButton><UiButton :disabled="!!pending || current.state === 'untrusted' || current.state === 'overridden' || current.config.enabled === false" @click="request('list', { name: current.name, check: true })">{{ t('检查连接') }}</UiButton><UiButton v-if="canSignIn" :disabled="!canChange || current.config.enabled === false" @click="request('login', { name: current.name })">{{ t('登录') }}</UiButton><UiButton :disabled="!canChange || current.inherited" class="mcp-delete" @click="removeTarget = current">{{ t(current.isOverride ? '恢复个人设置' : '移除') }}</UiButton></div>
        <details v-if="current.error" class="mcp-connection-error"><summary>{{ t('连接详情') }}</summary><pre>{{ current.error }}</pre></details>
        <details v-if="current.tools.length" class="mcp-tools"><summary>{{ t('工具') }} · {{ current.tools.length }}</summary><ul><li v-for="tool in current.tools" :key="tool.name"><strong>{{ tool.name }}</strong><p v-if="tool.description">{{ tool.description }}</p></li></ul></details>
        <details v-if="canSignIn" class="mcp-account"><summary>{{ t('账号') }}</summary><UiButton :disabled="!canChange" @click="request('logout', { name: current.name })">{{ t('退出登录') }}</UiButton></details>
      </section>
      <div v-else-if="!pending" class="mcp-empty"><p>{{ t(search ? '没有匹配的服务' : '暂无服务') }}</p><UiButton variant="primary" :disabled="!canChange" @click="beginEdit()">{{ t('添加服务') }}</UiButton></div>
    </div>
    <UiDialog v-if="removeTarget" :title="t(removeTarget.isOverride ? '恢复个人设置' : '移除服务')" content-class="mcp-confirm" @close="removeTarget = null"><h2>{{ t(removeTarget.isOverride ? '恢复个人设置' : '移除服务') }}</h2><p>{{ removeTarget.name }}</p><div class="mcp-actions"><UiButton :disabled="!!pending" @click="removeTarget = null">{{ t('取消') }}</UiButton><UiButton variant="danger" :disabled="!!pending" @click="request('delete', { name: removeTarget.name })">{{ t(removeTarget.isOverride ? '恢复' : '移除') }}</UiButton></div></UiDialog>
  </div>
</template>

<style scoped>
.mcp-settings { display: flex; flex-direction: column; gap: 18px; min-width: 0; }
.mcp-settings :deep(.ui-button) { min-height: 34px; padding: 6px 10px; border-radius: 7px; font-size: 14px; line-height: 1.4; justify-content: center; }
.mcp-settings :deep(.ui-input), .mcp-settings :deep(.ui-textarea) { box-sizing: border-box; width: 100%; min-height: 36px; padding: 8px 10px; border: 1px solid var(--color-border-default); border-radius: 7px; background: var(--color-bg-surface); color: var(--color-text-primary); font: inherit; font-size: 14px; line-height: 1.5; }
.mcp-settings :deep(.ui-input::placeholder), .mcp-settings :deep(.ui-textarea::placeholder) { color: var(--color-text-tertiary); }
.mcp-settings :deep(.ui-textarea) { resize: vertical; }
.mcp-toolbar :deep(.ui-button--plain), .mcp-actions :deep(.ui-button--plain) { border-color: var(--color-border-default); }
.mcp-toolbar, .mcp-actions, .mcp-kind, .mcp-login, .mcp-redirect { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.mcp-toolbar > :nth-child(2) { max-width: 200px; }
.mcp-layout { display: grid; grid-template-columns: minmax(150px, 190px) minmax(0, 1fr); gap: 22px; }
.mcp-layout-single { grid-template-columns: minmax(0, 1fr); }
.mcp-list { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.mcp-list > .ui-input { width: 100%; margin-bottom: 6px; }
.mcp-server { align-items: flex-start; flex-direction: column; height: auto; padding: 12px; text-align: left; gap: 5px; }
.mcp-settings :deep(.ui-button.mcp-server) { padding: 12px; align-items: flex-start; }
.mcp-server strong { overflow-wrap: anywhere; }
.mcp-server.selected, .mcp-kind .active { background: var(--color-bg-active); }
.mcp-state, .mcp-muted { color: var(--color-text-secondary); font-size: 13px; }
.mcp-state.connected { color: var(--color-success-text); }
.mcp-state.failed, .mcp-state.needs-auth, .mcp-error { color: var(--color-danger-text); }
.mcp-detail, .mcp-editor { display: flex; flex-direction: column; gap: 18px; min-width: 0; }
.mcp-detail-heading, .mcp-enabled, .mcp-trust { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.mcp-settings h2 { margin: 0; font-size: 17px; line-height: 1.5; overflow-wrap: anywhere; }
.mcp-editor label { display: flex; flex-direction: column; gap: 7px; font-size: 14px; }
.mcp-actions { justify-content: flex-end; }
.mcp-detail-actions { justify-content: flex-start; }
.mcp-delete { margin-left: auto; }
.mcp-address { margin: 0; color: var(--color-text-secondary); font-size: 14px; line-height: 1.6; overflow-wrap: anywhere; }
summary { cursor: pointer; font-size: 14px; line-height: 1.6; }
.mcp-advanced-content { display: flex; flex-direction: column; gap: 16px; padding-top: 14px; }
.mcp-pairs > .ui-button { align-self: flex-start; }
.mcp-pairs { display: flex; flex-direction: column; gap: 8px; font-size: 14px; }
.mcp-pair { display: grid; grid-template-columns: minmax(70px, 1fr) minmax(70px, 1.3fr) auto auto; gap: 6px; }
.mcp-pair .ui-input { min-width: 0; width: 100%; }
.mcp-pending { display: flex; align-items: center; gap: 8px; color: var(--color-text-secondary); font-size: 14px; }
.mcp-error { margin: 0; font-size: 14px; line-height: 1.6; overflow-wrap: anywhere; }
.mcp-trust { padding: 12px 0; font-size: 14px; }
.mcp-empty { grid-column: 1 / -1; display: flex; align-items: center; justify-content: center; flex-direction: column; gap: 12px; min-height: 240px; color: var(--color-text-secondary); }
.mcp-redirect { margin-top: 10px; }
.mcp-import-list, .mcp-tools ul { list-style: none; margin: 12px 0; padding: 0; display: flex; flex-direction: column; gap: 14px; }
.mcp-import-list li { display: flex; flex-direction: column; gap: 6px; overflow-wrap: anywhere; }
.mcp-import-list span, .mcp-tools p { color: var(--color-text-secondary); font-size: 14px; line-height: 1.6; margin: 4px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.mcp-connection-error pre { font-family: inherit; font-size: 14px; line-height: 1.6; white-space: pre-wrap; overflow-wrap: anywhere; }
:global(.mcp-confirm) { width: min(420px, calc(100vw - 32px)); padding: 24px; border: 1px solid var(--color-border-default); border-radius: 12px; background: var(--color-bg-elevated); color: var(--color-text-primary); }
@media (max-width: 680px) { .mcp-layout { grid-template-columns: 1fr; } .mcp-list { max-height: 220px; overflow-y: auto; } .mcp-toolbar > :nth-child(2) { max-width: 150px; } }
</style>
