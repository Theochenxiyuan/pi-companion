using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using System.Text.Json;

namespace PiCompanion.Core.Tasks;

public sealed class TaskProjection
{
    private const int ActivityLimit = 40;
    private readonly List<TaskActivity> _activities = [];
    private readonly List<TaskArtifact> _artifacts = [];
    private readonly List<LocalQueuedMessage> _localQueuedMessages = [];
    private readonly List<TranscriptBlock> _transcript = [];
    private string? _currentAssistantBlockId;
    private string? _currentThinkingBlockId;

    public TaskProjection(
        Guid taskId,
        Guid runId,
        string title,
        string workingDirectory,
        string model,
        string thinkingLevel,
        IReadOnlyList<string>? attachments = null,
        string? prompt = null,
        DateTimeOffset? createdAt = null,
        string permissionMode = "standard",
        string? preferredModel = null,
        string? preferredThinkingLevel = null,
        TaskScopeKind scopeKind = TaskScopeKind.Workspace)
    {
        TaskId = taskId;
        RunId = runId;
        Title = title;
        WorkingDirectory = workingDirectory;
        Model = model;
        ThinkingLevel = thinkingLevel;
        PreferredModel = string.IsNullOrWhiteSpace(preferredModel) ? model : preferredModel;
        PreferredThinkingLevel = string.IsNullOrWhiteSpace(preferredThinkingLevel) ? thinkingLevel : preferredThinkingLevel;
        PermissionMode = permissionMode;
        ScopeKind = scopeKind;
        Attachments = attachments?.ToArray() ?? [];
        Prompt = string.IsNullOrWhiteSpace(prompt) ? title : prompt;
        _transcript.Add(new TranscriptBlock(
            $"user-{runId:N}-initial",
            TranscriptBlockKind.UserMessage,
            TranscriptBlockStatus.Completed,
            "你",
            Prompt,
            0,
            0,
            createdAt ?? DateTimeOffset.UtcNow));
    }

    public Guid TaskId { get; }

    public Guid RunId { get; }

    public string Title { get; private set; }

    public string WorkingDirectory { get; }

    public TaskScopeKind ScopeKind { get; }

    public bool HasUserWorkspace => ScopeKind == TaskScopeKind.Workspace;

    public string Model { get; }

    public string ThinkingLevel { get; }

    public string PreferredModel { get; private set; }

    public string PreferredThinkingLevel { get; private set; }

    public string PermissionMode { get; }

    public IReadOnlyList<string> Attachments { get; }

    public IReadOnlyList<TaskArtifact> Artifacts => _artifacts;

    public string Prompt { get; }

    public RunStatus Status { get; private set; } = RunStatus.Draft;

    public long LastSequence { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    public string RuntimeStatusDetail { get; private set; } = string.Empty;

    public string? ActivityStatus { get; private set; }

    public string? AssistantText { get; private set; }

    public string? FinalAnswer { get; private set; }

    public IReadOnlyList<TaskActivity> Activities => _activities;

    public IReadOnlyList<TranscriptBlock> Transcript => _transcript;

    public IReadOnlyList<string> PendingSteering { get; private set; } = [];

    public IReadOnlyList<string> PendingFollowUps { get; private set; } = [];

    public IReadOnlyList<LocalQueuedMessage> LocalQueuedMessages => _localQueuedMessages;

    public Guid? LocalQueueAutoStartMessageId { get; private set; }

    public DateTimeOffset? LocalQueueAutoStartAt { get; private set; }

    public void RestoreArtifacts(IEnumerable<TaskArtifact>? artifacts)
    {
        _artifacts.Clear();
        if (artifacts is null)
        {
            return;
        }

        _artifacts.AddRange(artifacts
            .Where(artifact => artifact.TaskId == TaskId)
            .OrderBy(artifact => artifact.CreatedAt));
    }

    public bool AddArtifact(TaskArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.TaskId != TaskId || _artifacts.Any(existing => existing.Id == artifact.Id))
        {
            return false;
        }

        _artifacts.Add(artifact);
        _artifacts.Sort(static (left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
        return true;
    }

    public void BeginLocalQueueAutoStart(Guid messageId, DateTimeOffset startsAt)
    {
        if (_localQueuedMessages.All(message => message.Id != messageId))
        {
            throw new InvalidOperationException("未找到这条待发送消息。");
        }

        LocalQueueAutoStartMessageId = messageId;
        LocalQueueAutoStartAt = startsAt;
    }

    public void CancelLocalQueueAutoStart()
    {
        LocalQueueAutoStartMessageId = null;
        LocalQueueAutoStartAt = null;
    }

    public void RestoreLocalQueuedMessages(IEnumerable<LocalQueuedMessage>? messages)
    {
        _localQueuedMessages.Clear();
        if (messages is null)
        {
            return;
        }

        foreach (var message in messages
            .Where(item => item.Id != Guid.Empty && !string.IsNullOrWhiteSpace(item.Message)))
        {
            _localQueuedMessages.Add(message with
            {
                Message = NormalizeLocalMessage(message.Message),
                Attachments = NormalizeLocalAttachments(message.Attachments, validateAvailability: false),
            });
        }
    }

    public LocalQueuedMessage AddLocalQueuedMessage(
        string message,
        IReadOnlyList<string>? attachments = null)
    {
        var item = new LocalQueuedMessage(
            Guid.NewGuid(),
            NormalizeLocalMessage(message),
            DateTimeOffset.UtcNow,
            NormalizeLocalAttachments(attachments, validateAvailability: true));
        _localQueuedMessages.Add(item);
        return item;
    }

    public LocalQueuedMessage UpdateLocalQueuedMessage(
        Guid messageId,
        string message,
        IReadOnlyList<string>? attachments = null)
    {
        var index = _localQueuedMessages.FindIndex(item => item.Id == messageId);
        if (index < 0)
        {
            throw new InvalidOperationException("未找到这条待发送消息。");
        }

        var updated = _localQueuedMessages[index] with
        {
            Message = NormalizeLocalMessage(message),
            Attachments = NormalizeLocalAttachments(attachments, validateAvailability: true),
        };
        _localQueuedMessages[index] = updated;
        return updated;
    }

    public void MoveLocalQueuedMessage(Guid messageId, int newIndex)
    {
        var currentIndex = _localQueuedMessages.FindIndex(item => item.Id == messageId);
        if (currentIndex < 0)
        {
            throw new InvalidOperationException("未找到这条待发送消息。");
        }

        if (newIndex < 0 || newIndex >= _localQueuedMessages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        }

        if (currentIndex == newIndex)
        {
            return;
        }

        var message = _localQueuedMessages[currentIndex];
        _localQueuedMessages.RemoveAt(currentIndex);
        _localQueuedMessages.Insert(newIndex, message);
    }

    public LocalQueuedMessage RemoveLocalQueuedMessage(Guid messageId)
    {
        var index = _localQueuedMessages.FindIndex(item => item.Id == messageId);
        if (index < 0)
        {
            throw new InvalidOperationException("未找到这条待发送消息。");
        }

        var removed = _localQueuedMessages[index];
        _localQueuedMessages.RemoveAt(index);
        return removed;
    }

    private static string NormalizeLocalMessage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return message.Trim();
    }

    private static IReadOnlyList<string> NormalizeLocalAttachments(
        IReadOnlyList<string>? attachments,
        bool validateAvailability)
    {
        if (attachments is not { Count: > 0 })
        {
            return [];
        }

        if (validateAvailability)
        {
            return TaskAttachmentRules.NormalizeAndValidate(attachments);
        }

        var normalized = new List<string>(attachments.Count);
        foreach (var attachment in attachments)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(attachment) && Path.IsPathFullyQualified(attachment))
                {
                    normalized.Add(Path.GetFullPath(attachment));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).Take(TaskAttachmentRules.MaximumCount).ToArray();
    }

    public void Rename(string title)
    {
        var normalized = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            throw new ArgumentException("任务名称不能为空。", nameof(title));
        }

        Title = normalized;
    }

    public void UpdateExecutionDefaults(string model, string thinkingLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(thinkingLevel);
        PreferredModel = model.Trim();
        PreferredThinkingLevel = thinkingLevel.Trim();
    }

    public void SetSummary(string summary)
    {
        var normalized = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            throw new ArgumentException("任务总结不能为空。", nameof(summary));
        }

        Summary = normalized;
    }

    public bool Apply(CompanionRunEvent runEvent)
    {
        if (runEvent.TaskId != TaskId || runEvent.RunId != RunId || runEvent.Sequence <= LastSequence)
        {
            return false;
        }

        LastSequence = runEvent.Sequence;
        Status = runEvent.Status;
        ApplyTranscriptEvent(runEvent);
        ApplyQueueEvent(runEvent);

        var isActive = runEvent.Status.IsActive();
        if (runEvent.Payload.TryGetValue("activityStatus", out var activityStatus) &&
            !string.IsNullOrWhiteSpace(activityStatus))
        {
            ActivityStatus = activityStatus;
        }
        else if (isActive &&
            runEvent.Payload.TryGetValue("summary", out var legacyActivityStatus) &&
            !string.IsNullOrWhiteSpace(legacyActivityStatus))
        {
            ActivityStatus = legacyActivityStatus;
        }

        if (!isActive)
        {
            ActivityStatus = null;
        }

        if (!isActive && runEvent.Payload.TryGetValue("summary", out var runtimeStatusDetail))
        {
            RuntimeStatusDetail = GetUserFacingRuntimeStatusDetail(runEvent, runtimeStatusDetail);
        }

        if (runEvent.Payload.TryGetValue("finalText", out var finalAnswer) && !string.IsNullOrWhiteSpace(finalAnswer))
        {
            FinalAnswer = finalAnswer;
            AssistantText = finalAnswer;
        }

        if (runEvent.Kind is not (CompanionRunEventKind.AssistantTextDelta or CompanionRunEventKind.AssistantThinkingDelta) &&
            runEvent.Payload.TryGetValue("activity", out var activity) &&
            !string.IsNullOrWhiteSpace(activity))
        {
            _activities.Add(new TaskActivity(runEvent.Sequence, runEvent.Kind, activity, runEvent.Timestamp));
            if (_activities.Count > ActivityLimit)
            {
                _activities.RemoveRange(0, _activities.Count - ActivityLimit);
            }
        }

        return true;
    }

    private void ApplyTranscriptEvent(CompanionRunEvent runEvent)
    {
        switch (runEvent.Kind)
        {
            case CompanionRunEventKind.UserMessageAdded:
                AddUserMessage(runEvent);
                break;
            case CompanionRunEventKind.AssistantMessageStarted:
                CompleteCurrentAssistantBlocks(runEvent.Sequence);
                _currentAssistantBlockId = null;
                _currentThinkingBlockId = null;
                AssistantText = null;
                break;
            case CompanionRunEventKind.AssistantTextDelta:
                AppendAssistantDelta(runEvent);
                break;
            case CompanionRunEventKind.AssistantThinkingDelta:
                AppendThinkingDelta(runEvent);
                break;
            case CompanionRunEventKind.AssistantMessageCompleted:
                CompleteAssistantMessage(runEvent);
                break;
            case CompanionRunEventKind.ToolStarted:
            case CompanionRunEventKind.ToolProgressed:
            case CompanionRunEventKind.ToolCompleted:
            case CompanionRunEventKind.ToolFailed:
                UpsertToolBlock(runEvent);
                break;
            case CompanionRunEventKind.ApprovalRequested:
            case CompanionRunEventKind.QuestionRequested:
                AddInteractionBlock(runEvent);
                break;
            case CompanionRunEventKind.InteractionResolved:
                ResolveInteractionBlock(runEvent);
                break;
            case CompanionRunEventKind.CompactionStarted:
            case CompanionRunEventKind.CompactionCompleted:
            case CompanionRunEventKind.AutoRetryStarted:
            case CompanionRunEventKind.AutoRetryCompleted:
            case CompanionRunEventKind.SummarizationRetryStarted:
            case CompanionRunEventKind.SummarizationRetryProgressed:
            case CompanionRunEventKind.SummarizationRetryCompleted:
                UpsertLifecycleNotice(runEvent);
                break;
            case CompanionRunEventKind.WarningRaised:
            case CompanionRunEventKind.RunFailed:
                if (runEvent.Kind == CompanionRunEventKind.RunFailed) CancelPendingInteractionBlocks(runEvent.Sequence);
                AddNoticeBlock(runEvent);
                break;
            case CompanionRunEventKind.RunInterrupted:
                CancelPendingInteractionBlocks(runEvent.Sequence);
                break;
            case CompanionRunEventKind.RunSettled:
                CancelPendingInteractionBlocks(runEvent.Sequence);
                break;
        }
    }

    private void UpsertLifecycleNotice(CompanionRunEvent runEvent)
    {
        var isCompaction = runEvent.Kind is CompanionRunEventKind.CompactionStarted or
            CompanionRunEventKind.CompactionCompleted;
        var isSummarizationRetry = runEvent.Kind is CompanionRunEventKind.SummarizationRetryStarted or
            CompanionRunEventKind.SummarizationRetryProgressed or
            CompanionRunEventKind.SummarizationRetryCompleted;
        var isStarted = runEvent.Kind is CompanionRunEventKind.CompactionStarted or
            CompanionRunEventKind.AutoRetryStarted or
            CompanionRunEventKind.SummarizationRetryStarted;
        var isRunning = isStarted || runEvent.Kind == CompanionRunEventKind.SummarizationRetryProgressed;
        var title = isCompaction ? "上下文压缩" : isSummarizationRetry ? "摘要重试" : "自动重试";
        var cancelled = string.Equals(GetPayload(runEvent, "cancelled"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetPayload(runEvent, "aborted"), "true", StringComparison.OrdinalIgnoreCase);
        var status = isRunning
            ? TranscriptBlockStatus.Running
            : cancelled
                ? TranscriptBlockStatus.Cancelled
                : string.Equals(GetPayload(runEvent, "success"), "false", StringComparison.OrdinalIgnoreCase)
                    ? TranscriptBlockStatus.Failed
                    : TranscriptBlockStatus.Completed;
        var index = _transcript.FindLastIndex(block =>
            block.Kind == TranscriptBlockKind.Notice &&
            block.Title == title &&
            block.Status == TranscriptBlockStatus.Running);
        var content = GetPayload(runEvent, "activity") ?? GetPayload(runEvent, "summary") ?? title;
        if (index < 0 || isStarted)
        {
            _transcript.Add(new TranscriptBlock(
                $"lifecycle-{runEvent.EventId:N}",
                TranscriptBlockKind.Notice,
                status,
                title,
                content,
                runEvent.Sequence,
                runEvent.Sequence,
                runEvent.Timestamp));
            return;
        }

        var block = _transcript[index];
        _transcript[index] = block with
        {
            Status = status,
            Content = content,
            LastSequence = runEvent.Sequence,
        };
    }

    private void AddUserMessage(CompanionRunEvent runEvent)
    {
        var content = GetPayload(runEvent, "message") ?? GetPayload(runEvent, "activity");
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _transcript.Add(new TranscriptBlock(
            $"user-{runEvent.EventId:N}",
            TranscriptBlockKind.UserMessage,
            TranscriptBlockStatus.Completed,
            GetPayload(runEvent, "delivery") switch
            {
                "steer" => "你 · 调整方向",
                "follow_up" => "你 · 后续任务",
                _ => "你",
            },
            content,
            runEvent.Sequence,
            runEvent.Sequence,
            runEvent.Timestamp));
    }

    private void AppendAssistantDelta(CompanionRunEvent runEvent)
    {
        var delta = GetPayload(runEvent, "delta");
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        var index = EnsureCurrentBlock(
            ref _currentAssistantBlockId,
            runEvent,
            TranscriptBlockKind.AssistantMessage,
            "Pi Companion");
        var block = _transcript[index];
        _transcript[index] = block with
        {
            Content = string.Concat(block.Content, delta),
            LastSequence = runEvent.Sequence,
            Status = TranscriptBlockStatus.Running,
        };
        AssistantText = string.Concat(AssistantText, delta);
    }

    private void AppendThinkingDelta(CompanionRunEvent runEvent)
    {
        var delta = GetPayload(runEvent, "delta");
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        var index = EnsureCurrentBlock(
            ref _currentThinkingBlockId,
            runEvent,
            TranscriptBlockKind.Thinking,
            "思考过程");
        var block = _transcript[index];
        _transcript[index] = block with
        {
            Content = string.Concat(block.Content, delta),
            LastSequence = runEvent.Sequence,
            Status = TranscriptBlockStatus.Running,
        };
    }

    private void CompleteAssistantMessage(CompanionRunEvent runEvent)
    {
        var finalText = GetPayload(runEvent, "finalText");
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            var index = EnsureCurrentBlock(
                ref _currentAssistantBlockId,
                runEvent,
                TranscriptBlockKind.AssistantMessage,
                "Pi Companion");
            var block = _transcript[index];
            _transcript[index] = block with
            {
                Content = finalText,
                LastSequence = runEvent.Sequence,
                Status = TranscriptBlockStatus.Completed,
            };
        }
        else
        {
            CompleteBlock(_currentAssistantBlockId, runEvent.Sequence);
        }

        CompleteBlock(_currentThinkingBlockId, runEvent.Sequence);
    }

    private void UpsertToolBlock(CompanionRunEvent runEvent)
    {
        var toolCallId = GetPayload(runEvent, "toolCallId") ?? runEvent.EventId.ToString("N");
        var id = $"tool-{toolCallId}";
        var index = _transcript.FindIndex(block => block.Id == id);
        var status = runEvent.Kind switch
        {
            CompanionRunEventKind.ToolStarted or CompanionRunEventKind.ToolProgressed => TranscriptBlockStatus.Running,
            CompanionRunEventKind.ToolFailed => TranscriptBlockStatus.Failed,
            _ => TranscriptBlockStatus.Completed,
        };
        var title = GetPayload(runEvent, "toolName") ?? "工具";
        var content = GetPayload(runEvent, "activity") ?? title;
        var input = GetPayload(runEvent, "toolInput");
        var output = GetPayload(runEvent, "toolOutput");
        var blockKind = string.Equals(title, "web_search", StringComparison.OrdinalIgnoreCase)
            ? TranscriptBlockKind.WebSearch
            : TranscriptBlockKind.Tool;

        if (index < 0)
        {
            _transcript.Add(new TranscriptBlock(
                id,
                blockKind,
                status,
                title,
                content,
                runEvent.Sequence,
                runEvent.Sequence,
                runEvent.Timestamp,
                input,
                output));
            return;
        }

        var block = _transcript[index];
        _transcript[index] = block with
        {
            Kind = blockKind,
            Status = status,
            Title = title,
            Content = content,
            LastSequence = runEvent.Sequence,
            Input = input ?? block.Input,
            Output = output ?? block.Output,
        };
    }

    private void AddInteractionBlock(CompanionRunEvent runEvent)
    {
        var interactionId = GetPayload(runEvent, "interactionId") ?? runEvent.EventId.ToString("N");
        _transcript.Add(new TranscriptBlock(
            $"interaction-{interactionId}",
            TranscriptBlockKind.Interaction,
            TranscriptBlockStatus.Pending,
            runEvent.Kind == CompanionRunEventKind.ApprovalRequested ? "需要授权" : "需要你的回答",
            GetPayload(runEvent, "activity") ?? "Pi Agent 正在等待响应。",
            runEvent.Sequence,
            runEvent.Sequence,
            runEvent.Timestamp,
            InteractionId: interactionId,
            InteractionMethod: GetPayload(runEvent, "interactionMethod"),
            InteractionKind: runEvent.Kind == CompanionRunEventKind.ApprovalRequested ? "Approval" : "Question",
            InteractionOptions: DeserializeStringArray(GetPayload(runEvent, "interactionOptions"))));
    }

    private void ResolveInteractionBlock(CompanionRunEvent runEvent)
    {
        var interactionId = GetPayload(runEvent, "interactionId");
        var index = _transcript.FindLastIndex(block =>
            block.Kind == TranscriptBlockKind.Interaction &&
            block.Status == TranscriptBlockStatus.Pending &&
            (interactionId is null || block.InteractionId == interactionId));
        if (index < 0)
        {
            return;
        }

        var block = _transcript[index];
        var approved = !string.Equals(GetPayload(runEvent, "approved"), "false", StringComparison.OrdinalIgnoreCase);
        var output = approved
            ? GetPayload(runEvent, "response") ??
              (block.InteractionKind == "Approval" ? "已允许" : null)
            : block.InteractionKind == "Approval" ? "已拒绝" : null;
        _transcript[index] = block with
        {
            Status = approved ? TranscriptBlockStatus.Completed : TranscriptBlockStatus.Cancelled,
            LastSequence = runEvent.Sequence,
            Output = output,
        };
    }

    private void CancelPendingInteractionBlocks(long sequence)
    {
        for (var index = 0; index < _transcript.Count; index++)
        {
            var block = _transcript[index];
            if (block.Kind != TranscriptBlockKind.Interaction || block.Status != TranscriptBlockStatus.Pending)
            {
                continue;
            }

            _transcript[index] = block with
            {
                Status = TranscriptBlockStatus.Cancelled,
                LastSequence = sequence,
                Output = "运行结束，交互已取消",
            };
        }
    }

    private void AddNoticeBlock(CompanionRunEvent runEvent)
    {
        var content = GetPayload(runEvent, "activity") ?? GetPayload(runEvent, "summary");
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _transcript.Add(new TranscriptBlock(
            $"notice-{runEvent.EventId:N}",
            TranscriptBlockKind.Notice,
            runEvent.Kind == CompanionRunEventKind.RunFailed
                ? TranscriptBlockStatus.Failed
                : TranscriptBlockStatus.Completed,
            runEvent.Kind switch
            {
                CompanionRunEventKind.WarningRaised => "警告",
                _ => "运行失败",
            },
            content,
            runEvent.Sequence,
            runEvent.Sequence,
            runEvent.Timestamp));
    }

    private static string GetUserFacingRuntimeStatusDetail(CompanionRunEvent runEvent, string fallback)
    {
        if (runEvent.Kind != CompanionRunEventKind.RunInterrupted ||
            !runEvent.Payload.TryGetValue("exitReason", out var exitReason))
        {
            return fallback;
        }

        return exitReason switch
        {
            "application-restart" => "应用关闭时任务仍在进行，你可以继续提问",
            "startup-cancelled" => "启动已取消",
            "user-abort" => "已按你的要求停止",
            "abort-timeout" => "停止响应较慢，已为你强制结束",
            _ when exitReason.StartsWith("process-exit-", StringComparison.Ordinal) => "运行意外结束，你可以重试或继续提问",
            _ => "任务已停止",
        };
    }

    private int EnsureCurrentBlock(
        ref string? currentId,
        CompanionRunEvent runEvent,
        TranscriptBlockKind kind,
        string title)
    {
        if (currentId is not null)
        {
            var existingId = currentId;
            var existingIndex = _transcript.FindIndex(block => block.Id == existingId);
            if (existingIndex >= 0)
            {
                return existingIndex;
            }
        }

        currentId = $"{kind.ToString().ToLowerInvariant()}-{runEvent.EventId:N}";
        _transcript.Add(new TranscriptBlock(
            currentId,
            kind,
            TranscriptBlockStatus.Running,
            title,
            string.Empty,
            runEvent.Sequence,
            runEvent.Sequence,
            runEvent.Timestamp));
        return _transcript.Count - 1;
    }

    private void CompleteCurrentAssistantBlocks(long sequence)
    {
        CompleteBlock(_currentAssistantBlockId, sequence);
        CompleteBlock(_currentThinkingBlockId, sequence);
    }

    private void CompleteBlock(string? id, long sequence)
    {
        if (id is null)
        {
            return;
        }

        var index = _transcript.FindIndex(block => block.Id == id);
        if (index < 0)
        {
            return;
        }

        var block = _transcript[index];
        _transcript[index] = block with
        {
            LastSequence = sequence,
            Status = TranscriptBlockStatus.Completed,
        };
    }

    private static string? GetPayload(CompanionRunEvent runEvent, string key) =>
        runEvent.Payload.TryGetValue(key, out var value) ? value : null;

    private void ApplyQueueEvent(CompanionRunEvent runEvent)
    {
        if (runEvent.Kind != CompanionRunEventKind.QueueChanged)
        {
            return;
        }

        if (GetPayload(runEvent, "steeringQueue") is { } steering)
        {
            PendingSteering = DeserializeStringArray(steering);
        }

        if (GetPayload(runEvent, "followUpQueue") is { } followUps)
        {
            PendingFollowUps = DeserializeStringArray(followUps);
        }
    }

    private static IReadOnlyList<string> DeserializeStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
