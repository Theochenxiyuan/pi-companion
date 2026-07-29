using PiCompanion.Core.Activation;

namespace PiCompanion.Core.Tests;

public sealed class ExplorerActivationProtocolTests
{
    [Fact]
    public void Codec_RoundTripsUnicodeLongPathsAndWindowContext()
    {
        var request = CreateRequest(
            Path.Combine(Path.GetTempPath(), "项目", new string('长', 80)),
            [Path.Combine(Path.GetTempPath(), "项目", "需求 文档.md")]);

        var result = ExplorerActivationCodec.Deserialize(ExplorerActivationCodec.Serialize(request));

        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.WorkingDirectory, result.WorkingDirectory);
        Assert.Equal(request.SelectedPaths, result.SelectedPaths);
        Assert.Equal(new ScreenPoint(1900, 1060), result.CursorPosition);
        Assert.Equal(0x12345678, result.ExplorerWindowHandle);
    }

    [Fact]
    public void Normalize_DeduplicatesPathsCaseInsensitively()
    {
        var path = Path.Combine(Path.GetTempPath(), "File.txt");
        var request = CreateRequest(Path.GetTempPath(), [path, path.ToUpperInvariant()]);

        var result = ExplorerActivationValidator.Normalize(request);

        Assert.Single(result.SelectedPaths);
    }

    [Fact]
    public void Normalize_DoesNotTreatWorkingDirectoryAsAttachment()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "project");
        var request = CreateRequest(workingDirectory, [workingDirectory]);

        var result = ExplorerActivationValidator.Normalize(request);

        Assert.Empty(result.SelectedPaths);
    }

    [Fact]
    public void Normalize_PreservesSelectedFolderUnderWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "project");
        var selectedFolder = Path.Combine(workingDirectory, "selected-folder");
        var request = CreateRequest(workingDirectory, [selectedFolder]);

        var result = ExplorerActivationValidator.Normalize(request);

        Assert.Equal([selectedFolder], result.SelectedPaths);
    }

    [Fact]
    public void Normalize_RejectsRelativeWorkingDirectory()
    {
        var request = CreateRequest("relative", []);

        var exception = Assert.Throws<InvalidDataException>(() => ExplorerActivationValidator.Normalize(request));

        Assert.Contains("绝对路径", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_RejectsUnsupportedProtocolVersion()
    {
        var request = CreateRequest(Path.GetTempPath(), []) with { ProtocolVersion = 99 };

        Assert.Throws<InvalidDataException>(() => ExplorerActivationValidator.Normalize(request));
    }

    [Fact]
    public void Normalize_RejectsTooManyAttachments()
    {
        var paths = Enumerable.Range(0, ExplorerActivationProtocol.MaximumSelectedPathCount + 1)
            .Select(index => Path.Combine(Path.GetTempPath(), $"item-{index}"))
            .ToArray();
        var request = CreateRequest(Path.GetTempPath(), paths);

        Assert.Throws<InvalidDataException>(() => ExplorerActivationValidator.Normalize(request));
    }

    private static ExplorerActivationRequest CreateRequest(
        string workingDirectory,
        IReadOnlyList<string> selectedPaths) => new(
        ExplorerActivationProtocol.Version,
        Guid.NewGuid(),
        workingDirectory,
        selectedPaths,
        new ScreenPoint(1900, 1060),
        0x12345678,
        "Selection",
        DateTimeOffset.UtcNow);
}
