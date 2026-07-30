using System.Text.Json;
using PiCompanion.Application.PiRpc;

namespace PiCompanion.Core.Tests;

public sealed class PiConfigurationServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"pi-companion-configuration-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadsLastSuccessfulSnapshotWithoutStartingPi()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var cachePath = Path.Combine(_temporaryDirectory, "snapshot.json");
        var runtimePath = Path.Combine(_temporaryDirectory, "cli.js");
        await File.WriteAllTextAsync(runtimePath, string.Empty, TestContext.Current.CancellationToken);
        var expected = CreateSnapshot(runtimePath);
        await File.WriteAllTextAsync(
            cachePath,
            JsonSerializer.Serialize(expected, JsonOptions),
            TestContext.Current.CancellationToken);

        var service = new PiConfigurationService(
            new PiRuntimeResolver(runtimePath),
            Path.Combine(_temporaryDirectory, "missing-helper.mjs"),
            cachePath);

        var cached = Assert.IsType<PiConfigurationSnapshot>(service.CachedSnapshot);
        Assert.Equal(expected.Version, cached.Version);
        Assert.Equal(expected.DefaultModel, cached.DefaultModel);
        Assert.Equal(expected.Providers[0], cached.Providers[0]);
        Assert.Equal(expected.Models[0].Id, cached.Models[0].Id);
        Assert.Same(cached, await service.GetSnapshotAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IgnoresUnavailableDiskSnapshot()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var cachePath = Path.Combine(_temporaryDirectory, "snapshot.json");
        await File.WriteAllTextAsync(
            cachePath,
            JsonSerializer.Serialize(PiConfigurationSnapshot.Unavailable("offline"), JsonOptions),
            TestContext.Current.CancellationToken);

        var service = new PiConfigurationService(
            new PiRuntimeResolver(Path.Combine(_temporaryDirectory, "missing-cli.js")),
            Path.Combine(_temporaryDirectory, "missing-helper.mjs"),
            cachePath);

        Assert.Null(service.CachedSnapshot);
    }

    [Fact]
    public async Task ExplicitModelCatalogRefreshRequestsNetworkAndPublishesTheNewSnapshot()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var runtimePath = Path.Combine(_temporaryDirectory, "cli.js");
        var helperPath = Path.Combine(_temporaryDirectory, "settings-helper.mjs");
        await File.WriteAllTextAsync(runtimePath, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(helperPath, """
            let input = '';
            for await (const chunk of process.stdin) input += chunk;
            const request = JSON.parse(input);
            process.stdout.write(JSON.stringify({
              available: true,
              version: request.refreshModels === true ? 'network' : 'offline',
              runtimePath: request.piEntry,
              defaultModel: null,
              defaultThinkingLevel: 'off',
              autoCompact: true,
              autoRetry: true,
              providers: [],
              models: [],
              enabledModels: null,
              customProviders: [],
              modelsConfigRevision: null,
              error: null,
            }));
            """, TestContext.Current.CancellationToken);

        var node = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var service = new PiConfigurationService(
            new PiRuntimeResolver(runtimePath, _temporaryDirectory, node),
            helperPath,
            Path.Combine(_temporaryDirectory, "snapshot.json"));
        PiConfigurationSnapshot? published = null;
        service.SnapshotChanged += snapshot => published = snapshot;

        var offline = await service.RefreshSnapshotAsync(TestContext.Current.CancellationToken);
        var refreshed = await service.RefreshModelCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal("offline", offline.Version);
        Assert.Equal("network", refreshed.Version);
        Assert.Equal("network", service.CachedSnapshot?.Version);
        Assert.Equal("network", published?.Version);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static PiConfigurationSnapshot CreateSnapshot(string runtimePath) => new(
        Available: true,
        Version: "0.83.0",
        RuntimePath: runtimePath,
        DefaultModel: "openai-codex/gpt-5.6-sol",
        DefaultThinkingLevel: "xhigh",
        AutoCompact: true,
        AutoRetry: true,
        CompactionReserveTokens: 16384,
        CompactionKeepRecentTokens: 20000,
        RetryMaxRetries: 3,
        RetryBaseDelayMilliseconds: 2000,
        RetryMaxDelayMilliseconds: 60000,
        SteeringMode: "one-at-a-time",
        FollowUpMode: "one-at-a-time",
        Providers:
        [
            new PiProviderInfo("openai-codex", "OpenAI Codex", true, "oauth", "stored", false, true),
        ],
        Models:
        [
            new PiModelInfo(
                "openai-codex",
                "gpt-5.6-sol",
                "GPT-5.6 Sol",
                true,
                372_000,
                ["text", "image"],
                ["off", "minimal", "low", "medium", "high", "xhigh", "max"]),
        ],
        EnabledModels: null,
        CustomProviders: [],
        ModelsConfigRevision: null,
        Error: null);
}
