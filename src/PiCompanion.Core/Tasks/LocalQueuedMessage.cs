namespace PiCompanion.Core.Tasks;

public sealed record LocalQueuedMessage(
    Guid Id,
    string Message,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string>? Attachments = null);
