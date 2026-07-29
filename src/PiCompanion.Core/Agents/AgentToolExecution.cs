namespace PiCompanion.Core.Agents;

public sealed record AgentToolExecution(
    Guid TaskId,
    Guid RunId,
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    string ResultJson,
    bool IsError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
