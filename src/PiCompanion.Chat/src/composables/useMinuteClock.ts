import { onMounted, onUnmounted, ref } from 'vue'

export function useMinuteClock() {
  const now = ref(Date.now())
  let timer = 0

  onMounted(() => {
    timer = window.setInterval(() => {
      now.value = Date.now()
    }, 60_000)
  })

  onUnmounted(() => {
    if (timer) window.clearInterval(timer)
  })

  return now
}
