namespace PiCompanion.Core.Tasks;

public sealed record TranscriptBlock(
    string Id,
    TranscriptBlockKind Kind,
    TranscriptBlockStatus Status,
    string Title,
    string Content,
    long FirstSequence,
    long LastSequence,
    DateTimeOffset Timestamp,
    string? Input = null,
    string? Output = null,
    string? InteractionId = null,
    string? InteractionMethod = null,
    string? InteractionKind = null,
    IReadOnlyList<string>? InteractionOptions = null);
