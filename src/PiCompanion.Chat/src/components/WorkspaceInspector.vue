<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { BaseTree } from '@he-tree/vue'
import '@he-tree/vue/style/default.css'
import { UiButton, UiInput, UiMenu, UiMenuItem } from '@/components/ui'
import WorkspaceGitPanel from '@/components/WorkspaceGitPanel.vue'
import ContextSessionPanel from '@/components/ContextSessionPanel.vue'
import type {
  PiModelInfo,
  SessionStatisticsSnapshot,
  WorkspaceDirectoryListing,
  WorkspaceFileEntry,
  WorkspaceFileSearchResult,
  WorkspaceGitAction,
  WorkspaceGitActionCompleted,
  WorkspaceGitCommit,
  WorkspaceGitCommitMessageGenerated,
  WorkspaceGitEntry,
  WorkspaceGitHistorySnapshot,
  WorkspaceGitSnapshot,
} from '@/types/bridge'
import { t } from '@/i18n'

interface WorkspaceTreeNode extends WorkspaceFileEntry {
  children: WorkspaceTreeNode[]
  loaded: boolean
}

interface TreeNodeStat {
  open: boolean
}

type GitDecorationTone = 'added' | 'modified' | 'deleted' | 'renamed' | 'conflict'

interface GitDecoration {
  badge: string | null
  tone: GitDecorationTone
  tooltip: string
}

interface FolderGitAggregate {
  entries: Set<string>
  statuses: Map<string, number>
  tone: GitDecorationTone
}

const props = defineProps<{
  workingDirectory: string | null
  directoryUpdate: WorkspaceDirectoryListing | null
  searchUpdate: WorkspaceFileSearchResult | null
  gitUpdate: WorkspaceGitSnapshot | null
  gitHistoryUpdate?: WorkspaceGitHistorySnapshot | null
  gitHistoryLoading?: boolean
  gitActionResult?: WorkspaceGitActionCompleted | null
  gitPendingAction?: WorkspaceGitAction | null
  gitCommitMessageResult?: WorkspaceGitCommitMessageGenerated | null
  gitCommitMessageLoading?: boolean
  taskActive?: boolean
  taskId: string | null
  taskTitle: string | null
  selectedModel: PiModelInfo | null
  selectedModelReference: string
  sessionModelReference: string | null
  sessionUpdate: SessionStatisticsSnapshot | null
  sessionLoading: boolean
  sessionManualLoadAvailable: boolean
  activeTab: 'git' | 'files' | 'context'
  width: number
}>()

const emit = defineEmits<{
  loadDirectory: [requestId: string, relativePath: string]
  search: [requestId: string, query: string, includeIgnored: boolean]
  reveal: [entry: WorkspaceFileEntry]
  selectTab: [tab: 'git' | 'files' | 'context']
  refreshGit: []
  refreshGitHistory: [append: boolean]
  refreshSession: []
  openGitDiff: [entry: WorkspaceGitEntry]
  openGitCommit: [commit: WorkspaceGitCommit]
  stageGit: [paths: string[]]
  unstageGit: [paths: string[]]
  commitGit: [message: string]
  generateGitCommitMessage: []
  switchGitBranch: [branch: string]
  createGitBranch: [branch: string]
  updateGitBranch: [strategy: 'merge' | 'rebase', sourceBranch: string]
  abortGitOperation: []
  beginResize: [event: PointerEvent]
  setWidth: [width: number]
}>()

const query = ref('')
const includeIgnored = ref(false)
const searchOptionsOpen = ref(false)
const treeNodes = ref<WorkspaceTreeNode[]>([])
const displayedNodes = ref<WorkspaceTreeNode[]>([])
const openPaths = new Set<string>()
const pendingDirectories = new Map<string, string>()
const rootRequestId = ref<string | null>(null)
const searchRequestId = ref<string | null>(null)
const loadingRoot = ref(false)
const searching = ref(false)
const error = ref<string | null>(null)
const searchTruncated = ref(false)
const contextMenu = ref<{ x: number; y: number; entry: WorkspaceFileEntry } | null>(null)
let requestSequence = 0
let searchTimer = 0

const hasWorkspace = computed(() => Boolean(props.workingDirectory))
const isSearchMode = computed(() => query.value.trim().length > 0)
const emptyText = computed(() => {
  if (!hasWorkspace.value) return t('选择工作目录后显示文件')
  if (loadingRoot.value || searching.value) return t(isSearchMode.value ? '正在搜索文件…' : '正在读取目录…')
  if (error.value) return error.value
  return t(isSearchMode.value ? '没有匹配的文件' : '目录为空')
})
const gitEntriesByPath = computed(() => {
  const entries = new Map<string, WorkspaceGitEntry>()
  if (!props.gitUpdate ||
      props.gitUpdate.workingDirectory !== props.workingDirectory ||
      !props.gitUpdate.isRepository ||
      props.gitUpdate.error) return entries
  for (const entry of props.gitUpdate.entries) {
    entries.set(normalizeGitPath(entry.relativePath), entry)
  }
  return entries
})
const folderGitAggregates = computed(() => {
  const aggregates = new Map<string, FolderGitAggregate>()
  if (!props.gitUpdate ||
      props.gitUpdate.workingDirectory !== props.workingDirectory ||
      !props.gitUpdate.isRepository ||
      props.gitUpdate.error) return aggregates

  for (const entry of props.gitUpdate.entries) {
    const entryKey = `${entry.status}:${normalizeGitPath(entry.relativePath)}`
    const status = gitStatusText(entry)
    const tone = gitTone(entry)
    const paths = new Set([
      normalizeGitPath(entry.relativePath),
      ...(entry.originalRelativePath ? [normalizeGitPath(entry.originalRelativePath)] : []),
    ])
    const ancestors = new Set<string>()
    for (const path of paths) {
      const segments = path.split('/').filter(Boolean)
      for (let index = 1; index < segments.length; index++) {
        ancestors.add(segments.slice(0, index).join('/'))
      }
    }
    for (const ancestor of ancestors) {
      const aggregate = aggregates.get(ancestor) ?? {
        entries: new Set<string>(),
        statuses: new Map<string, number>(),
        tone,
      }
      if (!aggregate.entries.has(entryKey)) {
        aggregate.entries.add(entryKey)
        aggregate.statuses.set(status, (aggregate.statuses.get(status) ?? 0) + 1)
      }
      if (gitTonePriority(tone) > gitTonePriority(aggregate.tone)) aggregate.tone = tone
      aggregates.set(ancestor, aggregate)
    }
  }
  return aggregates
})

watch(
  () => props.workingDirectory,
  () => props.activeTab === 'files' ? resetAndRefresh() : resetFileState(),
  { immediate: true },
)

watch(
  () => props.activeTab,
  tab => {
    closeContextMenu()
    if (tab === 'files' && !treeNodes.value.length && props.workingDirectory) resetAndRefresh()
  },
)

watch(
  () => props.directoryUpdate,
  update => {
    if (!update || update.workingDirectory !== props.workingDirectory) return
    if (update.requestId === rootRequestId.value && update.relativePath === '') {
      loadingRoot.value = false
      error.value = update.error
      treeNodes.value = update.error ? [] : update.entries.map(toTreeNode)
      if (!isSearchMode.value) displayedNodes.value = treeNodes.value
      return
    }

    const relativePath = pendingDirectories.get(update.requestId)
    if (relativePath === undefined || relativePath !== update.relativePath) return
    pendingDirectories.delete(update.requestId)
    if (update.error) {
      error.value = update.error
      openPaths.delete(relativePath)
      return
    }

    const node = findNode(treeNodes.value, relativePath)
    if (!node) return
    node.children = update.entries.map(toTreeNode)
    node.loaded = true
    treeNodes.value = [...treeNodes.value]
    if (!isSearchMode.value) displayedNodes.value = treeNodes.value
  },
)

watch(
  () => props.searchUpdate,
  update => {
    if (!update || update.requestId !== searchRequestId.value || update.workingDirectory !== props.workingDirectory) return
    searching.value = false
    error.value = update.error
    searchTruncated.value = update.truncated
    displayedNodes.value = update.error ? [] : update.entries.map(entry => ({ ...toTreeNode(entry), loaded: true }))
  },
)

watch([query, includeIgnored], ([value, shouldIncludeIgnored]) => {
  if (searchTimer) window.clearTimeout(searchTimer)
  error.value = null
  searchTruncated.value = false
  const normalized = value.trim()
  if (!normalized) {
    searching.value = false
    searchRequestId.value = null
    displayedNodes.value = treeNodes.value
    return
  }

  searching.value = true
  searchTimer = window.setTimeout(() => {
    const requestId = nextRequestId('search')
    searchRequestId.value = requestId
    emit('search', requestId, normalized, shouldIncludeIgnored)
  }, 250)
})

onMounted(() => {
  window.addEventListener('click', closeContextMenu)
  window.addEventListener('blur', closeContextMenu)
  window.addEventListener('resize', closeContextMenu)
  window.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  if (searchTimer) window.clearTimeout(searchTimer)
  window.removeEventListener('click', closeContextMenu)
  window.removeEventListener('blur', closeContextMenu)
  window.removeEventListener('resize', closeContextMenu)
  window.removeEventListener('keydown', handleKeydown)
})

function nextRequestId(kind: string) {
  requestSequence += 1
  return `${kind}-${Date.now()}-${requestSequence}`
}

function resetAndRefresh() {
  resetFileState()
  if (!props.workingDirectory) return
  refresh()
}

function resetFileState() {
  query.value = ''
  includeIgnored.value = false
  treeNodes.value = []
  displayedNodes.value = []
  openPaths.clear()
  pendingDirectories.clear()
  error.value = null
  searchTruncated.value = false
  closeContextMenu()
  if (!props.workingDirectory) {
    loadingRoot.value = false
  }
}

function refresh() {
  if (!props.workingDirectory) return
  const requestId = nextRequestId('root')
  rootRequestId.value = requestId
  loadingRoot.value = true
  error.value = null
  treeNodes.value = []
  displayedNodes.value = []
  openPaths.clear()
  pendingDirectories.clear()
  emit('loadDirectory', requestId, '')
}

function refreshFilesAndGit() {
  refresh()
  emit('refreshGit')
}

function setIncludeIgnored(value: boolean) {
  includeIgnored.value = value
  searchOptionsOpen.value = false
}

function forwardGitUpdate(strategy: 'merge' | 'rebase', sourceBranch: string) {
  emit('updateGitBranch', strategy, sourceBranch)
}

function toTreeNode(entry: WorkspaceFileEntry): WorkspaceTreeNode {
  return { ...entry, children: [], loaded: !entry.isDirectory || !entry.hasChildren }
}

function findNode(nodes: WorkspaceTreeNode[], relativePath: string): WorkspaceTreeNode | null {
  for (const node of nodes) {
    if (node.relativePath === relativePath) return node
    const child = findNode(node.children, relativePath)
    if (child) return child
  }
  return null
}

function toggleDirectory(node: WorkspaceTreeNode, stat: TreeNodeStat) {
  if (!node.isDirectory || !node.hasChildren || node.isReparsePoint) return
  if (node.loaded) {
    stat.open = !stat.open
    if (stat.open) openPaths.add(node.relativePath)
    else openPaths.delete(node.relativePath)
    return
  }

  openPaths.add(node.relativePath)
  const requestId = nextRequestId('directory')
  pendingDirectories.set(requestId, node.relativePath)
  emit('loadDirectory', requestId, node.relativePath)
}

function applyOpenState(stat: any) {
  stat.open = Boolean(stat.data && openPaths.has(stat.data.relativePath))
  return stat
}

function isDirectoryLoading(relativePath: string) {
  return [...pendingDirectories.values()].includes(relativePath)
}

function openContextMenu(event: MouseEvent, entry: WorkspaceFileEntry) {
  const width = 190
  const height = 42
  contextMenu.value = {
    x: Math.max(8, Math.min(event.clientX, window.innerWidth - width - 8)),
    y: Math.max(8, Math.min(event.clientY, window.innerHeight - height - 8)),
    entry,
  }
}

function revealContextEntry() {
  if (contextMenu.value) emit('reveal', contextMenu.value.entry)
  closeContextMenu()
}

function closeContextMenu() {
  contextMenu.value = null
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') closeContextMenu()
}

function fileIconKind(entry: WorkspaceFileEntry) {
  if (entry.isDirectory) return 'folder'
  const extension = entry.name.split('.').at(-1)?.toLocaleLowerCase() ?? ''
  if (['md', 'mdx', 'txt', 'rst'].includes(extension)) return 'text'
  if (['cs', 'ts', 'tsx', 'js', 'jsx', 'vue', 'py', 'rs', 'go', 'java', 'json', 'toml', 'yaml', 'yml'].includes(extension)) return 'code'
  return 'file'
}

function normalizeGitPath(path: string) {
  return path.replaceAll('\\', '/').replace(/^\.\/+/u, '').toLocaleLowerCase()
}

function gitStatusBadge(entry: WorkspaceGitEntry) {
  if (entry.isUntracked) return 'U'
  return {
    Added: 'A',
    Modified: 'M',
    Deleted: 'D',
    Renamed: 'R',
    Copied: 'C',
    Unmerged: '!',
  }[entry.kind]
}

function gitStatusText(entry: WorkspaceGitEntry) {
  if (entry.isUntracked) return t('未跟踪')
  return t({
    Added: '已添加',
    Modified: '已修改',
    Deleted: '已删除',
    Renamed: '已重命名',
    Copied: '已复制',
    Unmerged: '有冲突',
  }[entry.kind])
}

function gitTone(entry: WorkspaceGitEntry): GitDecorationTone {
  if (entry.kind === 'Unmerged') return 'conflict'
  if (entry.kind === 'Deleted') return 'deleted'
  if (entry.kind === 'Renamed' || entry.kind === 'Copied') return 'renamed'
  if (entry.isUntracked || entry.kind === 'Added') return 'added'
  return 'modified'
}

function gitTonePriority(tone: GitDecorationTone) {
  return { added: 1, renamed: 2, modified: 3, deleted: 4, conflict: 5 }[tone]
}

function gitStageText(entry: WorkspaceGitEntry) {
  if (entry.isUntracked) return ''
  if (entry.isStaged && entry.isUnstaged) return t('已暂存和未暂存')
  if (entry.isStaged) return t('已暂存')
  if (entry.isUnstaged) return t('未暂存')
  return ''
}

function gitDecoration(entry: WorkspaceFileEntry): GitDecoration | null {
  const path = normalizeGitPath(entry.relativePath)
  if (!entry.isDirectory) {
    const gitEntry = gitEntriesByPath.value.get(path)
    if (!gitEntry) return null
    const stage = gitStageText(gitEntry)
    return {
      badge: gitStatusBadge(gitEntry),
      tone: gitTone(gitEntry),
      tooltip: stage
        ? t('Git：{status} · {stage}', { status: gitStatusText(gitEntry), stage })
        : t('Git：{status}', { status: gitStatusText(gitEntry) }),
    }
  }

  const aggregate = folderGitAggregates.value.get(path)
  if (!aggregate) return null
  const summary = [...aggregate.statuses.entries()]
    .map(([status, count]) => `${count} ${status}`)
    .join(t('、'))
  return {
    badge: null,
    tone: aggregate.tone,
    tooltip: t('包含 {count} 个 Git 变更：{summary}', {
      count: aggregate.entries.size,
      summary,
    }),
  }
}

function fileTreeRowTitle(entry: WorkspaceFileEntry) {
  const lines = [entry.relativePath]
  if (entry.isIgnored) {
    lines.push(t('已忽略 · {source}', { source: ignoreSourceText(entry.ignoreSource) }))
  }
  const decoration = gitDecoration(entry)
  if (decoration) lines.push(decoration.tooltip)
  return lines.join('\n')
}

function ignoreSourceText(source: string | null) {
  return source === 'built-in' ? t('内置规则') : source ?? t('忽略规则')
}
</script>

<template>
  <aside class="workspace-inspector" :aria-label="t('工作区侧栏')">
    <header class="inspector-tabs">
      <UiButton type="button" :class="{ active: activeTab === 'git' }" :aria-current="activeTab === 'git' ? 'page' : undefined" @click="emit('selectTab', 'git')">Git</UiButton>
      <UiButton type="button" :class="{ active: activeTab === 'files' }" :aria-current="activeTab === 'files' ? 'page' : undefined" @click="emit('selectTab', 'files')">{{ t('文件') }}</UiButton>
      <UiButton type="button" :class="{ active: activeTab === 'context' }" :aria-current="activeTab === 'context' ? 'page' : undefined" @click="emit('selectTab', 'context')">{{ t('上下文') }}</UiButton>
    </header>

    <WorkspaceGitPanel
      v-if="activeTab === 'git'"
      :working-directory="workingDirectory"
      :update="gitUpdate"
      :history-update="gitHistoryUpdate ?? null"
      :history-loading="gitHistoryLoading ?? false"
      :action-result="gitActionResult ?? null"
      :pending-action="gitPendingAction ?? null"
      :commit-message-result="gitCommitMessageResult ?? null"
      :commit-message-loading="gitCommitMessageLoading ?? false"
      :task-active="taskActive"
      @refresh="emit('refreshGit')"
      @refresh-history="emit('refreshGitHistory', $event)"
      @open-diff="emit('openGitDiff', $event)"
      @open-commit="emit('openGitCommit', $event)"
      @stage="emit('stageGit', $event)"
      @unstage="emit('unstageGit', $event)"
      @commit="emit('commitGit', $event)"
      @generate-commit-message="emit('generateGitCommitMessage')"
      @switch-branch="emit('switchGitBranch', $event)"
      @create-branch="emit('createGitBranch', $event)"
      @update-branch="forwardGitUpdate"
      @abort-operation="emit('abortGitOperation')"
    />

    <section v-else-if="activeTab === 'files'" class="file-panel">
      <div class="file-toolbar">
        <label class="file-search">
          <svg viewBox="0 0 20 20" aria-hidden="true"><circle cx="8.5" cy="8.5" r="5" /><path d="m12.2 12.2 4 4" /></svg>
          <UiInput v-model="query" type="search" :placeholder="t('搜索文件…')" :disabled="!hasWorkspace" />
        </label>
        <UiMenu
          v-model="searchOptionsOpen"
          class="file-search-options"
          content-class="file-search-options-menu"
          :aria-label="t('更多搜索选项')"
          align="end"
        >
          <template #trigger>
            <UiButton
              class="file-search-options-trigger"
              type="button"
              :title="t('更多搜索选项')"
              :aria-label="t('更多搜索选项')"
              :disabled="!hasWorkspace"
            >
              <svg viewBox="0 0 20 20" aria-hidden="true">
                <circle cx="4" cy="10" r="1" />
                <circle cx="10" cy="10" r="1" />
                <circle cx="16" cy="10" r="1" />
              </svg>
            </UiButton>
          </template>
          <UiMenuItem
            role="menuitemradio"
            :aria-checked="!includeIgnored"
            @select="setIncludeIgnored(false)"
          >
            <span class="file-search-option-check" aria-hidden="true">{{ includeIgnored ? '' : '✓' }}</span>
            <span>{{ t('排除忽略项') }}</span>
          </UiMenuItem>
          <UiMenuItem
            role="menuitemradio"
            :aria-checked="includeIgnored"
            @select="setIncludeIgnored(true)"
          >
            <span class="file-search-option-check" aria-hidden="true">{{ includeIgnored ? '✓' : '' }}</span>
            <span>{{ t('包含忽略项') }}</span>
          </UiMenuItem>
        </UiMenu>
        <UiButton type="button" :title="t('刷新文件和 Git 状态')" :aria-label="t('刷新文件和 Git 状态')" :disabled="!hasWorkspace || loadingRoot" @click="refreshFilesAndGit">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M16 7a6 6 0 1 0 .2 5M16 3v4h-4" /></svg>
        </UiButton>
      </div>

      <div class="file-tree-region" :aria-busy="loadingRoot || searching">
        <BaseTree
          v-if="displayedNodes.length"
          v-model="displayedNodes"
          class="workspace-file-tree"
          virtualization
          :virtualization-prerender-count="30"
          :default-open="false"
          :indent="15"
          :node-key="(stat: { data: WorkspaceTreeNode }) => stat.data.relativePath"
          :stat-handler="applyOpenState"
          :aria-label="t('工作区文件')"
        >
          <template #default="{ node, stat }">
            <div
              class="file-tree-row"
              :class="[
                { directory: node.isDirectory, loading: isDirectoryLoading(node.relativePath), ignored: node.isIgnored },
                gitDecoration(node) ? `git-${gitDecoration(node)?.tone}` : '',
              ]"
              :title="fileTreeRowTitle(node)"
              @click="toggleDirectory(node, stat)"
              @contextmenu.prevent.stop="openContextMenu($event, node)"
            >
              <span class="tree-chevron" :class="{ open: stat.open, hidden: !node.hasChildren || node.isReparsePoint }">
                <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m6 3 5 5-5 5" /></svg>
              </span>
              <span class="file-kind-icon" :class="fileIconKind(node)">
                <svg v-if="node.isDirectory" viewBox="0 0 20 20" aria-hidden="true"><path d="M2.5 5.5h6l1.5 2h7.5v8.5h-15z" /></svg>
                <svg v-else viewBox="0 0 20 20" aria-hidden="true"><path d="M4 2.5h7l5 5v10H4z" /><path d="M11 2.5v5h5" /></svg>
              </span>
              <span class="file-name">{{ node.name }}</span>
              <span v-if="isSearchMode || node.isIgnored || gitDecoration(node)" class="file-tree-meta">
                <span v-if="isSearchMode" class="file-relative-path">{{ node.relativePath }}</span>
                <span
                  v-if="node.isIgnored"
                  class="file-ignore-decoration"
                  :aria-label="t('已忽略 · {source}', { source: ignoreSourceText(node.ignoreSource) })"
                >{{ t('忽略') }}</span>
                <span
                  v-if="gitDecoration(node)?.badge"
                  class="file-git-decoration"
                  :class="gitDecoration(node)?.tone"
                  :aria-label="gitDecoration(node)?.tooltip"
                >{{ gitDecoration(node)?.badge }}</span>
                <span
                  v-else-if="gitDecoration(node)"
                  class="folder-git-decoration"
                  :class="gitDecoration(node)?.tone"
                  :aria-label="gitDecoration(node)?.tooltip"
                ></span>
              </span>
            </div>
          </template>
        </BaseTree>

        <div v-else class="file-tree-empty" :class="{ error: error }">
          <span v-if="loadingRoot || searching" class="file-loading-spinner"></span>
          <span>{{ emptyText }}</span>
          <UiButton v-if="error && hasWorkspace" type="button" @click="refresh">{{ t('重试') }}</UiButton>
        </div>
      </div>

      <footer v-if="searchTruncated" class="file-search-note">{{ t('结果较多，仅显示前 200 项') }}</footer>
    </section>

    <ContextSessionPanel
      v-else
      :task-id="taskId"
      :task-title="taskTitle"
      :selected-model="selectedModel"
      :selected-model-reference="selectedModelReference"
      :session-model-reference="sessionModelReference"
      :update="sessionUpdate"
      :loading="sessionLoading"
      :manual-load-available="sessionManualLoadAvailable"
      @refresh="emit('refreshSession')"
    />

    <div
      class="sidebar-resizer inspector-resizer"
      role="separator"
      :aria-label="t('调整右侧栏宽度')"
      aria-orientation="vertical"
      :aria-valuemin="300"
      :aria-valuemax="560"
      :aria-valuenow="width"
      tabindex="0"
      @pointerdown="emit('beginResize', $event)"
      @dblclick="emit('setWidth', 340)"
      @keydown.left.prevent="emit('setWidth', width + 12)"
      @keydown.right.prevent="emit('setWidth', width - 12)"
    ></div>

    <Teleport to="body">
      <div
        v-if="contextMenu"
        class="workspace-file-context-menu"
        role="menu"
        :style="{ left: `${contextMenu.x}px`, top: `${contextMenu.y}px` }"
        @click.stop
      >
        <UiButton type="button" role="menuitem" @click="revealContextEntry">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M2.5 5.5h6l1.5 2h7.5v8.5h-15z" /></svg>
          {{ t('在资源管理器中显示') }}
        </UiButton>
      </div>
    </Teleport>
  </aside>
</template>
