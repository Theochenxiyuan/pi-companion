namespace PiCompanion.Core.Agents;

public sealed record AgentContextUsage(
    long? Tokens,
    int ContextWindow,
    double? Percent);

public sealed record AgentSessionStatistics(
    string SessionId,
    string? SessionFile,
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int ToolResults,
    int TotalMessages,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long TotalTokens,
    double Cost,
    AgentContextUsage? ContextUsage);

public sealed record AgentSessionStatisticsRequest(
    Guid TaskId,
    string WorkingDirectory,
    string Model,
    string ThinkingLevel,
    string? SessionPath,
    bool LoadHistoricalSession = false);

public interface IAgentSessionStatisticsProvider
{
    Task<AgentSessionStatistics?> GetSessionStatisticsAsync(
        AgentSessionStatisticsRequest request,
        CancellationToken cancellationToken = default);
}
