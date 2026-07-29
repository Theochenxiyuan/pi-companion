namespace PiCompanion.Core.Activation;

public static class ExplorerActivationProtocol
{
    public const int Version = 1;
    public const int MaximumSelectedPathCount = PiCompanion.Core.Tasks.TaskAttachmentRules.MaximumCount;
    public const int MaximumPayloadBytes = 256 * 1024;
}

public sealed record ScreenPoint(int X, int Y);

public sealed record ExplorerActivationRequest(
    int ProtocolVersion,
    Guid RequestId,
    string WorkingDirectory,
    IReadOnlyList<string> SelectedPaths,
    ScreenPoint? CursorPosition,
    long ExplorerWindowHandle,
    string InvocationKind,
    DateTimeOffset Timestamp);
