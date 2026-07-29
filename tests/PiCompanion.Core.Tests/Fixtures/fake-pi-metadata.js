import readline from 'node:readline'

const args = process.argv.slice(2)
const requiredFlags = [
  '--mode', 'rpc',
  '--no-session',
  '--no-tools',
  '--no-extensions',
  '--no-prompt-templates',
  '--no-context-files',
  '--system-prompt',
  '--thinking', 'off',
  '--model', 'fake/metadata-model',
]

function hasRequiredFlags() {
  return requiredFlags.every((flag, index) => {
    if (index > 0 && requiredFlags[index - 1] === '--mode') return true
    if (index > 0 && requiredFlags[index - 1] === '--thinking') return true
    if (index > 0 && requiredFlags[index - 1] === '--model') return true
    if (flag === 'rpc' || flag === 'off' || flag === 'fake/metadata-model') return true
    const position = args.indexOf(flag)
    if (position < 0) return false
    const expectedValue = requiredFlags[index + 1]
    return !expectedValue || expectedValue.startsWith('--') || args[position + 1] === expectedValue
  })
}

function send(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`)
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
let sessionGeneration = 0
let lastPromptSession = 0
input.on('line', line => {
  const command = JSON.parse(line)
  if (!hasRequiredFlags()) {
    send({ id: command.id, type: 'response', success: false, error: 'Missing isolated metadata flags.' })
    return
  }

  if (command.type === 'get_state') {
    send({ id: command.id, type: 'response', success: true, data: { ready: true } })
    return
  }
  if (command.type === 'new_session') {
    sessionGeneration += 1
    send({ id: command.id, type: 'response', success: true })
    return
  }
  if (command.type === 'set_model' || command.type === 'set_thinking_level' || command.type === 'abort') {
    send({ id: command.id, type: 'response', success: true })
    return
  }
  if (command.type !== 'prompt') {
    send({ id: command.id, type: 'response', success: false, error: `Unsupported command: ${command.type}` })
    return
  }
  if (sessionGeneration <= lastPromptSession) {
    send({ id: command.id, type: 'response', success: false, error: 'Metadata prompts must use a fresh session.' })
    return
  }
  lastPromptSession = sessionGeneration
  if (command.message.includes('crash-metadata-worker')) {
    process.exit(23)
    return
  }

  const isTitle = command.message.includes('生成标题')
  const isCommitMessage = command.message.includes('Git 暂存区生成提交信息')
  const isSummaryRewrite = command.message.includes('压缩候选摘要')
  const forceOverlongSummary = command.message.includes('force-overlong-summary')
  if (!isTitle && !isSummaryRewrite && command.message.includes('Analyze the attached screenshot')) {
    const includesQuestionAnswerHistory = command.message.includes('"questionAnswerHistory"')
      && command.message.includes('Which scope should be summarized?')
      && command.message.includes('"answer":"Current run"')
    if (!includesQuestionAnswerHistory) {
      send({ id: command.id, type: 'response', success: false, error: 'Summary prompt omitted question and answer history.' })
      return
    }
  }
  if (!isTitle && !isCommitMessage && (command.message.includes('taskTitle') || !command.message.includes('"agentResult"'))) {
    if (!isSummaryRewrite) {
      send({ id: command.id, type: 'response', success: false, error: 'Summary prompt contains stale task context.' })
      return
    }
  }
  send({ id: command.id, type: 'response', success: true })
  const summary = isSummaryRewrite
    ? '已自然压缩为语义完整的一句话。'
    : forceOverlongSummary
      ? `总结：${'这是包含完整信息但明显超过长度限制的候选摘要，'.repeat(12)}`
      : '总结：AI generated summary.'
  const message = {
    role: 'assistant',
    content: [{
      type: 'text',
      text: isTitle
        ? '标题：“AI generated title。”'
        : isCommitMessage
          ? '提交信息：feat: generate staged commit message'
          : summary,
    }],
    stopReason: 'stop',
  }
  send({ type: 'message_end', message })
  send({ type: 'agent_settled' })
})
