using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using PiCompanion.Core.Evidence;
using PiCompanion.Application.Evidence;
using PiCompanion.Application.Files;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.Settings;
using PiCompanion.Application.Tasks;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiCompanion.Application.Demo;

public sealed class TaskCoordinator : IDisposable
{
    private const int MaximumConcurrentRuns = 2;
    private readonly object _gate = new();
    private readonly IAgentBackend _backend;
    private readonly IRunEventStore? _eventStore;
    private readonly IWorkspaceEvidenceService? _evidenceService;
    private readonly bool _ownsEvidenceService;
    private readonly ITaskMetadataGenerator? _metadataGenerator;
    private readonly Func<TaskSettings>? _taskSettingsResolver;
    private readonly AttachmentStagingService? _attachmentStaging;
    private readonly GeneralChatWorkspaceService? _generalChatWorkspaces;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Guid> _manuallyRenamedTasks = [];
    private readonly HashSet<Guid> _dispatchingLocalMessageIds = [];
    private readonly HashSet<Guid> _suppressedLocalQueueAutoStartRuns = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _localQueueAutoStartCancellations = [];
    private readonly Dictionary<Guid, List<TaskProjection>> _taskConversations = [];
    private readonly Dictionary<Guid, TaskProjection> _latestTasks = [];
    private readonly Dictionary<Guid, ScheduledRun> _scheduledRuns = [];
    private readonly LinkedList<Guid> _pendingRunIds = [];
    private readonly HashSet<Guid> _runningRunIds = [];
    private readonly Dictionary<string, Guid> _workspaceRunIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Guid Id, DateTimeOffset CreatedAt)> _createdWorkspaces =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, InMemoryWorkspacePresentation> _workspacePresentations = [];
    private readonly HashSet<string> _hiddenWorkspaceDirectories = new(StringComparer.OrdinalIgnoreCase);
    private TaskProjection? _current;
    private List<TaskProjection> _conversation = [];

    public TaskCoordinator(
        IAgentBackend backend,
        IRunEventStore? eventStore = null,
        IWorkspaceEvidenceService? evidenceService = null,
        ITaskMetadataGenerator? metadataGenerator = null,
        Func<TaskSettings>? taskSettingsResolver = null,
        AttachmentStagingService? attachmentStaging = null,
        GeneralChatWorkspaceService? generalChatWorkspaces = null)
    {
        _backend = backend;
        _eventStore = eventStore;
        _evidenceService = evidenceService ?? (eventStore is null ? null : WorkspaceEvidenceService.CreateDefault(eventStore));
        _ownsEvidenceService = evidenceService is null && _evidenceService is not null;
        _metadataGenerator = metadataGenerator;
        _taskSettingsResolver = taskSettingsResolver;
        _attachmentStaging = attachmentStaging;
        _generalChatWorkspaces = generalChatWorkspaces;
        var restored = _eventStore?.RestoreLatestProjection();
        if (restored is not null)
        {
            _conversation = _eventStore?.RestoreTaskRuns(restored.TaskId).ToList() ?? [restored];
            _current = _conversation.LastOrDefault(candidate => candidate.RunId == restored.RunId) ?? restored;
            if (_conversation.All(candidate => candidate.RunId != _current.RunId))
            {
                _conversation.Add(_current);
            }
            RegisterConversation(_conversation);
            RestoreLocalQueuedMessages(_current);
        }
        _backend.EventReceived += OnEventReceived;
        _backend.ToolExecutionCompleted += OnToolExecutionCompleted;
        if (_evidenceService is not null)
        {
            _evidenceService.EvidenceChanged += OnEvidenceChanged;
            if (restored is { ScopeKind: TaskScopeKind.Workspace } &&
                _eventStore?.GetRunEvidenceMetadata(restored.RunId) is { Finalized: false })
            {
                _evidenceService.FinalizeRun(restored.RunId);
            }
        }
        if (_metadataGenerator is ITaskMetadataGeneratorPrewarmer prewarmer &&
            _taskSettingsResolver?.Invoke() is { } taskSettings &&
            (taskSettings.AiTitleEnabled || taskSettings.AiSummaryEnabled))
        {
            _ = PrewarmMetadataGeneratorAsync(
                prewarmer,
                ResolveMetadataModel(taskSettings),
                _lifetime.Token);
        }
    }

    public event Action<TaskProjection?>? ProjectionChanged;

    public event Action<TaskProjection>? TaskChanged;

    public event Action<CompanionRunEvent>? RunEventReceived;

    public event Action<Guid>? EvidenceChanged;

    public TaskProjection? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public IReadOnlyList<TaskProjection> CurrentConversation
    {
        get
        {
            lock (_gate)
            {
                return _conversation.ToArray();
            }
        }
    }

    public IReadOnlyList<TaskProjection> ActiveTasks
    {
        get
        {
            lock (_gate)
            {
                return _latestTasks.Values
                    .Where(projection => projection.Status.IsActive())
                    .ToArray();
            }
        }
    }

    public bool IsWorkspaceActive(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        lock (_gate)
        {
            return _latestTasks.Values.Any(projection =>
                projection.Status.IsActive() &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(projection.WorkingDirectory)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public void InvalidateRuntimeResources(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (ActiveTasks.Count > 0)
            {
                throw new InvalidOperationException("仍有任务正在运行，不能重新加载全局 Runtime 资源。");
            }
        }
        else if (IsWorkspaceActive(workingDirectory))
        {
            throw new InvalidOperationException("目标工作区仍有任务正在运行，不能重新加载其 Runtime 资源。");
        }

        if (_backend is IAgentBackendResourceInvalidator invalidator)
        {
            invalidator.InvalidateIdleResources(workingDirectory);
        }
    }

    public IReadOnlyList<TaskHistoryEntry> RecentTasks
    {
        get
        {
            if (_eventStore is not null)
            {
                return _eventStore.GetRecentTasks();
            }

            lock (_gate)
            {
                return _latestTasks.Values
                    .Where(projection =>
                        projection.ScopeKind != TaskScopeKind.Workspace ||
                        !_hiddenWorkspaceDirectories.Contains(
                            NormalizeWorkspaceDirectory(projection.WorkingDirectory)))
                    .Select(projection => new TaskHistoryEntry(
                        projection.TaskId,
                        projection.RunId,
                        projection.Title,
                        projection.WorkingDirectory,
                        projection.Status,
                        projection.Summary,
                        projection.Transcript.LastOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow,
                        ScopeKind: projection.ScopeKind))
                    .OrderByDescending(task => task.UpdatedAt)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<TaskHistoryEntry> HistoryTasks =>
        _eventStore?.QueryTasks(new TaskHistoryQuery()) ?? RecentTasks;

    public IReadOnlyList<TaskHistoryEntry> GetHistoryTasksPage(int offset, int limit) =>
        _eventStore?.QueryTasks(new TaskHistoryQuery(Limit: limit, Offset: offset))
        ?? RecentTasks.Skip(offset).Take(limit).ToArray();

    public IReadOnlyList<TaskHistoryEntry> RecycleBinTasks =>
        _eventStore?.QueryTasks(new TaskHistoryQuery(IncludeDeleted: true)) ?? [];

    public IReadOnlyList<WorkspaceHistoryEntry> Workspaces
    {
        get
        {
            if (_eventStore is not null)
            {
                return _eventStore.GetWorkspaces();
            }

            lock (_gate)
            {
                var workspaces = _createdWorkspaces.ToDictionary(
                    pair => pair.Key,
                    pair => new WorkspaceHistoryEntry(
                        pair.Value.Id,
                        GetWorkspaceName(pair.Key),
                        pair.Key,
                        pair.Value.CreatedAt,
                        pair.Value.CreatedAt,
                        0,
                        false),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var group in _latestTasks.Values
                             .Where(task => task.ScopeKind == TaskScopeKind.Workspace)
                             .GroupBy(
                                 task => Path.TrimEndingDirectorySeparator(
                                     Path.GetFullPath(task.WorkingDirectory)),
                                 StringComparer.OrdinalIgnoreCase))
                {
                    if (_hiddenWorkspaceDirectories.Contains(group.Key))
                    {
                        continue;
                    }
                    var tasks = group.ToArray();
                    var updatedAt = tasks
                        .Select(task => task.Transcript.LastOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow)
                        .Max();
                    var existing = workspaces.GetValueOrDefault(group.Key);
                    if (existing is null)
                    {
                        var created = (Id: Guid.NewGuid(), CreatedAt: updatedAt);
                        _createdWorkspaces[group.Key] = created;
                        existing = new WorkspaceHistoryEntry(
                            created.Id,
                            GetWorkspaceName(group.Key),
                            group.Key,
                            created.CreatedAt,
                            created.CreatedAt,
                            0,
                            false);
                    }
                    workspaces[group.Key] = new WorkspaceHistoryEntry(
                        existing.Id,
                        GetWorkspaceName(group.Key),
                        group.Key,
                        existing.CreatedAt,
                        updatedAt,
                        tasks.Select(task => task.TaskId).Distinct().Count(),
                        tasks.Any(task => task.Status.IsActive()));
                }

                return workspaces.Values
                    .Select(ApplyWorkspacePresentation)
                    .OrderByDescending(workspace => workspace.UpdatedAt)
                    .ToArray();
            }
        }
    }

    public WorkspaceHistoryEntry CreateWorkspace(string workingDirectory)
    {
        var normalized = NormalizeWorkspaceDirectory(workingDirectory);
        if (_eventStore is not null)
        {
            return _eventStore.CreateWorkspace(normalized);
        }

        lock (_gate)
        {
            _hiddenWorkspaceDirectories.Remove(normalized);
            if (!_createdWorkspaces.TryGetValue(normalized, out var workspace))
            {
                workspace = (Guid.NewGuid(), DateTimeOffset.UtcNow);
                _createdWorkspaces.Add(normalized, workspace);
            }

            return ApplyWorkspacePresentation(new WorkspaceHistoryEntry(
                workspace.Id,
                GetWorkspaceName(normalized),
                normalized,
                workspace.CreatedAt,
                workspace.CreatedAt,
                0,
                false));
        }
    }

    public WorkspaceHistoryEntry UpdateWorkspacePresentation(
        Guid workspaceId,
        string? displayName,
        string iconKey,
        string colorKey)
    {
        if (_eventStore is not null)
        {
            return _eventStore.UpdateWorkspacePresentation(
                workspaceId,
                displayName,
                iconKey,
                colorKey);
        }

        var normalizedName = string.IsNullOrWhiteSpace(displayName)
            ? null
            : string.Join(' ', displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalizedName?.Length > 60)
        {
            throw new ArgumentException("工作区显示名称不能超过 60 个字符。", nameof(displayName));
        }

        var normalizedIcon = NormalizeWorkspacePresentationValue(
            iconKey,
            ["folder", "code", "terminal", "book", "globe", "flask", "database", "app"],
            "不支持的工作区图标。");
        var normalizedColor = NormalizeWorkspacePresentationValue(
            colorKey,
            ["blue", "indigo", "violet", "pink", "red", "orange", "green", "teal"],
            "不支持的工作区图标颜色。");
        lock (_gate)
        {
            var workspace = Workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId) ??
                throw new InvalidOperationException("工作区不存在或已不可用。");
            _workspacePresentations[workspaceId] = new InMemoryWorkspacePresentation(
                normalizedName,
                normalizedIcon,
                normalizedColor);
            return ApplyWorkspacePresentation(workspace);
        }
    }

    public void HideWorkspace(Guid workspaceId)
    {
        if (_eventStore is not null)
        {
            _eventStore.HideWorkspace(workspaceId);
            return;
        }

        lock (_gate)
        {
            var workspace = Workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId) ??
                throw new InvalidOperationException("工作区不存在或已不可用。");
            if (workspace.HasActiveTask)
            {
                throw new InvalidOperationException("工作区仍有运行中的任务，请先停止任务再隐藏。");
            }

            var normalized = NormalizeWorkspaceDirectory(workspace.WorkingDirectory);
            _createdWorkspaces.Remove(normalized);
            _workspacePresentations.Remove(workspaceId);
            _hiddenWorkspaceDirectories.Add(normalized);
        }
    }

    public Task PrepareAsync(
        string workingDirectory,
        string model,
        string thinkingLevel,
        CancellationToken cancellationToken = default) =>
        _backend is IAgentBackendPrewarmer prewarmer
            ? prewarmer.PrepareAsync(
                new AgentPreparationRequest(workingDirectory, model, thinkingLevel),
                cancellationToken)
            : Task.CompletedTask;

    public async Task<string> GenerateCommitMessageAsync(
        WorkspaceGitCommitMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_metadataGenerator is null)
        {
            throw new InvalidOperationException("AI 元数据生成器不可用。");
        }

        var settings = _taskSettingsResolver?.Invoke();
        var message = await _metadataGenerator.GenerateCommitMessageAsync(
            new CommitMessageSource(
                context.RepositoryName,
                context.Branch,
                context.RelativePaths,
                context.RecentSubjects,
                context.DiffText,
                context.Truncated),
            settings is null ? string.Empty : ResolveMetadataModel(settings),
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(message)
            ? throw new InvalidOperationException("AI 未能生成有效的提交信息。")
            : message;
    }

    public Task StartAsync(
        string prompt,
        string? workingDirectory,
        string model,
        string thinkingLevel,
        DemoRunMode mode,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? attachments = null,
        string? permissionMode = null,
        TaskScopeKind scopeKind = TaskScopeKind.Workspace) =>
        StartCoreAsync(
            Current,
            prompt,
            workingDirectory,
            model,
            thinkingLevel,
            mode,
            cancellationToken,
            attachments,
            permissionMode,
            scopeKind);

    private async Task StartCoreAsync(
        TaskProjection? current,
        string prompt,
        string? workingDirectory,
        string model,
        string thinkingLevel,
        DemoRunMode mode,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? attachments,
        string? permissionMode,
        TaskScopeKind scopeKind)
    {
        if (current?.Status.IsActive() == true)
        {
            throw new InvalidOperationException("当前已有任务正在运行，请先停止或等待它结束。");
        }

        var taskId = current?.TaskId ?? Guid.NewGuid();
        var effectiveScopeKind = current?.ScopeKind ?? scopeKind;
        if (current is { ScopeKind: TaskScopeKind.Workspace } &&
            !string.IsNullOrWhiteSpace(workingDirectory) &&
            !string.Equals(
                Path.GetFullPath(current.WorkingDirectory),
                Path.GetFullPath(workingDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("已创建的任务不能更改工作目录，请新建任务。");
        }

        var generalChatWorkspace = effectiveScopeKind == TaskScopeKind.GeneralChat
            ? _generalChatWorkspaces?.GetOrCreate(taskId)
                ?? throw new InvalidOperationException("General Chat 托管工作区服务不可用。")
            : null;
        var effectiveWorkingDirectory = current?.WorkingDirectory ??
            generalChatWorkspace?.WorkingDirectory ??
            NormalizeWorkspaceDirectory(workingDirectory);

        if (current is not null)
        {
            lock (_gate)
            {
                CancelLocalQueueAutoStartCore(current);
            }
        }

        var effectivePermissionMode = current?.PermissionMode ??
            (effectiveScopeKind == TaskScopeKind.GeneralChat
                ? "standard"
                : NormalizePermissionMode(permissionMode));
        var runId = Guid.NewGuid();
        var title = current?.Title ?? CreateTitle();
        var requestedAttachmentSnapshot = TaskAttachmentRules.NormalizeAndValidate(
            attachments ?? current?.Attachments);
        var stagedAttachments = _attachmentStaging?.StageForRun(
            taskId,
            runId,
            effectiveWorkingDirectory,
            requestedAttachmentSnapshot,
            alwaysSnapshot: effectiveScopeKind == TaskScopeKind.GeneralChat);
        var attachmentSnapshot = stagedAttachments?.PersistentPaths ?? requestedAttachmentSnapshot;
        var piSessionPath = current is null ? null : _eventStore?.GetLatestSessionPath(taskId);
        var piEntryCursor = current is null ? null : _eventStore?.GetLatestPiEntryCursor(taskId);
        IReadOnlyList<TaskProjection> existingConversation;
        lock (_gate)
        {
            existingConversation = _taskConversations.TryGetValue(taskId, out var registered)
                ? registered.ToArray()
                : current is null
                    ? []
                    : _conversation.Where(run => run.TaskId == taskId).ToArray();
        }
        var knownAssistantMessages = existingConversation
            .SelectMany(run => run.Transcript)
            .Where(block => block.Kind == TranscriptBlockKind.AssistantMessage && !string.IsNullOrWhiteSpace(block.Content))
            .Select(block => block.Content)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var projection = new TaskProjection(
            taskId,
            runId,
            title,
            effectiveWorkingDirectory,
            model,
            thinkingLevel,
            attachmentSnapshot,
            prompt,
            permissionMode: effectivePermissionMode,
            scopeKind: effectiveScopeKind);
        projection.RestoreArtifacts(current?.Artifacts);
        projection.RestoreLocalQueuedMessages(
            current?.LocalQueuedMessages ?? ReadLocalQueuedMessages(taskId));

        lock (_gate)
        {
            var conversation = _taskConversations.TryGetValue(taskId, out var registered)
                ? registered
                : [];
            conversation.Add(projection);
            _taskConversations[taskId] = conversation;
            _latestTasks[taskId] = projection;
            if (current is null || _current?.TaskId == taskId)
            {
                _conversation = conversation;
                _current = projection;
            }
        }

        _eventStore?.CreateRun(projection, prompt);
        NotifyTaskChanged(projection);
        try
        {
            await ScheduleRunAsync(
                new AgentRunRequest(
                    taskId,
                    runId,
                    title,
                    prompt,
                    effectiveWorkingDirectory,
                    model,
                    thinkingLevel,
                    mode.ToString(),
                    stagedAttachments?.RuntimePaths ?? attachmentSnapshot,
                    piSessionPath,
                    stagedAttachments?.ReadOnlyRoot,
                    piEntryCursor,
                    knownAssistantMessages,
                    effectivePermissionMode,
                    effectiveScopeKind,
                    generalChatWorkspace?.ArtifactDirectory),
                cancellationToken);
            if (current is null)
            {
                ScheduleTitleGeneration(projection, title);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OnEventReceived(new CompanionRunEvent(
                Guid.NewGuid(),
                taskId,
                runId,
                Math.Max(1, projection.LastSequence + 1),
                CompanionRunEventKind.RunFailed,
                DateTimeOffset.UtcNow,
                RunStatus.Failed,
                new Dictionary<string, string>
                {
                    ["activity"] = exception.Message,
                    ["summary"] = "Pi Runtime 启动失败",
                },
                "pi-companion-startup-v1"));
        }
    }

    private async Task ScheduleRunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        ScheduledRun scheduled;
        lock (_gate)
        {
            var projection = _latestTasks.GetValueOrDefault(request.TaskId);
            if (projection is null || projection.RunId != request.RunId)
            {
                throw new InvalidOperationException("待调度的任务投影不存在。");
            }

            scheduled = new ScheduledRun(
                request with { InitialSequence = projection.LastSequence + 1 },
                projection,
                cancellationToken);
            _scheduledRuns.Add(request.RunId, scheduled);
            _pendingRunIds.AddLast(request.RunId);
        }

        OnEventReceived(new CompanionRunEvent(
            Guid.NewGuid(),
            request.TaskId,
            request.RunId,
            scheduled.Request.InitialSequence,
            CompanionRunEventKind.RunQueued,
            DateTimeOffset.UtcNow,
            RunStatus.Queued,
            new Dictionary<string, string>
            {
                ["activity"] = "等待可用的运行槽位",
                ["activityStatus"] = "已加入任务队列",
            },
            "pi-companion-scheduler-v1"));
        await DispatchEligibleRunsAsync().ConfigureAwait(false);
    }

    private async Task DispatchEligibleRunsAsync()
    {
        List<ScheduledRun> starts = [];
        lock (_gate)
        {
            while (_runningRunIds.Count < MaximumConcurrentRuns)
            {
                var node = _pendingRunIds.First;
                while (node is not null)
                {
                    var candidate = _scheduledRuns[node.Value];
                    if (!_workspaceRunIds.ContainsKey(candidate.WorkspaceKey))
                    {
                        break;
                    }

                    node = node.Next;
                }

                if (node is null)
                {
                    break;
                }

                var runId = node.Value;
                _pendingRunIds.Remove(node);
                var scheduled = _scheduledRuns[runId];
                _runningRunIds.Add(runId);
                _workspaceRunIds.Add(scheduled.WorkspaceKey, runId);
                starts.Add(scheduled);
            }
        }

        if (starts.Count > 0)
        {
            await Task.WhenAll(starts.Select(StartScheduledRunAsync)).ConfigureAwait(false);
        }
    }

    private async Task StartScheduledRunAsync(ScheduledRun scheduled)
    {
        try
        {
            if (scheduled.Projection.ScopeKind == TaskScopeKind.Workspace)
            {
                _evidenceService?.BeginRun(
                    scheduled.Projection.TaskId,
                    scheduled.Projection.RunId,
                    scheduled.Projection.WorkingDirectory);
            }

            await _backend.StartRunAsync(
                scheduled.Request,
                scheduled.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (IsScheduled(scheduled.Projection.RunId))
            {
                PublishSchedulerTerminalEvent(
                    scheduled,
                    CompanionRunEventKind.RunInterrupted,
                    RunStatus.Interrupted,
                    "任务启动已取消",
                    "启动已取消",
                    "startup-cancelled");
            }
        }
        catch (Exception exception)
        {
            if (IsScheduled(scheduled.Projection.RunId))
            {
                PublishSchedulerTerminalEvent(
                    scheduled,
                    CompanionRunEventKind.RunFailed,
                    RunStatus.Failed,
                    exception.Message,
                    "Pi Runtime 启动失败",
                    "startup-failed");
            }
        }
    }

    private bool IsScheduled(Guid runId)
    {
        lock (_gate)
        {
            return _scheduledRuns.ContainsKey(runId);
        }
    }

    private void PublishSchedulerTerminalEvent(
        ScheduledRun scheduled,
        CompanionRunEventKind kind,
        RunStatus status,
        string activity,
        string summary,
        string exitReason)
    {
        OnEventReceived(new CompanionRunEvent(
            Guid.NewGuid(),
            scheduled.Projection.TaskId,
            scheduled.Projection.RunId,
            Math.Max(
                scheduled.Request.InitialSequence + 1,
                scheduled.Projection.LastSequence + 1),
            kind,
            DateTimeOffset.UtcNow,
            status,
            new Dictionary<string, string>
            {
                ["activity"] = activity,
                ["summary"] = summary,
                ["exitReason"] = exitReason,
            },
            "pi-companion-scheduler-v1"));
    }

    private static string NormalizePermissionMode(string? permissionMode) =>
        permissionMode?.Trim().ToLowerInvariant() switch
        {
            "read-only" => "read-only",
            "standard" => "standard",
            "full-access" => "full-access",
            _ => "standard",
        };

    public Task SteerAsync(string message, CancellationToken cancellationToken = default) =>
        _backend.SteerAsync(RequireCurrent().RunId, message, cancellationToken);

    public Task FollowUpAsync(string message, CancellationToken cancellationToken = default) =>
        _backend.FollowUpAsync(RequireCurrent().RunId, message, cancellationToken);

    public LocalQueuedMessage QueueLocalMessage(
        string message,
        IReadOnlyList<string>? attachments = null)
    {
        TaskProjection projection;
        LocalQueuedMessage queued;
        lock (_gate)
        {
            projection = RequireCurrent();
            if (!projection.Status.IsActive())
            {
                throw new InvalidOperationException("当前任务未在运行，消息应直接作为新一轮发送。");
            }

            queued = projection.AddLocalQueuedMessage(message, attachments);
            PersistLocalQueuedMessages(projection);
        }

        NotifyTaskChanged(projection);
        return queued;
    }

    public LocalQueuedMessage UpdateLocalMessage(
        Guid messageId,
        string message,
        IReadOnlyList<string>? attachments = null)
    {
        TaskProjection projection;
        LocalQueuedMessage updated;
        lock (_gate)
        {
            projection = RequireCurrent();
            ThrowIfLocalMessageIsDispatching(messageId);
            updated = projection.UpdateLocalQueuedMessage(messageId, message, attachments);
            PersistLocalQueuedMessages(projection);
        }

        NotifyTaskChanged(projection);
        return updated;
    }

    public void RemoveLocalMessage(Guid messageId)
    {
        TaskProjection projection;
        Guid? previousFirstId;
        lock (_gate)
        {
            projection = RequireCurrent();
            ThrowIfLocalMessageIsDispatching(messageId);
            previousFirstId = projection.LocalQueuedMessages.FirstOrDefault()?.Id;
            projection.RemoveLocalQueuedMessage(messageId);
            PersistLocalQueuedMessages(projection);
        }

        NotifyTaskChanged(projection);
        RestartLocalQueueAutoStartIfFirstChanged(projection, previousFirstId);
    }

    public void MoveLocalMessage(Guid messageId, int newIndex)
    {
        TaskProjection projection;
        Guid? previousFirstId;
        lock (_gate)
        {
            projection = RequireCurrent();
            ThrowIfLocalMessageIsDispatching(messageId);
            previousFirstId = projection.LocalQueuedMessages.FirstOrDefault()?.Id;
            projection.MoveLocalQueuedMessage(messageId, newIndex);
            PersistLocalQueuedMessages(projection);
        }

        NotifyTaskChanged(projection);
        RestartLocalQueueAutoStartIfFirstChanged(projection, previousFirstId);
    }

    public void CancelLocalQueueAutoStart()
    {
        TaskProjection projection;
        lock (_gate)
        {
            projection = RequireCurrent();
            if (projection.LocalQueueAutoStartMessageId is null)
            {
                return;
            }

            _suppressedLocalQueueAutoStartRuns.Add(projection.RunId);
            CancelLocalQueueAutoStartCore(projection);
        }

        NotifyTaskChanged(projection);
    }

    public void RefreshLocalQueueAutomation()
    {
        TaskProjection[] projections;
        lock (_gate)
        {
            projections = _latestTasks.Values.ToArray();
        }

        var settings = _taskSettingsResolver?.Invoke();
        if (settings?.AutoStartLocalQueueEnabled == true)
        {
            foreach (var projection in projections)
            {
                ScheduleLocalQueueAutoStart(projection);
            }
            return;
        }

        List<TaskProjection> changed = [];
        lock (_gate)
        {
            foreach (var projection in projections.Where(
                         candidate => candidate.LocalQueueAutoStartMessageId is not null))
            {
                CancelLocalQueueAutoStartCore(projection);
                changed.Add(projection);
            }
        }

        foreach (var projection in changed)
        {
            NotifyTaskChanged(projection);
        }
    }

    public Task DispatchLocalMessageAsync(
        Guid messageId,
        string delivery,
        CancellationToken cancellationToken = default) =>
        DispatchLocalMessageAsync(
            RequireCurrent().TaskId,
            messageId,
            delivery,
            cancellationToken);

    private async Task DispatchLocalMessageAsync(
        Guid taskId,
        Guid messageId,
        string delivery,
        CancellationToken cancellationToken)
    {
        TaskProjection projection;
        LocalQueuedMessage queued;
        var normalizedDelivery = delivery.Trim().ToLowerInvariant();
        lock (_gate)
        {
            projection = _latestTasks.GetValueOrDefault(taskId)
                ?? throw new InvalidOperationException("未找到待发送消息所属的任务。");
            queued = projection.LocalQueuedMessages.FirstOrDefault(item => item.Id == messageId)
                ?? throw new InvalidOperationException("未找到这条待发送消息。");
            if (!_dispatchingLocalMessageIds.Add(messageId))
            {
                throw new InvalidOperationException("这条消息正在发送。");
            }

            if (normalizedDelivery is "steer" or "follow-up")
            {
                if (!projection.Status.IsActive())
                {
                    _dispatchingLocalMessageIds.Remove(messageId);
                    throw new InvalidOperationException("当前任务已结束，请将消息作为新一轮发送。");
                }
                if (queued.Attachments is { Count: > 0 })
                {
                    _dispatchingLocalMessageIds.Remove(messageId);
                    throw new InvalidOperationException("带附件的待发送任务只能作为新一轮发送。");
                }
                if (_scheduledRuns.ContainsKey(projection.RunId) &&
                    !_runningRunIds.Contains(projection.RunId))
                {
                    _dispatchingLocalMessageIds.Remove(messageId);
                    throw new InvalidOperationException("任务仍在等待运行槽位，暂时不能发送 Steer 或 Follow-up。");
                }
            }
            else if (normalizedDelivery == "new-run")
            {
                if (projection.Status.IsActive())
                {
                    _dispatchingLocalMessageIds.Remove(messageId);
                    throw new InvalidOperationException("当前任务仍在运行，不能开始新一轮。");
                }
                CancelLocalQueueAutoStartCore(projection);
            }
            else
            {
                _dispatchingLocalMessageIds.Remove(messageId);
                throw new InvalidOperationException($"不支持的待发送方式：{delivery}");
            }
        }

        try
        {
            switch (normalizedDelivery)
            {
                case "steer":
                    await _backend.SteerAsync(projection.RunId, queued.Message, cancellationToken);
                    break;
                case "follow-up":
                    await _backend.FollowUpAsync(projection.RunId, queued.Message, cancellationToken);
                    break;
                case "new-run":
                    await StartCoreAsync(
                        projection,
                        queued.Message,
                        projection.WorkingDirectory,
                        projection.PreferredModel,
                        projection.PreferredThinkingLevel,
                        DemoRunMode.InteractiveSuccess,
                        cancellationToken,
                        queued.Attachments,
                        projection.PermissionMode,
                        projection.ScopeKind);
                    break;
            }

            TaskProjection? changed = null;
            Guid? previousFirstId = null;
            lock (_gate)
            {
                if (_latestTasks.TryGetValue(projection.TaskId, out var latest) &&
                    latest.LocalQueuedMessages.Any(item => item.Id == messageId))
                {
                    previousFirstId = latest.LocalQueuedMessages.FirstOrDefault()?.Id;
                    latest.RemoveLocalQueuedMessage(messageId);
                    PersistLocalQueuedMessages(latest);
                    changed = latest;
                }
            }

            if (changed is not null)
            {
                NotifyTaskChanged(changed);
                RestartLocalQueueAutoStartIfFirstChanged(changed, previousFirstId);
            }
        }
        finally
        {
            lock (_gate)
            {
                _dispatchingLocalMessageIds.Remove(messageId);
            }
        }
    }

    public Task ResolveInteractionAsync(
        bool approved,
        string? response = null,
        string? interactionId = null,
        CancellationToken cancellationToken = default) =>
        _backend.ResolveInteractionAsync(
            RequireCurrent().RunId,
            new InteractionResolution(approved, response, interactionId),
            cancellationToken);

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        var projection = RequireCurrent();
        ScheduledRun? queued = null;
        lock (_gate)
        {
            if (_scheduledRuns.TryGetValue(projection.RunId, out var scheduled) &&
                !_runningRunIds.Contains(projection.RunId))
            {
                queued = scheduled;
            }
        }

        if (queued is null)
        {
            return _backend.AbortAsync(projection.RunId, cancellationToken);
        }

        PublishSchedulerTerminalEvent(
            queued,
            CompanionRunEventKind.RunInterrupted,
            RunStatus.Interrupted,
            "任务已从运行队列中取消",
            "已取消排队",
            "queue-cancelled");
        return Task.CompletedTask;
    }

    public Task AbortRetryAsync(CancellationToken cancellationToken = default) =>
        _backend.AbortRetryAsync(RequireCurrent().RunId, cancellationToken);

    public Task CompactSessionAsync(
        Guid taskId,
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        var current = RequireCurrent();
        if (current.TaskId != taskId)
        {
            throw new InvalidOperationException("只能压缩当前任务的上下文。");
        }

        if (current.Status.IsActive())
        {
            throw new InvalidOperationException("任务运行中，完成或停止后才能压缩上下文。");
        }

        if (_backend is not IAgentSessionCommandController controller)
        {
            throw new NotSupportedException("当前 Agent 后端不支持压缩 Session。");
        }

        return controller.CompactAsync(
            new AgentSessionCommandRequest(
                current.TaskId,
                current.WorkingDirectory,
                current.Model,
                current.ThinkingLevel,
                _eventStore?.GetLatestSessionPath(current.TaskId)),
            customInstructions,
            cancellationToken);
    }

    public async Task<AgentSessionStatistics?> GetSessionStatisticsAsync(
        bool loadHistoricalSession = false,
        CancellationToken cancellationToken = default)
    {
        var current = Current;
        if (current is null)
        {
            return null;
        }

        var taskId = current.TaskId;
        var runId = current.RunId;
        var lastSequence = current.LastSequence;
        AgentSessionStatistics? statistics = null;
        if (_backend is IAgentSessionStatisticsProvider provider)
        {
            statistics = await provider.GetSessionStatisticsAsync(
                new AgentSessionStatisticsRequest(
                    taskId,
                    current.WorkingDirectory,
                    current.Model,
                    current.ThinkingLevel,
                    _eventStore?.GetLatestSessionPath(taskId),
                    loadHistoricalSession),
                cancellationToken).ConfigureAwait(false);
        }

        if (statistics is not null)
        {
            _eventStore?.UpsertSessionStatisticsCache(new SessionStatisticsCacheEntry(
                taskId,
                runId,
                lastSequence,
                statistics,
                DateTimeOffset.UtcNow));
            return statistics;
        }

        return loadHistoricalSession
            ? null
            : _eventStore?.GetSessionStatisticsCache(taskId, runId, lastSequence)?.Statistics;
    }

    public void BeginNewTask()
    {
        lock (_gate)
        {
            _current = null;
            _conversation = [];
        }

        ProjectionChanged?.Invoke(null);
    }

    public TaskProjection SelectTask(Guid taskId)
    {
        TaskProjection? selected = null;
        lock (_gate)
        {
            if (_current?.TaskId == taskId)
            {
                return _current;
            }

            if (_taskConversations.TryGetValue(taskId, out var registered))
            {
                _conversation = registered;
                _current = registered[^1];
                selected = _current;
            }
        }

        if (selected is not null)
        {
            ProjectionChanged?.Invoke(selected);
            return selected;
        }

        var conversation = _eventStore?.RestoreTaskRuns(taskId) ?? [];
        var projection = conversation.LastOrDefault() ??
            throw new InvalidOperationException("未找到这条任务记录。");
        RestoreLocalQueuedMessages(projection);
        lock (_gate)
        {
            _conversation = conversation.ToList();
            _current = projection;
            RegisterConversation(_conversation);
        }

        ProjectionChanged?.Invoke(projection);
        return projection;
    }

    public void RenameTask(Guid taskId, string title)
    {
        lock (_gate)
        {
            _manuallyRenamedTasks.Add(taskId);
        }

        _eventStore?.RenameTask(taskId, title);
        TaskProjection? changed = null;
        lock (_gate)
        {
            if (_taskConversations.TryGetValue(taskId, out var conversation))
            {
                foreach (var projection in conversation)
                {
                    projection.Rename(title);
                }
            }

            changed = _latestTasks.GetValueOrDefault(taskId);
        }

        if (changed is not null)
        {
            NotifyTaskChanged(changed);
        }
    }

    public void UpdateTaskExecutionDefaults(Guid taskId, string model, string thinkingLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(thinkingLevel);

        TaskProjection? changed = null;
        lock (_gate)
        {
            if (_current?.TaskId != taskId)
            {
                throw new InvalidOperationException("只能修改当前任务的模型设置。");
            }

            foreach (var projection in _conversation.Where(candidate => candidate.TaskId == taskId))
            {
                projection.UpdateExecutionDefaults(model, thinkingLevel);
            }

            changed = _current;
        }

        _eventStore?.UpdateTaskExecutionDefaults(taskId, model, thinkingLevel);
        NotifyTaskChanged(changed);
    }

    public void MoveTaskToRecycleBin(Guid taskId)
    {
        lock (_gate)
        {
            if (_latestTasks.TryGetValue(taskId, out var latest) && latest.Status.IsActive())
            {
                throw new InvalidOperationException("任务仍在运行，停止后才能移入回收站。");
            }
        }

        _eventStore?.MoveTaskToRecycleBin(taskId);
        var cleared = false;
        lock (_gate)
        {
            if (_latestTasks.TryGetValue(taskId, out var registered))
            {
                CancelLocalQueueAutoStartCore(registered);
            }
            _taskConversations.Remove(taskId);
            _latestTasks.Remove(taskId);
            if (_current?.TaskId == taskId)
            {
                _current = null;
                _conversation = [];
                cleared = true;
            }
        }

        if (cleared)
        {
            ProjectionChanged?.Invoke(null);
        }
    }

    public void RestoreTaskFromRecycleBin(Guid taskId) =>
        _eventStore?.RestoreTaskFromRecycleBin(taskId);

    public void DeleteTaskPermanently(Guid taskId)
    {
        lock (_gate)
        {
            if (_latestTasks.TryGetValue(taskId, out var latest) && latest.Status.IsActive())
            {
                throw new InvalidOperationException("任务仍在运行，停止后才能永久删除。");
            }
            _taskConversations.Remove(taskId);
            _latestTasks.Remove(taskId);
        }
        ReleaseGeneralChatWorkspace(taskId);
        _attachmentStaging?.DeleteTask(taskId);
        _generalChatWorkspaces?.DeleteTask(taskId);
        _eventStore?.DeleteTaskPermanently(taskId);
        _eventStore?.SetSettingJson(LocalQueuedMessagesSettingKey(taskId), "[]");
    }

    public void EmptyRecycleBin()
    {
        var deletedTaskIds = RecycleBinTasks.Select(task => task.TaskId).Distinct().ToArray();
        foreach (var taskId in deletedTaskIds)
        {
            ReleaseGeneralChatWorkspace(taskId);
            _attachmentStaging?.DeleteTask(taskId);
            _generalChatWorkspaces?.DeleteTask(taskId);
        }
        _eventStore?.EmptyRecycleBin();
        foreach (var taskId in deletedTaskIds)
        {
            _eventStore?.SetSettingJson(LocalQueuedMessagesSettingKey(taskId), "[]");
        }
    }

    private void ReleaseGeneralChatWorkspace(Guid taskId)
    {
        if (_backend is IAgentBackendWorkspaceReleaser releaser && _generalChatWorkspaces is not null)
        {
            releaser.ReleaseWorkspace(_generalChatWorkspaces.GetWorkingDirectory(taskId));
        }
    }

    public void ClearAttachmentCache()
    {
        if (ActiveTasks.Count > 0)
        {
            throw new InvalidOperationException("任务运行期间不能清理附件缓存。");
        }

        _attachmentStaging?.Clear();
    }

    public RunEvidenceSnapshot GetRunEvidence(Guid runId) =>
        _evidenceService?.GetRunEvidence(runId) ?? _eventStore?.GetRunEvidence(runId) ?? RunEvidenceSnapshot.Empty(runId);

    public FileDiffEvidence? GetFileDiff(Guid fileChangeId) =>
        _evidenceService?.GetFileDiff(fileChangeId) ??
        (_eventStore?.GetFileChange(fileChangeId) is { } file
            ? new FileDiffEvidence(file.Id, file.RunId, file.Path, file.DiffText, file.IsBinary, file.DiffTruncated, file.Source)
            : null);

    public TaskArtifact? GetArtifact(Guid artifactId) =>
        Current?.Artifacts.FirstOrDefault(artifact => artifact.Id == artifactId);

    public RecoveryResult RestoreFile(Guid fileChangeId)
    {
        if (ActiveTasks.Count > 0)
        {
            throw new InvalidOperationException("任务运行期间不能恢复文件。");
        }

        return _evidenceService?.RestoreFile(fileChangeId) ??
            throw new InvalidOperationException("当前没有可用的文件恢复服务。");
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        lock (_gate)
        {
            foreach (var cancellation in _localQueueAutoStartCancellations.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
            _localQueueAutoStartCancellations.Clear();
        }
        _backend.EventReceived -= OnEventReceived;
        _backend.ToolExecutionCompleted -= OnToolExecutionCompleted;
        if (_evidenceService is not null)
        {
            _evidenceService.EvidenceChanged -= OnEvidenceChanged;
            if (_ownsEvidenceService)
            {
                _evidenceService.Dispose();
            }
        }
        if (_backend is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (_metadataGenerator is IDisposable metadataDisposable)
        {
            metadataDisposable.Dispose();
        }
        _lifetime.Dispose();
    }

    private void ReleaseScheduledRun(Guid runId)
    {
        lock (_gate)
        {
            if (!_scheduledRuns.Remove(runId, out var scheduled))
            {
                return;
            }

            var pending = _pendingRunIds.Find(runId);
            if (pending is not null)
            {
                _pendingRunIds.Remove(pending);
            }

            _runningRunIds.Remove(runId);
            if (_workspaceRunIds.GetValueOrDefault(scheduled.WorkspaceKey) == runId)
            {
                _workspaceRunIds.Remove(scheduled.WorkspaceKey);
            }
        }
    }

    private void NotifyTaskChanged(TaskProjection projection)
    {
        TaskChanged?.Invoke(projection);
        if (Current?.TaskId == projection.TaskId)
        {
            ProjectionChanged?.Invoke(projection);
        }
    }

    private void OnEventReceived(CompanionRunEvent runEvent)
    {
        var isTerminal = runEvent.Kind is CompanionRunEventKind.RunSettled
            or CompanionRunEventKind.RunFailed
            or CompanionRunEventKind.RunInterrupted;
        TaskProjection? projection;
        lock (_gate)
        {
            projection = _taskConversations.TryGetValue(runEvent.TaskId, out var conversation)
                ? conversation.FirstOrDefault(candidate => candidate.RunId == runEvent.RunId)
                : null;
            if (projection is null || runEvent.Sequence <= projection.LastSequence)
            {
                return;
            }
        }

        lock (_gate)
        {
            if (runEvent.Sequence <= projection.LastSequence)
            {
                return;
            }

            _eventStore?.AppendRunEvent(runEvent);
            if (!projection.Apply(runEvent))
            {
                return;
            }
        }

        if (isTerminal)
        {
            try
            {
                if (projection.ScopeKind == TaskScopeKind.Workspace)
                {
                    _evidenceService?.FinalizeRun(runEvent.RunId);
                }
            }
            catch (Exception)
            {
                // Evidence collection failure must not rewrite the Pi run outcome.
            }
        }

        RunEventReceived?.Invoke(runEvent);
        NotifyTaskChanged(projection);
        if (isTerminal)
        {
            ReleaseScheduledRun(runEvent.RunId);
            ScheduleSummaryGeneration(projection);
            _ = DispatchEligibleRunsAsync();
        }
        if (runEvent.Kind == CompanionRunEventKind.RunSettled)
        {
            ScheduleLocalQueueAutoStart(projection);
        }
    }

    private void OnToolExecutionCompleted(AgentToolExecution execution)
    {
        TaskProjection? projection;
        lock (_gate)
        {
            projection = _taskConversations.TryGetValue(execution.TaskId, out var conversation)
                ? conversation.FirstOrDefault(candidate => candidate.RunId == execution.RunId)
                : null;
        }
        if (!execution.IsError &&
            string.Equals(execution.ToolName, "publish_artifact", StringComparison.Ordinal) &&
            projection is not null &&
            projection.TaskId == execution.TaskId &&
            projection.RunId == execution.RunId &&
            projection.ScopeKind == TaskScopeKind.GeneralChat &&
            TryCreateArtifact(execution, projection, out var artifact) &&
            projection.AddArtifact(artifact))
        {
            _eventStore?.UpsertTaskArtifact(artifact);
            NotifyTaskChanged(projection);
        }

        try
        {
            if (projection?.ScopeKind == TaskScopeKind.Workspace)
            {
                _evidenceService?.RecordToolExecution(execution);
            }
        }
        catch (Exception)
        {
            // The Pi adapter independently emits the tool outcome; evidence is best-effort here.
        }
    }

    private bool TryCreateArtifact(
        AgentToolExecution execution,
        TaskProjection projection,
        out TaskArtifact artifact)
    {
        artifact = null!;
        if (_generalChatWorkspaces is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(execution.ResultJson);
            var root = document.RootElement;
            if (root.TryGetProperty("details", out var details))
            {
                root = details;
            }
            if (root.TryGetProperty("artifact", out var artifactElement))
            {
                root = artifactElement;
            }

            var idText = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            var displayName = root.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : null;
            var contentType = root.TryGetProperty("contentType", out var typeElement) ? typeElement.GetString() : null;
            if (!Guid.TryParse(idText, out var id) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var artifactRoot = Path.GetFullPath(_generalChatWorkspaces.GetArtifactDirectory(projection.TaskId));
            var storagePath = Path.GetFullPath(path);
            if (!IsInside(storagePath, artifactRoot))
            {
                return false;
            }

            var info = new FileInfo(storagePath);
            using var stream = File.OpenRead(storagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            artifact = new TaskArtifact(
                id,
                projection.TaskId,
                execution.RunId,
                NormalizeArtifactName(displayName, storagePath),
                storagePath,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                info.Length,
                sha256,
                execution.CompletedAt);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeWorkspaceDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new InvalidOperationException("Workspace Task 需要有效的工作目录。");
        }

        var normalized = Path.GetFullPath(workingDirectory);
        return Directory.Exists(normalized)
            ? normalized
            : throw new DirectoryNotFoundException($"工作目录不存在：{normalized}");
    }

    private static string GetWorkspaceName(string workingDirectory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory));
        return string.IsNullOrWhiteSpace(name) ? workingDirectory : name;
    }

    private static string NormalizeArtifactName(string? displayName, string storagePath)
    {
        var fallback = Path.GetFileName(storagePath);
        var normalized = string.IsNullOrWhiteSpace(displayName) ? fallback : Path.GetFileName(displayName.Trim());
        return string.IsNullOrWhiteSpace(normalized) ? "artifact" : normalized;
    }

    private static bool IsInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathFullyQualified(relative));
    }

    private void OnEvidenceChanged(Guid runId) => EvidenceChanged?.Invoke(runId);

    private void RestoreLocalQueuedMessages(TaskProjection projection) =>
        projection.RestoreLocalQueuedMessages(ReadLocalQueuedMessages(projection.TaskId));

    private IReadOnlyList<LocalQueuedMessage> ReadLocalQueuedMessages(Guid taskId)
    {
        var json = _eventStore?.GetSettingJson(LocalQueuedMessagesSettingKey(taskId));
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<LocalQueuedMessage[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void PersistLocalQueuedMessages(TaskProjection projection) =>
        _eventStore?.SetSettingJson(
            LocalQueuedMessagesSettingKey(projection.TaskId),
            JsonSerializer.Serialize(projection.LocalQueuedMessages));

    private void ThrowIfLocalMessageIsDispatching(Guid messageId)
    {
        if (_dispatchingLocalMessageIds.Contains(messageId))
        {
            throw new InvalidOperationException("这条消息正在发送。");
        }
    }

    private void RestartLocalQueueAutoStartIfFirstChanged(
        TaskProjection projection,
        Guid? previousFirstId)
    {
        var shouldRestart = false;
        var cancelled = false;
        lock (_gate)
        {
            if (!_latestTasks.TryGetValue(projection.TaskId, out var latest) ||
                latest.RunId != projection.RunId ||
                projection.LocalQueueAutoStartMessageId is null ||
                projection.LocalQueueAutoStartMessageId != previousFirstId)
            {
                return;
            }

            var currentFirstId = projection.LocalQueuedMessages.FirstOrDefault()?.Id;
            if (currentFirstId == previousFirstId)
            {
                return;
            }

            if (currentFirstId is null)
            {
                CancelLocalQueueAutoStartCore(projection);
                cancelled = true;
            }
            else
            {
                shouldRestart = true;
            }
        }

        if (cancelled)
        {
            NotifyTaskChanged(projection);
        }
        else if (shouldRestart)
        {
            ScheduleLocalQueueAutoStart(projection, restart: true);
        }
    }

    private void ScheduleLocalQueueAutoStart(TaskProjection projection, bool restart = false)
    {
        var settings = _taskSettingsResolver?.Invoke();
        if (settings?.AutoStartLocalQueueEnabled != true)
        {
            return;
        }

        CancellationTokenSource cancellation;
        Guid messageId;
        DateTimeOffset startsAt;
        lock (_gate)
        {
            if (!_latestTasks.TryGetValue(projection.TaskId, out var latest) ||
                latest.RunId != projection.RunId ||
                projection.Status != RunStatus.Completed ||
                projection.LocalQueuedMessages.Count == 0 ||
                _suppressedLocalQueueAutoStartRuns.Contains(projection.RunId))
            {
                return;
            }

            if (projection.LocalQueueAutoStartMessageId is not null && !restart)
            {
                return;
            }

            CancelLocalQueueAutoStartCore(projection);
            messageId = projection.LocalQueuedMessages[0].Id;
            startsAt = DateTimeOffset.UtcNow.AddSeconds(settings.AutoStartLocalQueueDelaySeconds);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _localQueueAutoStartCancellations[projection.RunId] = cancellation;
            projection.BeginLocalQueueAutoStart(messageId, startsAt);
        }

        NotifyTaskChanged(projection);
        _ = RunLocalQueueAutoStartAsync(
            projection.TaskId,
            projection.RunId,
            messageId,
            startsAt,
            cancellation);
    }

    private async Task RunLocalQueueAutoStartAsync(
        Guid taskId,
        Guid runId,
        Guid messageId,
        DateTimeOffset startsAt,
        CancellationTokenSource cancellation)
    {
        try
        {
            var delay = startsAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            TaskProjection? projection;
            lock (_gate)
            {
                projection = _taskConversations.TryGetValue(taskId, out var conversation)
                    ? conversation.FirstOrDefault(candidate => candidate.RunId == runId)
                    : null;
                if (projection is null ||
                    projection.TaskId != taskId ||
                    projection.RunId != runId ||
                    projection.Status != RunStatus.Completed ||
                    projection.LocalQueueAutoStartMessageId != messageId ||
                    projection.LocalQueuedMessages.FirstOrDefault()?.Id != messageId)
                {
                    return;
                }

                if (_localQueueAutoStartCancellations.TryGetValue(runId, out var registered) &&
                    ReferenceEquals(registered, cancellation))
                {
                    _localQueueAutoStartCancellations.Remove(runId);
                }
                projection.CancelLocalQueueAutoStart();
            }

            NotifyTaskChanged(projection);
            await DispatchLocalMessageAsync(taskId, messageId, "new-run", _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The queue item remains available when automatic dispatch cannot start it.
        }
        finally
        {
            lock (_gate)
            {
                if (_localQueueAutoStartCancellations.TryGetValue(runId, out var registered) &&
                    ReferenceEquals(registered, cancellation))
                {
                    _localQueueAutoStartCancellations.Remove(runId);
                }
            }
            cancellation.Dispose();
        }
    }

    private void CancelLocalQueueAutoStartCore(TaskProjection projection)
    {
        if (_localQueueAutoStartCancellations.Remove(projection.RunId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        projection.CancelLocalQueueAutoStart();
    }

    private static string LocalQueuedMessagesSettingKey(Guid taskId) =>
        $"local-message-queue:{taskId:N}";

    private void ScheduleTitleGeneration(TaskProjection projection, string fallbackTitle)
    {
        var settings = _taskSettingsResolver?.Invoke();
        if (_metadataGenerator is null || settings?.AiTitleEnabled != true)
        {
            return;
        }

        _ = GenerateTitleAsync(
            projection.TaskId,
            projection.Prompt,
            fallbackTitle,
            ResolveMetadataModel(settings),
            _lifetime.Token);
    }

    private async Task GenerateTitleAsync(
        Guid taskId,
        string prompt,
        string fallbackTitle,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var title = await _metadataGenerator!.GenerateTitleAsync(prompt, model, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            TaskProjection? changed = null;
            lock (_gate)
            {
                if (_manuallyRenamedTasks.Contains(taskId) ||
                    !_taskConversations.TryGetValue(taskId, out var conversation) ||
                    !conversation.Any(candidate =>
                        string.Equals(candidate.Title, fallbackTitle, StringComparison.Ordinal)))
                {
                    return;
                }

                _eventStore?.RenameTask(taskId, title);
                foreach (var candidate in conversation)
                {
                    candidate.Rename(title);
                }

                changed = _latestTasks.GetValueOrDefault(taskId);
            }

            if (changed is not null)
            {
                NotifyTaskChanged(changed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // AI metadata is best-effort; the deterministic title remains available.
        }
    }

    private void ScheduleSummaryGeneration(TaskProjection projection)
    {
        var settings = _taskSettingsResolver?.Invoke();
        if (_metadataGenerator is null || settings?.AiSummaryEnabled != true)
        {
            return;
        }

        var source = new RunSummarySource(
            projection.Title,
            projection.Prompt,
            projection.Status.ToString(),
            projection.RuntimeStatusDetail,
            projection.FinalAnswer,
            projection.AssistantText,
            projection.Transcript
                .Where(block =>
                    block.Kind == TranscriptBlockKind.Interaction &&
                    block.InteractionKind == "Question")
                .Select(block => new RunSummaryInteraction(
                    block.Content,
                    block.InteractionOptions ?? [],
                    block.Output,
                    block.Status.ToString()))
                .ToArray());
        _ = GenerateSummaryAsync(
            projection.TaskId,
            projection.RunId,
            source,
            ResolveMetadataModel(settings),
            _lifetime.Token);
    }

    private async Task GenerateSummaryAsync(
        Guid taskId,
        Guid runId,
        RunSummarySource source,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _metadataGenerator!.GenerateRunSummaryAsync(source, model, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return;
            }

            _eventStore?.UpdateRunSummary(taskId, runId, summary);
            TaskProjection? changed = null;
            lock (_gate)
            {
                var projection = _taskConversations.TryGetValue(taskId, out var conversation)
                    ? conversation.FirstOrDefault(candidate => candidate.RunId == runId)
                    : null;
                projection?.SetSummary(summary);
                changed = projection;
            }

            if (changed is not null)
            {
                NotifyTaskChanged(changed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // AI metadata is best-effort; a failed request leaves the AI-only summary empty.
        }
    }

    private static async Task PrewarmMetadataGeneratorAsync(
        ITaskMetadataGeneratorPrewarmer prewarmer,
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            await prewarmer.PrepareAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Metadata is best-effort; the first real request will rebuild the worker.
        }
    }

    private sealed class ScheduledRun(
        AgentRunRequest request,
        TaskProjection projection,
        CancellationToken cancellationToken)
    {
        public AgentRunRequest Request { get; } = request;
        public TaskProjection Projection { get; } = projection;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public string WorkspaceKey { get; } = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(projection.WorkingDirectory));
    }

    private WorkspaceHistoryEntry ApplyWorkspacePresentation(WorkspaceHistoryEntry workspace)
    {
        if (!_workspacePresentations.TryGetValue(workspace.Id, out var presentation))
        {
            return workspace;
        }

        return workspace with
        {
            Name = presentation.DisplayName ?? GetWorkspaceName(workspace.WorkingDirectory),
            IconKey = presentation.IconKey,
            ColorKey = presentation.ColorKey,
            DisplayName = presentation.DisplayName,
        };
    }

    private static string NormalizeWorkspacePresentationValue(
        string value,
        IReadOnlyCollection<string> allowed,
        string errorMessage)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw new ArgumentException(errorMessage);
    }

    private sealed record InMemoryWorkspacePresentation(
        string? DisplayName,
        string IconKey,
        string ColorKey);

    private static string ResolveMetadataModel(TaskSettings settings) =>
        new[] { settings.AiMetadataModel, settings.AiSummaryModel, settings.AiTitleModel }
            .FirstOrDefault(model => !string.IsNullOrWhiteSpace(model))
            ?.Trim() ?? string.Empty;

    private void RegisterConversation(IReadOnlyList<TaskProjection> conversation)
    {
        if (conversation.Count == 0)
        {
            return;
        }

        var taskId = conversation[^1].TaskId;
        var registered = conversation.ToList();
        _taskConversations[taskId] = registered;
        _latestTasks[taskId] = registered[^1];
    }

    private TaskProjection RequireCurrent() => Current ?? throw new InvalidOperationException("尚未创建任务。");

    private static string CreateTitle() => $"新任务 · {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";
}
