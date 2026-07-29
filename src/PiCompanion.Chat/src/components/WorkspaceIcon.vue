<script setup lang="ts">
import type { WorkspaceColorKey, WorkspaceIconKey } from '@/types/bridge'

withDefaults(defineProps<{
  iconKey?: WorkspaceIconKey
  colorKey?: WorkspaceColorKey
  label?: string
}>(), {
  iconKey: 'folder',
  colorKey: 'blue',
  label: '',
})
</script>

<template>
  <span
    class="workspace-icon-visual"
    :class="`workspace-icon-color-${colorKey}`"
    :title="label || undefined"
    :aria-label="label || undefined"
    :aria-hidden="label ? undefined : 'true'"
  >
    <svg v-if="iconKey === 'code'" viewBox="0 0 20 20">
      <path d="m7.4 5.2-4 4.8 4 4.8M12.6 5.2l4 4.8-4 4.8M11.4 3.8 8.6 16.2" />
    </svg>
    <svg v-else-if="iconKey === 'terminal'" viewBox="0 0 20 20">
      <rect x="2.5" y="3.5" width="15" height="13" rx="2" />
      <path d="m5.5 7 2.8 2.8-2.8 2.8M10.5 12.5h4" />
    </svg>
    <svg v-else-if="iconKey === 'book'" viewBox="0 0 20 20">
      <path d="M3 4.3A2.3 2.3 0 0 1 5.3 2H10v14H5.3A2.3 2.3 0 0 0 3 18.3z" />
      <path d="M17 4.3A2.3 2.3 0 0 0 14.7 2H10v14h4.7a2.3 2.3 0 0 1 2.3 2.3z" />
    </svg>
    <svg v-else-if="iconKey === 'globe'" viewBox="0 0 20 20">
      <circle cx="10" cy="10" r="7.5" />
      <path d="M2.8 10h14.4M10 2.5c2.1 2.1 3.2 4.6 3.2 7.5S12.1 15.4 10 17.5C7.9 15.4 6.8 12.9 6.8 10S7.9 4.6 10 2.5Z" />
    </svg>
    <svg v-else-if="iconKey === 'flask'" viewBox="0 0 20 20">
      <path d="M7 2.5h6M8 2.5v4.4l-4.6 7.7A1.9 1.9 0 0 0 5 17.5h10a1.9 1.9 0 0 0 1.6-2.9L12 6.9V2.5" />
      <path d="M5.7 12h8.6" />
    </svg>
    <svg v-else-if="iconKey === 'database'" viewBox="0 0 20 20">
      <ellipse cx="10" cy="4.5" rx="6.5" ry="2.5" />
      <path d="M3.5 4.5v5c0 1.4 2.9 2.5 6.5 2.5s6.5-1.1 6.5-2.5v-5M3.5 9.5v5c0 1.4 2.9 2.5 6.5 2.5s6.5-1.1 6.5-2.5v-5" />
    </svg>
    <svg v-else-if="iconKey === 'app'" viewBox="0 0 20 20">
      <rect x="2.8" y="2.8" width="5.7" height="5.7" rx="1.2" />
      <rect x="11.5" y="2.8" width="5.7" height="5.7" rx="1.2" />
      <rect x="2.8" y="11.5" width="5.7" height="5.7" rx="1.2" />
      <rect x="11.5" y="11.5" width="5.7" height="5.7" rx="1.2" />
    </svg>
    <svg v-else viewBox="0 0 20 20">
      <path d="M2.5 5.5h5l1.5 2h8.5v7.8a1.7 1.7 0 0 1-1.7 1.7H4.2a1.7 1.7 0 0 1-1.7-1.7z" />
      <path d="M2.5 7.5V4.7A1.7 1.7 0 0 1 4.2 3h3.1l1.6 2h6.9a1.7 1.7 0 0 1 1.7 1.7v.8" />
    </svg>
  </span>
</template>
