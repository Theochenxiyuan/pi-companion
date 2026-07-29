using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Tasks;

namespace PiCompanion.Core.Tests;

public sealed class PiTaskMetadataGeneratorTests
{
    [Fact]
    public async Task GeneratesTitleAndSummaryThroughIsolatedPiRpc()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-pi-metadata.js");
        var diagnostics = Path.Combine(Path.GetTempPath(), $"pi-metadata-{Guid.NewGuid():N}.jsonl");
        using var generator = new PiTaskMetadataGenerator(
            new PiRuntimeResolver(fixture, AppContext.BaseDirectory, "node.exe"),
            diagnostics);
        var cancellationToken = TestContext.Current.CancellationToken;

        await generator.PrepareAsync("fake/metadata-model", cancellationToken);
        var title = await generator.GenerateTitleAsync(
            "Implement task metadata",
            "fake/metadata-model",
            cancellationToken);
        var summary = await generator.GenerateRunSummaryAsync(
            new RunSummarySource(
                "Old greeting that must not be summarized",
                "Analyze the attached screenshot",
                "Completed",
                "Completed",
                "Implemented title and summary generation.",
                null,
                [
                    new RunSummaryInteraction(
                        "Which scope should be summarized?",
                        ["Current run", "Entire task"],
                        "Current run",
                        "Completed"),
                ]),
            "fake/metadata-model",
            cancellationToken);
        var commitMessage = await generator.GenerateCommitMessageAsync(
            new CommitMessageSource(
                "pi-companion",
                "main",
                ["src/App.vue"],
                ["feat: existing style"],
                "diff --git a/src/App.vue b/src/App.vue\n+new behavior",
                false),
            "fake/metadata-model",
            cancellationToken);

        Assert.Equal("AI generated title", title);
        Assert.Equal("AI generated summary.", summary);
        Assert.Equal("feat: generate staged commit message", commitMessage);
        Assert.Single(
            File.ReadLines(diagnostics),
            line => line.Contains("\"eventName\":\"worker_started\"", StringComparison.Ordinal));
        File.Delete(diagnostics);
    }

    [Fact]
    public async Task RewritesAnOverlongSummaryInsteadOfHardTruncatingIt()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-pi-metadata.js");
        using var generator = new PiTaskMetadataGenerator(new PiRuntimeResolver(
            fixture,
            AppContext.BaseDirectory,
            "node.exe"));

        var summary = await generator.GenerateRunSummaryAsync(
            new RunSummarySource(
                "Task",
                "force-overlong-summary",
                "Completed",
                "Completed",
                "Implemented the requested change.",
                null),
            "fake/metadata-model",
            TestContext.Current.CancellationToken);

        Assert.Equal("已自然压缩为语义完整的一句话。", summary);
        Assert.NotNull(summary);
        Assert.False(summary.EndsWith('…'));
    }

    [Fact]
    public async Task RebuildsTheDedicatedWorkerAfterItExits()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-pi-metadata.js");
        using var generator = new PiTaskMetadataGenerator(new PiRuntimeResolver(
            fixture,
            AppContext.BaseDirectory,
            "node.exe"));

        await Assert.ThrowsAnyAsync<Exception>(() => generator.GenerateTitleAsync(
            "crash-metadata-worker",
            "fake/metadata-model",
            TestContext.Current.CancellationToken));

        var recovered = await generator.GenerateTitleAsync(
            "Implement task metadata",
            "fake/metadata-model",
            TestContext.Current.CancellationToken);

        Assert.Equal("AI generated title", recovered);
    }
}
