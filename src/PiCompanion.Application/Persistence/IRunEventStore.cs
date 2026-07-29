using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Evidence;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Application.Persistence;

public interface IRunEventStore
{
    void CreateRun(TaskProjection projection, string prompt);

    void AppendRunEvent(CompanionRunEvent runEvent);

    TaskProjection? RestoreLatestProjection();

    TaskProjection? RestoreProjection(Guid taskId);

    IReadOnlyList<TaskProjection> RestoreTaskRuns(Guid taskId);

    IReadOnlyList<TaskHistoryEntry> GetRecentTasks(int limit = 20);

    IReadOnlyList<TaskHistoryEntry> QueryTasks(TaskHistoryQuery query);

    WorkspaceHistoryEntry CreateWorkspace(string workingDirectory);

    IReadOnlyList<WorkspaceHistoryEntry> GetWorkspaces();

    WorkspaceHistoryEntry UpdateWorkspacePresentation(
        Guid workspaceId,
        string? displayName,
        string iconKey,
        string colorKey);

    void HideWorkspace(Guid workspaceId);

    void UpsertTaskArtifact(TaskArtifact artifact);

    IReadOnlyList<TaskArtifact> GetTaskArtifacts(Guid taskId);

    TaskArtifact? GetTaskArtifact(Guid artifactId);

    void RenameTask(Guid taskId, string title);

    void UpdateTaskExecutionDefaults(Guid taskId, string model, string thinkingLevel);

    void UpdateRunSummary(Guid taskId, Guid runId, string summary);

    void MoveTaskToRecycleBin(Guid taskId);

    void RestoreTaskFromRecycleBin(Guid taskId);

    void DeleteTaskPermanently(Guid taskId);

    void EmptyRecycleBin();

    void PurgeExpiredTasks(DateTimeOffset? taskHistoryCutoff, DateTimeOffset? recycleBinCutoff);

    string? GetLatestSessionPath(Guid taskId);

    string? GetLatestPiEntryCursor(Guid taskId);

    SessionStatisticsCacheEntry? GetSessionStatisticsCache(
        Guid taskId,
        Guid runId,
        long lastSequence);

    void UpsertSessionStatisticsCache(SessionStatisticsCacheEntry entry);

    IReadOnlyList<PersistedInteractionRequest> GetInteractionRequests(Guid runId);

    void UpsertRunEvidenceMetadata(RunEvidenceMetadata metadata);

    RunEvidenceMetadata? GetRunEvidenceMetadata(Guid runId);

    void UpsertFileChange(FileChangeEvidence fileChange);

    FileChangeEvidence? GetFileChange(Guid fileChangeId);

    void UpsertCommandExecution(CommandExecutionEvidence command);

    void UpsertTestResult(TestResultEvidence testResult);

    void ReplaceEvidenceWarnings(Guid runId, IReadOnlyList<EvidenceWarning> warnings);

    void AppendRecoveryAction(RecoveryActionEvidence action);

    RunEvidenceSnapshot GetRunEvidence(Guid runId);

    string? GetSettingJson(string key);

    void SetSettingJson(string key, string valueJson);
}

public sealed record TaskHistoryEntry(
    Guid TaskId,
    Guid RunId,
    string Title,
    string WorkingDirectory,
    RunStatus Status,
    string Summary,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null,
    TaskScopeKind ScopeKind = TaskScopeKind.Workspace,
    Guid? WorkspaceId = null);

public sealed record TaskHistoryQuery(
    string? Search = null,
    IReadOnlyList<RunStatus>? Statuses = null,
    bool IncludeDeleted = false,
    int? Limit = null,
    int Offset = 0);

public sealed record WorkspaceHistoryEntry(
    Guid Id,
    string Name,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TaskCount,
    bool HasActiveTask,
    string IconKey = "folder",
    string ColorKey = "blue",
    string? DisplayName = null);

public sealed record PersistedInteractionRequest(
    string InteractionId,
    Guid TaskId,
    Guid RunId,
    string Kind,
    string Method,
    string Title,
    IReadOnlyList<string> Options,
    string Status,
    string? Response,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record SessionStatisticsCacheEntry(
    Guid TaskId,
    Guid RunId,
    long LastSequence,
    AgentSessionStatistics Statistics,
    DateTimeOffset UpdatedAt);
