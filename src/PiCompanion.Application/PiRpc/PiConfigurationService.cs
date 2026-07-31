using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PiCompanion.Application.PiRpc;

public sealed record PiProviderInfo(
    string Id,
    string Name,
    bool Configured,
    string? AuthType,
    string? AuthSource,
    bool SupportsApiKey,
    bool SupportsOAuth,
    IReadOnlyList<string>? Capabilities = null);

public sealed record PiModelInfo(
    string Provider,
    string Id,
    string Name,
    bool Reasoning,
    int ContextWindow,
    IReadOnlyList<string> Input,
    IReadOnlyList<string> ThinkingLevels,
    string Api = "",
    string WebSearchSupport = "none");

public sealed record PiCustomModelInfo(
    string Id,
    string Name,
    bool Reasoning,
    bool ImageInput,
    int ContextWindow,
    int MaxTokens,
    bool? SupportsDeveloperRole = null);

public sealed record PiCustomProviderInfo(
    string Id,
    string Name,
    string BaseUrl,
    string Api,
    string CredentialMode,
    IReadOnlyList<PiCustomModelInfo> Models);

public sealed record PiOAuthLoginEvent(
    string Type,
    string? Message,
    string? Url,
    string? Instructions,
    string? UserCode,
    string? VerificationUri,
    int? IntervalSeconds,
    int? ExpiresInSeconds);

public sealed record PiConfigurationSnapshot(
    bool Available,
    string? Version,
    string? RuntimePath,
    string? DefaultModel,
    string DefaultThinkingLevel,
    bool AutoCompact,
    bool AutoRetry,
    int CompactionReserveTokens,
    int CompactionKeepRecentTokens,
    int RetryMaxRetries,
    int RetryBaseDelayMilliseconds,
    int RetryMaxDelayMilliseconds,
    string SteeringMode,
    string FollowUpMode,
    IReadOnlyList<PiProviderInfo> Providers,
    IReadOnlyList<PiModelInfo> Models,
    // Read only to migrate model visibility from Companion versions that wrote Pi's global scope.
    IReadOnlyList<string>? EnabledModels,
    IReadOnlyList<PiCustomProviderInfo> CustomProviders,
    string? ModelsConfigRevision,
    string? Error)
{
    public static PiConfigurationSnapshot Unavailable(string error) =>
        new(false, null, null, null, "high", true, true, 16384, 20000, 3, 2000, 60000,
            "one-at-a-time", "one-at-a-time", [], [], null, [], null, error);
}

public sealed class PiConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultFreshness = TimeSpan.FromSeconds(30);
    private readonly PiRuntimeResolver _runtimeResolver;
    private readonly string _helperPath;
    private readonly string _cachePath;
    private readonly object _snapshotSync = new();
    private readonly SemaphoreSlim _piMutationGate = new(1, 1);
    private PiConfigurationSnapshot? _cachedSnapshot;
    private Task<PiConfigurationSnapshot>? _snapshotRefresh;
    private bool _snapshotRefreshIncludesNetwork;
    private DateTimeOffset _lastLiveRefresh = DateTimeOffset.MinValue;

    public event Action<PiConfigurationSnapshot>? SnapshotChanged;

    public PiConfigurationService(
        PiRuntimeResolver runtimeResolver,
        string helperPath,
        string? cachePath = null)
    {
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _helperPath = Path.GetFullPath(helperPath);
        _cachePath = Path.GetFullPath(cachePath ?? GetDefaultCachePath());
        LoadCachedSnapshot();
    }

    public static PiConfigurationService CreateDefault() => new(
        new PiRuntimeResolver(),
        Path.Combine(AppContext.BaseDirectory, "PiExtension", "pi-settings.mjs"));

    public PiConfigurationSnapshot? CachedSnapshot
    {
        get
        {
            lock (_snapshotSync)
            {
                return _cachedSnapshot;
            }
        }
    }

    public Task<PiConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        CachedSnapshot is { } snapshot
            ? Task.FromResult(snapshot)
            : RefreshSnapshotAsync(cancellationToken);

    public Task<PiConfigurationSnapshot> EnsureFreshSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_snapshotSync)
        {
            if (_cachedSnapshot is { } snapshot &&
                DateTimeOffset.UtcNow - _lastLiveRefresh <= DefaultFreshness)
            {
                return Task.FromResult(snapshot);
            }
        }

        return RefreshSnapshotAsync(cancellationToken);
    }

    public Task<PiConfigurationSnapshot> RefreshSnapshotAsync(CancellationToken cancellationToken = default) =>
        RefreshSnapshotAsync(refreshModels: false, cancellationToken);

    public Task<PiConfigurationSnapshot> RefreshModelCatalogAsync(CancellationToken cancellationToken = default) =>
        RefreshSnapshotAsync(refreshModels: true, cancellationToken);

    private async Task<PiConfigurationSnapshot> RefreshSnapshotAsync(
        bool refreshModels,
        CancellationToken cancellationToken)
    {
        Task<PiConfigurationSnapshot> refresh;
        var followWithNetworkRefresh = false;
        lock (_snapshotSync)
        {
            if (_snapshotRefresh is null)
            {
                refresh = RefreshSnapshotCoreAsync(refreshModels);
                _snapshotRefresh = refresh;
                _snapshotRefreshIncludesNetwork = refreshModels;
                _ = refresh.ContinueWith(
                    completed =>
                    {
                        lock (_snapshotSync)
                        {
                            if (ReferenceEquals(_snapshotRefresh, completed))
                            {
                                _snapshotRefresh = null;
                                _snapshotRefreshIncludesNetwork = false;
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                refresh = _snapshotRefresh;
                followWithNetworkRefresh = refreshModels && !_snapshotRefreshIncludesNetwork;
            }
        }

        var snapshot = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        return followWithNetworkRefresh
            ? await RefreshSnapshotAsync(refreshModels: true, cancellationToken).ConfigureAwait(false)
            : snapshot;
    }

    public Task<PiConfigurationSnapshot> SaveApiKeyAsync(
        string providerId,
        string apiKey,
        CancellationToken cancellationToken = default) =>
        InvokeAndCacheAsync(new { action = "save-api-key", providerId, apiKey }, cancellationToken);

    public Task<PiConfigurationSnapshot> LogoutAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        InvokeAndCacheAsync(new { action = "logout", providerId }, cancellationToken);

    public async Task<PiConfigurationSnapshot> AddCustomProviderAsync(
        PiCustomProviderInfo provider,
        string? apiKey,
        string? modelsConfigRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        await _piMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InvokeAndCacheAsync(new
            {
                action = "add-custom-provider",
                provider,
                apiKey,
                modelsConfigRevision,
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _piMutationGate.Release();
        }
    }

    public async Task<PiConfigurationSnapshot> UpdateCustomProviderAsync(
        PiCustomProviderInfo provider,
        string? apiKey,
        string? modelsConfigRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        await _piMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InvokeAndCacheAsync(new
            {
                action = "update-custom-provider",
                provider,
                apiKey,
                modelsConfigRevision,
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _piMutationGate.Release();
        }
    }

    public async Task<PiConfigurationSnapshot> DeleteCustomProviderAsync(
        string providerId,
        string? modelsConfigRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await _piMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InvokeAndCacheAsync(new
            {
                action = "delete-custom-provider",
                providerId,
                modelsConfigRevision,
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _piMutationGate.Release();
        }
    }

    public Task<PiConfigurationSnapshot> SaveAgentDefaultsAsync(
        string defaultModel,
        string defaultThinkingLevel,
        bool autoCompact,
        bool autoRetry,
        int compactionReserveTokens,
        int compactionKeepRecentTokens,
        int retryMaxRetries,
        int retryBaseDelayMilliseconds,
        int retryMaxDelayMilliseconds,
        string steeringMode,
        string followUpMode,
        CancellationToken cancellationToken = default) =>
        InvokeAndCacheAsync(new
        {
            action = "save-agent-defaults",
            defaultModel,
            defaultThinkingLevel,
            autoCompact,
            autoRetry,
            compactionReserveTokens,
            compactionKeepRecentTokens,
            retryMaxRetries,
            retryBaseDelayMilliseconds,
            retryMaxDelayMilliseconds,
            steeringMode,
            followUpMode,
        }, cancellationToken);

    public async Task<PiConfigurationSnapshot> LoginOAuthAsync(
        string providerId,
        Action<PiOAuthLoginEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(onEvent);
        var runtime = _runtimeResolver.Resolve();
        if (!Path.GetExtension(runtime.RuntimePath).Equals(".js", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(runtime.RuntimePath).Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前 Pi Runtime 不支持从 GUI 启动 OAuth 登录。");
        }

        var startInfo = CreateHelperStartInfo(runtime);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Pi OAuth 登录进程未能启动。");
        }

        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(new { piEntry = runtime.RuntimePath, action = "login-oauth", providerId }, JsonOptions).AsMemory(),
            cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        PiConfigurationSnapshot? result = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
                if (kind == "event" && root.TryGetProperty("event", out var eventElement))
                {
                    var loginEvent = eventElement.Deserialize<PiOAuthLoginEvent>(JsonOptions);
                    if (loginEvent is not null)
                    {
                        if (loginEvent.Type == "device_code" && loginEvent.ExpiresInSeconds is > 0)
                        {
                            var timeoutSeconds = Math.Clamp((long)loginEvent.ExpiresInSeconds.Value + 30, 300, 1800);
                            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                        }
                        onEvent(loginEvent);
                    }
                }
                else if (kind == "result" && root.TryGetProperty("snapshot", out var snapshotElement))
                {
                    result = NormalizeSnapshot(snapshotElement.Deserialize<PiConfigurationSnapshot>(JsonOptions));
                }
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new InvalidOperationException("等待浏览器 OAuth 登录超时。");
        }

        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Pi OAuth 登录失败。" : error.Trim());
        }

        var snapshot = (result ?? throw new JsonException("Pi OAuth 登录未返回配置状态。")) with
        {
            RuntimePath = runtime.RuntimePath,
        };
        CacheSnapshot(snapshot, isLiveRefresh: true);
        return snapshot;
    }

    public string LaunchInteractiveLogin(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var runtime = _runtimeResolver.Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.FileName,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = true,
        };
        foreach (var argument in runtime.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--no-session");
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("未能打开 Pi 登录终端。");
        return $"已打开 Pi；请运行 /login {providerId}，完成后回到这里刷新状态。";
    }

    private async Task<PiConfigurationSnapshot> InvokeAsync(object request, CancellationToken cancellationToken)
    {
        var runtime = _runtimeResolver.Resolve();
        if (!Path.GetExtension(runtime.RuntimePath).Equals(".js", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(runtime.RuntimePath).Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前 Pi Runtime 是单文件可执行程序，暂不能从设置页读取 Provider 目录。");
        }

        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException("Pi 设置适配器缺失。", _helperPath);
        }

        var startInfo = CreateHelperStartInfo(runtime);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Pi 设置适配器未能启动。");
        }

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        using (var document = JsonDocument.Parse(payload))
        {
            var withRuntime = new Dictionary<string, object?> { ["piEntry"] = runtime.RuntimePath };
            foreach (var property in document.RootElement.EnumerateObject())
            {
                withRuntime[property.Name] = property.Value.Clone();
            }

            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(withRuntime, JsonOptions).AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("读取 Pi Provider 与模型信息超时。");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "Pi 设置适配器执行失败。" : error.Trim());
        }

        var snapshot = NormalizeSnapshot(JsonSerializer.Deserialize<PiConfigurationSnapshot>(output, JsonOptions)) ??
            throw new JsonException("Pi 设置适配器返回了空结果。");
        return snapshot with { RuntimePath = runtime.RuntimePath };
    }

    private async Task<PiConfigurationSnapshot> RefreshSnapshotCoreAsync(bool refreshModels)
    {
        try
        {
            var snapshot = await InvokeAsync(
                new { action = "snapshot", refreshModels },
                CancellationToken.None).ConfigureAwait(false);
            CacheSnapshot(snapshot, isLiveRefresh: true);
            return snapshot;
        }
        catch (Exception exception) when (
            !refreshModels &&
            exception is IOException or InvalidOperationException or JsonException or NotSupportedException)
        {
            return CachedSnapshot ?? PiConfigurationSnapshot.Unavailable(exception.Message);
        }
    }

    private async Task<PiConfigurationSnapshot> InvokeAndCacheAsync(
        object request,
        CancellationToken cancellationToken)
    {
        var snapshot = await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        CacheSnapshot(snapshot, isLiveRefresh: true);
        return snapshot;
    }

    private void CacheSnapshot(PiConfigurationSnapshot snapshot, bool isLiveRefresh)
    {
        if (!snapshot.Available)
        {
            return;
        }

        lock (_snapshotSync)
        {
            _cachedSnapshot = snapshot;
            if (isLiveRefresh)
            {
                _lastLiveRefresh = DateTimeOffset.UtcNow;
            }
        }

        PersistCachedSnapshot(snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    private void LoadCachedSnapshot()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var snapshot = NormalizeSnapshot(JsonSerializer.Deserialize<PiConfigurationSnapshot>(
                File.ReadAllText(_cachePath, Encoding.UTF8),
                JsonOptions));
            var runtime = _runtimeResolver.Resolve();
            if (snapshot?.Available == true &&
                string.Equals(snapshot.RuntimePath, runtime.RuntimePath, StringComparison.OrdinalIgnoreCase))
            {
                lock (_snapshotSync)
                {
                    _cachedSnapshot = snapshot;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
    }

    private void PersistCachedSnapshot(PiConfigurationSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_cachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static string GetDefaultCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PiCompanion",
        "cache",
        "pi-configuration-snapshot.json");

    private static PiConfigurationSnapshot? NormalizeSnapshot(PiConfigurationSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var legacySnapshot = string.IsNullOrWhiteSpace(snapshot.SteeringMode) ||
            string.IsNullOrWhiteSpace(snapshot.FollowUpMode);
        return snapshot with
        {
            CompactionReserveTokens = snapshot.CompactionReserveTokens <= 0 ? 16384 : snapshot.CompactionReserveTokens,
            CompactionKeepRecentTokens = snapshot.CompactionKeepRecentTokens <= 0 ? 20000 : snapshot.CompactionKeepRecentTokens,
            RetryMaxRetries = legacySnapshot ? 3 : snapshot.RetryMaxRetries,
            RetryBaseDelayMilliseconds = snapshot.RetryBaseDelayMilliseconds <= 0 ? 2000 : snapshot.RetryBaseDelayMilliseconds,
            RetryMaxDelayMilliseconds = legacySnapshot ? 60000 : snapshot.RetryMaxDelayMilliseconds,
            SteeringMode = string.IsNullOrWhiteSpace(snapshot.SteeringMode) ? "one-at-a-time" : snapshot.SteeringMode,
            FollowUpMode = string.IsNullOrWhiteSpace(snapshot.FollowUpMode) ? "one-at-a-time" : snapshot.FollowUpMode,
            CustomProviders = snapshot.CustomProviders ?? [],
        };
    }

    private ProcessStartInfo CreateHelperStartInfo(PiRuntimeCommand runtime)
    {
        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException("Pi 设置适配器缺失。", _helperPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        startInfo.ArgumentList.Add(_helperPath);
        return startInfo;
    }
}
