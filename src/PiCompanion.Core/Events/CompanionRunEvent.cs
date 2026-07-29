using PiCompanion.Core.Runs;

namespace PiCompanion.Core.Events;

public sealed record CompanionRunEvent(
    Guid EventId,
    Guid TaskId,
    Guid RunId,
    long Sequence,
    CompanionRunEventKind Kind,
    DateTimeOffset Timestamp,
    RunStatus Status,
    IReadOnlyDictionary<string, string> Payload,
    string SourceVersion = "pi-companion-v1");
