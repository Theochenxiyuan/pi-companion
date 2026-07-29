<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton, UiDialog, UiInput } from '@/components/ui'
import type {
  DiscoveredSkill,
  SkillContentVariant,
  SkillInstallation,
  SkillRemovalCompleted,
  SkillWorkspaceTrustCompleted,
  SkillsLoaded,
  WorkspaceHistoryEntry,
} from '@/types/bridge'
import { useI18n } from '@/i18n'

const props = withDefaults(defineProps<{
  snapshot: SkillsLoaded | null
  loading: boolean
  error: string | null
  workspace?: WorkspaceHistoryEntry | null
  globalOnly?: boolean
  removingInstallationId?: string | null
  removalResult?: SkillRemovalCompleted | null
  trustingWorkspaceId?: string | null
  trustResult?: SkillWorkspaceTrustCompleted | null
}>(), {
  workspace: null,
  globalOnly: false,
  removingInstallationId: null,
  removalResult: null,
  trustingWorkspaceId: null,
  trustResult: null,
})

const emit = defineEmits<{
  close: []
  refresh: []
  removeInstallation: [payload: {
    installationId: string
    expectedContentHash: string
    workspaceId: string
  }]
  trustWorkspace: [workspaceId: string]
}>()

const { locale, t } = useI18n()
type SkillManagerScope = 'workspace' | 'global'
const search = ref('')
const removalTarget = ref<{
  skill: DiscoveredSkill
  variant: SkillContentVariant
  installation: SkillInstallation
} | null>(null)

const title = computed(() => props.globalOnly
  ? t('Direct Chat 技能')
  : t('{name} 的技能', { name: props.workspace?.name ?? t('工作区') }))
const description = computed(() => props.globalOnly
  ? t('全局安装的技能。')
  : workspaceUntrusted.value
    ? t('工作区技能在信任后才会参与加载。')
    : t('同名时，工作区技能优先于全局技能。'))
const workspaceTrust = computed(() => {
  const workspaceId = props.workspace?.id
  if (!workspaceId) return null
  return props.snapshot?.workspaceTrust?.find(entry => entry.workspaceId === workspaceId) ?? null
})
const workspaceUntrusted = computed(() =>
  !props.globalOnly && Boolean(props.workspace) && workspaceTrust.value?.status !== 'trusted')

function trustWorkspace() {
  if (!props.workspace || props.trustingWorkspaceId) return
  emit('trustWorkspace', props.workspace.id)
}

function originsForScope(installation: SkillInstallation, scope: SkillManagerScope) {
  return installation.origins.filter(origin => scope === 'global'
    ? origin.scope === 'global'
    : origin.scope === 'workspace' && origin.workspaceId === props.workspace?.id)
}

function entriesForScope(skill: DiscoveredSkill, scope: SkillManagerScope) {
  return skill.variants.flatMap(variant =>
    variant.installations
      .filter(installation => originsForScope(installation, scope).length > 0)
      .map(installation => ({ skill, variant, installation })))
}

function filteredEntries(scope: SkillManagerScope) {
  const query = search.value.trim().toLocaleLowerCase(locale.value)
  return (props.snapshot?.skills ?? [])
    .flatMap(skill => entriesForScope(skill, scope))
    .filter(entry => !query || [
      entry.skill.name,
      entry.variant.description ?? '',
    ].some(value => value.toLocaleLowerCase(locale.value).includes(query)))
    .sort((left, right) =>
      left.skill.name.localeCompare(right.skill.name, locale.value))
}

const sections = computed<Array<{
  id: SkillManagerScope
  label: string
  description: string
  entries: ReturnType<typeof filteredEntries>
}>>(() => {
  const result: Array<{
    id: SkillManagerScope
    label: string
    description: string
    entries: ReturnType<typeof filteredEntries>
  }> = []
  if (!props.globalOnly) {
    result.push({
      id: 'workspace',
      label: t('工作区技能'),
      description: workspaceUntrusted.value
        ? t('当前未加载；信任工作区后可用。')
        : t('仅用于当前工作区；同名时优先使用。'),
      entries: filteredEntries('workspace'),
    })
  }
  result.push({
    id: 'global',
    label: t('全局技能'),
    description: props.globalOnly
      ? t('Direct Chat 可使用的技能。')
      : t('所有工作区可用；在这里仅供查看。'),
    entries: filteredEntries('global'),
  })
  return result
})

function hasSameNameInOtherScope(name: string, scope: SkillManagerScope) {
  if (props.globalOnly || workspaceUntrusted.value) return false
  const otherScope = scope === 'workspace' ? 'global' : 'workspace'
  return filteredEntries(otherScope).some(entry => entry.skill.name === name)
}

function priorityLabel(scope: SkillManagerScope) {
  return scope === 'workspace' ? t('覆盖全局') : t('已被工作区覆盖')
}

function sourceKind(installation: SkillInstallation, scope: SkillManagerScope) {
  const origins = originsForScope(installation, scope)
  return origins.some(origin => origin.source === 'pi') ? 'pi' : 'agent'
}

function sourceLabel(installation: SkillInstallation, scope: SkillManagerScope) {
  return sourceKind(installation, scope) === 'pi' ? 'Pi' : 'Agent'
}

function canRemove(
  variant: SkillContentVariant,
  installation: SkillInstallation,
) {
  if (!props.workspace || props.globalOnly || !variant.contentHash || !installation.removable) {
    return false
  }
  return installation.origins.some(origin =>
    origin.scope === 'workspace' &&
    origin.source === 'pi' &&
    origin.workspaceId === props.workspace?.id)
}

function requestRemoval(
  skill: DiscoveredSkill,
  variant: SkillContentVariant,
  installation: SkillInstallation,
) {
  if (!canRemove(variant, installation)) return
  removalTarget.value = { skill, variant, installation }
}

function confirmRemoval() {
  const target = removalTarget.value
  const workspaceId = props.workspace?.id
  if (!target?.variant.contentHash || !workspaceId) return
  emit('removeInstallation', {
    installationId: target.installation.id,
    expectedContentHash: target.variant.contentHash,
    workspaceId,
  })
  removalTarget.value = null
}
</script>

<template>
  <UiDialog
    :title="title"
    :description="description"
    overlay-class="skill-manager-backdrop"
    :content-class="['skill-manager', { 'global-only': globalOnly }]"
    @close="$emit('close')"
  >
      <header>
        <div>
          <h1>{{ title }}</h1>
          <p>{{ description }}</p>
        </div>
        <div class="skill-manager-actions">
          <UiButton type="button" :disabled="loading" @click="$emit('refresh')">
            {{ t(loading ? '正在刷新…' : '刷新') }}
          </UiButton>
          <UiButton class="skill-manager-close" type="button" :aria-label="t('关闭')" @click="$emit('close')">×</UiButton>
        </div>
      </header>

      <div v-if="workspaceUntrusted" class="skill-manager-trust">
        <div>
          <strong>{{ t('未信任工作区') }}</strong>
          <span>{{ t('Pi 不会加载此工作区中的项目技能。') }}</span>
        </div>
        <UiButton
          type="button"
          :disabled="Boolean(trustingWorkspaceId)"
          @click="trustWorkspace"
        >
          {{ t(trustingWorkspaceId === workspace?.id ? '正在信任…' : '信任工作区') }}
        </UiButton>
      </div>

      <label class="skill-manager-search">
        <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5" /><path d="m16 16 4 4" /></svg>
        <UiInput v-model="search" type="search" :placeholder="t('搜索技能')" :aria-label="t('搜索技能')" />
      </label>

      <div v-if="removalResult" class="skill-manager-result" :class="{ failed: !removalResult.succeeded }">
        {{ removalResult.message }}
      </div>
      <div v-if="trustResult" class="skill-manager-result" :class="{ failed: !trustResult.succeeded }">
        {{ trustResult.message }}
      </div>
      <div v-if="error" class="skill-manager-error" role="alert">{{ error }}</div>

      <div class="skill-manager-list" :class="{ 'single-section': sections.length === 1 }">
        <div v-if="loading && !snapshot" class="skill-manager-state">{{ t('正在读取本地技能…') }}</div>
        <section v-for="section in sections" :key="section.id" class="skill-manager-section">
          <div class="skill-manager-section-heading">
            <h2>{{ section.label }}</h2>
            <p>{{ section.description }}</p>
          </div>
          <div class="skill-manager-section-list">
            <div v-if="section.entries.length === 0" class="skill-manager-section-empty">
              {{ t(search ? '没有匹配的技能' : '未发现技能') }}
            </div>
            <article
              v-for="entry in section.entries"
              :key="`${section.id}:${entry.installation.id}`"
              class="skill-manager-item"
            >
              <div class="skill-manager-item-content">
                <div class="skill-manager-item-title">
                  <h3>{{ entry.skill.name }}</h3>
                  <span :class="`source-${sourceKind(entry.installation, section.id)}`">
                    {{ sourceLabel(entry.installation, section.id) }}
                  </span>
                  <span
                    v-if="hasSameNameInOtherScope(entry.skill.name, section.id)"
                    class="skill-manager-priority"
                  >
                    {{ priorityLabel(section.id) }}
                  </span>
                  <span
                    v-if="section.id === 'workspace' && workspaceUntrusted"
                    class="skill-manager-untrusted"
                  >
                    {{ t('未信任工作区') }}
                  </span>
                </div>
                <p>{{ entry.variant.description || t('没有提供技能描述。') }}</p>
              </div>
              <UiButton
                v-if="section.id === 'workspace' && canRemove(entry.variant, entry.installation)"
                class="skill-manager-remove"
                type="button"
                :disabled="removingInstallationId === entry.installation.id"
                @click="requestRemoval(entry.skill, entry.variant, entry.installation)"
              >
                {{ t(removingInstallationId === entry.installation.id ? '正在卸载…' : '卸载') }}
              </UiButton>
              <span v-else class="skill-manager-readonly">{{ t('只读') }}</span>
            </article>
          </div>
        </section>
      </div>
    <UiDialog
      v-if="removalTarget"
      :title="t('确认卸载技能')"
      overlay-class="skill-manager-confirm-backdrop"
      content-class="skill-manager-confirm"
      alert
      @close="removalTarget = null"
    >
        <h2>{{ t('卸载 {name}？', { name: removalTarget.skill.name }) }}</h2>
        <p>{{ t('技能会被移入项目技能目录中的可恢复位置。') }}</p>
        <div>
          <UiButton type="button" @click="removalTarget = null">{{ t('取消') }}</UiButton>
          <UiButton class="danger" type="button" @click="confirmRemoval">{{ t('卸载') }}</UiButton>
        </div>
    </UiDialog>
  </UiDialog>
</template>

<style scoped>
:global(.skill-manager-backdrop) { position: fixed; z-index: 90; inset: 0; display: grid; place-items: center; padding: 24px; background: var(--color-overlay-strong); backdrop-filter: blur(3px); }
:global(.skill-manager) { display: flex; width: min(1120px, calc(100vw - 48px)); max-height: min(820px, calc(100vh - 48px)); flex-direction: column; overflow: hidden; border: 1px solid var(--color-tone-8); border-radius: 14px; background: var(--color-tone-3); box-shadow: 0 28px 90px var(--color-overlay-strong); }
:global(.skill-manager:not(.global-only)) { min-height: min(620px, calc(100vh - 48px)); }
:global(.skill-manager.global-only) { width: min(760px, calc(100vw - 48px)); }
:global(.skill-manager > header) { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding: 22px 24px 17px; border-bottom: 1px solid var(--color-tone-7); }
:global(.skill-manager h1), :global(.skill-manager h2), :global(.skill-manager p) { margin: 0; }
:global(.skill-manager h1) { color: var(--color-tone-15); font-size: var(--font-size-title-lg); }
:global(.skill-manager header p) { margin-top: 6px; color: var(--color-tone-10); font-size: var(--font-size-caption); }
.skill-manager-actions { display: flex; align-items: center; gap: 8px; }
.skill-manager-actions button { min-height: 32px; padding: 5px 11px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-13); cursor: pointer; font: inherit; }
.skill-manager-actions .skill-manager-close { width: 32px; justify-content: center; padding: 0; font-size: 20px; text-align: center; }
.skill-manager-trust { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin: 14px 24px 0; padding: 11px 13px; border: 1px solid var(--color-warning-border); border-radius: 8px; background: var(--color-warning-surface); color: var(--color-warning-text); }
.skill-manager-trust > div { display: grid; gap: 2px; }
.skill-manager-trust span { font-size: var(--font-size-caption); }
.skill-manager-trust button { flex: none; min-height: 32px; padding: 5px 11px; border: 1px solid var(--color-warning-border); border-radius: 7px; background: var(--color-tone-3); color: var(--color-warning-text); cursor: pointer; font: inherit; }
.skill-manager-trust button:disabled { cursor: default; opacity: .6; }
.skill-manager-search { display: flex; align-items: center; gap: 9px; margin: 16px 24px 10px; padding: 8px 11px; border: 1px solid var(--color-tone-7); border-radius: 8px; background: var(--color-tone-4); }
.skill-manager-search svg { width: 16px; fill: none; stroke: var(--color-tone-10); stroke-width: 1.5; }
.skill-manager-search input { width: 100%; border: 0; outline: 0; background: transparent; color: var(--color-tone-14); font: inherit; }
.skill-manager-result, .skill-manager-error { margin: 0 24px 10px; padding: 9px 11px; border-radius: 7px; background: var(--color-success-surface); color: var(--color-success-text); font-size: var(--font-size-caption); }
.skill-manager-result.failed, .skill-manager-error { background: var(--color-danger-surface); color: var(--color-danger-text); }
.skill-manager-list { display: grid; grid-template-columns: minmax(0, 1.6fr) minmax(280px, 1fr); flex: 1 1 auto; gap: 16px; min-height: 0; overflow: hidden; padding: 6px 24px 24px; }
.skill-manager-list.single-section { grid-template-columns: minmax(0, 1fr); }
.skill-manager-state { padding: 48px 20px; color: var(--color-tone-10); text-align: center; }
.skill-manager-section { display: grid; grid-template-rows: auto minmax(0, 1fr); min-width: 0; min-height: 0; gap: 10px; }
.skill-manager-section-heading h2, .skill-manager-section-heading p { margin: 0; }
.skill-manager-section-heading h2 { color: var(--color-tone-14); font-size: var(--font-size-title-sm); }
.skill-manager-section-heading p { margin-top: 3px; color: var(--color-tone-9); font-size: var(--font-size-caption); }
.skill-manager-section-list { display: grid; align-content: start; gap: 8px; min-height: 0; overflow: auto; padding-right: 3px; }
.skill-manager-section-empty { padding: 18px 16px; border: 1px dashed var(--color-tone-7); border-radius: 9px; color: var(--color-tone-9); font-size: var(--font-size-caption); text-align: center; }
.skill-manager-item { display: flex; align-items: center; justify-content: space-between; gap: 14px; min-width: 0; padding: 12px 14px; border: 1px solid var(--color-tone-7); border-radius: 10px; background: var(--color-tone-4); }
.skill-manager-item-content { display: grid; min-width: 0; gap: 5px; }
.skill-manager-item-title { display: flex; align-items: center; gap: 8px; }
.skill-manager-item-title h3 { overflow: hidden; margin: 0; color: var(--color-tone-15); font-size: var(--font-size-title-sm); text-overflow: ellipsis; white-space: nowrap; }
.skill-manager-item-title span { flex: none; padding: 2px 7px; border: 1px solid var(--color-tone-7); border-radius: 999px; color: var(--color-tone-11); font-size: var(--font-size-caption); }
.skill-manager-item-title .source-pi { border-color: var(--color-success-border); background: var(--color-success-surface); color: var(--color-success-text); }
.skill-manager-item-title .source-agent { border-color: var(--color-info); background: var(--color-info-surface); color: var(--color-info-text); }
.skill-manager-item-title .skill-manager-priority { border-color: var(--color-warning-border); background: var(--color-warning-surface); color: var(--color-warning-text); }
.skill-manager-item-title .skill-manager-untrusted { border-color: var(--color-warning-border); background: var(--color-warning-surface); color: var(--color-warning-text); }
.skill-manager-item p { color: var(--color-tone-11); font-size: var(--font-size-caption); }
.skill-manager-remove { min-height: 30px; padding: 4px 10px; border: 1px solid var(--color-danger-border); border-radius: 7px; background: var(--color-danger-surface); color: var(--color-danger-text); cursor: pointer; font: inherit; }
.skill-manager-readonly { flex: none; padding: 3px 8px; border: 1px solid var(--color-tone-7); border-radius: 999px; color: var(--color-tone-10); font-size: var(--font-size-caption); }
:global(.skill-manager-confirm-backdrop) { position: fixed; z-index: 91; inset: 0; display: grid; place-items: center; padding: 24px; background: var(--color-overlay-strong); }
:global(.skill-manager-confirm) { width: min(460px, calc(100vw - 48px)); padding: 22px; border: 1px solid var(--color-tone-8); border-radius: 12px; background: var(--color-tone-3); }
:global(.skill-manager-confirm h2), :global(.skill-manager-confirm p) { margin: 0; }
:global(.skill-manager-confirm p) { margin-top: 8px; color: var(--color-tone-11); }
:global(.skill-manager-confirm > div) { display: flex; justify-content: flex-end; gap: 8px; margin-top: 18px; }
:global(.skill-manager-confirm button) { min-height: 32px; padding: 5px 12px; border: 1px solid var(--color-tone-8); border-radius: 7px; background: var(--color-tone-4); color: var(--color-tone-13); cursor: pointer; font: inherit; }
:global(.skill-manager-confirm .danger) { border-color: var(--color-danger-border); background: var(--color-danger-surface); color: var(--color-danger-text); }
@media (max-width: 880px) {
  .skill-manager-list { grid-template-columns: 1fr; overflow: auto; }
  .skill-manager-section { min-height: auto; }
  .skill-manager-section + .skill-manager-section { padding-top: 16px; border-top: 1px solid var(--color-tone-7); }
  .skill-manager-section-list { overflow: visible; }
}
@media (max-width: 640px) {
  :global(.skill-manager-backdrop) { padding: 10px; }
  :global(.skill-manager) { width: calc(100vw - 20px); max-height: calc(100vh - 20px); }
  :global(.skill-manager:not(.global-only)) { min-height: min(620px, calc(100vh - 20px)); }
  :global(.skill-manager > header) { padding: 17px; }
  .skill-manager-search { margin-inline: 17px; }
  .skill-manager-list { padding-inline: 17px; }
}
</style>
