namespace PiCompanion.Core.Tasks;

public sealed record TaskArtifact(
    Guid Id,
    Guid TaskId,
    Guid RunId,
    string DisplayName,
    string StoragePath,
    string ContentType,
    long Size,
    string Sha256,
    DateTimeOffset CreatedAt);
