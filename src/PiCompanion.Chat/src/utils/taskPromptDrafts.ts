const storageKey = 'pi-companion:task-prompt-drafts:v1'
const maximumDraftCount = 100

interface StoredTaskPromptDraft {
  text: string
  updatedAt: number
}

interface StoredTaskPromptDrafts {
  version: 1
  drafts: Record<string, StoredTaskPromptDraft>
}

function normalizeTaskId(taskId: string) {
  return taskId.trim().toLocaleLowerCase('en-US')
}

function emptyDrafts(): StoredTaskPromptDrafts {
  return { version: 1, drafts: {} }
}

function readDrafts(storage: Storage): StoredTaskPromptDrafts {
  try {
    const raw = storage.getItem(storageKey)
    if (!raw) return emptyDrafts()
    const parsed = JSON.parse(raw) as Partial<StoredTaskPromptDrafts>
    if (parsed.version !== 1 || !parsed.drafts || typeof parsed.drafts !== 'object') {
      return emptyDrafts()
    }

    const drafts: Record<string, StoredTaskPromptDraft> = {}
    for (const [taskId, value] of Object.entries(parsed.drafts)) {
      if (!value || typeof value.text !== 'string' || typeof value.updatedAt !== 'number') continue
      drafts[normalizeTaskId(taskId)] = value
    }
    return { version: 1, drafts }
  } catch {
    return emptyDrafts()
  }
}

function persistDrafts(storage: Storage, value: StoredTaskPromptDrafts) {
  try {
    if (!Object.keys(value.drafts).length) {
      storage.removeItem(storageKey)
      return
    }
    storage.setItem(storageKey, JSON.stringify(value))
  } catch {
    // Draft persistence must never make the composer unusable when WebView
    // storage is unavailable or full.
  }
}

export function loadTaskPromptDraft(taskId: string, storage: Storage = window.localStorage) {
  return readDrafts(storage).drafts[normalizeTaskId(taskId)]?.text ?? ''
}

export function saveTaskPromptDraft(taskId: string, text: string, storage: Storage = window.localStorage) {
  const normalizedTaskId = normalizeTaskId(taskId)
  if (!normalizedTaskId) return

  const stored = readDrafts(storage)
  if (!text) {
    delete stored.drafts[normalizedTaskId]
    persistDrafts(storage, stored)
    return
  }

  stored.drafts[normalizedTaskId] = { text, updatedAt: Date.now() }
  const entries = Object.entries(stored.drafts)
    .sort(([, left], [, right]) => right.updatedAt - left.updatedAt)
    .slice(0, maximumDraftCount)
  stored.drafts = Object.fromEntries(entries)
  persistDrafts(storage, stored)
}

export function clearStoredTaskPromptDrafts(storage: Storage = window.localStorage) {
  try {
    storage.removeItem(storageKey)
  } catch {
    // Best-effort cleanup for tests and future account/data reset flows.
  }
}
