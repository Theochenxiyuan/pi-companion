<script setup lang="ts">
import { ref } from 'vue'
import { UiButton, UiMenu, UiMenuItem } from '@/components/ui'
import { t } from '@/i18n'

defineProps<{
  attachmentsDisabled?: boolean
  skillsDisabled?: boolean
}>()

const emit = defineEmits<{
  selectAttachments: []
  invokeSkill: []
}>()

const open = ref(false)

function selectAttachments() {
  open.value = false
  emit('selectAttachments')
}

function invokeSkill() {
  open.value = false
  emit('invokeSkill')
}
</script>

<template>
  <UiMenu
    v-model="open"
    class="composer-action-menu"
    content-class="composer-add-menu"
    :aria-label="t('添加内容')"
  >
    <template #trigger>
      <UiButton
        class="composer-add-button"
        type="button"
        :aria-label="t('添加内容')"
        :title="t('添加内容')"
        :disabled="attachmentsDisabled && skillsDisabled"
      >
        <svg viewBox="0 0 20 20" aria-hidden="true"><path d="M10 4v12M4 10h12" /></svg>
      </UiButton>
    </template>
      <UiMenuItem :disabled="attachmentsDisabled" @select="selectAttachments">
        <svg viewBox="0 0 20 20" aria-hidden="true">
          <path d="M7.4 10.8 12 6.2a2.1 2.1 0 0 1 3 3l-6.1 6.1a3.5 3.5 0 0 1-5-5l6.4-6.4" />
        </svg>
        <span>{{ t('添加附件') }}</span>
      </UiMenuItem>
      <UiMenuItem :disabled="skillsDisabled" @select="invokeSkill">
        <svg viewBox="0 0 20 20" aria-hidden="true">
          <path d="M7 3h6v4h4v6h-4v4H7v-4H3V7h4z" />
          <path d="M8.5 8.5h3v3h-3z" />
        </svg>
        <span>{{ t('调用技能') }}</span>
      </UiMenuItem>
  </UiMenu>
</template>
