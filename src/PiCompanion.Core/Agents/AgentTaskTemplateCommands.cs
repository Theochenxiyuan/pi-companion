using PiCompanion.Core.Tasks;

namespace PiCompanion.Core.Agents;

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

public interface IAgentTaskTemplateCommandSource
{
    void SetTaskTemplateCreationHandler(
        Func<AgentTaskTemplateCreationRequest, CancellationToken, ValueTask<AgentTaskTemplateCreationResult>> handler);
}
