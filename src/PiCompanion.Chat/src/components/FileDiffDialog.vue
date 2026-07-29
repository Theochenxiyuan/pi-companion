<script setup lang="ts">
import { computed } from 'vue'
import { UiButton, UiDialog } from '@/components/ui'
import type { FileDiffEvidence } from '@/types/bridge'
import { getDiffStats, parseUnifiedDiff } from '@/utils/unifiedDiff'
import { t } from '@/i18n'

const props = defineProps<{ diff: FileDiffEvidence }>()
defineEmits<{ close: [] }>()

const lines = computed(() => parseUnifiedDiff(props.diff.diffText ?? ''))
const stats = computed(() => getDiffStats(lines.value))
const displayName = computed(() => props.diff.path.replace(/[\\/]+$/, '').split(/[\\/]/).at(-1) || props.diff.path)
</script>

<template>
  <UiDialog
    :title="t('文件 Diff')"
    overlay-class="dialog-backdrop diff-backdrop"
    content-class="diff-dialog"
    @close="$emit('close')"
  >
      <header>
        <div><strong>{{ displayName }}</strong><small>{{ diff.path }}</small></div>
        <UiButton type="button" :aria-label="t('关闭 Diff')" @click="$emit('close')">×</UiButton>
      </header>
      <div class="diff-meta">
        <span class="diff-stat added">+{{ stats.added }}</span>
        <span class="diff-stat removed">-{{ stats.removed }}</span>
        <span v-if="diff.truncated">{{ t('Diff 已按大小截断') }}</span>
      </div>
      <div v-if="diff.isBinary" class="binary-diff">
        {{ t(diff.source.startsWith('WorkspaceGit') ? '二进制文件不提供文本 Diff。' : '二进制文件不提供文本 Diff；恢复仍使用字节级备份。') }}
      </div>
      <div v-else-if="diff.diffText" class="unified-diff" role="table" :aria-label="t('文本修改记录')">
        <div v-for="line in lines" :key="line.key" class="diff-line" :class="line.kind" role="row">
          <span class="diff-line-number old" aria-hidden="true">{{ line.oldLine ?? '' }}</span>
          <span class="diff-line-number new" aria-hidden="true">{{ line.newLine ?? '' }}</span>
          <span class="diff-marker" aria-hidden="true">{{ line.marker }}</span>
          <code>{{ line.content }}</code>
        </div>
      </div>
      <div v-else class="binary-diff">{{ t('没有可显示的文本 Diff。') }}</div>
  </UiDialog>
</template>
