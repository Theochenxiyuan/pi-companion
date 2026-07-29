using PiCompanion.Core.Events;

namespace PiCompanion.Core.Agents;

public interface IAgentBackend
{
    event Action<CompanionRunEvent>? EventReceived;

    event Action<AgentToolExecution>? ToolExecutionCompleted;

    Task StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);

    Task SteerAsync(Guid runId, string message, CancellationToken cancellationToken = default);

    Task FollowUpAsync(Guid runId, string message, CancellationToken cancellationToken = default);

    Task ResolveInteractionAsync(
        Guid runId,
        InteractionResolution resolution,
        CancellationToken cancellationToken = default);

    Task AbortAsync(Guid runId, CancellationToken cancellationToken = default);

    Task AbortRetryAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed record AgentPreparationRequest(
    string WorkingDirectory,
    string Model,
    string ThinkingLevel);

public interface IAgentBackendPrewarmer
{
    Task PrepareAsync(
        AgentPreparationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAgentBackendWorkspaceReleaser
{
    void ReleaseWorkspace(string workingDirectory);
}

public interface IAgentBackendResourceInvalidator
{
    void InvalidateIdleResources(string? workingDirectory = null);
}

public sealed record AgentSessionCommandRequest(
    Guid TaskId,
    string WorkingDirectory,
    string Model,
    string ThinkingLevel,
    string? SessionPath);

public interface IAgentSessionCommandController
{
    Task CompactAsync(
        AgentSessionCommandRequest request,
        string? customInstructions = null,
        CancellationToken cancellationToken = default);
}
