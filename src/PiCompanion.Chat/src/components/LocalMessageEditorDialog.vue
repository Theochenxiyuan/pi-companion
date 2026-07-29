<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from 'vue'
import { UiButton, UiDialog, UiTextarea } from '@/components/ui'
import type { ComposerAttachment, LocalQueuedMessage } from '@/types/bridge'
import { useI18n } from '@/i18n'

const { t } = useI18n()
const props = defineProps<{
  item: LocalQueuedMessage
  selectedAttachments?: ComposerAttachment[] | null
}>()
const emit = defineEmits<{
  confirm: [message: string, attachments: string[]]
  cancel: []
  selectAttachments: [attachments: string[]]
}>()

const message = ref(props.item.message)
const attachments = ref<ComposerAttachment[]>([...(props.item.attachments ?? [])])
const input = ref<InstanceType<typeof UiTextarea> | null>(null)

watch(
  () => props.selectedAttachments,
  selected => {
    if (selected) attachments.value = [...selected]
  },
)

function removeAttachment(path: string) {
  attachments.value = attachments.value.filter(attachment => attachment.path !== path)
}

function confirm() {
  const normalized = message.value.trim()
  if (!normalized) return
  emit('confirm', normalized, attachments.value.map(attachment => attachment.path))
}

onMounted(() => {
  void nextTick(() => input.value?.focus())
})
</script>

<template>
  <UiDialog
    :title="t('编辑待发送任务')"
    overlay-class="dialog-backdrop local-message-editor-backdrop"
    content-class="local-message-editor-dialog"
    @close="$emit('cancel')"
  >
      <header>
        <div>
          <h2>{{ t('编辑待发送任务') }}</h2>
        </div>
        <UiButton type="button" :aria-label="t('关闭')" @click="$emit('cancel')">×</UiButton>
      </header>
      <div class="local-message-editor-body">
        <label>
          <span>{{ t('任务内容') }}</span>
          <UiTextarea
            ref="input"
            v-model="message"
            rows="7"
            :placeholder="t('描述要完成的任务')"
            @keydown.ctrl.enter.prevent="confirm"
            @keydown.meta.enter.prevent="confirm"
          ></UiTextarea>
        </label>
        <section class="local-message-editor-attachments">
          <header>
            <div>
              <strong>{{ t('附件') }}</strong>
              <small>{{ t('附件只会随新一轮任务发送。') }}</small>
            </div>
            <UiButton type="button" @click="$emit('selectAttachments', attachments.map(attachment => attachment.path))">{{ t('添加附件') }}</UiButton>
          </header>
          <div v-if="attachments.length" class="local-message-editor-attachment-list">
            <div v-for="attachment in attachments" :key="attachment.path" class="local-message-editor-attachment" :title="attachment.path">
              <b>{{ attachment.kind }}</b>
              <span>{{ attachment.displayName }}</span>
              <UiButton type="button" :aria-label="t('移除附件 {name}', { name: attachment.displayName })" @click="removeAttachment(attachment.path)">×</UiButton>
            </div>
          </div>
          <p v-else class="local-message-editor-empty">{{ t('没有附件') }}</p>
        </section>
        <p v-if="attachments.length" class="local-message-editor-note">{{ t('含附件的项目不能立即调整或定为后续，只能作为新一轮发送。') }}</p>
      </div>
      <footer>
        <UiButton type="button" @click="$emit('cancel')">{{ t('取消') }}</UiButton>
        <UiButton class="primary" type="button" :disabled="!message.trim()" @click="confirm">{{ t('确认') }}</UiButton>
      </footer>
  </UiDialog>
</template>
