using PiCompanion.Core.Tasks;
using System.Text.Json.Serialization;

namespace PiCompanion.Core.Agents;

public sealed record AgentCommandContext(
    string RequestId,
    Guid SourceTaskId,
    Guid SourceRunId,
    TaskScopeKind ScopeKind,
    string WorkingDirectory,
    string Language);

public sealed record AgentTaskTemplateView(
    string Id,
    string Name,
    string? Prompt,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TaskTemplateTargetKind>))]
    TaskTemplateTargetKind TargetKind,
    Guid? WorkspaceId,
    string? Model,
    string? ThinkingLevel,
    string? PermissionMode,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsBuiltIn,
    string Origin);

public sealed record AgentTaskTemplateListRequest(
    AgentCommandContext Context,
    string? Query);

public sealed record AgentTaskTemplateGetRequest(
    AgentCommandContext Context,
    string TemplateId);

public sealed record AgentScheduledTaskView(
    Guid Id,
    string Name,
    bool IsEnabled,
    Guid? TemplateId,
    string? TemplateName,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TaskTemplateTargetKind>))]
    TaskTemplateTargetKind TargetKind,
    Guid? WorkspaceId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ScheduledTaskFrequency>))]
    ScheduledTaskFrequency Frequency,
    DateTime LocalStartAt,
    IReadOnlyList<string> DaysOfWeek,
    string TimeZoneId,
    DateTimeOffset? NextRunAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentScheduledTaskListRequest(
    AgentCommandContext Context,
    string? Query,
    bool? IsEnabled);

public sealed record AgentScheduledTaskCreationRequest(
    AgentCommandContext Context,
    string Name,
    string? TemplateId,
    string? Prompt,
    TaskTemplateTargetKind? TargetKind,
    Guid? WorkspaceId,
    string? Model,
    string? ThinkingLevel,
    string? PermissionMode,
    ScheduledTaskFrequency Frequency,
    DateTime LocalStartAt,
    ScheduledDaysOfWeek DaysOfWeek,
    string TimeZoneId,
    bool IsEnabled);

public sealed record AgentScheduledTaskCreationResult(
    ScheduledTask ScheduledTask,
    bool AlreadyExisted);

public sealed record AgentTaskTemplateCreationRequest(
    string RequestId,
    Guid SourceTaskId,
    Guid SourceRunId,
    string Name,
    string Prompt,
    TaskTemplateTargetKind TargetKind,
    Guid? WorkspaceId,
    string? Model,
    string? ThinkingLevel,
    string? PermissionMode,
    bool IsPinned);

public sealed record AgentTaskTemplateCreationResult(
    TaskTemplate Template,
    bool AlreadyExisted);

public interface IAgentCompanionCommandSource
{
    void SetTaskTemplateListHandler(
        Func<AgentTaskTemplateListRequest, CancellationToken, ValueTask<IReadOnlyList<AgentTaskTemplateView>>> handler);

    void SetTaskTemplateGetHandler(
        Func<AgentTaskTemplateGetRequest, CancellationToken, ValueTask<AgentTaskTemplateView>> handler);

    void SetTaskTemplateCreationHandler(
        Func<AgentTaskTemplateCreationRequest, CancellationToken, ValueTask<AgentTaskTemplateCreationResult>> handler);

    void SetScheduledTaskListHandler(
        Func<AgentScheduledTaskListRequest, CancellationToken, ValueTask<IReadOnlyList<AgentScheduledTaskView>>> handler);

    void SetScheduledTaskCreationHandler(
        Func<AgentScheduledTaskCreationRequest, CancellationToken, ValueTask<AgentScheduledTaskCreationResult>> handler);
}
