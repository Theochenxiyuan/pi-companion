import fs from 'node:fs'
import path from 'node:path'
import readline from 'node:readline'

const args = process.argv.slice(2)
const sessionDirectoryIndex = args.indexOf('--session-dir')
const sessionDirectory = sessionDirectoryIndex >= 0 ? args[sessionDirectoryIndex + 1] : process.cwd()
fs.mkdirSync(sessionDirectory, { recursive: true })
const startCountFile = path.join(sessionDirectory, 'fake-start-count.txt')
const startCount = fs.existsSync(startCountFile) ? Number.parseInt(fs.readFileSync(startCountFile, 'utf8'), 10) || 0 : 0
fs.writeFileSync(startCountFile, String(startCount + 1), 'utf8')
fs.writeFileSync(path.join(sessionDirectory, 'fake-args.json'), JSON.stringify(args), 'utf8')
const sessionFile = path.join(sessionDirectory, 'fake-session.jsonl')
fs.writeFileSync(sessionFile, '{"type":"session","id":"fake-session"}\n', 'utf8')
let streaming = false
let abortMode = false
let ignoreAbortResponse = false
let pendingInteraction = null
const steeringQueue = []
const followUpQueue = []
const textOnlyModel = process.argv.includes('text-only/model')
const entries = []
let leafId = null
let entrySequence = 0
let injectRecoveredEntryAfterRead = false
let currentModel = textOnlyModel
  ? { provider: 'text-only', id: 'model', input: ['text'] }
  : { provider: 'fake', id: 'fake-model', input: ['text', 'image'] }
let currentThinkingLevel = 'medium'

function activeContext() {
  const contextFile = process.env.PI_COMPANION_CONTEXT_FILE
  if (contextFile && fs.existsSync(contextFile)) {
    try {
      return JSON.parse(fs.readFileSync(contextFile, 'utf8'))
    } catch {
    }
  }
  return {
    permissionToken: process.env.PI_COMPANION_PERMISSION_TOKEN,
    readOnlyRoots: process.env.PI_COMPANION_READ_ONLY_ATTACHMENT_ROOT
      ? [process.env.PI_COMPANION_READ_ONLY_ATTACHMENT_ROOT]
      : [],
  }
}

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`)
}

function response(command, success = true, data = undefined) {
  send({ id: command.id, type: 'response', command: command.type, success, ...(data === undefined ? {} : { data }) })
}

function appendAssistantEntry(text, stopReason = 'stop') {
  const id = `entry-${++entrySequence}`
  const message = { role: 'assistant', content: [{ type: 'text', text }], stopReason }
  entries.push({ type: 'message', id, parentId: leafId, timestamp: new Date().toISOString(), message })
  leafId = id
  return message
}

function settle(text = '真实回答', stopReason = 'stop') {
  const message = appendAssistantEntry(text, stopReason)
  send({ type: 'message_end', message })
  streaming = false
  send({ type: 'agent_end', messages: [message] })
  send({ type: 'agent_settled' })
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
input.on('line', (line) => {
  const command = JSON.parse(line)
  fs.appendFileSync(path.join(sessionDirectory, 'fake-command-log.jsonl'), `${JSON.stringify(command)}\n`, 'utf8')
  switch (command.type) {
    case 'get_state':
      response(command, true, {
        model: currentModel,
        thinkingLevel: currentThinkingLevel,
        isStreaming: streaming,
        pendingMessageCount: 0,
        sessionFile,
        sessionId: 'fake-session',
      })
      break
    case 'prompt':
      fs.writeFileSync(path.join(sessionDirectory, 'fake-last-prompt.txt'), command.message, 'utf8')
      fs.writeFileSync(path.join(sessionDirectory, 'fake-last-images.json'), JSON.stringify(
        (command.images || []).map(image => ({ type: image.type, mimeType: image.mimeType, data: image.data })),
      ), 'utf8')
      fs.writeFileSync(
        path.join(sessionDirectory, 'fake-attachment-root.txt'),
        activeContext().readOnlyRoots?.[0] || '',
        'utf8',
      )
      abortMode = command.message.includes('wait-for-abort')
      ignoreAbortResponse = command.message.includes('ignore-abort-response')
      response(command)
      streaming = true
      send({ type: 'agent_start' })
      if (command.message.includes('permission-flow')) {
        pendingInteraction = { id: 'permission-1', kind: 'permission' }
        send({
          type: 'extension_ui_request',
          id: pendingInteraction.id,
          method: 'select',
          title: `[PI_COMPANION_PERMISSION:${activeContext().permissionToken}]\nShell 命令请求\n\ndotnet test`,
          options: ['允许一次', '本任务内允许同类操作', '拒绝'],
        })
      } else if (command.message.includes('custom-question-flow')) {
        pendingInteraction = { id: 'custom-question-1', kind: 'question' }
        send({
          type: 'extension_ui_request',
          id: pendingInteraction.id,
          method: 'select',
          title: '下一步检查什么？',
          options: ['权限策略', '队列状态', '其他…'],
        })
      } else if (command.message.includes('question-flow')) {
        pendingInteraction = { id: 'question-1', kind: 'question' }
        send({
          type: 'extension_ui_request',
          id: pendingInteraction.id,
          method: 'select',
          title: '下一步检查什么？',
          options: ['权限策略', '队列状态'],
        })
      } else if (command.message.includes('edit-evidence')) {
        send({ type: 'tool_execution_start', toolCallId: 'edit-evidence-1', toolName: 'edit', args: { path: 'sample.txt', edits: [{ oldText: 'old', newText: 'new' }] } })
        send({
          type: 'tool_execution_end',
          toolCallId: 'edit-evidence-1',
          toolName: 'edit',
          result: {
            content: [{ type: 'text', text: 'Successfully replaced 1 block(s) in sample.txt.' }],
            details: { diff: '-old\n+new', patch: '--- a/sample.txt\n+++ b/sample.txt\n@@ -1 +1 @@\n-old\n+new\n', firstChangedLine: 1 },
          },
          isError: false,
        })
        settle('编辑完成')
      } else if (command.message.includes('test-failure-evidence')) {
        send({ type: 'tool_execution_start', toolCallId: 'bash-test-1', toolName: 'bash', args: { command: 'dotnet test' } })
        send({
          type: 'tool_execution_end',
          toolCallId: 'bash-test-1',
          toolName: 'bash',
          result: { content: [{ type: 'text', text: '1 test failed\n\nCommand exited with code 1' }], details: {} },
          isError: true,
        })
        settle('测试完成')
      } else if (command.message.includes('long-tool-output')) {
        send({ type: 'tool_execution_start', toolCallId: 'long-output-1', toolName: 'web_search', args: { query: 'large result' } })
        send({
          type: 'tool_execution_end',
          toolCallId: 'long-output-1',
          toolName: 'web_search',
          result: { content: [{ type: 'text', text: 'x'.repeat(30000) }] },
          isError: false,
        })
        settle('搜索完成')
      } else if (command.message.includes('lifecycle-flow')) {
        send({ type: 'compaction_start', reason: 'threshold' })
        send({ type: 'compaction_end', reason: 'threshold', result: { summary: 'summary' }, aborted: false, willRetry: false })
        send({ type: 'summarization_retry_scheduled', attempt: 1, maxAttempts: 3, delayMs: 2000, errorMessage: 'terminated' })
        send({ type: 'summarization_retry_attempt_start', source: 'compaction', reason: 'threshold' })
        send({ type: 'summarization_retry_finished' })
        send({ type: 'auto_retry_start', attempt: 1, maxAttempts: 3, delayMs: 1500, errorMessage: 'rate limited' })
        send({ type: 'auto_retry_end', success: true, attempt: 1 })
        settle('重试后完成')
      } else if (command.message.includes('retry-wait')) {
        send({ type: 'auto_retry_start', attempt: 1, maxAttempts: 3, delayMs: 30000, errorMessage: 'rate limited' })
      } else if (command.message.includes('agent-error-detail')) {
        const message = appendAssistantEntry('', 'error')
        message.errorMessage = 'Provider rejected the request: invalid model configuration.'
        send({ type: 'message_end', message })
        streaming = false
        send({ type: 'agent_end', messages: [message] })
        send({ type: 'agent_settled' })
      } else if (!abortMode) {
        const partial = { role: 'assistant', content: [], stopReason: 'stop' }
        send({ type: 'message_start', message: partial })
        send({
          type: 'message_update',
          message: partial,
          assistantMessageEvent: { type: 'text_delta', contentIndex: 0, delta: '真实回答', partial },
        })
        send({ type: 'tool_execution_start', toolCallId: 'tool-1', toolName: 'read', args: { path: 'README.md' } })
        send({
          type: 'tool_execution_end',
          toolCallId: 'tool-1',
          toolName: 'read',
          result: { content: [{ type: 'text', text: 'read result' }] },
          isError: false,
        })
        const message = appendAssistantEntry('真实回答')
        send({ type: 'message_end', message })
        streaming = false
        send({ type: 'agent_end', messages: [message] })
        if (!command.message.includes('legacy-no-settled')) {
          send({ type: 'agent_settled' })
        }
        if (command.message.includes('seed-reconcile')) injectRecoveredEntryAfterRead = true
      }
      break
    case 'get_session_stats':
      response(command, true, {
        sessionFile,
        sessionId: 'fake-session',
        userMessages: 4,
        assistantMessages: 7,
        toolCalls: 3,
        toolResults: 3,
        totalMessages: 11,
        tokens: { input: 1200, output: 340, cacheRead: 800, cacheWrite: 20, total: 2360 },
        cost: 0.0123,
        contextUsage: { tokens: 2200, contextWindow: 128000, percent: 1.71875 },
      })
      break
    case 'get_entries': {
      const sinceIndex = command.since ? entries.findIndex(entry => entry.id === command.since) : -1
      if (command.since && sinceIndex < 0) {
        response(command, false)
        break
      }
      response(command, true, { entries: entries.slice(sinceIndex + 1), leafId })
      if (injectRecoveredEntryAfterRead) {
        injectRecoveredEntryAfterRead = false
        appendAssistantEntry('从中断窗口恢复的回答')
      }
      break
    }
    case 'abort':
      if (ignoreAbortResponse) break
      response(command)
      streaming = false
      send({
        type: 'agent_end',
        messages: [{ role: 'assistant', content: [], stopReason: 'aborted' }],
      })
      break
    case 'steer':
      steeringQueue.push(command.message)
      response(command)
      send({ type: 'queue_update', steering: steeringQueue, followUp: followUpQueue })
      break
    case 'follow_up':
      followUpQueue.push(command.message)
      response(command)
      send({ type: 'queue_update', steering: steeringQueue, followUp: followUpQueue })
      break
    case 'abort_retry':
      response(command)
      send({ type: 'auto_retry_end', success: false, attempt: 1, finalError: '用户取消了自动重试' })
      break
    case 'switch_session':
      response(command, true, command.type === 'switch_session' ? { cancelled: false } : undefined)
      break
    case 'compact':
      response(command, true, { summary: 'compacted' })
      break
    case 'new_session':
      entries.splice(0, entries.length)
      leafId = null
      response(command, true, { cancelled: false })
      break
    case 'set_model':
      currentModel = { provider: command.provider, id: command.modelId, input: ['text', 'image'] }
      response(command, true, currentModel)
      break
    case 'set_thinking_level':
      currentThinkingLevel = command.level
      response(command)
      break
    case 'set_steering_mode':
    case 'set_follow_up_mode':
      response(command)
      break
    case 'extension_ui_response':
      if (!pendingInteraction || command.id !== pendingInteraction.id) break
      if (pendingInteraction.kind === 'permission') {
        const allowed = !command.cancelled && command.value !== '拒绝'
        if (allowed) {
          send({ type: 'tool_execution_start', toolCallId: 'tool-write-1', toolName: 'bash', args: { command: 'dotnet test' } })
          send({
            type: 'tool_execution_end',
            toolCallId: 'tool-write-1',
            toolName: 'bash',
            result: { content: [{ type: 'text', text: 'tests passed' }] },
            isError: false,
          })
        }
        const outcome = allowed ? `授权结果：${command.value}` : '授权结果：拒绝'
        pendingInteraction = null
        settle(outcome)
      } else {
        const outcome = command.cancelled ? '问题已取消' : `问题回答：${command.value}`
        pendingInteraction = null
        settle(outcome)
      }
      break
    default:
      response(command, false)
      break
  }
})
