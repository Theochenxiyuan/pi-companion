<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { UiButton, UiDialog, UiInput, UiMenu, UiMenuItem, UiSelect } from '@/components/ui'
import type {
  DiscoveredSkill,
  SkillContentVariant,
  SkillDiagnostic,
  SkillInstallation,
  SkillImportCompleted,
  SkillImportPreparation,
  SkillImportSource,
  SkillImportSourceKind,
  SkillOrigin,
  SkillRemovalCompleted,
  SkillWorkspaceTrust,
  SkillWorkspaceTrustCompleted,
  SkillsLoaded,
  WorkspaceHistoryEntry,
} from '@/types/bridge'
import { useI18n } from '@/i18n'

const props = withDefaults(defineProps<{
  snapshot: SkillsLoaded | null
  loading: boolean
  error: string | null
  sidebarCollapsed: boolean
  contextWorkspace?: WorkspaceHistoryEntry | null
  globalOnly?: boolean
  removingInstallationId?: string | null
  removalResult?: SkillRemovalCompleted | null
  workspaces?: WorkspaceHistoryEntry[]
  importPhase?: 'source' | 'target' | 'commit' | null
  importSource?: SkillImportSource | null
  importPreparation?: SkillImportPreparation | null
  importError?: string | null
  importResult?: SkillImportCompleted | null
  trustingWorkspaceId?: string | null
  trustResult?: SkillWorkspaceTrustCompleted | null
}>(), {
  contextWorkspace: null,
  globalOnly: false,
  removingInstallationId: null,
  removalResult: null,
  workspaces: () => [],
  importPhase: null,
  importSource: null,
  importPreparation: null,
  importError: null,
  importResult: null,
  trustingWorkspaceId: null,
  trustResult: null,
})

const emit = defineEmits<{
  toggleSidebar: []
  refresh: []
  clearContext: []
  removeInstallation: [payload: {
    installationId: string
    expectedContentHash: string
  }]
  trustWorkspace: [workspaceId: string]
  openImport: []
  beginImport: [sourceKind: SkillImportSourceKind]
  prepareImport: [payload: {
    targetScope: 'global' | 'workspace'
    workspaceId?: string
  }]
  confirmImport: []
  cancelImport: []
}>()

const { locale, t } = useI18n()
const search = ref('')
const selectedSkillName = ref<string | null>(null)
const removalTarget = ref<{
  skill: DiscoveredSkill
  variant: SkillContentVariant
  installation: SkillInstallation
} | null>(null)
const importOpen = ref(false)
const reselectMenuOpen = ref(false)
const selectedImportScope = ref('')
const selectedImportWorkspaceId = ref('')

const hasContext = computed(() => props.globalOnly || Boolean(props.contextWorkspace))
const title = computed(() => {
  if (props.globalOnly) return t('全局技能')
  if (props.contextWorkspace) return t('{name} 的技能', { name: props.contextWorkspace.name })
  return t('技能')
})
const contextDescription = computed(() => {
  if (props.globalOnly) return t('Direct Chat 可用的全局技能。')
  if (props.contextWorkspace) return t('当前工作区可用的全局与工作区技能。')
  return t('本机 Pi 与通用 Agent 原生目录中的全部技能。')
})

function diagnosticsForContext(diagnostics: SkillDiagnostic[]) {
  if (!hasContext.value) return diagnostics
  const workspaceId = props.contextWorkspace?.id ?? null
  return diagnostics.filter(diagnostic =>
    diagnostic.workspaceId === null || diagnostic.workspaceId === workspaceId)
}

function originsForContext(installation: SkillInstallation) {
  if (props.globalOnly) {
    return installation.origins.filter(origin => origin.scope === 'global')
  }
  if (props.contextWorkspace) {
    return installation.origins.filter(origin =>
      origin.scope === 'global' || origin.workspaceId === props.contextWorkspace?.id)
  }
  return installation.origins
}

function compatibilityOrigins(installation: SkillInstallation) {
  return originsForContext(installation).filter(origin => origin.isCompatibilityLink)
}

function compatibilityScopeTitle(installation: SkillInstallation) {
  return [...new Set(originsForContext(installation).map(originScopeTitle))].join(' · ')
}

function installationBelongsToContext(
  variant: SkillContentVariant,
  installation: SkillInstallation,
) {
  if (props.globalOnly) {
    return installation.isGloballyEffective ||
      (originsForContext(installation).length > 0 &&
        (!variant.isAvailable || diagnosticsForContext(installation.diagnostics).length > 0))
  }
  const workspaceId = props.contextWorkspace?.id
  if (workspaceId) {
    return installation.effectiveWorkspaceIds.includes(workspaceId) ||
      (originsForContext(installation).length > 0 &&
        (!variant.isAvailable || diagnosticsForContext(installation.diagnostics).length > 0))
  }
  return true
}

function relevantInstallations(variant: SkillContentVariant) {
  return variant.installations.filter(installation =>
    installationBelongsToContext(variant, installation))
}

function relevantVariants(skill: DiscoveredSkill) {
  return skill.variants.filter(variant => relevantInstallations(variant).length > 0)
}

function isEffective(installation: SkillInstallation) {
  if (props.globalOnly) return installation.isGloballyEffective
  if (props.contextWorkspace) {
    return installation.effectiveWorkspaceIds.includes(props.contextWorkspace.id)
  }
  return installation.isGloballyEffective || installation.effectiveWorkspaceIds.length > 0
}

function primaryEntry(skill: DiscoveredSkill) {
  const entries = relevantVariants(skill).flatMap(variant =>
    relevantInstallations(variant).map(installation => ({ variant, installation })))
  return entries.find(entry => entry.variant.isAvailable && isEffective(entry.installation))
    ?? entries[0]
    ?? null
}

function workspaceTrust(workspaceId: string | null) {
  if (!workspaceId) return null
  return props.snapshot?.workspaceTrust?.find(entry => entry.workspaceId === workspaceId) ?? null
}

function untrustedWorkspaceForInstallation(installation: SkillInstallation) {
  for (const origin of originsForContext(installation)) {
    if (origin.scope !== 'workspace' || !origin.workspaceId) continue
    const trust = workspaceTrust(origin.workspaceId)
    if (trust && trust.status !== 'trusted') return trust
  }
  return null
}

function untrustedWorkspaceForSkill(skill: DiscoveredSkill) {
  for (const variant of relevantVariants(skill)) {
    for (const installation of relevantInstallations(variant)) {
      const trust = untrustedWorkspaceForInstallation(installation)
      if (trust) return trust
    }
  }
  return null
}

function trustWorkspace(trust: SkillWorkspaceTrust | null) {
  if (!trust || props.trustingWorkspaceId) return
  emit('trustWorkspace', trust.workspaceId)
}

const visibleSkills = computed(() => {
  const query = search.value.trim().toLocaleLowerCase(locale.value)
  return (props.snapshot?.skills ?? []).filter(skill => {
    const variants = relevantVariants(skill)
    if (variants.length === 0) return false
    if (!query) return true
    return [
      skill.name,
      ...variants.map(variant => variant.description ?? ''),
    ].some(value => value.toLocaleLowerCase(locale.value).includes(query))
  })
})

const selectedSkill = computed(() =>
  props.snapshot?.skills.find(skill => skill.name === selectedSkillName.value) ?? null)
const availableCount = computed(() =>
  visibleSkills.value.filter(skill => {
    const entry = primaryEntry(skill)
    return Boolean(entry?.variant.isAvailable && isEffective(entry.installation))
  }).length)
const issueCount = computed(() =>
  visibleSkills.value.filter(skill =>
    relevantVariants(skill).length > 1 ||
    relevantVariants(skill).some(variant =>
      !variant.isAvailable ||
      relevantInstallations(variant).some(installation =>
        diagnosticsForContext(installation.diagnostics).length > 0))).length)
const importScopeOptions = computed(() => [
  { value: '', label: t('请选择导入范围') },
  { value: 'global', label: t('全局') },
  { value: 'workspace', label: t('工作区') },
])
const importWorkspaceOptions = computed(() => [
  { value: '', label: t('请选择工作区') },
  ...props.workspaces.map(workspace => ({
    value: workspace.id,
    label: workspace.name,
    tooltip: workspace.workingDirectory,
  })),
])
const hasValidImportTarget = computed(() =>
  selectedImportScope.value === 'global' ||
  (selectedImportScope.value === 'workspace' &&
    props.workspaces.some(workspace => workspace.id === selectedImportWorkspaceId.value)))
const canCommitImport = computed(() => {
  const source = props.importSource
  const preparation = props.importPreparation
  if (!source || !preparation || props.importPhase !== null) return false
  if (preparation.sourceToken !== source.token ||
      preparation.targetScope !== selectedImportScope.value) return false
  return selectedImportScope.value === 'global'
    ? preparation.workspaceId === null
    : preparation.workspaceId === selectedImportWorkspaceId.value
})

watch(selectedImportScope, scope => {
  if (scope !== 'workspace') selectedImportWorkspaceId.value = ''
})

watch(
  [() => props.importSource?.token, selectedImportScope, selectedImportWorkspaceId],
  () => {
    if (!importOpen.value || !props.importSource || !hasValidImportTarget.value) return
    if (selectedImportScope.value === 'global') {
      emit('prepareImport', { targetScope: 'global' })
    } else {
      emit('prepareImport', {
        targetScope: 'workspace',
        workspaceId: selectedImportWorkspaceId.value,
      })
    }
  },
)

watch(() => props.importResult, result => {
  if (result?.succeeded && importOpen.value) {
    importOpen.value = false
    selectedImportScope.value = ''
    selectedImportWorkspaceId.value = ''
  }
})

function installationCount(skill: DiscoveredSkill) {
  return relevantVariants(skill)
    .reduce((count, variant) => count + relevantInstallations(variant).length, 0)
}

function skillTone(skill: DiscoveredSkill) {
  const entry = primaryEntry(skill)
  if (!entry?.variant.isAvailable) return 'invalid'
  if (untrustedWorkspaceForSkill(skill)) return 'warning'
  if (relevantVariants(skill).length > 1) return 'collision'
  if (diagnosticsForContext(skill.diagnostics).length > 0) return 'warning'
  return 'available'
}

function skillStatus(skill: DiscoveredSkill) {
  const entry = primaryEntry(skill)
  if (!entry?.variant.isAvailable) return t('不可用')
  if (untrustedWorkspaceForSkill(skill)) return t('未信任工作区')
  if (relevantVariants(skill).length > 1) return t('内容不一致')
  if (diagnosticsForContext(skill.diagnostics).some(
    diagnostic => diagnostic.code === 'name-collision')) {
    return t('名称冲突')
  }
  if (diagnosticsForContext(skill.diagnostics).length > 0) return t('可用 · 有问题')
  return t('可用')
}

function originScopeTitle(origin: SkillOrigin) {
  return origin.scope === 'global'
    ? t('全局')
    : origin.workspaceName ?? t('工作区')
}

function originSourceLabel(origin: SkillOrigin) {
  return origin.source === 'pi' ? 'Pi' : 'Agent'
}

function originSourceKind(origin: SkillOrigin) {
  return origin.source === 'pi' ? 'pi' : 'agent'
}

function diagnosticTitle(diagnostic: SkillDiagnostic) {
  const labels: Record<string, string> = {
    'description-required': t('缺少描述'),
    'description-too-long': t('描述过长'),
    'name-collision': t('名称冲突'),
    'name-invalid': t('名称不规范'),
    'frontmatter-invalid': t('格式无法解析'),
    'content-missing': t('内容缺失'),
    'content-inspection-failed': t('内容检查失败'),
    'workspace-untrusted': t('未信任工作区'),
  }
  return labels[diagnostic.code] ?? t('技能问题')
}

function additionalMetadata(variant: SkillContentVariant) {
  const reserved = new Set([
    'name',
    'description',
    'version',
    'license',
    'disable-model-invocation',
  ])
  return Object.entries(variant.metadata)
    .filter(([key, value]) => !reserved.has(key) && value.length > 0)
    .sort(([left], [right]) => left.localeCompare(right, locale.value))
}

function metadataLabel(key: string) {
  const labels: Record<string, string> = {
    author: t('作者'),
    compatibility: t('兼容性'),
  }
  return labels[key] ?? key
}

function formattedTime(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return new Intl.DateTimeFormat(locale.value, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date)
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 / 1024).toFixed(1)} MB`
}

function openDetails(skill: DiscoveredSkill) {
  selectedSkillName.value = skill.name
  removalTarget.value = null
}

function closeDetails() {
  selectedSkillName.value = null
  removalTarget.value = null
}

function requestRemoval(
  skill: DiscoveredSkill,
  variant: SkillContentVariant,
  installation: SkillInstallation,
) {
  if (!installation.removable || !variant.contentHash) return
  removalTarget.value = { skill, variant, installation }
}

function confirmRemoval() {
  const target = removalTarget.value
  if (!target?.variant.contentHash) return
  emit('removeInstallation', {
    installationId: target.installation.id,
    expectedContentHash: target.variant.contentHash,
  })
  removalTarget.value = null
}

function openImportDialog() {
  if (hasContext.value) return
  importOpen.value = true
  selectedImportScope.value = ''
  selectedImportWorkspaceId.value = ''
  emit('openImport')
}

function closeImportDialog() {
  if (props.importPhase === 'commit') return
  importOpen.value = false
  reselectMenuOpen.value = false
  selectedImportScope.value = ''
  selectedImportWorkspaceId.value = ''
  emit('cancelImport')
}

function chooseImportSource(sourceKind: SkillImportSourceKind) {
  if (props.importPhase) return
  reselectMenuOpen.value = false
  emit('beginImport', sourceKind)
}
</script>

<template>
  <main class="management-main management-skills skills-view">
    <header class="topbar management-topbar">
      <div class="topbar-leading">
        <UiButton
          class="sidebar-toggle"
          type="button"
          :aria-label="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
          :title="t(sidebarCollapsed ? '展开侧栏' : '收起侧栏')"
          @click="$emit('toggleSidebar')"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3.5" y="4" width="17" height="16" rx="2" /><path d="M9 4v16" /></svg>
        </UiButton>
        <div class="location management-location">
          <strong>{{ title }}</strong>
          <small>{{ contextDescription }}</small>
        </div>
      </div>
      <div class="skills-topbar-actions">
        <UiButton
          v-if="!hasContext"
          class="skills-import"
          type="button"
          @click="openImportDialog"
        >{{ t('导入技能') }}</UiButton>
        <UiButton
          v-if="hasContext"
          class="skills-context-clear"
          type="button"
          @click="$emit('clearContext')"
        >{{ t('查看全部技能') }}</UiButton>
        <UiButton
          class="skills-refresh"
          type="button"
          :disabled="loading"
          :aria-label="t('刷新技能')"
          @click="$emit('refresh')"
        >
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M16 7a6 6 0 1 0 .3 5.5" /><path d="M16 3v4h-4" /></svg>
          {{ loading ? t('正在刷新…') : t('刷新') }}
        </UiButton>
      </div>
    </header>

    <section class="management-content skills-content">
      <div
        v-if="importResult"
        class="skills-action-result"
        :class="{ failed: !importResult.succeeded }"
        role="status"
      >
        <strong>{{ importResult.succeeded ? t('技能已导入') : t('技能导入失败') }}</strong>
        <span>{{ importResult.message }}</span>
      </div>
      <div
        v-if="removalResult"
        class="skills-action-result"
        :class="{ failed: !removalResult.succeeded }"
        role="status"
      >
        <strong>{{ removalResult.succeeded ? t('技能已卸载') : t('技能卸载失败') }}</strong>
        <span>{{ removalResult.message }}</span>
      </div>
      <div
        v-if="trustResult"
        class="skills-action-result"
        :class="{ failed: !trustResult.succeeded }"
        role="status"
      >
        <strong>{{ trustResult.succeeded ? t('工作区已受 Pi 信任') : t('工作区信任失败') }}</strong>
        <span>{{ trustResult.message }}</span>
      </div>

      <div class="skills-summary" aria-live="polite">
        <span><strong>{{ visibleSkills.length }}</strong>{{ t('个技能') }}</span>
        <span><strong>{{ availableCount }}</strong>{{ t('可用') }}</span>
        <span v-if="issueCount"><strong>{{ issueCount }}</strong>{{ t('有问题') }}</span>
        <time v-if="snapshot" :datetime="snapshot.scannedAt">
          {{ t('扫描于 {time}', { time: formattedTime(snapshot.scannedAt) }) }}
        </time>
      </div>

      <label class="skills-search">
        <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5" /><path d="m12.5 12.5 4 4" /></svg>
        <UiInput v-model="search" type="search" :placeholder="t('搜索技能')" />
      </label>

      <div v-if="loading && !snapshot" class="management-empty skills-loading" role="status">
        <span class="management-empty-icon">π</span>
        <strong>{{ t('正在读取本地技能…') }}</strong>
      </div>
      <div v-else-if="error && !snapshot" class="management-empty skills-error" role="alert">
        <span class="management-empty-icon">!</span>
        <strong>{{ t('技能读取失败') }}</strong>
        <p>{{ error }}</p>
        <UiButton type="button" @click="$emit('refresh')">{{ t('重试') }}</UiButton>
      </div>
      <div v-else-if="visibleSkills.length === 0" class="management-empty">
        <span class="management-empty-icon">π</span>
        <strong>{{ search.trim() ? t('没有匹配的技能') : t('未发现技能') }}</strong>
        <p v-if="!search.trim()">{{ t('原生技能目录中没有当前上下文可展示的技能。') }}</p>
      </div>

      <div v-else class="skills-grid">
        <article
          v-for="skill in visibleSkills"
          :key="skill.id"
          class="skill-card"
          :class="`skill-${skillTone(skill)}`"
        >
          <header class="skill-card-header">
            <div>
              <h2 :title="skill.name">{{ skill.name }}</h2>
              <span class="skill-status">{{ skillStatus(skill) }}</span>
            </div>
            <span
              v-if="primaryEntry(skill)?.variant.disableModelInvocation"
              class="skill-manual-badge"
            >{{ t('仅用户手动') }}</span>
          </header>
          <p class="skill-description">
            {{ primaryEntry(skill)?.variant.description || t('没有提供技能描述。') }}
          </p>
          <div class="skill-card-summary">
            <span>{{ t('{count} 处安装', { count: installationCount(skill) }) }}</span>
            <span>{{ t('{count} 种内容', { count: relevantVariants(skill).length }) }}</span>
            <strong v-if="relevantVariants(skill).length > 1">{{ t('内容不同') }}</strong>
          </div>
          <footer class="skill-card-footer">
            <UiButton
              v-if="untrustedWorkspaceForSkill(skill)"
              class="skill-trust-button"
              type="button"
              :disabled="Boolean(trustingWorkspaceId)"
              @click="trustWorkspace(untrustedWorkspaceForSkill(skill))"
            >
              {{ t(trustingWorkspaceId === untrustedWorkspaceForSkill(skill)?.workspaceId
                ? '正在信任…'
                : '信任工作区') }}
            </UiButton>
            <UiButton type="button" @click="openDetails(skill)">{{ t('查看详情') }}</UiButton>
          </footer>
        </article>
      </div>
    </section>

    <UiDialog
      v-if="selectedSkill"
      :title="t('{name} 技能详情', { name: selectedSkill.name })"
      overlay-class="management-dialog-backdrop skill-detail-backdrop"
      :content-class="[
        'skill-detail-dialog',
        {
          'has-summary':
            installationCount(selectedSkill) > 1 ||
            relevantVariants(selectedSkill).length > 1,
        },
      ]"
      @close="closeDetails"
    >
        <header class="skill-detail-header">
          <div>
            <span>{{ t('技能详情') }}</span>
            <h2>{{ selectedSkill.name }}</h2>
            <p>{{ primaryEntry(selectedSkill)?.variant.description || t('没有提供技能描述。') }}</p>
          </div>
          <UiButton type="button" :aria-label="t('关闭')" @click="closeDetails">×</UiButton>
        </header>

        <div
          v-if="installationCount(selectedSkill) > 1 || relevantVariants(selectedSkill).length > 1"
          class="skill-detail-summary"
        >
          <span>{{ t('{count} 处安装', { count: installationCount(selectedSkill) }) }}</span>
          <span>{{ t('{count} 种内容', { count: relevantVariants(selectedSkill).length }) }}</span>
          <strong v-if="relevantVariants(selectedSkill).length > 1">{{ t('同名内容不一致') }}</strong>
        </div>

        <div class="skill-variant-list">
          <section
            v-for="(variant, variantIndex) in relevantVariants(selectedSkill)"
            :key="variant.id"
            class="skill-variant"
          >
            <header>
              <span>{{ t('内容版本 {index}', { index: variantIndex + 1 }) }}</span>
              <code :title="variant.contentHash || ''">
                {{ variant.contentHash ? variant.contentHash.slice(0, 12) : t('无法计算指纹') }}
              </code>
            </header>

            <dl class="skill-metadata-grid">
              <div><dt>{{ t('版本') }}</dt><dd>{{ variant.version || '—' }}</dd></div>
              <div><dt>{{ t('许可证') }}</dt><dd>{{ variant.license || '—' }}</dd></div>
              <div><dt>{{ t('调用方式') }}</dt><dd>{{ variant.disableModelInvocation ? t('仅用户手动') : t('模型或用户') }}</dd></div>
              <div><dt>{{ t('文件') }}</dt><dd>{{ t('{count} 个 · {size}', { count: variant.fileCount, size: formatBytes(variant.totalSize) }) }}</dd></div>
              <div><dt>{{ t('最近修改') }}</dt><dd>{{ variant.lastModifiedAt ? formattedTime(variant.lastModifiedAt) : '—' }}</dd></div>
            </dl>

            <dl v-if="additionalMetadata(variant).length" class="skill-extra-metadata">
              <div v-for="[key, value] in additionalMetadata(variant)" :key="key">
                <dt>{{ metadataLabel(key) }}</dt>
                <dd>{{ value }}</dd>
              </div>
            </dl>

            <div class="skill-installation-list">
              <article
                v-for="installation in relevantInstallations(variant)"
                :key="installation.id"
                class="skill-installation"
              >
                <div class="skill-installation-copy">
                  <template v-if="compatibilityOrigins(installation).length">
                    <div class="skill-installation-context">
                      <strong>{{ compatibilityScopeTitle(installation) }}</strong>
                      <span v-if="!variant.isAvailable" class="unavailable">{{ t('不可用') }}</span>
                    </div>
                    <div class="skill-installation-path">
                      <div class="skill-installation-path-label">
                        <span class="source-agent">Agent</span>
                        <span>{{ t('真实目录') }}</span>
                      </div>
                      <code :title="installation.installPath">{{ installation.installPath }}</code>
                    </div>
                    <div
                      v-for="(origin, linkIndex) in compatibilityOrigins(installation)"
                      :key="`compatibility-link:${origin.installPath}:${linkIndex}`"
                      class="skill-installation-path compatibility"
                    >
                      <div class="skill-installation-path-label">
                        <span class="source-pi">Pi</span>
                        <span>{{ t('兼容入口') }}</span>
                        <span v-if="origin.inherited">{{ t('继承') }}</span>
                        <span
                          v-if="origin.scope === 'workspace' &&
                            workspaceTrust(origin.workspaceId)?.status !== 'trusted'"
                          class="untrusted"
                        >{{ t('未信任工作区') }}</span>
                      </div>
                      <code :title="origin.installPath">{{ origin.installPath }}</code>
                    </div>
                  </template>
                  <template v-else>
                    <div
                      v-for="(origin, originIndex) in originsForContext(installation)"
                      :key="`${origin.scope}:${origin.source}:${origin.rootPath}:${originIndex}`"
                      class="skill-installation-heading"
                    >
                      <strong>{{ originScopeTitle(origin) }}</strong>
                      <span :class="`source-${originSourceKind(origin)}`">{{ originSourceLabel(origin) }}</span>
                      <span v-if="origin.inherited">{{ t('继承') }}</span>
                      <span v-if="!variant.isAvailable" class="unavailable">{{ t('不可用') }}</span>
                      <span
                        v-if="origin.scope === 'workspace' &&
                          workspaceTrust(origin.workspaceId)?.status !== 'trusted'"
                        class="untrusted"
                      >{{ t('未信任工作区') }}</span>
                    </div>
                    <code :title="installation.installPath">{{ installation.installPath }}</code>
                  </template>
                </div>
                <div class="skill-installation-actions">
                  <UiButton
                    v-if="untrustedWorkspaceForInstallation(installation)"
                    class="skill-trust-button"
                    type="button"
                    :disabled="Boolean(trustingWorkspaceId)"
                    @click="trustWorkspace(untrustedWorkspaceForInstallation(installation))"
                  >
                    {{ t(trustingWorkspaceId ===
                      untrustedWorkspaceForInstallation(installation)?.workspaceId
                      ? '正在信任…'
                      : '信任工作区') }}
                  </UiButton>
                  <UiButton
                    v-if="installation.removable"
                    class="skill-remove-button"
                    type="button"
                    :disabled="removingInstallationId === installation.id"
                    @click="requestRemoval(selectedSkill, variant, installation)"
                  >
                    {{ removingInstallationId === installation.id ? t('正在卸载…') : t('卸载') }}
                  </UiButton>
                </div>
                <ul
                  v-if="diagnosticsForContext(installation.diagnostics).length"
                  class="skill-diagnostics"
                >
                  <li
                    v-for="(diagnostic, index) in diagnosticsForContext(installation.diagnostics)"
                    :key="`${diagnostic.code}:${diagnostic.workspaceId}:${index}`"
                  >
                    <span aria-hidden="true">!</span>
                    <p><strong>{{ diagnosticTitle(diagnostic) }}</strong>{{ diagnostic.message }}</p>
                  </li>
                </ul>
              </article>
            </div>
          </section>
        </div>

        <UiDialog
          v-if="removalTarget"
          :title="t('确认卸载技能')"
          overlay-class="skill-removal-confirm-backdrop"
          content-class="skill-removal-confirm-dialog"
          alert
          @close="removalTarget = null"
        >
            <span aria-hidden="true">!</span>
            <div>
              <h3>{{ t('卸载这个位置的技能？') }}</h3>
              <p>{{ t('卸载前会重新校验内容指纹。原内容将移到技能根目录中的可恢复位置。') }}</p>
              <code>{{ removalTarget.installation.installPath }}</code>
            </div>
            <footer>
              <UiButton type="button" @click="removalTarget = null">{{ t('取消') }}</UiButton>
              <UiButton class="danger" type="button" @click="confirmRemoval">{{ t('卸载') }}</UiButton>
            </footer>
        </UiDialog>
    </UiDialog>

    <UiDialog
      v-if="importOpen"
      :title="t('导入技能')"
      overlay-class="management-dialog-backdrop skill-import-backdrop"
      content-class="skill-import-dialog"
      @close="closeImportDialog"
    >
      <header class="skill-import-header">
        <div>
          <span>{{ t('本地导入') }}</span>
          <h2>{{ t('导入技能') }}</h2>
          <p>{{ t('选择导入位置和本地来源，检查无误后再正式导入。') }}</p>
        </div>
        <UiButton
          class="skill-import-close"
          type="button"
          :aria-label="t('关闭')"
          @click="closeImportDialog"
        >×</UiButton>
      </header>
      <div class="skill-import-workbench">
        <section class="skill-import-location">
          <div class="skill-import-section-heading">
            <div>
              <h3>{{ t('导入位置') }}</h3>
              <p>{{ t('技能将写入所选位置的 Pi 原生技能目录。') }}</p>
            </div>
          </div>
          <div class="skill-import-targets">
            <label class="skill-import-target-field">
              <span>{{ t('导入范围') }}</span>
              <UiSelect
                v-model="selectedImportScope"
                :options="importScopeOptions"
                :ariaLabelText="t('导入范围')"
              />
            </label>
            <label class="skill-import-target-field">
              <span>{{ t('工作区') }}</span>
              <UiSelect
                v-model="selectedImportWorkspaceId"
                :options="importWorkspaceOptions"
                :ariaLabelText="t('工作区')"
                :disabled="selectedImportScope !== 'workspace'"
                searchable
                :searchPlaceholder="t('搜索工作区')"
              />
            </label>
          </div>
          <div
            v-if="importPreparation"
            class="skill-import-target-ready"
          >
            <span>{{ t('目标已就绪') }}</span>
            <code>{{ importPreparation.targetPath }}</code>
          </div>
          <p v-else-if="importPhase === 'target'" class="skill-import-inline-status">
            {{ t('正在检查导入位置…') }}
          </p>
          <div
            v-if="importPreparation?.requiresProjectTrust"
            class="skill-import-warning skill-import-trust-warning"
          >
            <strong>{{ t('需要信任此 Pi 项目') }}</strong>
            <p>{{ t('正式导入会将整个工作区标记为受 Pi 信任；该决定也会影响其他项目级 Pi 资源。') }}</p>
          </div>
        </section>

        <section class="skill-import-source">
          <div
            v-if="!importSource"
            class="skill-import-source-empty"
          >
            <UiButton
              class="skill-import-source-button"
              type="button"
              :disabled="Boolean(importPhase)"
              @click="chooseImportSource('folder')"
            >{{ importPhase === 'source' ? t('正在检查…') : t('选择文件夹') }}</UiButton>
            <UiButton
              class="skill-import-source-button"
              type="button"
              :disabled="Boolean(importPhase)"
              @click="chooseImportSource('zip')"
            >{{ importPhase === 'source' ? t('正在检查…') : t('选择 ZIP') }}</UiButton>
          </div>
          <template v-else>
            <div class="skill-import-source-header">
              <div>
                <span>{{ importSource.sourceKind === 'folder' ? t('文件夹') : 'ZIP' }}</span>
                <h3>{{ importSource.name }}</h3>
                <p>{{ importSource.description || t('没有提供技能描述。') }}</p>
              </div>
              <div class="skill-import-source-actions">
                <UiMenu
                  v-model="reselectMenuOpen"
                  content-class="skill-import-reselect-menu"
                  :aria-label="t('重新选择来源')"
                  align="end"
                >
                  <template #trigger>
                    <UiButton
                      class="skill-import-source-button compact"
                      type="button"
                      :disabled="Boolean(importPhase)"
                    >
                      {{ t('重新选择') }}
                      <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4" /></svg>
                    </UiButton>
                  </template>
                  <UiMenuItem @select="chooseImportSource('folder')">
                    {{ t('文件夹') }}
                  </UiMenuItem>
                  <UiMenuItem @select="chooseImportSource('zip')">
                    ZIP
                  </UiMenuItem>
                </UiMenu>
              </div>
            </div>
            <div class="skill-import-source-summary">
              <span>{{ t('{count} 个文件 · {size}', {
                  count: importSource.fileCount,
                  size: formatBytes(importSource.totalBytes),
                }) }}</span>
              <span
                v-if="importSource.scriptFiles.length"
                class="skill-import-source-risk"
              >{{ t('含 {count} 个脚本', { count: importSource.scriptFiles.length }) }}</span>
              <span
                v-if="importSource.executableFiles.length"
                class="skill-import-source-risk"
              >{{ t('含 {count} 个可执行文件', { count: importSource.executableFiles.length }) }}</span>
            </div>
            <div class="skill-import-file-heading">
              <strong>{{ t('文件清单') }}</strong>
              <span>{{ t('共 {count} 个', { count: importSource.fileCount }) }}</span>
            </div>
            <div class="skill-import-file-list" tabindex="0">
              <div
                v-for="file in importSource.files"
                :key="file.relativePath"
                class="skill-import-file"
              >
                <code>{{ file.relativePath }}</code>
                <span
                  v-if="file.kind !== 'file'"
                  class="skill-import-file-kind"
                >{{ file.kind === 'script' ? t('脚本') : t('可执行') }}</span>
                <span>{{ formatBytes(file.size) }}</span>
              </div>
            </div>
          </template>
        </section>
      </div>
      <p v-if="importError" class="skill-import-error" role="alert">{{ importError }}</p>
      <footer class="skill-import-actions">
        <UiButton type="button" @click="closeImportDialog">{{ t('取消') }}</UiButton>
        <UiButton
          class="primary"
          type="button"
          :disabled="!canCommitImport"
          @click="$emit('confirmImport')"
        >
          {{ importPhase === 'commit'
            ? t('正在导入…')
            : importPreparation?.requiresProjectTrust
              ? t('信任并导入')
              : t('导入') }}
        </UiButton>
      </footer>
    </UiDialog>
  </main>
</template>

<style scoped>
.skill-card { display: flex; flex-direction: column; }
.skill-card .skill-description { flex: 1; }
.skill-card-summary { display: flex; flex-wrap: wrap; gap: 7px 12px; padding: 0 16px 14px; color: var(--color-tone-10); font-size: var(--font-size-caption); }
.skill-card-summary strong { color: var(--color-warning-text); font-weight: var(--font-weight-medium); }
.skill-card-footer { display: flex; justify-content: flex-end; gap: 8px; padding: 10px 16px 12px; border-top: 1px solid var(--color-tone-7); }
.skill-card-footer button, .skill-remove-button { min-height: 32px; padding: 5px 11px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-13); cursor: pointer; font-size: var(--font-size-body-sm); }
.skill-card-footer button:hover, .skill-remove-button:hover { border-color: var(--color-tone-10); color: var(--color-tone-15); }
.skill-trust-button { min-height: 32px; padding: 5px 11px; border: 1px solid var(--color-warning-border); border-radius: 7px; background: var(--color-warning-surface); color: var(--color-warning-text); cursor: pointer; font-size: var(--font-size-body-sm); }
.skill-trust-button:hover { filter: brightness(1.08); }
.skill-trust-button:disabled { cursor: default; opacity: .6; }
.skills-topbar-actions .skills-import { border-color: var(--color-tone-9); color: var(--color-tone-14); }
.skills-topbar-actions .skills-import:disabled { cursor: wait; opacity: .55; }
.skills-action-result { display: grid; width: min(100%, 1120px); gap: 3px; margin: 0 auto 14px; padding: 11px 13px; border: 1px solid var(--color-success-border); border-radius: 8px; background: var(--color-success-surface); color: var(--color-success-text); font-size: var(--font-size-caption); }
.skills-action-result.failed { border-color: var(--color-danger-border); background: var(--color-danger-surface); color: var(--color-danger-text); }
:global(.skill-detail-backdrop) { position: fixed; z-index: 80; inset: 0; display: grid; place-items: center; padding: 20px; background: var(--color-overlay-strong); backdrop-filter: blur(2px); }
:global(.skill-detail-dialog) { position: relative; display: grid; grid-template-rows: auto minmax(0, 1fr); width: min(860px, calc(100vw - 40px)); max-height: min(820px, calc(100vh - 40px)); overflow: hidden; border: 1px solid var(--color-tone-8); border-radius: 12px; background: var(--color-tone-3); box-shadow: 0 30px 90px var(--color-overlay-strong); }
:global(.skill-detail-dialog).has-summary { grid-template-rows: auto auto minmax(0, 1fr); }
.skill-detail-header { display: flex; justify-content: space-between; gap: 20px; padding: 22px 24px 16px; border-bottom: 1px solid var(--color-tone-7); }
.skill-detail-header > div { min-width: 0; }
.skill-detail-header span { color: var(--color-tone-9); font-size: var(--font-size-micro); text-transform: uppercase; letter-spacing: .08em; }
.skill-detail-header h2 { margin: 4px 0 5px; color: var(--color-tone-15); font-size: var(--font-size-title-lg); }
.skill-detail-header p { margin: 0; color: var(--color-tone-11); font-size: var(--font-size-body-sm); line-height: var(--line-height-reading); }
.skill-detail-header > button { width: 32px; height: 32px; justify-content: center; border: 0; border-radius: 7px; background: transparent; color: var(--color-tone-10); cursor: pointer; font-size: 22px; text-align: center; }
.skill-detail-header > button:hover { background: var(--color-tone-6); color: var(--color-tone-14); }
.skill-detail-summary { display: flex; gap: 14px; padding: 12px 24px; border-bottom: 1px solid var(--color-tone-7); color: var(--color-tone-10); font-size: var(--font-size-caption); }
.skill-detail-summary strong { color: var(--color-warning-text); }
.skill-variant-list { display: grid; gap: 12px; overflow: auto; padding: 16px 24px 22px; }
.skill-variant { display: grid; gap: 12px; padding: 15px; border: 1px solid var(--color-tone-7); border-radius: 10px; background: var(--color-tone-4); }
.skill-variant > header { display: flex; align-items: center; justify-content: space-between; gap: 14px; }
.skill-variant > header span { color: var(--color-tone-10); font-size: var(--font-size-caption); }
.skill-variant > header > code { max-width: 170px; overflow: hidden; color: var(--color-tone-10); font-size: var(--font-size-micro); text-overflow: ellipsis; }
.skill-metadata-grid { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 1px; margin: 0; overflow: hidden; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-7); }
.skill-metadata-grid > div { display: grid; align-content: start; gap: 3px; min-width: 0; padding: 8px 10px; background: var(--color-tone-4); }
.skill-metadata-grid dt, .skill-extra-metadata dt { color: var(--color-tone-9); font-size: var(--font-size-caption); }
.skill-metadata-grid dd, .skill-extra-metadata dd { min-width: 0; margin: 0; color: var(--color-tone-13); font-size: var(--font-size-caption); overflow-wrap: anywhere; }
.skill-extra-metadata { display: grid; gap: 7px; margin: 0; }
.skill-extra-metadata > div { display: flex; align-items: flex-start; gap: 12px; min-width: 0; padding: 9px 11px; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-3); }
.skill-extra-metadata dt { flex: none; padding: 2px 7px; border: 1px solid var(--color-info); border-radius: 999px; background: var(--color-info-surface); color: var(--color-info-text); font-size: var(--font-size-micro); line-height: 1.4; }
.skill-extra-metadata dd { padding-top: 1px; line-height: var(--line-height-reading); }
.skill-installation-list { display: grid; gap: 9px; }
.skill-installation { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 10px; padding: 10px 11px; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-3); }
.skill-installation-copy { display: grid; min-width: 0; gap: 5px; }
.skill-installation-heading { display: flex; flex-wrap: wrap; align-items: center; gap: 7px; }
.skill-installation-heading strong { color: var(--color-tone-14); font-size: var(--font-size-body-sm); }
.skill-installation-heading span { padding: 2px 7px; border: 1px solid var(--color-tone-7); border-radius: 999px; background: var(--color-tone-6); color: var(--color-tone-10); font-size: var(--font-size-micro); }
.skill-installation-heading .source-pi { border-color: var(--color-success-border); background: var(--color-success-surface); color: var(--color-success-text); }
.skill-installation-heading .source-agent { border-color: var(--color-info); background: var(--color-info-surface); color: var(--color-info-text); }
.skill-installation-heading .compatibility-link { border-color: var(--color-tone-8); background: var(--color-tone-4); color: var(--color-tone-11); }
.skill-installation-heading .unavailable { border-color: var(--color-danger-border); background: var(--color-danger-surface); color: var(--color-danger-text); }
.skill-installation-heading .untrusted { border-color: var(--color-warning-border); background: var(--color-warning-surface); color: var(--color-warning-text); }
.skill-installation-copy > code { overflow: hidden; color: var(--color-tone-10); font-size: var(--font-size-micro); text-overflow: ellipsis; white-space: nowrap; }
.skill-installation-context { display: flex; flex-wrap: wrap; align-items: center; gap: 7px; }
.skill-installation-context strong { color: var(--color-tone-14); font-size: var(--font-size-body-sm); }
.skill-installation-context > span, .skill-installation-path-label > span { padding: 2px 7px; border: 1px solid var(--color-tone-7); border-radius: 999px; background: var(--color-tone-6); color: var(--color-tone-10); font-size: var(--font-size-micro); }
.skill-installation-context .unavailable { border-color: var(--color-danger-border); background: var(--color-danger-surface); color: var(--color-danger-text); }
.skill-installation-path { display: grid; grid-template-columns: auto minmax(0, 1fr); align-items: center; gap: 5px 9px; min-width: 0; }
.skill-installation-path-label { display: flex; flex-wrap: wrap; align-items: center; gap: 5px; }
.skill-installation-path-label .source-agent { border-color: var(--color-info); background: var(--color-info-surface); color: var(--color-info-text); }
.skill-installation-path-label .source-pi { border-color: var(--color-success-border); background: var(--color-success-surface); color: var(--color-success-text); }
.skill-installation-path-label .untrusted { border-color: var(--color-warning-border); background: var(--color-warning-surface); color: var(--color-warning-text); }
.skill-installation-path > code { min-width: 0; overflow: hidden; color: var(--color-tone-11); font-size: var(--font-size-micro); text-overflow: ellipsis; white-space: nowrap; }
.skill-installation-actions { display: flex; align-items: flex-start; gap: 7px; }
.skill-remove-button { align-self: start; border-color: var(--color-danger-border); color: var(--color-danger-text); }
.skill-remove-button:disabled { cursor: wait; opacity: .55; }
.skill-installation .skill-diagnostics { grid-column: 1 / -1; }
:global(.skill-removal-confirm-backdrop) { position: absolute; z-index: 5; inset: 0; display: grid; place-items: center; padding: 24px; background: var(--color-overlay); backdrop-filter: blur(2px); }
:global(.skill-removal-confirm-dialog) { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 14px; width: min(500px, 100%); padding: 21px; border: 1px solid var(--color-tone-8); border-radius: 10px; background: var(--color-tone-4); box-shadow: 0 24px 70px var(--color-overlay-strong); }
:global(.skill-removal-confirm-dialog) > span { display: grid; width: 34px; height: 34px; place-items: center; border: 1px solid var(--color-danger-border); border-radius: 50%; background: var(--color-danger-surface); color: var(--color-danger-text); font-weight: var(--font-weight-bold); }
:global(.skill-removal-confirm-backdrop) h3 { margin: 1px 0 7px; color: var(--color-tone-15); font-size: var(--font-size-body-lg); }
:global(.skill-removal-confirm-backdrop) p { margin: 0 0 10px; color: var(--color-tone-11); font-size: var(--font-size-body-sm); line-height: var(--line-height-reading); }
:global(.skill-removal-confirm-backdrop) code { display: block; color: var(--color-tone-10); font-size: var(--font-size-micro); overflow-wrap: anywhere; }
:global(.skill-removal-confirm-backdrop) footer { display: flex; grid-column: 1 / -1; justify-content: flex-end; gap: 8px; margin-top: 6px; }
:global(.skill-removal-confirm-backdrop) footer button { min-height: 33px; padding: 6px 13px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-13); cursor: pointer; }
:global(.skill-removal-confirm-backdrop) footer .danger { border-color: var(--color-danger-border-strong); background: var(--color-danger-surface-emphasis); color: var(--color-danger-text-strong); }
:global(.skill-import-backdrop) { position: fixed; z-index: 85; inset: 0; display: grid; place-items: center; padding: 20px; background: var(--color-overlay-strong); backdrop-filter: blur(2px); }
:global(.skill-import-dialog) { display: grid; grid-template-rows: auto minmax(0, 1fr) auto auto; width: min(940px, calc(100vw - 40px)); height: min(680px, calc(100vh - 40px)); overflow: hidden; padding: 0; border: 1px solid var(--color-tone-8); border-radius: 12px; background: var(--color-tone-3); box-shadow: 0 30px 90px var(--color-overlay-strong); }
.skill-import-header { display: flex; justify-content: space-between; gap: 18px; }
.skill-import-header { padding: 20px 22px 17px; border-bottom: 1px solid var(--color-tone-7); }
.skill-import-header span, .skill-import-target-field > span { color: var(--color-tone-9); font-size: var(--font-size-caption); }
.skill-import-header h2 { margin: 4px 0 5px; color: var(--color-tone-15); font-size: var(--font-size-title-md); }
.skill-import-header p { margin: 0; color: var(--color-tone-10); font-size: var(--font-size-body-sm); line-height: var(--line-height-reading); }
.skill-import-close { width: 32px; height: 32px; justify-content: center; border: 0; border-radius: 7px; background: transparent; color: var(--color-tone-10); cursor: pointer; font-size: 22px; }
.skill-import-close:hover { background: var(--color-tone-6); color: var(--color-tone-14); }
.skill-import-workbench { display: grid; grid-template-columns: minmax(240px, .72fr) minmax(0, 1.28fr); min-height: 0; }
.skill-import-location, .skill-import-source { min-width: 0; min-height: 0; padding: 20px 22px; }
.skill-import-location { border-right: 1px solid var(--color-tone-7); background: var(--color-tone-4); }
.skill-import-source { display: flex; flex-direction: column; overflow: hidden; }
.skill-import-section-heading { display: block; }
.skill-import-section-heading h3, .skill-import-source-header h3 { margin: 1px 0 4px; color: var(--color-tone-15); font-size: var(--font-size-body-lg); }
.skill-import-section-heading p, .skill-import-source-header p { margin: 0; color: var(--color-tone-10); font-size: var(--font-size-caption); line-height: var(--line-height-reading); }
.skill-import-targets { display: grid; gap: 14px; margin-top: 22px; }
.skill-import-target-field { display: grid; min-width: 0; gap: 7px; }
.skill-import-target-ready { display: grid; gap: 5px; margin-top: 17px; padding: 10px 11px; border: 1px solid var(--color-success-border); border-radius: 8px; background: var(--color-success-surface); }
.skill-import-target-ready span { color: var(--color-success-text); font-size: var(--font-size-caption); font-weight: var(--font-weight-semibold); }
.skill-import-target-ready code { color: var(--color-tone-10); font-size: var(--font-size-micro); overflow-wrap: anywhere; }
.skill-import-inline-status { margin: 17px 0 0; color: var(--color-tone-10); font-size: var(--font-size-caption); }
.skill-import-source-empty { display: flex; flex: 1; align-items: center; justify-content: center; gap: 10px; }
:global(.skill-import-source-button) { min-height: 33px; padding: 6px 12px; appearance: none; border: 1px solid var(--color-tone-9); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-14); cursor: pointer; }
:global(.skill-import-source-button:hover) { border-color: var(--color-tone-11); background: var(--color-tone-6); }
:global(.skill-import-source-button:disabled) { cursor: wait; opacity: .5; }
:global(.skill-import-source-button.compact) { min-height: 29px; padding: 4px 8px; font-size: var(--font-size-caption); }
.skill-import-source-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; }
.skill-import-source-header > div:first-child { min-width: 0; }
.skill-import-source-header > div:first-child > span { color: var(--color-tone-9); font-size: var(--font-size-micro); text-transform: uppercase; letter-spacing: .06em; }
.skill-import-source-actions { display: flex; flex: none; gap: 6px; }
.skill-import-source-actions svg { width: 14px; height: 14px; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.5; }
:global(.skill-import-reselect-menu) { z-index: 100; min-width: 140px; padding: 5px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); box-shadow: 0 14px 36px var(--color-overlay); }
:global(.skill-import-reselect-menu .ui-menu-item) { display: flex; width: 100%; min-height: 31px; align-items: center; padding: 6px 9px; appearance: none; border: 0; border-radius: 5px; background: transparent; color: var(--color-tone-13); cursor: pointer; font-size: var(--font-size-body-sm); text-align: left; }
:global(.skill-import-reselect-menu .ui-menu-item:hover),
:global(.skill-import-reselect-menu .ui-menu-item[data-highlighted]) { background: var(--color-tone-6); color: var(--color-tone-15); }
.skill-import-source-summary { display: flex; flex-wrap: wrap; align-items: center; gap: 5px 10px; margin-top: 12px; color: var(--color-tone-10); font-size: var(--font-size-caption); }
.skill-import-source-risk { color: var(--color-warning-text); font-weight: var(--font-weight-medium); }
.skill-import-file-heading { display: flex; align-items: center; justify-content: space-between; margin-top: 15px; padding-bottom: 7px; }
.skill-import-file-heading strong { color: var(--color-tone-13); font-size: var(--font-size-caption); }
.skill-import-file-heading span { color: var(--color-tone-9); font-size: var(--font-size-micro); }
.skill-import-file-list { min-height: 0; flex: 1; overflow: auto; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-4); scrollbar-gutter: stable; }
.skill-import-file-list:focus-visible { outline: 2px solid var(--color-focus); outline-offset: 2px; }
.skill-import-file { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: 9px; min-height: 34px; padding: 7px 10px; border-bottom: 1px solid var(--color-tone-7); }
.skill-import-file:last-child { border-bottom: 0; }
.skill-import-file code { overflow: hidden; color: var(--color-tone-12); font-size: var(--font-size-micro); text-overflow: ellipsis; white-space: nowrap; }
.skill-import-file > span:last-child { color: var(--color-tone-9); font-size: var(--font-size-micro); }
.skill-import-file-kind { padding: 2px 6px; border: 1px solid var(--color-warning-border); border-radius: 999px; background: var(--color-warning-surface); color: var(--color-warning-text); font-size: var(--font-size-micro); }
.skill-import-error { margin: 0 22px; padding: 9px 11px; border: 1px solid var(--color-danger-border); border-radius: 8px; background: var(--color-danger-surface); color: var(--color-danger-text); font-size: var(--font-size-caption); }
.skill-import-actions { display: flex; justify-content: flex-end; gap: 8px; margin: 0; padding: 14px 22px; border-top: 1px solid var(--color-tone-7); }
.skill-import-actions button { min-height: 33px; padding: 6px 13px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-13); cursor: pointer; }
.skill-import-actions .primary { border-color: var(--color-tone-14); background: var(--color-tone-15); color: var(--color-tone-3); font-weight: var(--font-weight-semibold); }
.skill-import-actions button:disabled { cursor: not-allowed; opacity: .45; }
.skill-import-warning { display: grid; gap: 5px; margin-top: 12px; padding: 10px 11px; border: 1px solid var(--color-warning-border); border-radius: 8px; background: var(--color-warning-surface); }
.skill-import-warning strong { color: var(--color-warning-text); font-size: var(--font-size-body-sm); }
.skill-import-warning p { margin: 0; color: var(--color-tone-10); font-size: var(--font-size-caption); line-height: var(--line-height-reading); }
@media (max-width: 700px) {
  :global(.skill-detail-dialog) { width: calc(100vw - 20px); max-height: calc(100vh - 20px); }
  .skill-detail-header, .skill-detail-summary, .skill-variant-list { padding-left: 15px; padding-right: 15px; }
  .skill-metadata-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .skill-installation { grid-template-columns: 1fr; }
  .skill-remove-button { justify-self: start; }
  :global(.skill-import-dialog) { height: calc(100vh - 20px); width: calc(100vw - 20px); }
  .skill-import-workbench { grid-template-columns: 1fr; overflow: auto; }
  .skill-import-location { border-right: 0; border-bottom: 1px solid var(--color-tone-7); }
  .skill-import-source { min-height: 370px; }
}
</style>
