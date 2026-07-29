using PiCompanion.Core.Activation;

namespace PiCompanion.Desktop.Activation;

internal static class ExplorerActivationFactory
{
    public static ExplorerActivationRequest CreatePreview() => new(
        ExplorerActivationProtocol.Version,
        Guid.NewGuid(),
        Environment.CurrentDirectory,
        [],
        null,
        0,
        "CommandLinePreview",
        DateTimeOffset.UtcNow);
}
