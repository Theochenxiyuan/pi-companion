using PiCompanion.Core.Activation;

namespace PiCompanion.Desktop.Activation;

internal static class ExplorerActivationFactory
{
    public const string ActivateWindowInvocationKind = "ActivateWindow";

    public static ExplorerActivationRequest CreatePreview() => new(
        ExplorerActivationProtocol.Version,
        Guid.NewGuid(),
        Environment.CurrentDirectory,
        [],
        null,
        0,
        "CommandLinePreview",
        DateTimeOffset.UtcNow);

    public static ExplorerActivationRequest CreateWindowActivation() => new(
        ExplorerActivationProtocol.Version,
        Guid.NewGuid(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile
            ? profile
            : AppContext.BaseDirectory,
        [],
        null,
        0,
        ActivateWindowInvocationKind,
        DateTimeOffset.UtcNow);
}
