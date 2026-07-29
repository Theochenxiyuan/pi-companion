<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { UiButton, UiInput, UiNativeSelect, UiTextarea } from '@/components/ui'
import type {
  WorkspaceGitAction,
  WorkspaceGitActionCompleted,
  WorkspaceGitCommit,
  WorkspaceGitCommitMessageGenerated,
  WorkspaceGitEntry,
  WorkspaceGitHistorySnapshot,
  WorkspaceGitSnapshot,
} from '@/types/bridge'
import { t, useI18n } from '@/i18n'

const props = defineProps<{
  workingDirectory: string | null
  update: WorkspaceGitSnapshot | null
  historyUpdate: WorkspaceGitHistorySnapshot | null
  historyLoading?: boolean
  actionResult: WorkspaceGitActionCompleted | null
  pendingAction: WorkspaceGitAction | null
  commitMessageResult?: WorkspaceGitCommitMessageGenerated | null
  commitMessageLoading?: boolean
  taskActive?: boolean
}>()
const { locale } = useI18n()

const emit = defineEmits<{
  refresh: []
  refreshHistory: [append: boolean]
  openDiff: [entry: WorkspaceGitEntry]
  openCommit: [commit: WorkspaceGitCommit]
  stage: [paths: string[]]
  unstage: [paths: string[]]
  commit: [message: string]
  generateCommitMessage: []
  switchBranch: [branch: string]
  createBranch: [branch: string]
  updateBranch: [strategy: 'merge' | 'rebase', sourceBranch: string]
  abortOperation: []
}>()

const activeTab = ref<'commit' | 'update' | 'history'>('commit')
const commitMessage = ref('')
const updateStrategy = ref<'merge' | 'rebase'>('merge')
const sourceBranch = ref('')
const creatingBranch = ref(false)
const newBranchName = ref('')
const branchMenuOpen = ref(false)
const branchSearch = ref('')
const branchMenuRoot = ref<HTMLElement | null>(null)
const commitMessageInput = ref<InstanceType<typeof UiTextarea> | null>(null)
const stagedExpanded = ref(true)
const unstagedExpanded = ref(true)

const snapshot = computed(() =>
  props.update?.workingDirectory === props.workingDirectory ? props.update : null)
const history = computed(() =>
  props.historyUpdate?.workingDirectory === props.workingDirectory ? props.historyUpdate : null)
const stagedEntries = computed(() => snapshot.value?.entries.filter(entry => entry.isStaged) ?? [])
const unstagedEntries = computed(() => snapshot.value?.entries.filter(entry => entry.isUnstaged) ?? [])
const additions = computed(() => snapshot.value?.entries.reduce((sum, entry) => sum + entry.addedLines, 0) ?? 0)
const deletions = computed(() => snapshot.value?.entries.reduce((sum, entry) => sum + entry.deletedLines, 0) ?? 0)
const branches = computed(() => {
  const reported = snapshot.value?.branches ?? []
  const current = snapshot.value?.branch
  if (!snapshot.value?.isRepository ||
      snapshot.value.isDetached ||
      !current ||
      reported.some(branch => branch.name === current)) {
    return reported
  }

  // An unborn repository has a symbolic branch name but no refs/heads entry
  // until its first commit. Keep the branch selector and create action visible.
  return [{
    name: current,
    shortHash: '',
    subject: '',
    isCurrent: true,
  }, ...reported]
})
const filteredBranches = computed(() => {
  const query = branchSearch.value.trim().toLocaleLowerCase()
  return query
    ? branches.value.filter(branch => branch.name.toLocaleLowerCase().includes(query))
    : branches.value
})
const otherBranches = computed(() =>
  branches.value.filter(branch => !branch.isCurrent && branch.name !== snapshot.value?.branch))
const canManageBranches = computed(() => snapshot.value?.canManageBranches ?? true)
const repositoryClean = computed(() => (snapshot.value?.entries.length ?? 0) === 0)
const operationState = computed(() => snapshot.value?.operationState ?? 'None')
const branchMenuDisabled = computed(() =>
  Boolean(props.taskActive) ||
  !canManageBranches.value ||
  operationState.value !== 'None' ||
  Boolean(props.pendingAction))
const branchMutationDisabled = computed(() => branchMenuDisabled.value || !repositoryClean.value)
const canCommit = computed(() =>
  stagedEntries.value.length > 0 &&
  commitMessage.value.trim().length > 0 &&
  !props.taskActive &&
  !props.pendingAction)
const canGenerateCommitMessage = computed(() =>
  stagedEntries.value.length > 0 &&
  !props.commitMessageLoading &&
  !props.pendingAction)
const generatedMessageStale = computed(() => {
  const generatedFingerprint = props.commitMessageResult?.stagedFingerprint
  const currentFingerprint = snapshot.value?.stagedFingerprint
  return Boolean(
    props.commitMessageResult?.succeeded &&
    generatedFingerprint &&
    currentFingerprint &&
    generatedFingerprint !== currentFingerprint,
  )
})
const canUpdate = computed(() =>
  canManageBranches.value &&
  repositoryClean.value &&
  operationState.value === 'None' &&
  Boolean(sourceBranch.value) &&
  !snapshot.value?.isDetached &&
  !props.taskActive &&
  !props.pendingAction)

watch(
  [otherBranches, sourceBranch],
  ([available]) => {
    if (!available.some(branch => branch.name === sourceBranch.value)) {
      sourceBranch.value = available[0]?.name ?? ''
    }
  },
  { immediate: true },
)

watch(
  () => props.workingDirectory,
  () => {
    activeTab.value = 'commit'
    commitMessage.value = ''
    sourceBranch.value = ''
    creatingBranch.value = false
    newBranchName.value = ''
    branchMenuOpen.value = false
    branchSearch.value = ''
    stagedExpanded.value = true
    unstagedExpanded.value = true
    void nextTick(resizeCommitMessage)
  },
)

watch(
  () => props.actionResult,
  result => {
    if (result?.workingDirectory === props.workingDirectory &&
        result.succeeded &&
        result.action === 'commit') {
      commitMessage.value = ''
      void nextTick(resizeCommitMessage)
    }
  },
)

watch(
  () => props.commitMessageResult,
  result => {
    if (!result?.succeeded ||
        !result.message ||
        result.workingDirectory !== props.workingDirectory) return
    const currentFingerprint = snapshot.value?.stagedFingerprint
    if (result.stagedFingerprint &&
        currentFingerprint &&
        result.stagedFingerprint !== currentFingerprint) return
    commitMessage.value = result.message
    void nextTick(resizeCommitMessage)
  },
)

function selectTab(tab: 'commit' | 'update' | 'history') {
  activeTab.value = tab
  if (tab === 'history') emit('refreshHistory', false)
}

function fileName(path: string) {
  return path.split('/').at(-1) ?? path
}

function directoryName(path: string) {
  const separator = path.lastIndexOf('/')
  return separator < 0 ? '' : path.slice(0, separator)
}

function statusLabel(entry: WorkspaceGitEntry) {
  if (entry.isUntracked) return 'U'
  return {
    Added: 'A',
    Deleted: 'D',
    Renamed: 'R',
    Copied: 'C',
    Unmerged: '!',
    Modified: 'M',
  }[entry.kind]
}

function fileIconKind(path: string) {
  const extension = path.split('.').at(-1)?.toLocaleLowerCase() ?? ''
  if (['md', 'mdx', 'txt', 'rst'].includes(extension)) return 'text'
  if (['cs', 'ts', 'tsx', 'js', 'jsx', 'vue', 'py', 'rs', 'go', 'java', 'json', 'toml', 'yaml', 'yml'].includes(extension)) return 'code'
  return 'file'
}

function runCommit() {
  const message = commitMessage.value.trim()
  if (canCommit.value) emit('commit', message)
}

function resizeCommitMessage() {
  commitMessageInput.value?.resizeToContent(120)
}

function changeBranch(branch: string) {
  branchMenuOpen.value = false
  branchSearch.value = ''
  if (branch && branch !== snapshot.value?.branch) emit('switchBranch', branch)
}

function toggleBranchMenu() {
  if (!snapshot.value?.isRepository || !branches.value.length) return
  branchMenuOpen.value = !branchMenuOpen.value
  if (!branchMenuOpen.value) branchSearch.value = ''
}

function beginCreateBranch() {
  branchMenuOpen.value = false
  branchSearch.value = ''
  creatingBranch.value = true
}

function closeBranchMenu(event: PointerEvent) {
  if (!branchMenuRoot.value?.contains(event.target as Node)) {
    branchMenuOpen.value = false
    branchSearch.value = ''
  }
}

function createBranch() {
  const branch = newBranchName.value.trim()
  if (!branch || props.pendingAction) return
  emit('createBranch', branch)
  newBranchName.value = ''
  creatingBranch.value = false
}

onMounted(() => {
  document.addEventListener('pointerdown', closeBranchMenu)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', closeBranchMenu)
})

function formatCommitDate(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}
</script>

<template>
  <section class="workspace-git-panel" :aria-label="t('Git 工作区变更')">
    <header class="git-branch-row">
      <div ref="branchMenuRoot" class="git-branch-select">
        <UiButton
          v-if="snapshot?.isRepository && branches.length"
          class="git-current-branch"
          type="button"
          :aria-label="t('切换本地分支')"
          :aria-expanded="branchMenuOpen"
          @click="toggleBranchMenu"
          @keydown.esc="branchMenuOpen = false"
        >
          <svg class="git-branch-icon" viewBox="0 0 20 20" aria-hidden="true"><circle cx="5" cy="4" r="1.7" /><circle cx="5" cy="15.5" r="1.7" /><circle cx="14.5" cy="7" r="1.7" /><path d="M5 5.7v8.1M6.7 6.5c4.2 0 3.7.5 6.1.5" /></svg>
          <strong>{{ snapshot.branch ?? 'Git' }}</strong>
          <svg class="git-branch-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4" /></svg>
          <small v-if="snapshot.isDetached">detached</small>
        </UiButton>
        <div v-else class="git-current-branch static">
          <svg class="git-branch-icon" viewBox="0 0 20 20" aria-hidden="true"><circle cx="5" cy="4" r="1.7" /><circle cx="5" cy="15.5" r="1.7" /><circle cx="14.5" cy="7" r="1.7" /><path d="M5 5.7v8.1M6.7 6.5c4.2 0 3.7.5 6.1.5" /></svg>
          <strong>{{ snapshot?.branch ?? 'Git' }}</strong>
        </div>
        <div v-if="branchMenuOpen" class="git-branch-menu">
          <label class="git-branch-search">
            <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5" /><path d="m12.3 12.3 4 4" /></svg>
            <UiInput v-model="branchSearch" type="search" :placeholder="t('搜索分支…')" autofocus />
          </label>
          <UiButton class="git-create-branch-option" type="button" :disabled="branchMutationDisabled" @click="beginCreateBranch">
            <span aria-hidden="true">+</span>{{ t('创建新分支…') }}
          </UiButton>
          <div class="git-branch-menu-heading">{{ t('本地分支') }}</div>
          <UiButton
            v-for="branch in filteredBranches"
            :key="branch.name"
            class="git-branch-option"
            type="button"
            :class="{ current: branch.isCurrent || branch.name === snapshot?.branch }"
            :disabled="branchMutationDisabled && branch.name !== snapshot?.branch"
            @click="changeBranch(branch.name)"
          >
            <span>{{ branch.name }}</span>
            <strong v-if="branch.isCurrent || branch.name === snapshot?.branch">{{ t('当前') }}</strong>
          </UiButton>
          <div v-if="!filteredBranches.length" class="git-branch-menu-empty">{{ t('没有匹配的分支') }}</div>
        </div>
      </div>
      <div class="git-branch-actions">
        <UiButton type="button" :aria-label="t('刷新 Git 变更')" :title="t('刷新 Git 变更')" @click="emit('refresh')">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M16 7a6 6 0 1 0 .2 5M16 3v4h-4" /></svg>
        </UiButton>
      </div>
    </header>

    <form v-if="creatingBranch" class="git-new-branch" @submit.prevent="createBranch">
      <UiInput v-model="newBranchName" :placeholder="t('分支名称')" maxlength="255" autofocus />
      <UiButton type="button" @click="creatingBranch = false">{{ t('取消') }}</UiButton>
      <UiButton class="primary" type="submit" :disabled="!newBranchName.trim() || Boolean(pendingAction)">{{ t('创建') }}</UiButton>
    </form>

    <div v-if="!workingDirectory" class="git-panel-empty">{{ t('选择工作目录后显示 Git 变更') }}</div>
    <div v-else-if="!snapshot" class="git-panel-empty"><span class="file-loading-spinner"></span>{{ t('正在读取 Git 状态…') }}</div>
    <div v-else-if="snapshot.error" class="git-panel-empty error">{{ snapshot.error }}</div>
    <div v-else-if="!snapshot.isRepository" class="git-panel-empty">{{ t('当前工作目录不是 Git 仓库') }}</div>

    <template v-else>
      <nav class="git-mode-tabs" :aria-label="t('Git 操作')">
        <UiButton type="button" :class="{ active: activeTab === 'commit' }" @click="selectTab('commit')">
          {{ t('提交') }}
        </UiButton>
        <UiButton type="button" :class="{ active: activeTab === 'update' }" @click="selectTab('update')">{{ t('更新') }}</UiButton>
        <UiButton type="button" :class="{ active: activeTab === 'history' }" @click="selectTab('history')">{{ t('提交历史') }}</UiButton>
      </nav>

      <section v-if="activeTab === 'commit'" class="git-commit-view">
        <div class="git-change-section">
          <section v-if="stagedEntries.length" class="git-change-group">
            <header>
              <UiButton
                class="git-change-group-batch"
                type="button"
                :title="t('全部取消暂存')"
                :aria-label="t('全部取消暂存')"
                :disabled="taskActive || Boolean(pendingAction)"
                @click="emit('unstage', stagedEntries.map(entry => entry.relativePath))"
              >−</UiButton>
              <UiButton class="git-change-group-toggle" type="button" :aria-expanded="stagedExpanded" @click="stagedExpanded = !stagedExpanded">
                <strong>{{ t('已暂存') }}</strong>
                <i>{{ stagedEntries.length }}</i>
                <svg viewBox="0 0 16 16" aria-hidden="true" :class="{ collapsed: !stagedExpanded }"><path d="m4 6 4 4 4-4" /></svg>
              </UiButton>
            </header>
            <div v-if="stagedExpanded" class="git-change-files">
              <div v-for="entry in stagedEntries" :key="`staged:${entry.relativePath}`" class="git-change-file">
                <UiButton class="git-change-action" type="button" :title="t('取消暂存')" :disabled="taskActive || Boolean(pendingAction)" @click="emit('unstage', [entry.relativePath])">−</UiButton>
                <UiButton class="git-change-main" type="button" :title="t('{path} · 点击查看 Diff', { path: entry.relativePath })" @click="emit('openDiff', entry)">
                  <span class="git-change-status" :class="entry.kind.toLocaleLowerCase()">{{ statusLabel(entry) }}</span>
                  <span class="file-kind-icon" :class="fileIconKind(entry.relativePath)">
                    <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 2.5h7l5 5v10H4z" /><path d="M11 2.5v5h5" /></svg>
                  </span>
                  <span class="git-change-path" :class="{ 'has-directory': directoryName(entry.relativePath) }">
                    <small v-if="directoryName(entry.relativePath)" class="git-change-directory"><span>{{ directoryName(entry.relativePath) }}</span><i>/</i></small>
                    <strong>{{ fileName(entry.relativePath) }}</strong>
                  </span>
                  <span v-if="entry.isBinary" class="git-binary-label">{{ t('二进制') }}</span>
                  <span v-else class="git-line-stats"><b>+{{ entry.addedLines }}</b><i>-{{ entry.deletedLines }}</i></span>
                </UiButton>
              </div>
            </div>
          </section>

          <section v-if="unstagedEntries.length" class="git-change-group">
            <header>
              <UiButton
                class="git-change-group-batch"
                type="button"
                :title="t('全部暂存')"
                :aria-label="t('全部暂存')"
                :disabled="taskActive || Boolean(pendingAction)"
                @click="emit('stage', unstagedEntries.map(entry => entry.relativePath))"
              >+</UiButton>
              <UiButton class="git-change-group-toggle" type="button" :aria-expanded="unstagedExpanded" @click="unstagedExpanded = !unstagedExpanded">
                <strong>{{ t('更改') }}</strong>
                <i>{{ unstagedEntries.length }}</i>
                <svg viewBox="0 0 16 16" aria-hidden="true" :class="{ collapsed: !unstagedExpanded }"><path d="m4 6 4 4 4-4" /></svg>
              </UiButton>
            </header>
            <div v-if="unstagedExpanded" class="git-change-files">
              <div v-for="entry in unstagedEntries" :key="`unstaged:${entry.relativePath}`" class="git-change-file">
                <UiButton class="git-change-action" type="button" :title="t('暂存')" :disabled="taskActive || Boolean(pendingAction)" @click="emit('stage', [entry.relativePath])">+</UiButton>
                <UiButton class="git-change-main" type="button" :title="t('{path} · 点击查看 Diff', { path: entry.relativePath })" @click="emit('openDiff', entry)">
                  <span class="git-change-status" :class="entry.kind.toLocaleLowerCase()">{{ statusLabel(entry) }}</span>
                  <span class="file-kind-icon" :class="fileIconKind(entry.relativePath)">
                    <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4 2.5h7l5 5v10H4z" /><path d="M11 2.5v5h5" /></svg>
                  </span>
                  <span class="git-change-path" :class="{ 'has-directory': directoryName(entry.relativePath) }">
                    <small v-if="directoryName(entry.relativePath)" class="git-change-directory"><span>{{ directoryName(entry.relativePath) }}</span><i>/</i></small>
                    <strong>{{ fileName(entry.relativePath) }}</strong>
                  </span>
                  <span v-if="entry.isBinary" class="git-binary-label">{{ t('二进制') }}</span>
                  <span v-else class="git-line-stats"><b>+{{ entry.addedLines }}</b><i>-{{ entry.deletedLines }}</i></span>
                </UiButton>
              </div>
            </div>
          </section>

          <div v-if="!snapshot.entries.length" class="git-panel-empty compact">{{ t('工作区没有未提交的变更') }}</div>
        </div>

        <form class="git-commit-form" @submit.prevent="runCommit">
          <div class="git-commit-summary">
            <strong>{{ t('提交') }}</strong>
            <span>{{ taskActive ? t('任务运行中，Git 写入暂不可用') : t('{count} 个已暂存文件', { count: stagedEntries.length }) }}</span>
            <UiButton
              class="git-ai-generate"
              type="button"
              :disabled="!canGenerateCommitMessage"
              @click="emit('generateCommitMessage')"
            >
              <span v-if="commitMessageLoading" class="file-loading-spinner"></span>
              <svg v-else viewBox="0 0 18 18" aria-hidden="true"><path d="m9 2 .8 2.7L12.5 6l-2.7.8L9 9.5l-.8-2.7L5.5 6l2.7-1.3zM14 10l.6 1.9 1.9.6-1.9.6L14 15l-.6-1.9-1.9-.6 1.9-.6z" /></svg>
              <span>{{ t('生成') }}</span>
            </UiButton>
            <small v-if="snapshot.entries.length"><b>+{{ additions }}</b><i>-{{ deletions }}</i></small>
          </div>
          <UiTextarea
            ref="commitMessageInput"
            v-model="commitMessage"
            :placeholder="t('提交信息')"
            maxlength="4000"
            rows="1"
            :disabled="Boolean(pendingAction)"
            @input="resizeCommitMessage"
            @keydown.ctrl.enter.prevent="runCommit"
          ></UiTextarea>
          <UiButton class="primary" type="submit" :disabled="!canCommit">
            <span v-if="pendingAction === 'commit'" class="file-loading-spinner"></span>
            {{ t('提交') }}
          </UiButton>
          <p v-if="commitMessageResult?.error" class="git-commit-message-notice error">
            {{ commitMessageResult.error }}
          </p>
          <p v-else-if="generatedMessageStale" class="git-commit-message-notice warning">
            {{ t('暂存内容已变化，请重新生成提交信息。') }}
          </p>
          <p v-else-if="commitMessageResult?.succeeded && commitMessageResult.truncatedInput" class="git-commit-message-notice">
            {{ t('Diff 较大，提交信息根据已读取的暂存内容生成。') }}
          </p>
        </form>
      </section>

      <section v-else-if="activeTab === 'update'" class="git-update-view">
        <div v-if="operationState !== 'None'" class="git-operation-alert">
          <strong>{{ t('Git 操作尚未完成') }}</strong>
          <p>{{ t(operationState === 'Merge' ? '仓库正在合并；可处理冲突后提交，或中止合并。' : '仓库正在变基；请在外部 Git 工具中继续，或在这里中止变基。') }}</p>
          <UiButton class="danger" type="button" :disabled="Boolean(pendingAction)" @click="emit('abortOperation')">{{ t('中止操作') }}</UiButton>
        </div>

        <template v-else>
          <header>
            <strong>{{ t('更新分支') }}</strong>
            <p>{{ t('将另一个本地分支的更改带入 {branch}。', { branch: snapshot.branch ?? 'HEAD' }) }}</p>
          </header>

          <div class="git-update-strategies">
            <UiButton type="button" :class="{ active: updateStrategy === 'merge' }" @click="updateStrategy = 'merge'">
              <strong>{{ t('合并') }}</strong>
              <span>{{ t('创建合并提交并保留分支历史。') }}</span>
            </UiButton>
            <UiButton type="button" :class="{ active: updateStrategy === 'rebase' }" @click="updateStrategy = 'rebase'">
              <strong>{{ t('变基') }}</strong>
              <span>{{ t('把当前提交移动到所选分支之上。') }}</span>
            </UiButton>
          </div>

          <label class="git-update-source">
            <span>{{ t('要合入 {branch} 的本地分支', { branch: snapshot.branch ?? 'HEAD' }) }}</span>
            <UiNativeSelect v-model="sourceBranch" :disabled="!canManageBranches || Boolean(pendingAction)">
              <option v-if="!otherBranches.length" value="">{{ t('没有其他本地分支') }}</option>
              <option v-for="branch in otherBranches" :key="branch.name" :value="branch.name">{{ branch.name }}</option>
            </UiNativeSelect>
          </label>

          <p v-if="!canManageBranches" class="git-update-warning">{{ t('工作目录不是仓库根目录，已禁用分支操作。') }}</p>
          <p v-else-if="taskActive" class="git-update-warning">{{ t('任务运行中，Git 写入暂不可用') }}</p>
          <p v-else-if="!repositoryClean" class="git-update-warning">{{ t('更新分支前，请先提交或处理全部更改。') }}</p>

          <footer>
            <span v-if="sourceBranch" class="git-update-summary">
              <template v-if="locale === 'en-US'">
                <template v-if="updateStrategy === 'merge'">Merge <strong>{{ sourceBranch }}</strong> into <strong>{{ snapshot.branch ?? 'HEAD' }}</strong>.</template>
                <template v-else>Rebase <strong>{{ snapshot.branch ?? 'HEAD' }}</strong> onto <strong>{{ sourceBranch }}</strong>.</template>
              </template>
              <template v-else>
                <template v-if="updateStrategy === 'merge'">这将把 <strong>{{ sourceBranch }}</strong> 合并到 <strong>{{ snapshot.branch ?? 'HEAD' }}</strong></template>
                <template v-else>这将把 <strong>{{ snapshot.branch ?? 'HEAD' }}</strong> 变基到 <strong>{{ sourceBranch }}</strong></template>
              </template>
            </span>
            <UiButton
              class="primary"
              type="button"
              :disabled="!canUpdate"
              @click="emit('updateBranch', updateStrategy, sourceBranch)"
            >{{ t(updateStrategy === 'merge' ? '合并' : '变基') }}</UiButton>
          </footer>
        </template>
      </section>

      <section v-else class="git-history-view">
        <div v-if="!history" class="git-panel-empty">
          <span v-if="historyLoading" class="file-loading-spinner"></span>
          {{ t('正在读取提交历史…') }}
        </div>
        <div v-else-if="history.error && !history.entries.length" class="git-panel-empty error">{{ history.error }}</div>
        <div v-else-if="!history.entries.length" class="git-panel-empty">{{ t('还没有提交历史') }}</div>
        <div v-else class="git-history-list">
          <UiButton
            v-for="(commit, index) in history.entries"
            :key="commit.hash"
            type="button"
            :class="{ 'last-commit': index === history.entries.length - 1 }"
            @click="emit('openCommit', commit)"
          >
            <span class="git-history-node" aria-hidden="true"></span>
            <span class="git-history-content">
              <strong>{{ commit.subject || t('无提交标题') }}</strong>
              <small>{{ commit.authorName }} · {{ formatCommitDate(commit.timestamp) }}</small>
            </span>
            <code>{{ commit.shortHash }}</code>
          </UiButton>
          <p v-if="history.error" class="git-history-load-error">{{ history.error }}</p>
          <div v-if="history.hasMore" class="git-history-load-more">
            <UiButton type="button" :disabled="historyLoading" @click="emit('refreshHistory', true)">
              <span v-if="historyLoading" class="file-loading-spinner"></span>
              {{ t(historyLoading ? '正在加载…' : '加载更多') }}
            </UiButton>
          </div>
        </div>
      </section>
    </template>
  </section>
</template>
