export const bridgeProtocolVersion = 57

export type PermissionMode = 'read-only' | 'standard' | 'full-access'
export type TaskScopeKind = 'Workspace' | 'GeneralChat'

export type PiThinkingLevel = 'off' | 'minimal' | 'low' | 'medium' | 'high' | 'xhigh' | 'max'

export interface GeneralSettings {
  launchAtLogin: boolean
  keepRunningInTray: boolean
  language: 'zh-CN' | 'en-US'
  theme: 'dark' | 'light' | 'system'
  logLevel: 'error' | 'warning' | 'information' | 'debug'
  uiScalePercent: number
  gitAutoRefreshSeconds: 0 | 5 | 10 | 30 | 60
}

export interface MonitorSettings {
  position: 'top-left' | 'top-right' | 'bottom-left' | 'bottom-right' | 'last-position'
  showOnStartup: boolean
  alwaysOnTop: boolean
  autoCollapseSeconds: number
  animationsEnabled: boolean
}

export interface TaskSettings {
  aiTitleEnabled: boolean
  aiTitleModel: string
  aiSummaryEnabled: boolean
  aiSummaryModel: string
  aiMetadataModel: string
  recentTaskCount: number
  recentTaskSubtitle: 'workspace' | 'latest-run'
  permissionMode: PermissionMode
  fileChangesExpandedByDefault: boolean
  completionBehavior: 'keep-expanded' | 'collapse-monitor' | 'show-chat'
  autoStartLocalQueueEnabled: boolean
  autoStartLocalQueueDelaySeconds: 0 | 15 | 30 | 60
}

export interface AgentSettings {
  defaultModel: string
  defaultThinkingLevel: PiThinkingLevel
  autoCompact: boolean
  autoRetry: boolean
  compactionReserveTokens: number
  compactionKeepRecentTokens: number
  retryMaxRetries: number
  retryBaseDelayMilliseconds: number
  retryMaxDelayMilliseconds: number
  steeringMode: 'one-at-a-time' | 'all'
  followUpMode: 'one-at-a-time' | 'all'
}

export interface NotificationSettings {
  notifyOnCompletion: boolean
  notifyOnFailure: boolean
  notifyWhenAttentionRequired: boolean
  playSound: boolean
  onlyWhenAppIsInBackground: boolean
}

export interface DataRetentionSettings {
  taskHistoryDays: number
  recycleBinDays: number
  logDays: number
}

export interface PiCompanionSettings {
  general: GeneralSettings
  monitor: MonitorSettings
  tasks: TaskSettings
  agent: AgentSettings
  notifications: NotificationSettings
  dataRetention: DataRetentionSettings
}

export interface PiProviderInfo {
  id: string
  name: string
  configured: boolean
  authType: 'api_key' | 'oauth' | 'environment' | 'configuration' | null
  authSource: string | null
  supportsApiKey: boolean
  supportsOAuth: boolean
  capabilities?: string[]
}

export interface PiCustomModelInfo {
  id: string
  name: string
  reasoning: boolean
  imageInput: boolean
  contextWindow: number
  maxTokens: number
  supportsDeveloperRole?: boolean
}

export interface PiCustomProviderInfo {
  id: string
  name: string
  baseUrl: string
  api: 'openai-completions' | 'openai-responses' | 'anthropic-messages' | 'google-generative-ai'
  credentialMode: 'api-key' | 'local'
  models: PiCustomModelInfo[]
}

export interface PiModelInfo {
  provider: string
  id: string
  name: string
  reasoning: boolean
  contextWindow: number
  input: string[]
  thinkingLevels: string[]
  api?: string
  webSearchSupport?: 'none' | 'native'
}

export interface PiConfigurationSnapshot {
  available: boolean
  version: string | null
  runtimePath: string | null
  defaultModel: string | null
  defaultThinkingLevel: PiThinkingLevel
  autoCompact: boolean
  autoRetry: boolean
  compactionReserveTokens: number
  compactionKeepRecentTokens: number
  retryMaxRetries: number
  retryBaseDelayMilliseconds: number
  retryMaxDelayMilliseconds: number
  steeringMode: 'one-at-a-time' | 'all'
  followUpMode: 'one-at-a-time' | 'all'
  providers: PiProviderInfo[]
  models: PiModelInfo[]
  enabledModels: string[] | null
  customProviders: PiCustomProviderInfo[]
  modelsConfigRevision: string | null
  error: string | null
}

export interface SettingsSnapshot {
  values: PiCompanionSettings
  pi: PiConfigurationSnapshot
  dataDirectory: string
  logDirectory: string
}

export interface SettingsActionCompleted {
  message: string
  succeeded: boolean
  operation?: 'companion-auto-save' | 'pi-agent-save'
  silent?: boolean
}

export type SkillScope = 'global' | 'workspace'
export type SkillSource = 'pi' | 'agents'
export type SkillLocationStatus = 'missing' | 'empty' | 'loaded' | 'inaccessible'

export interface SkillDiagnostic {
  code: string
  severity: 'warning'
  message: string
  path: string
  winnerPath: string | null
  workspaceId: string | null
  workspaceName: string | null
}

export interface SkillOrigin {
  scope: SkillScope
  source: SkillSource
  rootPath: string
  workspaceId: string | null
  workspaceName: string | null
  workspacePath: string | null
  inherited: boolean
  installPath: string
  isCompatibilityLink: boolean
  linkTarget: string | null
}

export interface SkillInstallation {
  id: string
  filePath: string
  baseDirectory: string
  canonicalPath: string
  installPath: string
  isSingleFile: boolean
  isGloballyEffective: boolean
  effectiveWorkspaceIds: string[]
  origins: SkillOrigin[]
  diagnostics: SkillDiagnostic[]
  removable: boolean
  removalReason: string | null
}

export interface SkillContentVariant {
  id: string
  contentHash: string | null
  description: string | null
  version: string | null
  license: string | null
  metadata: Record<string, string>
  disableModelInvocation: boolean
  isAvailable: boolean
  fileCount: number
  totalSize: number
  lastModifiedAt: string | null
  installations: SkillInstallation[]
}

export interface DiscoveredSkill {
  id: string
  name: string
  variants: SkillContentVariant[]
  diagnostics: SkillDiagnostic[]
}

export interface SkillScanLocation {
  id: string
  scope: SkillScope
  source: SkillSource
  path: string
  status: SkillLocationStatus
  skillCount: number
  workspaceId: string | null
  workspaceName: string | null
  workspacePath: string | null
  inherited: boolean
  message: string | null
}

export interface LoadSkillsRequest {
  requestId: string
}

export interface TrustSkillWorkspaceRequest {
  requestId: string
  workspaceId: string
}

export interface RemoveSkillInstallationRequest {
  requestId: string
  installationId: string
  expectedContentHash: string
  workspaceId?: string
}

export type SkillImportSourceKind = 'folder' | 'zip'

export interface BeginSkillImportRequest {
  requestId: string
  sourceKind: SkillImportSourceKind
}

export interface PrepareSkillImportRequest {
  requestId: string
  sourceToken: string
  targetScope: SkillScope
  workspaceId?: string
}

export interface ConfirmSkillImportRequest {
  requestId: string
  token: string
}

export interface CancelSkillImportRequest {
  requestId: string
  sourceToken?: string
  preparationToken?: string
}

export interface SkillsLoaded {
  requestId: string
  scannedAt: string
  skills: DiscoveredSkill[]
  locations: SkillScanLocation[]
  diagnostics: SkillDiagnostic[]
  workspaceTrust: SkillWorkspaceTrust[]
}

export interface SkillWorkspaceTrust {
  workspaceId: string
  workspaceName: string
  workspacePath: string
  status: 'trusted' | 'declined' | 'undecided'
  decisionPath: string | null
  inherited: boolean
}

export interface SkillRemovalCompleted {
  requestId: string
  succeeded: boolean
  message: string
  removedInstallationId: string | null
  recoveryPath: string | null
  snapshot: SkillsLoaded
}

export interface SkillWorkspaceTrustCompleted {
  requestId: string
  succeeded: boolean
  message: string
  workspaceId: string
  snapshot: SkillsLoaded
}

export interface SkillImportFile {
  relativePath: string
  size: number
  kind: 'file' | 'script' | 'executable'
}

export interface SkillImportSource {
  token: string
  name: string
  description: string | null
  sourceKind: SkillImportSourceKind
  contentHash: string
  fileCount: number
  totalBytes: number
  files: SkillImportFile[]
  scriptFiles: string[]
  executableFiles: string[]
}

export interface SkillImportSourceInspected {
  requestId: string
  succeeded: boolean
  cancelled: boolean
  message: string
  source: SkillImportSource | null
}

export interface SkillImportPreparation {
  token: string
  sourceToken: string
  name: string
  description: string | null
  targetScope: SkillScope
  workspaceId: string | null
  workspaceName: string | null
  targetPath: string
  sourceKind: SkillImportSourceKind
  contentHash: string
  fileCount: number
  totalBytes: number
  files: SkillImportFile[]
  scriptFiles: string[]
  executableFiles: string[]
  requiresProjectTrust: boolean
  trustStatus: 'trusted' | 'declined' | 'undecided' | 'not-required'
}

export interface SkillImportReady {
  requestId: string
  succeeded: boolean
  message: string
  preparation: SkillImportPreparation | null
}

export interface SkillImportCompleted {
  requestId: string
  succeeded: boolean
  cancelled: boolean
  message: string
  skillName: string | null
  targetPath: string | null
  snapshot: SkillsLoaded
}

export interface PiOAuthLoginProgress {
  providerId: string
  phase: 'idle' | 'opening' | 'waiting'
}

export interface BridgeEnvelope<T = unknown> {
  protocolVersion: number
  type: string
  payload: T
}

export interface WorkspaceFileEntry {
  name: string
  relativePath: string
  isDirectory: boolean
  hasChildren: boolean
  isReparsePoint: boolean
  isIgnored: boolean
  ignoreSource: string | null
}

export interface WorkspaceDirectoryListing {
  requestId: string
  workingDirectory: string
  relativePath: string
  entries: WorkspaceFileEntry[]
  inaccessibleEntries: number
  error: string | null
}

export interface WorkspaceFileSearchResult {
  requestId: string
  workingDirectory: string
  query: string
  entries: WorkspaceFileEntry[]
  truncated: boolean
  visitedEntries: number
  inaccessibleEntries: number
  error: string | null
}

export interface WorkspaceGitEntry {
  relativePath: string
  originalRelativePath: string | null
  status: string
  indexStatus: string
  workingTreeStatus: string
  kind: 'Added' | 'Modified' | 'Deleted' | 'Renamed' | 'Copied' | 'Unmerged'
  isStaged: boolean
  isUnstaged: boolean
  isUntracked: boolean
  isBinary: boolean
  addedLines: number
  deletedLines: number
}

export interface WorkspaceGitSnapshot {
  requestId: string
  workingDirectory: string
  isRepository: boolean
  repositoryRoot: string | null
  branch: string | null
  isDetached: boolean
  branches?: WorkspaceGitBranch[]
  operationState?: 'None' | 'Merge' | 'Rebase'
  canManageBranches?: boolean
  entries: WorkspaceGitEntry[]
  stagedFingerprint?: string | null
  error: string | null
}

export interface WorkspaceGitCommitMessageGenerated {
  requestId: string
  workingDirectory: string
  succeeded: boolean
  message: string | null
  stagedFingerprint: string | null
  truncatedInput: boolean
  error: string | null
}

export interface WorkspaceGitBranch {
  name: string
  shortHash: string
  subject: string
  isCurrent: boolean
}

export interface WorkspaceGitCommit {
  hash: string
  shortHash: string
  subject: string
  authorName: string
  authorEmail: string
  timestamp: string
  parents: string[]
}

export interface WorkspaceGitHistorySnapshot {
  requestId: string
  workingDirectory: string
  entries: WorkspaceGitCommit[]
  offset: number
  hasMore: boolean
  error: string | null
}

export interface WorkspaceGitCommitFileDiff {
  relativePath: string
  originalRelativePath: string | null
  status: 'Added' | 'Modified' | 'Deleted' | 'Renamed' | 'Copied'
  addedLines: number | null
  deletedLines: number | null
  diffText: string | null
  isBinary: boolean
  truncated: boolean
}

export interface WorkspaceGitCommitDiff {
  workingDirectory: string
  hash: string
  shortHash: string
  subject: string
  files: WorkspaceGitCommitFileDiff[]
  truncated: boolean
}

export type WorkspaceGitAction =
  | 'stage'
  | 'unstage'
  | 'commit'
  | 'switch-branch'
  | 'create-branch'
  | 'update-branch'
  | 'abort-operation'

export interface WorkspaceGitActionCompleted {
  requestId: string
  workingDirectory: string
  action: WorkspaceGitAction
  succeeded: boolean
  message: string
  detail: string | null
}

export interface AgentSessionStatistics {
  sessionId: string
  sessionFile: string | null
  userMessages: number
  assistantMessages: number
  toolCalls: number
  toolResults: number
  totalMessages: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  totalTokens: number
  cost: number
  contextUsage: {
    tokens: number | null
    contextWindow: number
    percent: number | null
  } | null
}

export interface SessionStatisticsSnapshot {
  requestId: string
  taskId: string | null
  available: boolean
  statistics: AgentSessionStatistics | null
  error: string | null
}

export interface ComposerDraft {
  workingDirectory: string
  prompt: string
  model: string
  thinkingLevel: string
  permissionMode?: PermissionMode
  attachments: ComposerAttachment[]
}

export interface ComposerAttachment {
  path: string
  displayName: string
  kind: string
  isAvailable: boolean
  previewDataUrl?: string | null
}

export interface TaskActivity {
  sequence: number
  kind: string
  text: string
  timestamp: string
}

export type TranscriptBlockKind =
  | 'UserMessage'
  | 'AssistantMessage'
  | 'Thinking'
  | 'Tool'
  | 'WebSearch'
  | 'Interaction'
  | 'Notice'

export type TranscriptBlockStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'

export interface TranscriptBlock {
  id: string
  kind: TranscriptBlockKind
  status: TranscriptBlockStatus
  title: string
  content: string
  firstSequence: number
  lastSequence: number
  timestamp: string
  input: string | null
  output: string | null
  interactionId: string | null
  interactionMethod: string | null
  interactionKind: 'Approval' | 'Question' | null
  interactionOptions: string[]
}

export interface TaskSnapshot {
  id: string
  runId: string
  title: string
  prompt: string
  workingDirectory: string
  scopeKind?: TaskScopeKind
  model: string
  thinkingLevel: string
  permissionMode?: PermissionMode
  attachments: string[]
  artifacts?: TaskArtifact[]
  status: string
  statusText: string
  summary: string
  activityStatus?: string | null
  assistantText: string | null
  finalAnswer: string | null
  lastSequence: number
  pendingSteering: string[]
  pendingFollowUps: string[]
  localQueuedMessages?: LocalQueuedMessage[]
  localQueueAutoStartMessageId?: string | null
  localQueueAutoStartAt?: string | null
  transcript: TranscriptBlock[]
  runs: TaskRunSnapshot[]
  activities: TaskActivity[]
}

export interface LocalQueuedMessage {
  id: string
  message: string
  createdAt: string
  attachments?: ComposerAttachment[]
}

export interface LocalMessageAttachmentsSelected {
  requestId: string
  attachments: ComposerAttachment[]
}

export interface TaskRunSnapshot {
  id: string
  prompt: string
  model: string
  thinkingLevel: string
  messageAttachments: string[]
  status: string
  statusText: string
  summary: string
  activityStatus?: string | null
  assistantText: string | null
  finalAnswer: string | null
  lastSequence: number
  pendingSteering: string[]
  pendingFollowUps: string[]
  transcript: TranscriptBlock[]
  activities: TaskActivity[]
  artifacts?: TaskArtifact[]
  evidence?: RunEvidence
}

export interface TaskArtifact {
  id: string
  runId: string
  displayName: string
  contentType: string
  size: number
  sha256: string
  createdAt: string
}

export type TestEvidenceStatus = 'Passed' | 'Failed' | 'NotRun' | 'Unknown'
export type RecoveryAvailability = 'Unavailable' | 'Available' | 'Conflict' | 'Recovered'

export interface RunEvidence {
  runId: string
  finalized: boolean
  isGitRepository: boolean
  gitRoot: string | null
  headBefore: string | null
  headAfter: string | null
  testStatus: TestEvidenceStatus
  files: FileChangeEvidence[]
  commands: CommandExecutionEvidence[]
  tests: TestResultEvidence[]
  warnings: EvidenceWarning[]
}

export interface FileChangeEvidence {
  id: string
  path: string
  relativePath: string
  kind: 'Added' | 'Modified' | 'Deleted' | 'Renamed' | 'Unknown'
  confidence: 'Confirmed' | 'Observed' | 'PreExisting' | 'Unknown'
  source: string
  beforeHash: string | null
  afterHash: string | null
  beforeSize: number | null
  afterSize: number | null
  isBinary: boolean
  hasDiff: boolean
  addedLines: number
  deletedLines: number
  diffTruncated: boolean
  recovery: RecoveryAvailability
  recoveryMessage: string | null
}

export interface CommandExecutionEvidence {
  id: string
  toolCallId: string
  command: string
  workingDirectory: string
  startedAt: string
  durationMilliseconds: number
  exitCode: number | null
  cancelled: boolean
  timedOut: boolean
  outputSummary: string
  fullOutputPath: string | null
  isTest: boolean
  detectedFramework: string | null
  status: TestEvidenceStatus
}

export interface TestResultEvidence {
  id: string
  commandExecutionId: string
  command: string
  framework: string
  status: TestEvidenceStatus
  exitCode: number | null
  completedAt: string
}

export interface EvidenceWarning {
  code: string
  message: string
  createdAt: string
}

export interface FileDiffEvidence {
  changeId: string
  runId: string
  path: string
  diffText: string | null
  isBinary: boolean
  truncated: boolean
  source: string
}

export interface RecoveryCompleted {
  changeId: string
  succeeded: boolean
  status: RecoveryAvailability
  message: string
}

export interface TaskHistoryEntry {
  id: string
  runId: string
  title: string
  workingDirectory: string
  scopeKind?: TaskScopeKind
  status: string
  statusText: string
  summary: string
  updatedAt: string
  deletedAt: string | null
  workspaceId?: string | null
}

export interface WorkspaceHistoryEntry {
  id: string
  name: string
  workingDirectory: string
  createdAt: string
  updatedAt: string
  taskCount: number
  hasActiveTask: boolean
  iconKey?: WorkspaceIconKey
  colorKey?: WorkspaceColorKey
  displayName?: string | null
}

export type WorkspaceIconKey =
  | 'folder'
  | 'code'
  | 'terminal'
  | 'book'
  | 'globe'
  | 'flask'
  | 'database'
  | 'app'

export type WorkspaceColorKey =
  | 'blue'
  | 'indigo'
  | 'violet'
  | 'pink'
  | 'red'
  | 'orange'
  | 'green'
  | 'teal'

export interface InitializeSnapshot {
  currentTask: TaskSnapshot | null
  lastSequence: number
  workspaces?: WorkspaceHistoryEntry[]
  recentTasks: TaskHistoryEntry[]
  historyTasks: TaskHistoryEntry[]
  historyHasMore?: boolean
  recycleBinTasks: TaskHistoryEntry[]
  draft: ComposerDraft | null
  settings?: SettingsSnapshot
  capabilities: string[]
}

export interface TaskCollections {
  workspaces?: WorkspaceHistoryEntry[]
  recentTasks: TaskHistoryEntry[]
  historyTasks: TaskHistoryEntry[]
  historyHasMore?: boolean
  recycleBinTasks: TaskHistoryEntry[]
}

export interface TaskHistoryPage {
  requestId: string
  offset: number
  items: TaskHistoryEntry[]
  hasMore: boolean
  replaces: boolean
}

export interface RunEvent {
  eventId: string
  taskId: string
  runId: string
  sequence: number
  kind: string
  status: string
  timestamp: string
  payload: Record<string, string>
}

export interface TaskDelta {
  id: string
  runId: string
  status: string
  statusText: string
  summary: string
  activityStatus?: string | null
  assistantText: string | null
  finalAnswer: string | null
  lastSequence: number
  pendingSteering: string[]
  pendingFollowUps: string[]
  updatedAt: string
  transcriptUpserts: TranscriptBlock[]
  activityUpserts: TaskActivity[]
}

export interface AppendEvents {
  events: RunEvent[]
  task: TaskDelta
}
