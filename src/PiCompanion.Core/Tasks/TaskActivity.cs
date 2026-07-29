using PiCompanion.Core.Events;

namespace PiCompanion.Core.Tasks;

public sealed record TaskActivity(
    long Sequence,
    CompanionRunEventKind Kind,
    string Text,
    DateTimeOffset Timestamp);
