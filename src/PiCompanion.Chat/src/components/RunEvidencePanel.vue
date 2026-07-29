<script setup lang="ts">
import { computed, ref } from 'vue'
import { UiButton } from '@/components/ui'
import type { FileChangeEvidence, RunEvidence, TaskRunSnapshot } from '@/types/bridge'
import { t } from '@/i18n'

const props = withDefaults(defineProps<{
  run: TaskRunSnapshot
  taskActive: boolean
  expandedByDefault?: boolean
}>(), { expandedByDefault: false })

defineEmits<{
  openDiff: [file: FileChangeEvidence]
  requestRecovery: [file: FileChangeEvidence]
}>()

const expanded = ref(props.expandedByDefault)

const evidence = computed<RunEvidence>(() => props.run.evidence ?? {
  runId: props.run.id,
  finalized: false,
  isGitRepository: false,
  gitRoot: null,
  headBefore: null,
  headAfter: null,
  testStatus: 'NotRun',
  files: [],
  commands: [],
  tests: [],
  warnings: [],
})

const visibleFiles = computed(() => evidence.value.files.filter(file => file.confidence !== 'PreExisting'))
const hiddenWarningCodes = new Set(['git-dirty-baseline', 'shell-coverage'])
const detailWarnings = computed(() => evidence.value.warnings.filter(warning => !hiddenWarningCodes.has(warning.code)))

const visible = computed(() => (
  visibleFiles.value.length > 0
  || detailWarnings.value.length > 0
  || evidence.value.testStatus !== 'NotRun'
))

const title = computed(() => {
  if (visibleFiles.value.length > 0) return t('文件变更')
  if (evidence.value.testStatus !== 'NotRun') return t('验证结果')
  return t('运行详情')
})

const subtitle = computed(() => {
  if (visibleFiles.value.length > 0) return t('{count} 个文件', { count: visibleFiles.value.length })
  return ''
})

function testStatusLabel(status: string) {
  const label = { Passed: '测试通过', Failed: '测试失败', NotRun: '无测试记录', Unknown: '测试状态未知' }[status]
  return label ? t(label) : status
}

function evidenceTone(status: string) {
  if (status === 'Passed' || status === 'Available' || status === 'Recovered') return 'success'
  if (status === 'Failed' || status === 'Conflict') return 'danger'
  if (status === 'Unknown') return 'waiting'
  return 'idle'
}

function changeKindLabel(kind: string) {
  const label = { Added: '新增', Modified: '修改', Deleted: '删除', Renamed: '重命名', Unknown: '变化' }[kind]
  return label ? t(label) : kind
}

function fileOriginLabel(file: FileChangeEvidence) {
  const label = {
    Confirmed: 'Agent 修改',
    Observed: '',
    PreExisting: '运行前已有',
    Unknown: '来源未确认',
  }[file.confidence] ?? '文件变化'
  return label ? t(label) : ''
}

function fileOriginDetail(file: FileChangeEvidence) {
  const source = t({
    PiEditPatch: '由 Pi 编辑工具提供修改补丁',
    BackupComparison: '根据修改前备份与当前文件生成差异',
    GitDiff: '根据 Git 工作区生成差异',
    FileSystemWatcher: '由文件系统监测发现变化',
  }[file.source] ?? '根据本轮运行记录确认变化')
  return t('{origin}：{source}', { origin: fileOriginLabel(file), source })
}

function recoveryLabel(status: string) {
  const label = { Available: '恢复', Conflict: '有冲突', Recovered: '已恢复', Unavailable: '不可恢复' }[status]
  return label ? t(label) : status
}

</script>

<template>
  <section v-if="visible" class="evidence-panel">
    <header class="evidence-header">
      <UiButton
        class="evidence-toggle"
        type="button"
        :aria-expanded="expanded"
        @click="expanded = !expanded"
      >
        <span class="evidence-chevron" aria-hidden="true">›</span>
        <strong>{{ title }}</strong>
        <span v-if="subtitle">{{ subtitle }}</span>
      </UiButton>
      <span v-if="evidence.testStatus !== 'NotRun'" class="evidence-status" :class="evidenceTone(evidence.testStatus)">
        {{ testStatusLabel(evidence.testStatus) }}
      </span>
    </header>

    <div v-if="expanded && visibleFiles.length" class="evidence-section">
      <div class="file-change-list">
        <div v-for="file in visibleFiles" :key="file.id" class="file-change-row">
          <UiButton
            class="file-diff-button"
            type="button"
            :disabled="file.isBinary || !file.hasDiff"
            :title="file.path"
            @click="$emit('openDiff', file)"
          >
            <span class="change-kind" :class="file.kind.toLowerCase()">{{ changeKindLabel(file.kind) }}</span>
            <span class="file-change-copy"><strong>{{ file.relativePath }}</strong><small v-if="fileOriginLabel(file)" :title="fileOriginDetail(file)">{{ fileOriginLabel(file) }}</small></span>
            <span v-if="!file.isBinary && file.hasDiff" class="file-diff-stats" :aria-label="t('文本变更统计')">
              <b>+{{ file.addedLines }}</b><i>-{{ file.deletedLines }}</i>
            </span>
            <span v-else class="file-diff-action">{{ t(file.isBinary ? '二进制' : '无文本 Diff') }}</span>
          </UiButton>
          <UiButton
            class="restore-file-button"
            type="button"
            :class="evidenceTone(file.recovery)"
            :disabled="file.recovery !== 'Available' || taskActive"
            :title="file.recoveryMessage ?? ''"
            @click="$emit('requestRecovery', file)"
          >{{ recoveryLabel(file.recovery) }}</UiButton>
        </div>
      </div>
    </div>

    <ul v-if="expanded && detailWarnings.length" class="evidence-warnings">
      <li v-for="warning in detailWarnings" :key="warning.code">{{ warning.message }}</li>
    </ul>
  </section>
</template>
