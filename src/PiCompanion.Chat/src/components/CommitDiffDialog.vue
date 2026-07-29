<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { UiButton, UiDialog, UiInput } from '@/components/ui'
import type { WorkspaceGitCommitDiff, WorkspaceGitCommitFileDiff } from '@/types/bridge'
import { getDiffStats, parseUnifiedDiff } from '@/utils/unifiedDiff'
import { t } from '@/i18n'

const props = defineProps<{ diff: WorkspaceGitCommitDiff }>()
defineEmits<{ close: [] }>()

const query = ref('')
const selectedPath = ref(props.diff.files[0]?.relativePath ?? '')

const normalizedQuery = computed(() => query.value.trim().toLocaleLowerCase())
const filteredFiles = computed(() => {
  if (!normalizedQuery.value) return props.diff.files
  return props.diff.files.filter((file) =>
    file.relativePath.toLocaleLowerCase().includes(normalizedQuery.value) ||
    file.originalRelativePath?.toLocaleLowerCase().includes(normalizedQuery.value))
})
const selectedFile = computed(() =>
  props.diff.files.find((file) => file.relativePath === selectedPath.value) ??
  filteredFiles.value[0] ??
  null)
const selectedIndex = computed(() =>
  selectedFile.value ? props.diff.files.indexOf(selectedFile.value) : -1)
const lines = computed(() => parseUnifiedDiff(selectedFile.value?.diffText ?? ''))
const selectedStats = computed(() => getDiffStats(lines.value))
const totalStats = computed(() => props.diff.files.reduce(
  (total, file) => ({
    added: total.added + (file.addedLines ?? 0),
    deleted: total.deleted + (file.deletedLines ?? 0),
  }),
  { added: 0, deleted: 0 },
))

watch(
  () => props.diff.hash,
  () => {
    query.value = ''
    selectedPath.value = props.diff.files[0]?.relativePath ?? ''
  },
)
watch(filteredFiles, (files) => {
  if (!files.some((file) => file.relativePath === selectedPath.value)) {
    selectedPath.value = files[0]?.relativePath ?? ''
  }
})

function fileName(path: string) {
  return path.split('/').at(-1) || path
}

function directoryName(path: string) {
  const separator = path.lastIndexOf('/')
  return separator > 0 ? path.slice(0, separator) : ''
}

function statusLabel(status: WorkspaceGitCommitFileDiff['status']) {
  return t({
    Added: '新增',
    Modified: '修改',
    Deleted: '删除',
    Renamed: '重命名',
    Copied: '复制',
  }[status])
}

function selectOffset(offset: number) {
  const target = props.diff.files[selectedIndex.value + offset]
  if (target) selectedPath.value = target.relativePath
}
</script>

<template>
  <UiDialog
    :title="t('提交 Diff')"
    overlay-class="dialog-backdrop diff-backdrop"
    content-class="diff-dialog commit-diff-dialog"
    @close="$emit('close')"
  >
      <header>
        <div>
          <strong>{{ diff.subject || t('无提交标题') }}</strong>
          <small><code>{{ diff.shortHash }}</code> · {{ t('{count} 个文件', { count: diff.files.length }) }}</small>
        </div>
        <UiButton type="button" :aria-label="t('关闭 Diff')" @click="$emit('close')">×</UiButton>
      </header>

      <div class="diff-meta">
        <span class="diff-stat added">+{{ totalStats.added }}</span>
        <span class="diff-stat removed">-{{ totalStats.deleted }}</span>
        <span v-if="diff.truncated">{{ t('部分文件 Diff 已按大小截断') }}</span>
      </div>

      <div class="commit-diff-layout">
        <aside class="commit-diff-sidebar">
          <label class="commit-diff-search">
            <span>{{ t('搜索提交文件') }}</span>
            <UiInput
              v-model="query"
              type="search"
              :placeholder="t('搜索文件…')"
              :aria-label="t('搜索提交文件')"
            />
          </label>
          <nav :aria-label="t('提交文件列表')">
            <UiButton
              v-for="file in filteredFiles"
              :key="`${file.originalRelativePath ?? ''}:${file.relativePath}`"
              type="button"
              :class="{ selected: file.relativePath === selectedFile?.relativePath }"
              :aria-pressed="file.relativePath === selectedFile?.relativePath"
              :title="file.relativePath"
              @click="selectedPath = file.relativePath"
            >
              <span class="commit-file-status" :class="file.status.toLocaleLowerCase()">
                {{ statusLabel(file.status) }}
              </span>
              <span class="commit-file-name">
                <strong>{{ fileName(file.relativePath) }}</strong>
                <small v-if="directoryName(file.relativePath)">{{ directoryName(file.relativePath) }}</small>
              </span>
              <span v-if="file.isBinary" class="commit-file-binary">{{ t('二进制') }}</span>
              <span v-else class="commit-file-stats">
                <b>+{{ file.addedLines ?? 0 }}</b><i>-{{ file.deletedLines ?? 0 }}</i>
              </span>
            </UiButton>
            <p v-if="filteredFiles.length === 0">{{ t('没有匹配的提交文件') }}</p>
          </nav>
        </aside>

        <main v-if="selectedFile" class="commit-diff-content">
          <header class="commit-diff-file-header">
            <div>
              <strong>{{ selectedFile.relativePath }}</strong>
              <small v-if="selectedFile.originalRelativePath">
                {{ selectedFile.originalRelativePath }} → {{ selectedFile.relativePath }}
              </small>
              <small v-else>{{ statusLabel(selectedFile.status) }}</small>
            </div>
            <div class="commit-diff-file-actions">
              <span v-if="!selectedFile.isBinary">
                <b>+{{ selectedStats.added }}</b><i>-{{ selectedStats.removed }}</i>
              </span>
              <UiButton
                type="button"
                :disabled="selectedIndex <= 0"
                :aria-label="t('上一个文件')"
                :title="t('上一个文件')"
                @click="selectOffset(-1)"
              >↑</UiButton>
              <UiButton
                type="button"
                :disabled="selectedIndex < 0 || selectedIndex >= diff.files.length - 1"
                :aria-label="t('下一个文件')"
                :title="t('下一个文件')"
                @click="selectOffset(1)"
              >↓</UiButton>
            </div>
          </header>

          <div v-if="selectedFile.isBinary" class="binary-diff">
            {{ t('二进制文件不提供文本 Diff。') }}
          </div>
          <div
            v-else-if="selectedFile.diffText"
            class="unified-diff commit-unified-diff"
            role="table"
            :aria-label="t('文本修改记录')"
          >
            <div v-for="line in lines" :key="line.key" class="diff-line" :class="line.kind" role="row">
              <span class="diff-line-number old" aria-hidden="true">{{ line.oldLine ?? '' }}</span>
              <span class="diff-line-number new" aria-hidden="true">{{ line.newLine ?? '' }}</span>
              <span class="diff-marker" aria-hidden="true">{{ line.marker }}</span>
              <code>{{ line.content }}</code>
            </div>
          </div>
          <div v-else class="binary-diff">{{ t('没有可显示的文本 Diff。') }}</div>
        </main>
        <div v-else class="commit-diff-empty">{{ t('没有匹配的提交文件') }}</div>
      </div>
  </UiDialog>
</template>
