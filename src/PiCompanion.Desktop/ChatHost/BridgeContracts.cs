using System.IO;
using PiCompanion.Core.Events;
using PiCompanion.Core.Evidence;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Settings;
using PiCompanion.Application.Skills;
using PiCompanion.Desktop.Shell;

namespace PiCompanion.Desktop.ChatHost;

internal static class BridgeContracts
{
    public const int ProtocolVersion = 57;

    public static InitializeSnapshotDto CreateSnapshot(
        TaskProjection? projection,
        IReadOnlyList<TaskProjection> conversation,
        IReadOnlyList<WorkspaceHistoryEntry> workspaces,
        IReadOnlyList<TaskHistoryEntry> recentTasks,
        IReadOnlyList<TaskHistoryEntry> historyTasks,
        bool historyHasMore,
        IReadOnlyList<TaskHistoryEntry> recycleBinTasks,
        ComposerDraft? draft,
        SettingsSnapshotDto settings,
        Func<Guid, RunEvidenceSnapshot>? evidenceResolver = null) => new(
        projection is null ? null : CreateTask(projection, conversation, evidenceResolver),
        projection?.LastSequence ?? 0,
        workspaces.Select(CreateWorkspace).ToArray(),
        recentTasks.Select(CreateHistoryTask).ToArray(),
        historyTasks.Select(CreateHistoryTask).ToArray(),
        historyHasMore,
        recycleBinTasks.Select(CreateHistoryTask).ToArray(),
        draft,
        settings,
        new[]
        {
            "pi-rpc", "sqlite-history", "session-recovery", "workspace-permissions",
            "workspace-customization", "workspace-git-ai-commit-message",
            "write-tools", "shell-approval", "ask-user", "steer", "follow-up", "queue-view", "abort",
            "task-multi-run", "task-history", "task-search", "task-filter", "task-rename", "recycle-bin",
            "incremental-task-delta", "virtual-transcript", "safe-markdown",
            "file-evidence", "git-diff", "test-evidence", "safe-file-recovery",
            "workspace-file-browser", "workspace-git-browser", "workspace-git-write", "session-statistics",
            "settings", "ui-scale", "pi-provider-settings", "pi-custom-provider", "pi-custom-provider-edit", "pi-model-catalog", "diagnostics-export",
            "task-notifications", "file-change-collapse-default", "task-completion-behavior", "data-retention",
            "pi-compaction-strategy", "pi-retry-strategy", "pi-message-delivery",
            "task-execution-defaults",
            "local-message-queue", "local-message-queue-attachments", "local-message-queue-auto-start",
            "image-attachment-thumbnails", "managed-clipboard-attachments",
            "open-current-task", "general-chat", "published-artifacts",
            "independent-workspaces", "workspace-new-task",
            "skill-native-discovery", "skill-content-fingerprints", "skill-pi-removal",
            "skill-local-direct-import", "skill-workspace-trust",
        });

    public static SkillsLoadedDto CreateSkillsLoaded(
        string requestId,
        SkillDiscoverySnapshot snapshot) => new(
        requestId,
        snapshot.ScannedAt,
        snapshot.Skills
            .GroupBy(static skill => skill.Name, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DiscoveredSkillDto(
                group.Key,
                group.Key,
                group
                    .GroupBy(
                        static skill => skill.ContentHash ?? $"unavailable:{skill.CanonicalPath}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(variant =>
                    {
                        var representative = variant.First();
                        return new SkillContentVariantDto(
                            representative.ContentHash ?? variant.Key,
                            representative.ContentHash,
                            representative.Description,
                            representative.Version,
                            representative.License,
                            representative.Metadata,
                            representative.DisableModelInvocation,
                            representative.IsAvailable,
                            representative.FileCount,
                            representative.TotalSize,
                            representative.LastModifiedAt,
                            variant
                                .OrderBy(static skill => skill.FilePath, StringComparer.OrdinalIgnoreCase)
                                .Select(CreateSkillInstallation)
                                .ToArray());
                    })
                    .OrderByDescending(static variant => variant.Installations.Any(
                        static installation => installation.IsGloballyEffective))
                    .ThenBy(static variant => variant.Id, StringComparer.Ordinal)
                    .ToArray(),
                group
                    .SelectMany(static skill => skill.Diagnostics)
                    .Select(CreateSkillDiagnostic)
                    .ToArray()))
            .ToArray(),
        snapshot.Locations.Select(location => new SkillScanLocationDto(
            location.Id,
            location.Scope,
            location.Source,
            location.Path,
            location.Status,
            location.SkillCount,
            location.WorkspaceId,
            location.WorkspaceName,
            location.WorkspacePath,
            location.Inherited,
            location.Message)).ToArray(),
        snapshot.Diagnostics.Select(CreateSkillDiagnostic).ToArray(),
        snapshot.WorkspaceTrust.Select(trust => new SkillWorkspaceTrustDto(
            trust.WorkspaceId,
            trust.WorkspaceName,
            trust.WorkspacePath,
            trust.Status,
            trust.DecisionPath,
            trust.Inherited)).ToArray());

    public static SkillRemovalCompletedDto CreateSkillRemovalCompleted(
        string requestId,
        bool succeeded,
        string message,
        string? removedInstallationId,
        string? recoveryPath,
        SkillDiscoverySnapshot snapshot) => new(
        requestId,
        succeeded,
        message,
        removedInstallationId,
        recoveryPath,
        CreateSkillsLoaded(requestId, snapshot));

    public static SkillWorkspaceTrustCompletedDto CreateSkillWorkspaceTrustCompleted(
        string requestId,
        bool succeeded,
        string message,
        Guid workspaceId,
        SkillDiscoverySnapshot snapshot) => new(
        requestId,
        succeeded,
        message,
        workspaceId,
        CreateSkillsLoaded(requestId, snapshot));

    public static SkillImportSourceInspectedDto CreateSkillImportSourceInspected(
        string requestId,
        bool succeeded,
        bool cancelled,
        string message,
        SkillImportSourceInspection? source) => new(
        requestId,
        succeeded,
        cancelled,
        message,
        source is null ? null : new SkillImportSourceDto(
            source.Token,
            source.Name,
            source.Description,
            source.SourceKind,
            source.ContentHash,
            source.FileCount,
            source.TotalBytes,
            source.Files.Select(CreateSkillImportFile).ToArray(),
            source.ScriptFiles,
            source.ExecutableFiles));

    public static SkillImportReadyDto CreateSkillImportReady(
        string requestId,
        bool succeeded,
        string message,
        SkillImportPreparation? preparation) => new(
        requestId,
        succeeded,
        message,
        preparation is null ? null : new SkillImportPreparationDto(
            preparation.Token,
            preparation.SourceToken,
            preparation.Name,
            preparation.Description,
            preparation.Scope,
            preparation.WorkspaceId,
            preparation.WorkspaceName,
            preparation.TargetPath,
            preparation.SourceKind,
            preparation.ContentHash,
            preparation.FileCount,
            preparation.TotalBytes,
            preparation.Files.Select(CreateSkillImportFile).ToArray(),
            preparation.ScriptFiles,
            preparation.ExecutableFiles,
            preparation.RequiresProjectTrust,
            preparation.TrustStatus));

    private static SkillImportFileDto CreateSkillImportFile(SkillImportFile file) =>
        new(file.RelativePath, file.Size, file.Kind);

    public static SkillImportCompletedDto CreateSkillImportCompleted(
        string requestId,
        bool succeeded,
        bool cancelled,
        string message,
        string? skillName,
        string? targetPath,
        SkillDiscoverySnapshot snapshot) => new(
        requestId,
        succeeded,
        cancelled,
        message,
        skillName,
        targetPath,
        CreateSkillsLoaded(requestId, snapshot));

    private static SkillInstallationDto CreateSkillInstallation(DiscoveredSkill skill)
    {
        var removable = SkillRemovalService.CanRemove(skill, out var removalReason);
        return new SkillInstallationDto(
            SkillRemovalService.CreateInstallationId(skill),
            skill.FilePath,
            skill.BaseDirectory,
            skill.CanonicalPath,
            skill.InstallPath,
            skill.IsSingleFile,
            skill.IsGloballyEffective,
            skill.EffectiveWorkspaceIds,
            skill.Origins.Select(origin => new SkillOriginDto(
                origin.Scope,
                origin.Source,
                origin.RootPath,
                origin.WorkspaceId,
                origin.WorkspaceName,
                origin.WorkspacePath,
                origin.Inherited,
                origin.InstallPath,
                origin.IsCompatibilityLink,
                origin.LinkTarget)).ToArray(),
            skill.Diagnostics.Select(CreateSkillDiagnostic).ToArray(),
            removable,
            removalReason);
    }

    private static SkillDiagnosticDto CreateSkillDiagnostic(SkillDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Severity,
        diagnostic.Message,
        diagnostic.Path,
        diagnostic.WinnerPath,
        diagnostic.WorkspaceId,
        diagnostic.WorkspaceName);

    public static SettingsSnapshotDto CreateSettingsSnapshot(
        PiCompanionSettings settings,
        PiConfigurationSnapshot piConfiguration)
    {
        var projectedSettings = settings;
        if (piConfiguration.Available && !string.IsNullOrWhiteSpace(piConfiguration.DefaultModel))
        {
            projectedSettings = projectedSettings with
            {
                Agent = projectedSettings.Agent with
                {
                    DefaultModel = piConfiguration.DefaultModel,
                    DefaultThinkingLevel = piConfiguration.DefaultThinkingLevel,
                    AutoCompact = piConfiguration.AutoCompact,
                    AutoRetry = piConfiguration.AutoRetry,
                    CompactionReserveTokens = piConfiguration.CompactionReserveTokens,
                    CompactionKeepRecentTokens = piConfiguration.CompactionKeepRecentTokens,
                    RetryMaxRetries = piConfiguration.RetryMaxRetries,
                    RetryBaseDelayMilliseconds = piConfiguration.RetryBaseDelayMilliseconds,
                    RetryMaxDelayMilliseconds = piConfiguration.RetryMaxDelayMilliseconds,
                    SteeringMode = piConfiguration.SteeringMode,
                    FollowUpMode = piConfiguration.FollowUpMode,
                },
            };
        }

        var concreteDefaultModel = piConfiguration.DefaultModel ?? projectedSettings.Agent.DefaultModel;
        if (!string.IsNullOrWhiteSpace(concreteDefaultModel))
        {
            var metadataModel = string.IsNullOrWhiteSpace(projectedSettings.Tasks.AiMetadataModel)
                ? string.IsNullOrWhiteSpace(projectedSettings.Tasks.AiSummaryModel)
                    ? string.IsNullOrWhiteSpace(projectedSettings.Tasks.AiTitleModel)
                        ? concreteDefaultModel
                        : projectedSettings.Tasks.AiTitleModel
                    : projectedSettings.Tasks.AiSummaryModel
                : projectedSettings.Tasks.AiMetadataModel;
            projectedSettings = projectedSettings with
            {
                Tasks = projectedSettings.Tasks with
                {
                    AiTitleModel = metadataModel,
                    AiSummaryModel = metadataModel,
                    AiMetadataModel = metadataModel,
                },
            };
        }
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion");
        return new SettingsSnapshotDto(
            projectedSettings,
            piConfiguration,
            dataDirectory,
            Path.Combine(dataDirectory, "logs"));
    }

    public static TaskCollectionsDto CreateTaskCollections(
        IReadOnlyList<WorkspaceHistoryEntry> workspaces,
        IReadOnlyList<TaskHistoryEntry> recentTasks,
        IReadOnlyList<TaskHistoryEntry> historyTasks,
        bool historyHasMore,
        IReadOnlyList<TaskHistoryEntry> recycleBinTasks) => new(
        workspaces.Select(CreateWorkspace).ToArray(),
        recentTasks.Select(CreateHistoryTask).ToArray(),
        historyTasks.Select(CreateHistoryTask).ToArray(),
        historyHasMore,
        recycleBinTasks.Select(CreateHistoryTask).ToArray());

    public static WorkspaceHistoryEntryDto CreateWorkspace(WorkspaceHistoryEntry workspace) => new(
        workspace.Id,
        workspace.Name,
        workspace.WorkingDirectory,
        workspace.CreatedAt,
        workspace.UpdatedAt,
        workspace.TaskCount,
        workspace.HasActiveTask,
        workspace.IconKey,
        workspace.ColorKey,
        workspace.DisplayName);

    public static TaskHistoryEntryDto CreateHistoryTask(TaskHistoryEntry task) => new(
        task.TaskId,
        task.RunId,
        task.Title,
        task.ScopeKind == TaskScopeKind.Workspace ? task.WorkingDirectory : string.Empty,
        task.ScopeKind.ToString(),
        task.Status.ToString(),
        task.Status.ToDisplayText(),
        task.Summary,
        task.UpdatedAt,
        task.DeletedAt,
        task.WorkspaceId);

    public static TaskSnapshotDto CreateTask(
        TaskProjection projection,
        IReadOnlyList<TaskProjection>? conversation = null,
        Func<Guid, RunEvidenceSnapshot>? evidenceResolver = null) => new(
        projection.TaskId,
        projection.RunId,
        projection.Title,
        projection.Prompt,
        projection.ScopeKind == TaskScopeKind.Workspace ? projection.WorkingDirectory : string.Empty,
        projection.ScopeKind.ToString(),
        projection.PreferredModel,
        projection.PreferredThinkingLevel,
        projection.PermissionMode,
        projection.Attachments,
        projection.Artifacts.Select(CreateArtifact).ToArray(),
        projection.Status.ToString(),
        projection.Status.ToDisplayText(),
        projection.Summary,
        projection.ActivityStatus,
        projection.AssistantText,
        projection.FinalAnswer,
        projection.LastSequence,
        projection.PendingSteering,
        projection.PendingFollowUps,
        projection.LocalQueuedMessages.Select(message => new LocalQueuedMessageDto(
            message.Id,
            message.Message,
            message.CreatedAt,
            (message.Attachments ?? []).Select(ComposerAttachment.FromPath).ToArray())).ToArray(),
        projection.LocalQueueAutoStartMessageId,
        projection.LocalQueueAutoStartAt,
        projection.Transcript.Select(CreateTranscriptBlock).ToArray(),
        CreateRuns(projection, conversation, evidenceResolver),
        projection.Activities.Select(activity => new ActivityDto(
            activity.Sequence,
            activity.Kind.ToString(),
            activity.Text,
            activity.Timestamp)).ToArray());

    private static IReadOnlyList<TaskRunSnapshotDto> CreateRuns(
        TaskProjection projection,
        IReadOnlyList<TaskProjection>? conversation,
        Func<Guid, RunEvidenceSnapshot>? evidenceResolver)
    {
        var runs = (conversation is { Count: > 0 } ? conversation : [projection])
            .Where(run => run.TaskId == projection.TaskId)
            .ToArray();
        var snapshots = new List<TaskRunSnapshotDto>(runs.Length);
        var previousAttachments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        {
            var messageAttachments = run.Attachments
                .Where(path => !previousAttachments.Contains(path))
                .ToArray();
            snapshots.Add(CreateRun(
                run,
                messageAttachments,
                evidenceResolver?.Invoke(run.RunId) ?? RunEvidenceSnapshot.Empty(run.RunId)));
            previousAttachments = run.Attachments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return snapshots;
    }

    private static TaskRunSnapshotDto CreateRun(
        TaskProjection projection,
        IReadOnlyList<string> messageAttachments,
        RunEvidenceSnapshot evidence) => new(
        projection.RunId,
        projection.Prompt,
        projection.Model,
        projection.ThinkingLevel,
        messageAttachments,
        projection.Status.ToString(),
        projection.Status.ToDisplayText(),
        projection.Summary,
        projection.ActivityStatus,
        projection.AssistantText,
        projection.FinalAnswer,
        projection.LastSequence,
        projection.PendingSteering,
        projection.PendingFollowUps,
        projection.Transcript.Select(CreateTranscriptBlock).ToArray(),
        projection.Activities.Select(activity => new ActivityDto(
            activity.Sequence,
            activity.Kind.ToString(),
            activity.Text,
            activity.Timestamp)).ToArray(),
        projection.Artifacts
            .Where(artifact => artifact.RunId == projection.RunId)
            .Select(CreateArtifact)
            .ToArray(),
        CreateEvidence(evidence));

    private static TaskArtifactDto CreateArtifact(TaskArtifact artifact) => new(
        artifact.Id,
        artifact.RunId,
        artifact.DisplayName,
        artifact.ContentType,
        artifact.Size,
        artifact.Sha256,
        artifact.CreatedAt);

    public static RunEvidenceDto CreateEvidence(RunEvidenceSnapshot evidence) => new(
        evidence.RunId,
        evidence.Finalized,
        evidence.IsGitRepository,
        evidence.GitRoot,
        evidence.HeadBefore,
        evidence.HeadAfter,
        evidence.TestStatus.ToString(),
        evidence.Files.Select(file => new FileChangeDto(
            file.Id,
            file.Path,
            file.RelativePath,
            file.Kind.ToString(),
            file.Confidence.ToString(),
            file.Source,
            file.BeforeHash,
            file.AfterHash,
            file.BeforeSize,
            file.AfterSize,
            file.IsBinary,
            file.HasDiff,
            file.AddedLines,
            file.DeletedLines,
            file.DiffTruncated,
            file.Recovery.ToString(),
            file.RecoveryMessage)).ToArray(),
        evidence.Commands.Select(command => new CommandExecutionDto(
            command.Id,
            command.ToolCallId,
            command.Command,
            command.StartedAt,
            command.Duration.TotalMilliseconds,
            command.ExitCode,
            command.Cancelled,
            command.TimedOut,
            command.OutputSummary,
            command.IsTest,
            command.DetectedFramework,
            command.Status.ToString())).ToArray(),
        evidence.Tests.Select(test => new TestResultDto(
            test.Id,
            test.CommandExecutionId,
            test.Command,
            test.Framework,
            test.Status.ToString(),
            test.ExitCode,
            test.CompletedAt)).ToArray(),
        evidence.Warnings.Select(warning => new EvidenceWarningDto(
            warning.Code,
            warning.Message,
            warning.CreatedAt)).ToArray());

    public static FileDiffDto CreateFileDiff(FileDiffEvidence diff) => new(
        diff.FileChangeId,
        diff.RunId,
        diff.Path,
        diff.DiffText,
        diff.IsBinary,
        diff.Truncated,
        diff.Source);

    public static RunEventDto CreateEvent(CompanionRunEvent runEvent) => new(
        runEvent.EventId,
        runEvent.TaskId,
        runEvent.RunId,
        runEvent.Sequence,
        runEvent.Kind.ToString(),
        runEvent.Status.ToString(),
        runEvent.Timestamp,
        runEvent.Payload);

    public static AppendEventsDto CreateAppendEvents(
        CompanionRunEvent runEvent,
        TaskProjection projection) => new(
        [CreateEvent(runEvent)],
        new TaskDeltaDto(
            projection.TaskId,
            projection.RunId,
            projection.Status.ToString(),
            projection.Status.ToDisplayText(),
            projection.Summary,
            projection.ActivityStatus,
            projection.AssistantText,
            projection.FinalAnswer,
            projection.LastSequence,
            projection.PendingSteering,
            projection.PendingFollowUps,
            runEvent.Timestamp,
            projection.Transcript
                .Where(block => block.LastSequence == runEvent.Sequence)
                .Select(CreateTranscriptBlock)
                .ToArray(),
            projection.Activities
                .Where(activity => activity.Sequence == runEvent.Sequence)
                .Select(activity => new ActivityDto(
                    activity.Sequence,
                    activity.Kind.ToString(),
                    activity.Text,
                    activity.Timestamp))
                .ToArray()));

    private static TranscriptBlockDto CreateTranscriptBlock(TranscriptBlock block) => new(
        block.Id,
        block.Kind.ToString(),
        block.Status.ToString(),
        block.Title,
        block.Content,
        block.FirstSequence,
        block.LastSequence,
        block.Timestamp,
        block.Input,
        block.Output,
        block.InteractionId,
        block.InteractionMethod,
        block.InteractionKind,
        block.InteractionOptions ?? []);
}

internal sealed record BridgeEnvelope<T>(int ProtocolVersion, string Type, T Payload);

internal sealed record InitializeSnapshotDto(
    TaskSnapshotDto? CurrentTask,
    long LastSequence,
    IReadOnlyList<WorkspaceHistoryEntryDto> Workspaces,
    IReadOnlyList<TaskHistoryEntryDto> RecentTasks,
    IReadOnlyList<TaskHistoryEntryDto> HistoryTasks,
    bool HistoryHasMore,
    IReadOnlyList<TaskHistoryEntryDto> RecycleBinTasks,
    ComposerDraft? Draft,
    SettingsSnapshotDto Settings,
    IReadOnlyList<string> Capabilities);

internal sealed record SettingsSnapshotDto(
    PiCompanionSettings Values,
    PiConfigurationSnapshot Pi,
    string DataDirectory,
    string LogDirectory);

internal sealed record LoadSkillsRequestDto(string RequestId);

internal sealed record TrustSkillWorkspaceRequestDto(
    string RequestId,
    Guid WorkspaceId);

internal sealed record RemoveSkillInstallationRequestDto(
    string RequestId,
    string InstallationId,
    string ExpectedContentHash,
    Guid? WorkspaceId = null);

internal sealed record BeginSkillImportRequestDto(
    string RequestId,
    string SourceKind);

internal sealed record PrepareSkillImportRequestDto(
    string RequestId,
    string SourceToken,
    string TargetScope,
    Guid? WorkspaceId = null);

internal sealed record ConfirmSkillImportRequestDto(
    string RequestId,
    string Token);

internal sealed record CancelSkillImportRequestDto(
    string RequestId,
    string? SourceToken = null,
    string? PreparationToken = null);

internal sealed record SkillsLoadedDto(
    string RequestId,
    DateTimeOffset ScannedAt,
    IReadOnlyList<DiscoveredSkillDto> Skills,
    IReadOnlyList<SkillScanLocationDto> Locations,
    IReadOnlyList<SkillDiagnosticDto> Diagnostics,
    IReadOnlyList<SkillWorkspaceTrustDto> WorkspaceTrust);

internal sealed record SkillWorkspaceTrustDto(
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspacePath,
    string Status,
    string? DecisionPath,
    bool Inherited);

internal sealed record DiscoveredSkillDto(
    string Id,
    string Name,
    IReadOnlyList<SkillContentVariantDto> Variants,
    IReadOnlyList<SkillDiagnosticDto> Diagnostics);

internal sealed record SkillContentVariantDto(
    string Id,
    string? ContentHash,
    string? Description,
    string? Version,
    string? License,
    IReadOnlyDictionary<string, string> Metadata,
    bool DisableModelInvocation,
    bool IsAvailable,
    int FileCount,
    long TotalSize,
    DateTimeOffset? LastModifiedAt,
    IReadOnlyList<SkillInstallationDto> Installations);

internal sealed record SkillInstallationDto(
    string Id,
    string FilePath,
    string BaseDirectory,
    string CanonicalPath,
    string InstallPath,
    bool IsSingleFile,
    bool IsGloballyEffective,
    IReadOnlyList<Guid> EffectiveWorkspaceIds,
    IReadOnlyList<SkillOriginDto> Origins,
    IReadOnlyList<SkillDiagnosticDto> Diagnostics,
    bool Removable,
    string? RemovalReason);

internal sealed record SkillRemovalCompletedDto(
    string RequestId,
    bool Succeeded,
    string Message,
    string? RemovedInstallationId,
    string? RecoveryPath,
    SkillsLoadedDto Snapshot);

internal sealed record SkillWorkspaceTrustCompletedDto(
    string RequestId,
    bool Succeeded,
    string Message,
    Guid WorkspaceId,
    SkillsLoadedDto Snapshot);

internal sealed record SkillImportSourceInspectedDto(
    string RequestId,
    bool Succeeded,
    bool Cancelled,
    string Message,
    SkillImportSourceDto? Source);

internal sealed record SkillImportSourceDto(
    string Token,
    string Name,
    string? Description,
    string SourceKind,
    string ContentHash,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<SkillImportFileDto> Files,
    IReadOnlyList<string> ScriptFiles,
    IReadOnlyList<string> ExecutableFiles);

internal sealed record SkillImportReadyDto(
    string RequestId,
    bool Succeeded,
    string Message,
    SkillImportPreparationDto? Preparation);

internal sealed record SkillImportPreparationDto(
    string Token,
    string SourceToken,
    string Name,
    string? Description,
    string TargetScope,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string TargetPath,
    string SourceKind,
    string ContentHash,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<SkillImportFileDto> Files,
    IReadOnlyList<string> ScriptFiles,
    IReadOnlyList<string> ExecutableFiles,
    bool RequiresProjectTrust,
    string TrustStatus);

internal sealed record SkillImportFileDto(
    string RelativePath,
    long Size,
    string Kind);

internal sealed record SkillImportCompletedDto(
    string RequestId,
    bool Succeeded,
    bool Cancelled,
    string Message,
    string? SkillName,
    string? TargetPath,
    SkillsLoadedDto Snapshot);

internal sealed record SkillOriginDto(
    string Scope,
    string Source,
    string RootPath,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? WorkspacePath,
    bool Inherited,
    string InstallPath,
    bool IsCompatibilityLink,
    string? LinkTarget);

internal sealed record SkillScanLocationDto(
    string Id,
    string Scope,
    string Source,
    string Path,
    string Status,
    int SkillCount,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? WorkspacePath,
    bool Inherited,
    string? Message);

internal sealed record SkillDiagnosticDto(
    string Code,
    string Severity,
    string Message,
    string Path,
    string? WinnerPath,
    Guid? WorkspaceId,
    string? WorkspaceName);

internal sealed record TaskCollectionsDto(
    IReadOnlyList<WorkspaceHistoryEntryDto> Workspaces,
    IReadOnlyList<TaskHistoryEntryDto> RecentTasks,
    IReadOnlyList<TaskHistoryEntryDto> HistoryTasks,
    bool HistoryHasMore,
    IReadOnlyList<TaskHistoryEntryDto> RecycleBinTasks);

internal sealed record WorkspaceHistoryEntryDto(
    Guid Id,
    string Name,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TaskCount,
    bool HasActiveTask,
    string IconKey,
    string ColorKey,
    string? DisplayName);

internal sealed record TaskHistoryEntryDto(
    Guid Id,
    Guid RunId,
    string Title,
    string WorkingDirectory,
    string ScopeKind,
    string Status,
    string StatusText,
    string Summary,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    Guid? WorkspaceId);

internal sealed record TaskSnapshotDto(
    Guid Id,
    Guid RunId,
    string Title,
    string Prompt,
    string WorkingDirectory,
    string ScopeKind,
    string Model,
    string ThinkingLevel,
    string PermissionMode,
    IReadOnlyList<string> Attachments,
    IReadOnlyList<TaskArtifactDto> Artifacts,
    string Status,
    string StatusText,
    string Summary,
    string? ActivityStatus,
    string? AssistantText,
    string? FinalAnswer,
    long LastSequence,
    IReadOnlyList<string> PendingSteering,
    IReadOnlyList<string> PendingFollowUps,
    IReadOnlyList<LocalQueuedMessageDto> LocalQueuedMessages,
    Guid? LocalQueueAutoStartMessageId,
    DateTimeOffset? LocalQueueAutoStartAt,
    IReadOnlyList<TranscriptBlockDto> Transcript,
    IReadOnlyList<TaskRunSnapshotDto> Runs,
    IReadOnlyList<ActivityDto> Activities);

internal sealed record LocalQueuedMessageDto(
    Guid Id,
    string Message,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ComposerAttachment> Attachments);

internal sealed record TaskRunSnapshotDto(
    Guid Id,
    string Prompt,
    string Model,
    string ThinkingLevel,
    IReadOnlyList<string> MessageAttachments,
    string Status,
    string StatusText,
    string Summary,
    string? ActivityStatus,
    string? AssistantText,
    string? FinalAnswer,
    long LastSequence,
    IReadOnlyList<string> PendingSteering,
    IReadOnlyList<string> PendingFollowUps,
    IReadOnlyList<TranscriptBlockDto> Transcript,
    IReadOnlyList<ActivityDto> Activities,
    IReadOnlyList<TaskArtifactDto> Artifacts,
    RunEvidenceDto Evidence);

internal sealed record TaskArtifactDto(
    Guid Id,
    Guid RunId,
    string DisplayName,
    string ContentType,
    long Size,
    string Sha256,
    DateTimeOffset CreatedAt);

internal sealed record RunEvidenceDto(
    Guid RunId,
    bool Finalized,
    bool IsGitRepository,
    string? GitRoot,
    string? HeadBefore,
    string? HeadAfter,
    string TestStatus,
    IReadOnlyList<FileChangeDto> Files,
    IReadOnlyList<CommandExecutionDto> Commands,
    IReadOnlyList<TestResultDto> Tests,
    IReadOnlyList<EvidenceWarningDto> Warnings);

internal sealed record FileChangeDto(
    Guid Id,
    string Path,
    string RelativePath,
    string Kind,
    string Confidence,
    string Source,
    string? BeforeHash,
    string? AfterHash,
    long? BeforeSize,
    long? AfterSize,
    bool IsBinary,
    bool HasDiff,
    int AddedLines,
    int DeletedLines,
    bool DiffTruncated,
    string Recovery,
    string? RecoveryMessage);

internal sealed record CommandExecutionDto(
    Guid Id,
    string ToolCallId,
    string Command,
    DateTimeOffset StartedAt,
    double DurationMilliseconds,
    int? ExitCode,
    bool Cancelled,
    bool TimedOut,
    string OutputSummary,
    bool IsTest,
    string? DetectedFramework,
    string Status);

internal sealed record TestResultDto(
    Guid Id,
    Guid CommandExecutionId,
    string Command,
    string Framework,
    string Status,
    int? ExitCode,
    DateTimeOffset CompletedAt);

internal sealed record EvidenceWarningDto(string Code, string Message, DateTimeOffset CreatedAt);

internal sealed record FileDiffDto(
    Guid ChangeId,
    Guid RunId,
    string Path,
    string? DiffText,
    bool IsBinary,
    bool Truncated,
    string Source);

internal sealed record TranscriptBlockDto(
    string Id,
    string Kind,
    string Status,
    string Title,
    string Content,
    long FirstSequence,
    long LastSequence,
    DateTimeOffset Timestamp,
    string? Input,
    string? Output,
    string? InteractionId,
    string? InteractionMethod,
    string? InteractionKind,
    IReadOnlyList<string> InteractionOptions);

internal sealed record ActivityDto(long Sequence, string Kind, string Text, DateTimeOffset Timestamp);

internal sealed record AppendEventsDto(
    IReadOnlyList<RunEventDto> Events,
    TaskDeltaDto Task);

internal sealed record TaskDeltaDto(
    Guid Id,
    Guid RunId,
    string Status,
    string StatusText,
    string Summary,
    string? ActivityStatus,
    string? AssistantText,
    string? FinalAnswer,
    long LastSequence,
    IReadOnlyList<string> PendingSteering,
    IReadOnlyList<string> PendingFollowUps,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TranscriptBlockDto> TranscriptUpserts,
    IReadOnlyList<ActivityDto> ActivityUpserts);

internal sealed record RunEventDto(
    Guid EventId,
    Guid TaskId,
    Guid RunId,
    long Sequence,
    string Kind,
    string Status,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Payload);
