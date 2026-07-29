using PiCompanion.Core.Tasks;

namespace PiCompanion.Core.Agents;

public sealed record AgentRunRequest(
    Guid TaskId,
    Guid RunId,
    string Title,
    string Prompt,
    string WorkingDirectory,
    string Model,
    string ThinkingLevel,
    string Mode,
    IReadOnlyList<string>? Attachments = null,
    string? PiSessionPath = null,
    string? ReadOnlyAttachmentRoot = null,
    string? PiEntryCursor = null,
    IReadOnlyList<string>? KnownAssistantMessages = null,
    string PermissionMode = "standard",
    TaskScopeKind ScopeKind = TaskScopeKind.Workspace,
    string? ArtifactDirectory = null,
    long InitialSequence = 0);

public sealed record InteractionResolution(
    bool Approved,
    string? Response = null,
    string? InteractionId = null);
