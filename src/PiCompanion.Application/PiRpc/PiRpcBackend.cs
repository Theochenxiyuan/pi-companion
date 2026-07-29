using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiCompanion.Application.Skills;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Application.PiRpc;

public sealed class PiRpcBackend : IAgentBackend, IAgentBackendPrewarmer, IAgentBackendWorkspaceReleaser,
    IAgentBackendResourceInvalidator, IAgentSessionStatisticsProvider, IAgentSessionCommandController, IDisposable
{
    private const int ToolOutputMaximumLength = 24_000;
    private const string OtherChoice = "其他…";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumWorkerCount = 2;
    private static readonly TimeSpan WarmWorkerIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WarmWorkerCleanupInterval = TimeSpan.FromMinutes(1);
    private const int MaximumNativeImageCount = 8;
    private const long MaximumNativeImageBytes = 10L * 1024 * 1024;
    private const long MaximumNativeImageTotalBytes = 24L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> NativeImageMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _historicalStatisticsGate = new(1, 1);
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private readonly PiRuntimeResolver _runtimeResolver;
    private readonly string _sessionDirectory;
    private readonly string _logDirectory;
    private readonly string? _extensionPath;
    private readonly string? _webSearchExtensionPath;
    private readonly string _backupDirectory;
    private readonly string _grantDirectory;
    private readonly SkillDiscoveryService? _skillDiscovery;
    private readonly PiProjectTrustService _projectTrust;
    private readonly Timer _warmCleanupTimer;
    private readonly Dictionary<Guid, RunContext> _active = [];
    private readonly List<RunContext> _warm = [];
    private bool _disposed;

    public PiRpcBackend(
        PiRuntimeResolver runtimeResolver,
        string sessionDirectory,
        string logDirectory,
        string? extensionPath = null,
        string? backupDirectory = null,
        string? grantDirectory = null,
        string? webSearchExtensionPath = null,
        SkillDiscoveryService? skillDiscovery = null,
        PiProjectTrustService? projectTrust = null)
    {
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _sessionDirectory = Path.GetFullPath(sessionDirectory);
        _logDirectory = Path.GetFullPath(logDirectory);
        _extensionPath = string.IsNullOrWhiteSpace(extensionPath) ? null : Path.GetFullPath(extensionPath);
        _webSearchExtensionPath = string.IsNullOrWhiteSpace(webSearchExtensionPath)
            ? null
            : Path.GetFullPath(webSearchExtensionPath);
        _backupDirectory = Path.GetFullPath(backupDirectory ?? Path.Combine(_sessionDirectory, "..", "backups"));
        _grantDirectory = Path.GetFullPath(grantDirectory ?? Path.Combine(_sessionDirectory, "..", "permission-grants"));
        _skillDiscovery = skillDiscovery;
        _projectTrust = projectTrust ?? new PiProjectTrustService();
        _warmCleanupTimer = new Timer(
            _ => RemoveExpiredWarmWorkers(),
            null,
            WarmWorkerCleanupInterval,
            WarmWorkerCleanupInterval);
    }

    public static PiRpcBackend CreateDefault(
        SkillDiscoveryService? skillDiscovery = null)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion");
        return new PiRpcBackend(
            new PiRuntimeResolver(),
            Path.Combine(dataDirectory, "sessions"),
            Path.Combine(dataDirectory, "logs"),
            Path.Combine(AppContext.BaseDirectory, "PiExtension", "pi-companion.mjs"),
            Path.Combine(dataDirectory, "backups"),
            Path.Combine(dataDirectory, "permission-grants"),
            Path.Combine(AppContext.BaseDirectory, "PiExtension", "pi-web-search.mjs"),
            skillDiscovery ?? new SkillDiscoveryService());
    }

    public event Action<CompanionRunEvent>? EventReceived;

    public event Action<AgentToolExecution>? ToolExecutionCompleted;

    public async Task PrepareAsync(
        AgentPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{request.WorkingDirectory}");
        }

        await _preparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RunContext? context = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_extensionPath is null || !File.Exists(_extensionPath))
            {
                throw new FileNotFoundException("应用自带的 Pi Companion Extension 缺失。", _extensionPath);
            }

            var runtime = _runtimeResolver.Resolve();
            var preparationRequest = new AgentRunRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Pi RPC 预热",
                string.Empty,
                request.WorkingDirectory,
                request.Model,
                request.ThinkingLevel,
                "Preparation",
                PermissionMode: "read-only");
            var reuseKey = CreateReuseKey(runtime, preparationRequest);
            List<RunContext> stale;
            bool alreadyPrepared;
            bool activeAtCapacity;
            lock (_gate)
            {
                stale = _warm
                    .Where(candidate => !CanReuse(candidate, candidate.ReuseKey))
                    .ToList();
                foreach (var candidate in stale)
                {
                    _warm.Remove(candidate);
                }

                alreadyPrepared =
                    _active.Values.Any(candidate => CanReuse(candidate, reuseKey)) ||
                    _warm.Any(candidate => CanReuse(candidate, reuseKey));
                activeAtCapacity = _active.Count >= MaximumWorkerCount;
            }

            await StopStaleWarmContextsAsync(stale).ConfigureAwait(false);
            if (alreadyPrepared || activeAtCapacity)
            {
                return;
            }

            var runtimeFileStem = Guid.NewGuid().ToString("N");
            context = new RunContext(
                preparationRequest,
                reuseKey,
                Path.Combine(_sessionDirectory, ".runtime", $"{runtimeFileStem}.run"),
                Path.Combine(_sessionDirectory, ".runtime", $"{runtimeFileStem}.context.json"))
            {
                ExpectedStop = false,
                SuppressEvents = true,
                CurrentStatus = RunStatus.Starting,
            };

            Directory.CreateDirectory(_sessionDirectory);
            Directory.CreateDirectory(_logDirectory);
            Directory.CreateDirectory(_backupDirectory);
            Directory.CreateDirectory(_grantDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(context.RunIdentityPath)!);
            WriteRunIdentity(context);
            WriteRuntimeContext(context, "read-only");

            var process = new Process
            {
                StartInfo = CreateStartInfo(
                    runtime,
                    preparationRequest,
                    context.PermissionToken,
                    "read-only",
                    context.RunIdentityPath,
                    context.RuntimeContextPath),
                EnableRaisingEvents = true,
            };
            if (!process.Start())
            {
                throw new InvalidOperationException("Pi Runtime 进程未能启动。");
            }

            context.Process = process;
            context.Job = new WindowsJobObject();
            context.Job.Assign(process);
            context.StdoutTask = ReadStdoutAsync(context);
            context.StderrTask = ReadStderrAsync(context);
            context.ExitTask = ObserveExitAsync(context);
            var state = await SendCommandAsync(
                context,
                new Dictionary<string, object?> { ["type"] = "get_state" },
                cancellationToken).ConfigureAwait(false);
            PublishSessionState(context, state);
            context.LastUsedAt = DateTimeOffset.UtcNow;

            RunContext? evicted = null;
            var retained = false;
            lock (_gate)
            {
                if (!_disposed &&
                    !_active.Values.Any(candidate => CanReuse(candidate, reuseKey)) &&
                    !_warm.Any(candidate => CanReuse(candidate, reuseKey)))
                {
                    _warm.Add(context);
                    retained = true;
                    var idleCapacity = MaximumWorkerCount - _active.Count;
                    if (_warm.Count > idleCapacity)
                    {
                        evicted = _warm.OrderBy(candidate => candidate.LastUsedAt).First();
                        _warm.Remove(evicted);
                        evicted.ExpectedStop = true;
                    }
                }
            }

            if (!retained)
            {
                context.ExpectedStop = true;
                StopProcess(context);
                await WaitForShutdownAsync(context).ConfigureAwait(false);
            }

            if (evicted is not null)
            {
                StopProcess(evicted);
                await WaitForShutdownAsync(evicted).ConfigureAwait(false);
            }
        }
        catch
        {
            if (context is not null)
            {
                context.ExpectedStop = true;
                StopProcess(context);
                await WaitForShutdownAsync(context).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _preparationGate.Release();
        }
    }

    public async Task StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{request.WorkingDirectory}");
        }

        await _preparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _preparationGate.Release();
        var runtime = _runtimeResolver.Resolve();
        var permissionMode = NormalizePermissionMode(request.PermissionMode);
        var reuseKey = CreateReuseKey(runtime, request);
        RunContext context;
        List<RunContext> staleWarm = [];
        var reused = false;
        lock (_gate)
        {
            if (_active.ContainsKey(request.RunId))
            {
                throw new InvalidOperationException("Pi RPC 后端当前已有活动 Run。");
            }

            if (_active.Values.Any(candidate =>
                    !candidate.IsTerminal &&
                    candidate.Request.TaskId == request.TaskId))
            {
                throw new InvalidOperationException("同一个任务不能同时运行多个 Pi Run。");
            }

            if (_active.Count >= MaximumWorkerCount)
            {
                throw new InvalidOperationException("Pi RPC 后端已达到最大并发 Run 数。");
            }

            staleWarm = _warm.Where(warm => !CanReuse(warm, warm.ReuseKey)).ToList();
            foreach (var stale in staleWarm)
            {
                _warm.Remove(stale);
            }

            var warm = _warm
                .Where(candidate => CanReuse(candidate, reuseKey))
                .OrderByDescending(candidate => candidate.LastUsedAt)
                .FirstOrDefault();
            if (warm is not null)
            {
                context = warm;
                _warm.Remove(warm);
                context.BeginRun(request);
                reused = true;
            }
            else
            {
                while (_active.Count + _warm.Count >= MaximumWorkerCount)
                {
                    var oldest = _warm.OrderBy(candidate => candidate.LastUsedAt).First();
                    _warm.Remove(oldest);
                    oldest.ExpectedStop = true;
                    staleWarm.Add(oldest);
                }

                context = new RunContext(
                    request,
                    reuseKey,
                    Path.Combine(_sessionDirectory, ".runtime", $"{Guid.NewGuid():N}.run"),
                    Path.Combine(_sessionDirectory, ".runtime", $"{Guid.NewGuid():N}.context.json"));
            }

            _active.Add(request.RunId, context);
        }

        Emit(context, CompanionRunEventKind.RunQueued, RunStatus.Queued, string.Empty, "等待 Pi Runtime");
        try
        {
            await StopStaleWarmContextsAsync(staleWarm).ConfigureAwait(false);
            if (_extensionPath is null || !File.Exists(_extensionPath))
            {
                throw new FileNotFoundException("应用自带的 Pi Companion Extension 缺失。", _extensionPath);
            }

            Directory.CreateDirectory(_sessionDirectory);
            Directory.CreateDirectory(_logDirectory);
            Directory.CreateDirectory(_backupDirectory);
            Directory.CreateDirectory(_grantDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(context.RunIdentityPath)!);
            WriteRunIdentity(context);
            WriteRuntimeContext(context, permissionMode);

            if (reused)
            {
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.RunStarted,
                    "rpc-reused",
                    "已复用当前工作区的 Pi RPC",
                    "正在复用 Pi RPC");
                await ConfigureReusedContextAsync(context, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var startInfo = CreateStartInfo(
                    runtime,
                    request,
                    context.PermissionToken,
                    permissionMode,
                    context.RunIdentityPath,
                    context.RuntimeContextPath);
                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true,
                };
                if (!process.Start())
                {
                    throw new InvalidOperationException("Pi Runtime 进程未能启动。");
                }

                context.Process = process;
                context.Job = new WindowsJobObject();
                context.Job.Assign(process);
                context.StdoutTask = ReadStdoutAsync(context);
                context.StderrTask = ReadStderrAsync(context);
                context.ExitTask = ObserveExitAsync(context);
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.RunStarted,
                    "rpc-connecting",
                    $"已启动应用私有 Pi RPC：{Path.GetFileName(runtime.RuntimePath)}",
                    "正在连接 Pi RPC",
                    new Dictionary<string, string> { ["runtimePath"] = runtime.RuntimePath });

                if (!string.IsNullOrWhiteSpace(request.PiSessionPath))
                {
                    if (File.Exists(request.PiSessionPath))
                    {
                        EmitStartupPhase(
                            context,
                            CompanionRunEventKind.QueueChanged,
                            "session-restoring",
                            "开始恢复已有 Pi Session",
                            "正在恢复 Pi Session");
                        await SendCommandAsync(
                            context,
                            new Dictionary<string, object?>
                            {
                                ["type"] = "switch_session",
                                ["sessionPath"] = request.PiSessionPath,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        Emit(
                            context,
                            CompanionRunEventKind.WarningRaised,
                            RunStatus.Starting,
                            "已保存的 Pi Session 文件不存在，将创建新 Session",
                            "Session 恢复不可用");
                    }
                }

                if (string.IsNullOrWhiteSpace(request.PiSessionPath) || !File.Exists(request.PiSessionPath))
                {
                    EmitStartupPhase(
                        context,
                        CompanionRunEventKind.QueueChanged,
                        "session-creating",
                        "Pi RPC 正在创建新 Session",
                        "正在创建 Pi Session");
                }

                var state = await SendCommandAsync(
                    context,
                    new Dictionary<string, object?> { ["type"] = "get_state" },
                    cancellationToken).ConfigureAwait(false);
                PublishSessionState(context, state);
            }

            await ConfigureQueueModesAsync(context, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(context.Request.PiEntryCursor))
            {
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    "session-reconciling",
                    "开始对齐 Pi Session 增量历史",
                    "正在同步 Pi Session 历史");
            }

            await ReconcileSessionEntriesAsync(context, cancellationToken).ConfigureAwait(false);
            if (context.SupportsNativeImages && context.Request.Attachments is { Count: > 0 })
            {
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    "attachments-preparing",
                    "开始准备原生图片附件",
                    "正在准备附件");
            }

            var nativeImages = await PrepareNativeImagesAsync(context, cancellationToken).ConfigureAwait(false);
            var promptCommand = new Dictionary<string, object?>
            {
                ["type"] = "prompt",
                ["message"] = BuildPrompt(request, nativeImages.Select(image => image.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            };
            if (nativeImages.Count > 0)
            {
                promptCommand["images"] = nativeImages.Select(image => new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["data"] = image.Base64Data,
                    ["mimeType"] = image.MimeType,
                }).ToArray();
            }

            EmitStartupPhase(
                context,
                CompanionRunEventKind.QueueChanged,
                "prompt-submitting",
                "Pi Session 准备完成",
                "正在提交任务");
            await SendCommandAsync(context, promptCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (context.TrySetTerminal())
            {
                Emit(
                    context,
                    CompanionRunEventKind.RunInterrupted,
                    RunStatus.Interrupted,
                    "任务启动已取消",
                    "启动已取消",
                    new Dictionary<string, string> { ["exitReason"] = "startup-cancelled" });
                context.Completion.TrySetResult();
            }

            context.ExpectedStop = true;
            StopProcess(context);
            ClearCurrent(context);
            throw;
        }
        catch
        {
            context.ExpectedStop = true;
            StopProcess(context);
            ClearCurrent(context);
            throw;
        }
    }

    public Task SteerAsync(Guid runId, string message, CancellationToken cancellationToken = default) =>
        SendMessageCommandAsync(runId, "steer", message, cancellationToken);

    public Task FollowUpAsync(Guid runId, string message, CancellationToken cancellationToken = default) =>
        SendMessageCommandAsync(runId, "follow_up", message, cancellationToken);

    public async Task ResolveInteractionAsync(
        Guid runId,
        InteractionResolution resolution,
        CancellationToken cancellationToken = default)
    {
        var context = RequireCurrent(runId);
        PendingInteraction interaction;
        lock (context.InteractionGate)
        {
            var index = string.IsNullOrWhiteSpace(resolution.InteractionId)
                ? 0
                : context.PendingInteractions.FindIndex(item => item.Id == resolution.InteractionId);
            if (index < 0 || context.PendingInteractions.Count == 0)
            {
                throw new InvalidOperationException("指定的交互请求已不存在或已处理。");
            }

            interaction = context.PendingInteractions[index];
            if (!context.ResolvingInteractionIds.Add(interaction.Id))
            {
                throw new InvalidOperationException("该交互请求正在处理，请勿重复提交。");
            }
        }

        var response = new Dictionary<string, object?>
        {
            ["type"] = "extension_ui_response",
            ["id"] = interaction.Id,
        };
        var selectedValue = resolution.Approved
            ? resolution.Response ?? interaction.DefaultValue
            : null;
        if (!resolution.Approved)
        {
            response["cancelled"] = true;
        }
        else if (interaction.Method.Equals("confirm", StringComparison.Ordinal))
        {
            response["confirmed"] = true;
        }
        else
        {
            if (interaction.Method is "input" or "editor" && string.IsNullOrWhiteSpace(selectedValue))
            {
                lock (context.InteractionGate)
                {
                    context.ResolvingInteractionIds.Remove(interaction.Id);
                }

                throw new InvalidOperationException("请输入回答后再提交。");
            }

            var acceptsCustomChoice = interaction.Method == "select" &&
                interaction.Options.Contains(OtherChoice, StringComparer.Ordinal);
            if (interaction.Options.Count > 0 &&
                (string.IsNullOrWhiteSpace(selectedValue) ||
                 (!acceptsCustomChoice && !interaction.Options.Contains(selectedValue, StringComparer.Ordinal))))
            {
                lock (context.InteractionGate)
                {
                    context.ResolvingInteractionIds.Remove(interaction.Id);
                }

                throw new InvalidOperationException("请选择交互请求提供的有效选项。");
            }

            response["value"] = selectedValue;
        }

        try
        {
            await WriteMessageAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (context.InteractionGate)
            {
                context.ResolvingInteractionIds.Remove(interaction.Id);
            }

            throw;
        }

        var interactionPayload = new Dictionary<string, string>
        {
            ["interactionId"] = interaction.Id,
            ["approved"] = resolution.Approved ? "true" : "false",
        };
        if (resolution.Approved && !string.IsNullOrWhiteSpace(selectedValue))
        {
            interactionPayload["response"] = selectedValue;
        }

        PendingInteraction? nextInteraction;
        lock (context.InteractionGate)
        {
            context.PendingInteractions.RemoveAll(item => item.Id == interaction.Id);
            context.ResolvingInteractionIds.Remove(interaction.Id);
            nextInteraction = context.PendingInteractions.FirstOrDefault();
        }

        var nextStatus = nextInteraction is null
            ? RunStatus.Running
            : nextInteraction.IsApproval ? RunStatus.WaitingForApproval : RunStatus.WaitingForAnswer;
        Emit(
            context,
            CompanionRunEventKind.InteractionResolved,
            nextStatus,
            resolution.Approved ? "已提交交互响应" : "已取消交互请求",
            nextInteraction is null ? "Pi Agent 继续运行" : "仍有交互请求等待处理",
            interactionPayload);
    }

    public async Task AbortAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var context = RequireCurrent(runId);
        if (context.AbortRequested)
        {
            return;
        }

        context.AbortRequested = true;
        Emit(context, CompanionRunEventKind.QueueChanged, RunStatus.Cancelling, "正在停止任务", "正在停止");
        _ = ForceAbortAfterTimeoutAsync(context);
        try
        {
            await SendCommandWithoutResponseAsync(
                context,
                new Dictionary<string, object?> { ["type"] = "abort" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (context.AbortRequested)
        {
            // The force-abort watchdog owns the terminal outcome even if the RPC
            // process exits or stops accepting input before acknowledging abort.
        }
    }

    public async Task AbortRetryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var context = RequireCurrent(runId);
        if (!context.AutoRetryActive)
        {
            throw new InvalidOperationException("当前没有正在等待的自动重试。");
        }

        context.MarkRetryAbortRequested();
        try
        {
            await SendCommandAsync(
                context,
                new Dictionary<string, object?> { ["type"] = "abort_retry" },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            context.ClearRetryAbortRequested();
            throw;
        }
    }

    public async Task<AgentSessionStatistics?> GetSessionStatisticsAsync(
        AgentSessionStatisticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        RunContext? context;
        lock (_gate)
        {
            context = _active.Values.FirstOrDefault(active => active.Request.TaskId == request.TaskId)
                ?? _warm.FirstOrDefault(warm => warm.Request.TaskId == request.TaskId);
        }

        if (context is not null)
        {
            return await ReadSessionStatisticsAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (!request.LoadHistoricalSession)
        {
            return null;
        }

        return await LoadHistoricalSessionStatisticsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompactAsync(
        AgentSessionCommandRequest request,
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        RunContext? active;
        RunContext? warm;
        lock (_gate)
        {
            active = _active.Values.FirstOrDefault(candidate => candidate.Request.TaskId == request.TaskId);
            warm = _warm.FirstOrDefault(candidate => candidate.Request.TaskId == request.TaskId);
        }

        if (active is not null)
        {
            throw new InvalidOperationException("任务运行中，完成或停止后才能压缩上下文。");
        }

        if (warm is not null)
        {
            await SendCommandAsync(
                warm,
                new Dictionary<string, object?>
                {
                    ["type"] = "compact",
                    ["customInstructions"] = string.IsNullOrWhiteSpace(customInstructions)
                        ? null
                        : customInstructions.Trim(),
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunHistoricalSessionCommandAsync(
            request.TaskId,
            request.WorkingDirectory,
            request.Model,
            request.ThinkingLevel,
            request.SessionPath,
            "历史 Session 压缩",
            async (context, token) =>
            {
                await SendCommandAsync(
                    context,
                    new Dictionary<string, object?>
                    {
                        ["type"] = "compact",
                        ["customInstructions"] = string.IsNullOrWhiteSpace(customInstructions)
                            ? null
                            : customInstructions.Trim(),
                    },
                    token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentSessionStatistics?> ReadSessionStatisticsAsync(
        RunContext context,
        CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            context,
            new Dictionary<string, object?> { ["type"] = "get_session_stats" },
            cancellationToken).ConfigureAwait(false);
        return ParseSessionStatistics(response);
    }

    private async Task<AgentSessionStatistics?> LoadHistoricalSessionStatisticsAsync(
        AgentSessionStatisticsRequest statisticsRequest,
        CancellationToken cancellationToken) =>
        await RunHistoricalSessionCommandAsync(
            statisticsRequest.TaskId,
            statisticsRequest.WorkingDirectory,
            statisticsRequest.Model,
            statisticsRequest.ThinkingLevel,
            statisticsRequest.SessionPath,
            "历史 Session 统计",
            ReadSessionStatisticsAsync,
            cancellationToken).ConfigureAwait(false);

    private async Task<T> RunHistoricalSessionCommandAsync<T>(
        Guid taskId,
        string workingDirectory,
        string model,
        string thinkingLevel,
        string? sourceSessionPath,
        string title,
        Func<RunContext, CancellationToken, Task<T>> command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceSessionPath))
        {
            throw new InvalidOperationException("该历史任务没有可恢复的 Pi Session。");
        }

        var sessionPath = Path.GetFullPath(sourceSessionPath);
        if (!File.Exists(sessionPath))
        {
            throw new FileNotFoundException("历史 Pi Session 文件不存在。", sessionPath);
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"历史任务的工作目录不存在：{workingDirectory}");
        }

        if (_extensionPath is null || !File.Exists(_extensionPath))
        {
            throw new FileNotFoundException("应用自带的 Pi Companion Extension 缺失。", _extensionPath);
        }

        await _historicalStatisticsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var runtime = _runtimeResolver.Resolve();
            const string permissionMode = "read-only";
            var runRequest = new AgentRunRequest(
                taskId,
                Guid.NewGuid(),
                title,
                string.Empty,
                workingDirectory,
                model,
                thinkingLevel,
                "HistoryStatistics",
                PiSessionPath: sessionPath,
                PermissionMode: permissionMode);
            var runIdentityPath = Path.Combine(_sessionDirectory, ".runtime", $"{runRequest.RunId:N}.stats");
            var runtimeContextPath = Path.Combine(_sessionDirectory, ".runtime", $"{runRequest.RunId:N}.stats.context.json");
            var context = new RunContext(
                runRequest,
                CreateReuseKey(runtime, runRequest),
                runIdentityPath,
                runtimeContextPath)
            {
                ExpectedStop = true,
                SuppressEvents = true,
                CurrentStatus = RunStatus.Starting,
            };

            try
            {
                Directory.CreateDirectory(_sessionDirectory);
                Directory.CreateDirectory(_logDirectory);
                Directory.CreateDirectory(_backupDirectory);
                Directory.CreateDirectory(_grantDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(runIdentityPath)!);
                WriteRunIdentity(context);
                WriteRuntimeContext(context, permissionMode);

                var process = new Process
                {
                    StartInfo = CreateStartInfo(
                        runtime,
                        runRequest,
                        context.PermissionToken,
                        permissionMode,
                        runIdentityPath,
                        runtimeContextPath),
                    EnableRaisingEvents = true,
                };
                if (!process.Start())
                {
                    throw new InvalidOperationException("Pi Runtime 进程未能启动。");
                }

                context.Process = process;
                context.Job = new WindowsJobObject();
                context.Job.Assign(process);
                context.StdoutTask = ReadStdoutAsync(context);
                context.StderrTask = ReadStderrAsync(context);
                context.ExitTask = ObserveExitAsync(context);

                await SendCommandAsync(
                    context,
                    new Dictionary<string, object?>
                    {
                        ["type"] = "switch_session",
                        ["sessionPath"] = sessionPath,
                    },
                    cancellationToken).ConfigureAwait(false);
                return await command(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                StopProcess(context);
                await WaitForShutdownAsync(context).ConfigureAwait(false);
            }
        }
        finally
        {
            _historicalStatisticsGate.Release();
        }
    }

    private static AgentSessionStatistics? ParseSessionStatistics(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tokens = data.TryGetProperty("tokens", out var tokenData) && tokenData.ValueKind == JsonValueKind.Object
            ? tokenData
            : default;
        AgentContextUsage? contextUsage = null;
        if (data.TryGetProperty("contextUsage", out var contextData) && contextData.ValueKind == JsonValueKind.Object)
        {
            var contextWindow = GetOptionalInt32(contextData, "contextWindow") ?? 0;
            if (contextWindow > 0)
            {
                contextUsage = new AgentContextUsage(
                    GetOptionalInt64(contextData, "tokens"),
                    contextWindow,
                    GetOptionalDouble(contextData, "percent"));
            }
        }

        return new AgentSessionStatistics(
            GetOptionalString(data, "sessionId") ?? string.Empty,
            GetOptionalString(data, "sessionFile"),
            GetOptionalInt32(data, "userMessages") ?? 0,
            GetOptionalInt32(data, "assistantMessages") ?? 0,
            GetOptionalInt32(data, "toolCalls") ?? 0,
            GetOptionalInt32(data, "toolResults") ?? 0,
            GetOptionalInt32(data, "totalMessages") ?? 0,
            GetOptionalInt64(tokens, "input") ?? 0,
            GetOptionalInt64(tokens, "output") ?? 0,
            GetOptionalInt64(tokens, "cacheRead") ?? 0,
            GetOptionalInt64(tokens, "cacheWrite") ?? 0,
            GetOptionalInt64(tokens, "total") ?? 0,
            GetOptionalDouble(data, "cost") ?? 0,
            contextUsage);
    }

    public void Dispose()
    {
        RunContext[] active;
        RunContext[] warm;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _warmCleanupTimer.Dispose();
            active = _active.Values.ToArray();
            warm = _warm.ToArray();
            _active.Clear();
            _warm.Clear();
        }

        foreach (var context in active)
        {
            context.ExpectedStop = true;
            context.TrySetTerminal();
            StopProcess(context);
            DeleteRuntimeFiles(context);
        }

        foreach (var idle in warm)
        {
            idle.ExpectedStop = true;
            StopProcess(idle);
            DeleteRuntimeFiles(idle);
        }
    }

    public void ReleaseWorkspace(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        RunContext[] matches;
        lock (_gate)
        {
            matches = _warm
                .Where(context => HasWorkingDirectory(context, normalized))
                .ToArray();
            foreach (var context in matches)
            {
                _warm.Remove(context);
            }

            var activeMatches = _active.Values
                .Where(context => HasWorkingDirectory(context, normalized))
                .ToArray();
            foreach (var context in activeMatches)
            {
                _active.Remove(context.Request.RunId);
            }
            matches = [.. matches, .. activeMatches];

            foreach (var context in matches)
            {
                context.ExpectedStop = true;
            }
        }

        foreach (var context in matches.Distinct())
        {
            StopProcess(context);
            WaitForShutdownAsync(context).GetAwaiter().GetResult();
        }
    }

    public void InvalidateIdleResources(string? workingDirectory = null)
    {
        var normalized = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        RunContext[] matches;
        lock (_gate)
        {
            matches = _warm
                .Where(context => normalized is null || HasWorkingDirectory(context, normalized))
                .ToArray();
            foreach (var context in matches)
            {
                _warm.Remove(context);
                context.ExpectedStop = true;
            }
        }

        foreach (var context in matches)
        {
            StopProcess(context);
            WaitForShutdownAsync(context).GetAwaiter().GetResult();
        }
    }

    private static bool HasWorkingDirectory(RunContext context, string workingDirectory) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.Request.WorkingDirectory)),
            workingDirectory,
            StringComparison.OrdinalIgnoreCase);

    private string CreateReuseKey(
        PiRuntimeCommand runtime,
        AgentRunRequest request)
    {
        var extensionVersion = _extensionPath is not null && File.Exists(_extensionPath)
            ? $"{_extensionPath}|{File.GetLastWriteTimeUtc(_extensionPath).Ticks}|{new FileInfo(_extensionPath).Length}"
            : _extensionPath ?? string.Empty;
        var webSearchSupport = PiWebSearchCapabilities.ResolveModelReference(request.Model);
        var webSearchExtensionVersion =
            webSearchSupport != PiWebSearchSupport.None &&
            _webSearchExtensionPath is not null &&
            File.Exists(_webSearchExtensionPath)
                ? $"{_webSearchExtensionPath}|{File.GetLastWriteTimeUtc(_webSearchExtensionPath).Ticks}|{new FileInfo(_webSearchExtensionPath).Length}"
                : string.Empty;
        return string.Join(
            '\n',
            runtime.FileName,
            runtime.RuntimePath,
            string.Join('\0', runtime.PrefixArguments),
            extensionVersion,
            webSearchSupport,
            webSearchExtensionVersion,
            Path.GetFullPath(request.WorkingDirectory),
            request.ScopeKind.ToString(),
            ResolveWorkspaceTrustStatus(request),
            string.IsNullOrWhiteSpace(request.ArtifactDirectory)
                ? string.Empty
                : Path.GetFullPath(request.ArtifactDirectory),
            IsDefaultModel(request.Model) ? "default-model" : "dynamic-model");
    }

    private static bool CanReuse(RunContext context, string reuseKey)
    {
        if (!string.Equals(context.ReuseKey, reuseKey, StringComparison.Ordinal) || context.Process is null)
        {
            return false;
        }

        try
        {
            return !context.Process.HasExited && !context.Lifetime.IsCancellationRequested;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void RemoveExpiredWarmWorkers()
    {
        RunContext[] expired;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - WarmWorkerIdleTimeout;
            expired = _warm.Where(context => context.LastUsedAt <= cutoff).ToArray();
            foreach (var context in expired)
            {
                _warm.Remove(context);
                context.ExpectedStop = true;
            }
        }

        foreach (var context in expired)
        {
            StopProcess(context);
            _ = WaitForShutdownAsync(context);
        }
    }

    private static void WriteRunIdentity(RunContext context) =>
        File.WriteAllText(context.RunIdentityPath, context.Request.RunId.ToString("D"), new UTF8Encoding(false));

    private void WriteRuntimeContext(RunContext context, string permissionMode)
    {
        var temporaryPath = $"{context.RuntimeContextPath}.{Guid.NewGuid():N}.tmp";
        var readOnlyRoots = string.IsNullOrWhiteSpace(context.Request.ReadOnlyAttachmentRoot)
            ? Array.Empty<string>()
            : [Path.GetFullPath(context.Request.ReadOnlyAttachmentRoot)];
        var skillAccess = ResolveSkillReadAccess(context.Request);
        var document = new RuntimeContextDocument(
            4,
            context.Generation,
            context.Request.TaskId,
            context.Request.RunId,
            Path.GetFullPath(context.Request.WorkingDirectory),
            permissionMode,
            context.PermissionToken,
            readOnlyRoots,
            skillAccess.Roots,
            skillAccess.Files,
            context.Request.ScopeKind.ToString(),
            skillAccess.WorkspaceTrustStatus);
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporaryPath, context.RuntimeContextPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private SkillReadAccess ResolveSkillReadAccess(AgentRunRequest request)
    {
        var workspaceTrustStatus = ResolveWorkspaceTrustStatus(request);
        if (_skillDiscovery is null)
        {
            return new SkillReadAccess([], [], workspaceTrustStatus);
        }

        var workspaceId = request.TaskId;
        IReadOnlyList<SkillDiscoveryWorkspace> workspaces =
            request.ScopeKind == TaskScopeKind.Workspace
                ? [new SkillDiscoveryWorkspace(
                    workspaceId,
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(request.WorkingDirectory)),
                    request.WorkingDirectory,
                    workspaceTrustStatus)]
                : [];
        var snapshot = _skillDiscovery.Discover(workspaces);
        var effective = snapshot.Skills.Where(skill =>
            skill.IsAvailable &&
            (request.ScopeKind == TaskScopeKind.GeneralChat
                ? skill.IsGloballyEffective
                : skill.EffectiveWorkspaceIds.Contains(workspaceId)));
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in effective)
        {
            if (string.Equals(
                    Path.GetFileName(skill.FilePath),
                    "SKILL.md",
                    StringComparison.Ordinal))
            {
                roots.Add(Path.GetFullPath(skill.BaseDirectory));
            }
            else
            {
                files.Add(Path.GetFullPath(skill.FilePath));
            }
        }

        return new SkillReadAccess(
            roots.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            files.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            workspaceTrustStatus);
    }

    private string ResolveWorkspaceTrustStatus(AgentRunRequest request) =>
        request.ScopeKind == TaskScopeKind.GeneralChat
            ? "not-applicable"
            : _projectTrust.GetStatus(request.WorkingDirectory).Status;

    private static void DeleteRuntimeFiles(RunContext context)
    {
        try
        {
            File.Delete(context.RunIdentityPath);
            File.Delete(context.RuntimeContextPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task StopStaleWarmContextsAsync(IReadOnlyList<RunContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        foreach (var context in contexts)
        {
            context.ExpectedStop = true;
            StopProcess(context);
        }

        await Task.WhenAll(contexts.Select(WaitForShutdownAsync)).ConfigureAwait(false);
    }

    private async Task ConfigureReusedContextAsync(
        RunContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var preparedWorker = context.PreviousRequest?.Mode == "Preparation";
        if (preparedWorker && !string.IsNullOrWhiteSpace(request.PiSessionPath))
        {
            if (File.Exists(request.PiSessionPath))
            {
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    "session-restoring",
                    "开始从预热 Pi RPC 恢复已有 Session",
                    "正在恢复 Pi Session");
                await SendCommandAsync(
                    context,
                    new Dictionary<string, object?>
                    {
                        ["type"] = "switch_session",
                        ["sessionPath"] = request.PiSessionPath,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Emit(
                    context,
                    CompanionRunEventKind.WarningRaised,
                    RunStatus.Starting,
                    "已保存的 Pi Session 文件不存在，将使用预热 Session",
                    "Session 恢复不可用");
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    "session-prewarmed",
                    "已接管预热 Pi Session",
                    "正在使用预热 Pi Session");
            }
        }
        else if (preparedWorker)
        {
            EmitStartupPhase(
                context,
                CompanionRunEventKind.QueueChanged,
                "session-prewarmed",
                "已接管预热 Pi Session",
                "正在使用预热 Pi Session");
        }
        else if (!preparedWorker && context.PreviousRequest?.TaskId != request.TaskId)
        {
            if (!string.IsNullOrWhiteSpace(request.PiSessionPath) && File.Exists(request.PiSessionPath))
            {
                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    "session-restoring",
                    "开始切换到已有 Pi Session",
                    "正在恢复 Pi Session");
                await SendCommandAsync(
                    context,
                    new Dictionary<string, object?>
                    {
                        ["type"] = "switch_session",
                        ["sessionPath"] = request.PiSessionPath,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.PiSessionPath))
                {
                    Emit(
                        context,
                        CompanionRunEventKind.WarningRaised,
                        RunStatus.Starting,
                        "已保存的 Pi Session 文件不存在，将创建新 Session",
                        "Session 恢复不可用");
                }

                EmitStartupPhase(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    "session-creating",
                    "开始为任务创建新 Pi Session",
                    "正在创建 Pi Session");
                await SendCommandAsync(
                    context,
                    new Dictionary<string, object?> { ["type"] = "new_session" },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            EmitStartupPhase(
                context,
                CompanionRunEventKind.QueueChanged,
                "session-continuing",
                "继续使用当前任务的 Pi Session",
                "正在继续 Pi Session");
        }

        EmitStartupPhase(
            context,
            CompanionRunEventKind.QueueChanged,
            "session-configuring",
            "开始应用模型和思考设置",
            "正在配置 Pi Session");
        if (!IsDefaultModel(request.Model) && TrySplitModelReference(request.Model, out var provider, out var modelId))
        {
            await SendCommandAsync(
                context,
                new Dictionary<string, object?>
                {
                    ["type"] = "set_model",
                    ["provider"] = provider,
                    ["modelId"] = modelId,
                },
                cancellationToken).ConfigureAwait(false);
        }

        await SendCommandAsync(
            context,
            new Dictionary<string, object?>
            {
                ["type"] = "set_thinking_level",
                ["level"] = NormalizeThinkingLevel(request.ThinkingLevel),
            },
            cancellationToken).ConfigureAwait(false);

        var state = await SendCommandAsync(
            context,
            new Dictionary<string, object?> { ["type"] = "get_state" },
            cancellationToken).ConfigureAwait(false);
        PublishSessionState(context, state);
    }

    private async Task ConfigureQueueModesAsync(
        RunContext context,
        CancellationToken cancellationToken)
    {
        await SendCommandAsync(
            context,
            new Dictionary<string, object?>
            {
                ["type"] = "set_steering_mode",
                ["mode"] = "one-at-a-time",
            },
            cancellationToken).ConfigureAwait(false);

        await SendCommandAsync(
            context,
            new Dictionary<string, object?>
            {
                ["type"] = "set_follow_up_mode",
                ["mode"] = "one-at-a-time",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TrySplitModelReference(
        string reference,
        out string provider,
        out string modelId)
    {
        var separator = reference.IndexOf('/');
        if (separator <= 0 || separator >= reference.Length - 1)
        {
            provider = string.Empty;
            modelId = string.Empty;
            return false;
        }

        provider = reference[..separator];
        modelId = reference[(separator + 1)..];
        return true;
    }

    private ProcessStartInfo CreateStartInfo(
        PiRuntimeCommand runtime,
        AgentRunRequest request,
        string permissionToken,
        string permissionMode,
        string runIdentityPath,
        string runtimeContextPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.FileName,
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        foreach (var argument in runtime.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PI_COMPANION_WORKING_DIRECTORY"] = Path.GetFullPath(request.WorkingDirectory);
        startInfo.Environment["PI_COMPANION_BACKUP_DIRECTORY"] = _backupDirectory;
        startInfo.Environment["PI_COMPANION_GRANT_DIRECTORY"] = _grantDirectory;
        startInfo.Environment["PI_COMPANION_TASK_ID"] = request.TaskId.ToString("D");
        startInfo.Environment["PI_COMPANION_RUN_ID"] = request.RunId.ToString("D");
        startInfo.Environment["PI_COMPANION_RUN_ID_FILE"] = runIdentityPath;
        startInfo.Environment["PI_COMPANION_CONTEXT_FILE"] = runtimeContextPath;
        startInfo.Environment["PI_COMPANION_PERMISSION_TOKEN"] = permissionToken;
        startInfo.Environment["PI_COMPANION_PERMISSION_MODE"] = permissionMode;
        startInfo.Environment["PI_COMPANION_SCOPE_KIND"] = request.ScopeKind.ToString();
        var workspaceTrustStatus = ResolveWorkspaceTrustStatus(request);
        startInfo.Environment["PI_COMPANION_WORKSPACE_TRUST_STATUS"] = workspaceTrustStatus;
        startInfo.Environment["PI_WEB_SEARCH_CONFIG"] = $"{runtimeContextPath}.web-search-config";
        if (!string.IsNullOrWhiteSpace(request.ReadOnlyAttachmentRoot))
        {
            startInfo.Environment["PI_COMPANION_READ_ONLY_ATTACHMENT_ROOT"] =
                Path.GetFullPath(request.ReadOnlyAttachmentRoot);
        }
        if (!string.IsNullOrWhiteSpace(request.ArtifactDirectory))
        {
            startInfo.Environment["PI_COMPANION_ARTIFACT_DIRECTORY"] =
                Path.GetFullPath(request.ArtifactDirectory);
        }

        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        startInfo.ArgumentList.Add("--session-dir");
        startInfo.ArgumentList.Add(_sessionDirectory);
        var webSearchSupport = PiWebSearchCapabilities.ResolveModelReference(request.Model);
        var enableWebSearch = webSearchSupport != PiWebSearchSupport.None;
        if (enableWebSearch && (_webSearchExtensionPath is null || !File.Exists(_webSearchExtensionPath)))
        {
            throw new FileNotFoundException(
                "当前模型支持自带网络搜索，但应用随附的 Web Search Extension 缺失。",
                _webSearchExtensionPath);
        }
        startInfo.ArgumentList.Add("--tools");
        var tools = request.ScopeKind == TaskScopeKind.GeneralChat
            ? "read,grep,find,ls,edit,write,ask_user,list_available_skills,publish_artifact"
            : "read,grep,find,ls,edit,write,bash,ask_user,list_available_skills";
        startInfo.ArgumentList.Add(enableWebSearch ? $"{tools},web_search" : tools);
        startInfo.ArgumentList.Add("--no-extensions");
        startInfo.ArgumentList.Add("--extension");
        startInfo.ArgumentList.Add(_extensionPath!);
        if (enableWebSearch)
        {
            startInfo.ArgumentList.Add("--extension");
            startInfo.ArgumentList.Add(_webSearchExtensionPath!);
        }
        startInfo.ArgumentList.Add("--no-prompt-templates");
        startInfo.ArgumentList.Add("--append-system-prompt");
        var workspaceSystemPrompt =
            "Pi Companion 会在工具执行前实施工作目录和用户授权策略。需要用户作出选择或补充信息时，必须调用 ask_user；不要自行猜测用户答案。被拒绝或阻止的操作不得换用其他工具绕过。";
        if (request.ScopeKind == TaskScopeKind.Workspace &&
            !string.Equals(workspaceTrustStatus, "trusted", StringComparison.Ordinal))
        {
            workspaceSystemPrompt +=
                " 当前工作区未受 Pi 信任，因此项目级技能及其他需要项目信任的 Pi 资源不会加载；全局技能仍可用。若用户询问技能缺失、技能不可用或可用技能范围，必须说明工作区信任是可能原因，并建议用户在 Pi Companion 的工作区技能管理中信任该工作区。不得自行更改或声称已经更改工作区信任状态。";
        }
        startInfo.ArgumentList.Add(
            request.ScopeKind == TaskScopeKind.GeneralChat
                ? "这是 General Chat。当前目录是 Pi Companion 管理的隔离工作区，不是用户项目目录。你只能读取当前目录和提示中列出的只读附件，只能在当前目录创建或修改文件。用户要求生成文件时，先在当前目录完成文件，再调用 publish_artifact 返回最终文件；未成功调用 publish_artifact 时不得声称文件已经交付。不要向用户展示内部路径。Shell 在 General Chat 中不可用。需要用户作出选择或补充信息时，必须调用 ask_user；被拒绝或阻止的操作不得换用其他工具绕过。"
                : workspaceSystemPrompt);

        var thinking = NormalizeThinkingLevel(request.ThinkingLevel);
        startInfo.ArgumentList.Add("--thinking");
        startInfo.ArgumentList.Add(thinking);
        if (!IsDefaultModel(request.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(request.Model);
        }

        return startInfo;
    }

    private async Task SendMessageCommandAsync(
        Guid runId,
        string command,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var context = RequireCurrent(runId);
        await SendCommandAsync(
            context,
            new Dictionary<string, object?>
            {
                ["type"] = command,
                ["message"] = message,
            },
            cancellationToken).ConfigureAwait(false);
        var delivery = command == "steer" ? "steer" : "follow_up";
        Emit(
            context,
            CompanionRunEventKind.UserMessageAdded,
            context.CurrentStatus,
            message,
            delivery == "steer" ? "已调整 Agent 方向" : "已添加后续任务",
            new Dictionary<string, string>
            {
                ["message"] = message,
                ["delivery"] = delivery,
            });
    }

    private async Task<JsonElement> SendCommandAsync(
        RunContext context,
        Dictionary<string, object?> command,
        CancellationToken cancellationToken)
    {
        var id = $"pc-{context.Request.RunId:N}-{Interlocked.Increment(ref context.RequestId)}";
        command["id"] = id;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!context.PendingResponses.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Pi RPC request id collision.");
        }

        try
        {
            await WriteMessageAsync(context, command, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!response.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                var error = response.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : "未知 Pi RPC 错误";
                throw new InvalidOperationException(error);
            }

            return response;
        }
        finally
        {
            context.PendingResponses.TryRemove(id, out _);
        }
    }

    private static async Task SendCommandWithoutResponseAsync(
        RunContext context,
        Dictionary<string, object?> command,
        CancellationToken cancellationToken)
    {
        command["id"] = $"pc-{context.Request.RunId:N}-{Interlocked.Increment(ref context.RequestId)}";
        await WriteMessageAsync(context, command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteMessageAsync(
        RunContext context,
        IReadOnlyDictionary<string, object?> message,
        CancellationToken cancellationToken)
    {
        var process = context.Process ?? throw new InvalidOperationException("Pi Runtime 尚未启动。");
        if (process.HasExited)
        {
            throw new InvalidOperationException("Pi Runtime 已退出。");
        }

        var json = JsonSerializer.Serialize(message, JsonOptions);
        await context.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context.WriteLock.Release();
        }
    }

    private async Task ReadStdoutAsync(RunContext context)
    {
        var process = context.Process ?? throw new InvalidOperationException("Pi Runtime 尚未启动。");
        var parser = new JsonlFrameParser();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(
                    buffer.AsMemory(),
                    context.Lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                foreach (var frame in parser.Append(buffer.AsSpan(0, count)))
                {
                    ProcessFrame(context, frame);
                }
            }

            var finalFrame = parser.Complete();
            if (!string.IsNullOrWhiteSpace(finalFrame))
            {
                ProcessFrame(context, finalFrame);
            }
        }
        catch (OperationCanceledException) when (context.ExpectedStop)
        {
        }
        catch (Exception exception)
        {
            if (!context.ExpectedStop && !context.IsTerminal)
            {
                FailRun(context, $"Pi RPC stdout 解析失败：{exception.Message}", "rpc-parse-error");
            }
        }
    }

    private async Task ReadStderrAsync(RunContext context)
    {
        var process = context.Process ?? throw new InvalidOperationException("Pi Runtime 尚未启动。");
        var logPath = Path.Combine(_logDirectory, $"pi-{context.Request.RunId:N}.log");
        try
        {
            await using var stream = new FileStream(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            while (await process.StandardError.ReadLineAsync(context.Lifetime.Token).ConfigureAwait(false) is { } line)
            {
                await writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O} {line}").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.ExpectedStop)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task ObserveExitAsync(RunContext context)
    {
        var process = context.Process ?? throw new InvalidOperationException("Pi Runtime 尚未启动。");
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (var pending in context.PendingResponses.Values)
        {
            pending.TrySetException(new InvalidOperationException($"Pi Runtime 已退出，代码 {process.ExitCode}。"));
        }

        if (context.ExpectedStop || context.IsTerminal)
        {
            return;
        }

        if (!context.TrySetTerminal())
        {
            return;
        }

        var status = context.AgentStarted ? RunStatus.Interrupted : RunStatus.Failed;
        var kind = context.AgentStarted ? CompanionRunEventKind.RunInterrupted : CompanionRunEventKind.RunFailed;
        Emit(
            context,
            kind,
            status,
            $"运行意外结束（错误代码 {process.ExitCode}）",
            context.AgentStarted ? "运行意外结束，你可以重试或继续提问" : "任务启动失败",
            new Dictionary<string, string> { ["exitReason"] = $"process-exit-{process.ExitCode}" });
        context.Completion.TrySetResult();
        ClearCurrent(context);
    }

    private void ProcessFrame(RunContext context, string frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;
            var type = GetString(root, "type");
            if (type.Equals("response", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("id", out var idElement) &&
                    idElement.GetString() is { } id &&
                    context.PendingResponses.TryGetValue(id, out var pending))
                {
                    pending.TrySetResult(root.Clone());
                }

                return;
            }

            HandleEvent(context, type, root);
        }
        catch (JsonException exception)
        {
            Emit(
                context,
                CompanionRunEventKind.WarningRaised,
                context.CurrentStatus,
                $"忽略无效 Pi RPC JSON：{exception.Message}",
                "收到无效 Pi RPC 数据");
        }
    }

    private void HandleEvent(RunContext context, string type, JsonElement root)
    {
        switch (type)
        {
            case "agent_start":
                context.AgentStarted = true;
                Emit(context, CompanionRunEventKind.RunStarted, RunStatus.Running, "Pi Agent 开始处理任务", "正在分析任务");
                break;
            case "agent_end":
                context.HasAgentError |= HasAgentError(root);
                context.AgentErrorMessage = GetAgentErrorMessage(root) ?? context.AgentErrorMessage;
                _ = FinalizeAfterAgentEndAsync(context, context.Generation);
                break;
            case "agent_settled":
                context.MarkSettledEventReceived();
                _ = FinalizeAfterAgentSettledAsync(context, context.Generation);
                break;
            case "message_start":
                if (GetNestedString(root, "message", "role") == "assistant")
                {
                    Emit(
                        context,
                        CompanionRunEventKind.AssistantMessageStarted,
                        RunStatus.Running,
                        "Agent 开始生成回答",
                        "正在生成回答");
                }

                break;
            case "message_update":
                HandleMessageUpdate(context, root);
                break;
            case "message_end":
                HandleMessageEnd(context, root);
                break;
            case "tool_execution_start":
                RememberToolStart(context, root);
                Emit(
                    context,
                    CompanionRunEventKind.ToolStarted,
                    RunStatus.Running,
                    DescribeTool(root, "开始"),
                    $"正在使用 {GetOptionalString(root, "toolName") ?? "工具"}",
                    CreateToolPayload(root, string.Empty));
                break;
            case "tool_execution_update":
                Emit(
                    context,
                    CompanionRunEventKind.ToolProgressed,
                    RunStatus.Running,
                    DescribeToolProgress(root),
                    $"{GetOptionalString(root, "toolName") ?? "工具"} 仍在运行",
                    CreateToolPayload(root, ExtractToolOutput(root, "partialResult")));
                break;
            case "tool_execution_end":
                var isError = root.TryGetProperty("isError", out var isErrorElement) && isErrorElement.GetBoolean();
                PublishToolExecution(context, root, isError);
                Emit(
                    context,
                    isError ? CompanionRunEventKind.ToolFailed : CompanionRunEventKind.ToolCompleted,
                    RunStatus.Running,
                    DescribeTool(root, isError ? "失败" : "完成"),
                    isError ? "工具执行失败" : "工具执行完成",
                    CreateToolPayload(root, ExtractToolOutput(root, "result")));
                break;
            case "queue_update":
                var steeringMessages = ReadStringArray(root, "steering");
                var followUpMessages = ReadStringArray(root, "followUp");
                var steering = steeringMessages.Count;
                var followUp = followUpMessages.Count;
                context.PendingMessages = steering + followUp;
                Emit(
                    context,
                    CompanionRunEventKind.QueueChanged,
                    context.CurrentStatus,
                    $"方向调整 {steering} 条，后续任务 {followUp} 条",
                    context.PendingMessages == 0 ? "消息队列为空" : "消息已排队",
                    new Dictionary<string, string>
                    {
                        ["steeringCount"] = steering.ToString(),
                        ["followUpCount"] = followUp.ToString(),
                        ["steeringQueue"] = JsonSerializer.Serialize(steeringMessages, JsonOptions),
                        ["followUpQueue"] = JsonSerializer.Serialize(followUpMessages, JsonOptions),
                    });
                break;
            case "extension_ui_request":
                HandleExtensionUiRequest(context, root);
                break;
            case "extension_error":
                Emit(
                    context,
                    CompanionRunEventKind.WarningRaised,
                    context.CurrentStatus,
                    GetOptionalString(root, "error") ?? "Pi Extension 报错",
                    "Pi Extension 报错");
                break;
            case "turn_start":
            case "turn_end":
            case "entry_appended":
            case "session_info_changed":
            case "thinking_level_changed":
                break;
            case "compaction_start":
                HandleCompactionStart(context, root);
                break;
            case "compaction_end":
                HandleCompactionEnd(context, root);
                break;
            case "auto_retry_start":
                HandleAutoRetryStart(context, root);
                break;
            case "auto_retry_end":
                HandleAutoRetryEnd(context, root);
                break;
            case "summarization_retry_scheduled":
                HandleSummarizationRetryScheduled(context, root);
                break;
            case "summarization_retry_attempt_start":
                HandleSummarizationRetryAttemptStart(context, root);
                break;
            case "summarization_retry_finished":
                HandleSummarizationRetryFinished(context);
                break;
            default:
                Emit(
                    context,
                    CompanionRunEventKind.WarningRaised,
                    context.CurrentStatus,
                    $"已忽略未知 Pi RPC 事件：{type}",
                    "Pi RPC 协议包含未知事件",
                    new Dictionary<string, string> { ["rawType"] = type });
                break;
        }
    }

    private void HandleMessageUpdate(RunContext context, JsonElement root)
    {
        if (!root.TryGetProperty("assistantMessageEvent", out var update))
        {
            return;
        }

        var updateType = GetString(update, "type");
        switch (updateType)
        {
            case "text_delta":
                var text = GetOptionalString(update, "delta");
                if (!string.IsNullOrEmpty(text))
                {
                    Emit(
                        context,
                        CompanionRunEventKind.AssistantTextDelta,
                        RunStatus.Running,
                        Truncate(text, 1200),
                        "正在生成回答",
                        new Dictionary<string, string> { ["delta"] = text });
                }

                break;
            case "thinking_delta":
                var thinking = GetOptionalString(update, "delta");
                if (!string.IsNullOrEmpty(thinking))
                {
                    Emit(
                        context,
                        CompanionRunEventKind.AssistantThinkingDelta,
                        RunStatus.Running,
                        Truncate(thinking, 800),
                        "正在思考",
                        new Dictionary<string, string> { ["delta"] = thinking });
                }

                break;
            case "error":
                context.HasAgentError = true;
                context.AgentErrorMessage = NormalizeAgentErrorMessage(
                    GetOptionalString(update, "error") ??
                    GetOptionalString(update, "errorMessage")) ?? context.AgentErrorMessage;
                Emit(
                    context,
                    CompanionRunEventKind.WarningRaised,
                    RunStatus.Running,
                    GetOptionalString(update, "error") ?? "模型流返回错误",
                    "模型流返回错误");
                break;
        }
    }

    private void HandleMessageEnd(RunContext context, JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            GetOptionalString(message, "role") != "assistant")
        {
            return;
        }

        var stopReason = GetOptionalString(message, "stopReason");
        context.HasAgentError |= stopReason is "error";
        if (stopReason is "error")
        {
            context.AgentErrorMessage = NormalizeAgentErrorMessage(
                GetOptionalString(message, "errorMessage")) ?? context.AgentErrorMessage;
        }
        var finalText = ExtractAssistantText(message);
        const string activity = "Agent 完成一条回答";
        var payload = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            payload["finalText"] = finalText;
        }

        if (!string.IsNullOrWhiteSpace(stopReason))
        {
            payload["stopReason"] = stopReason;
        }

        Emit(
            context,
            CompanionRunEventKind.AssistantMessageCompleted,
            RunStatus.Running,
            activity,
            stopReason is "error" ? "回答生成失败" : "回答已生成",
            payload);
    }

    private void HandleCompactionStart(RunContext context, JsonElement root)
    {
        var reason = GetOptionalString(root, "reason") ?? "unknown";
        var reasonText = reason switch
        {
            "manual" => "手动请求",
            "threshold" => "上下文接近容量上限",
            "overflow" => "上下文已超出模型限制",
            _ => "Pi 请求",
        };
        Emit(
            context,
            CompanionRunEventKind.CompactionStarted,
            RunStatus.Running,
            $"正在压缩上下文（{reasonText}）",
            "正在压缩上下文",
            new Dictionary<string, string> { ["reason"] = reason });
    }

    private void HandleCompactionEnd(RunContext context, JsonElement root)
    {
        var aborted = GetOptionalBoolean(root, "aborted");
        var willRetry = GetOptionalBoolean(root, "willRetry");
        var error = GetOptionalString(root, "errorMessage");
        var activity = aborted
            ? "上下文压缩已取消"
            : !string.IsNullOrWhiteSpace(error)
                ? willRetry ? "上下文压缩失败，Pi 将自动重试" : $"上下文压缩失败：{error}"
                : "上下文压缩完成，Agent 将继续运行";
        Emit(
            context,
            CompanionRunEventKind.CompactionCompleted,
            RunStatus.Running,
            activity,
            aborted ? "上下文压缩已取消" : willRetry ? "上下文压缩失败，等待重试" : "上下文压缩完成",
            new Dictionary<string, string>
            {
                ["reason"] = GetOptionalString(root, "reason") ?? "unknown",
                ["aborted"] = aborted ? "true" : "false",
                ["willRetry"] = willRetry ? "true" : "false",
                ["success"] = !aborted && string.IsNullOrWhiteSpace(error) ? "true" : "false",
                ["error"] = error ?? string.Empty,
            });
    }

    private void HandleAutoRetryStart(RunContext context, JsonElement root)
    {
        context.MarkAutoRetryStarted();
        var attempt = GetOptionalInt32(root, "attempt");
        var maxAttempts = GetOptionalInt32(root, "maxAttempts");
        var delayMs = GetOptionalInt32(root, "delayMs");
        var delayText = delayMs is > 0 ? $"，{Math.Ceiling(delayMs.Value / 1000d):0} 秒后重试" : string.Empty;
        Emit(
            context,
            CompanionRunEventKind.AutoRetryStarted,
            RunStatus.Running,
            $"模型请求暂时失败，准备第 {attempt ?? 1}/{maxAttempts ?? 1} 次自动重试{delayText}",
            "Pi 正在等待自动重试",
            new Dictionary<string, string>
            {
                ["attempt"] = (attempt ?? 1).ToString(),
                ["maxAttempts"] = (maxAttempts ?? 1).ToString(),
                ["delayMs"] = (delayMs ?? 0).ToString(),
                ["error"] = GetOptionalString(root, "errorMessage") ?? string.Empty,
            });
    }

    private void HandleAutoRetryEnd(RunContext context, JsonElement root)
    {
        var success = GetOptionalBoolean(root, "success");
        var abortRequested = context.CompleteAutoRetry();
        var cancelled = !success && abortRequested;
        var attempt = GetOptionalInt32(root, "attempt") ?? 1;
        var finalError = GetOptionalString(root, "finalError");
        Emit(
            context,
            CompanionRunEventKind.AutoRetryCompleted,
            RunStatus.Running,
            success
                ? $"第 {attempt} 次自动重试已恢复"
                : cancelled
                    ? "自动重试已由用户取消"
                    : $"自动重试已结束：{finalError ?? "仍未恢复"}",
            success ? "自动重试成功，Agent 继续运行" : cancelled ? "自动重试已取消" : "自动重试未能恢复",
            new Dictionary<string, string>
            {
                ["attempt"] = attempt.ToString(),
                ["success"] = success ? "true" : "false",
                ["cancelled"] = cancelled ? "true" : "false",
                ["error"] = finalError ?? string.Empty,
            });
    }

    private void HandleSummarizationRetryScheduled(RunContext context, JsonElement root)
    {
        var attempt = GetOptionalInt32(root, "attempt") ?? 1;
        var maxAttempts = GetOptionalInt32(root, "maxAttempts") ?? 1;
        var delayMs = GetOptionalInt32(root, "delayMs") ?? 0;
        var delayText = delayMs > 0 ? $"，{Math.Ceiling(delayMs / 1000d):0} 秒后重试" : string.Empty;
        Emit(
            context,
            CompanionRunEventKind.SummarizationRetryStarted,
            RunStatus.Running,
            $"摘要生成暂时失败，准备第 {attempt}/{maxAttempts} 次重试{delayText}",
            "Pi 正在等待摘要重试",
            new Dictionary<string, string>
            {
                ["attempt"] = attempt.ToString(),
                ["maxAttempts"] = maxAttempts.ToString(),
                ["delayMs"] = delayMs.ToString(),
                ["error"] = GetOptionalString(root, "errorMessage") ?? string.Empty,
            });
    }

    private void HandleSummarizationRetryAttemptStart(RunContext context, JsonElement root)
    {
        var source = GetOptionalString(root, "source") ?? "unknown";
        var reason = GetOptionalString(root, "reason") ?? "unknown";
        var target = source == "branchSummary" ? "分支摘要" : "上下文摘要";
        Emit(
            context,
            CompanionRunEventKind.SummarizationRetryProgressed,
            RunStatus.Running,
            $"正在重试生成{target}",
            $"Pi 正在重试{target}",
            new Dictionary<string, string>
            {
                ["source"] = source,
                ["reason"] = reason,
            });
    }

    private void HandleSummarizationRetryFinished(RunContext context)
    {
        Emit(
            context,
            CompanionRunEventKind.SummarizationRetryCompleted,
            RunStatus.Running,
            "摘要重试流程已结束",
            "Pi 已结束摘要重试",
            new Dictionary<string, string> { ["success"] = "true" });
    }

    private void HandleExtensionUiRequest(RunContext context, JsonElement root)
    {
        var method = GetOptionalString(root, "method") ?? string.Empty;
        if (method == "notify")
        {
            var notificationMessage = GetOptionalString(root, "message") ?? "Pi Companion Extension 通知";
            var notifyType = GetOptionalString(root, "notifyType");
            if (notifyType is "warning" or "error")
            {
                Emit(
                    context,
                    CompanionRunEventKind.WarningRaised,
                    context.CurrentStatus,
                    notificationMessage,
                    notifyType == "error" ? "Extension 错误" : "Extension 警告");
            }

            return;
        }

        if (method is not ("select" or "confirm" or "input" or "editor"))
        {
            return;
        }

        var id = GetString(root, "id");
        var title = GetOptionalString(root, "title") ?? "Pi Agent 需要输入";
        var permissionMarker = $"[PI_COMPANION_PERMISSION:{context.PermissionToken}]";
        var isPermissionRequest = title.StartsWith(permissionMarker, StringComparison.Ordinal);
        if (isPermissionRequest)
        {
            title = title[permissionMarker.Length..].TrimStart();
        }

        var options = method == "select" ? ReadStringArray(root, "options") : [];
        var defaultValue = options.FirstOrDefault() ??
            (method == "editor" ? GetOptionalString(root, "prefill") : null);
        var isApproval = method == "confirm" || isPermissionRequest;
        var prompt = method == "confirm" && GetOptionalString(root, "message") is { Length: > 0 } confirmMessage
            ? $"{title}\n\n{confirmMessage}"
            : title;
        var interaction = new PendingInteraction(id, method, defaultValue, options, isApproval);
        lock (context.InteractionGate)
        {
            if (context.PendingInteractions.Any(item => item.Id == id))
            {
                return;
            }

            context.PendingInteractions.Add(interaction);
        }

        var payload = new Dictionary<string, string>
        {
            ["interactionId"] = id,
            ["interactionMethod"] = method,
            ["interactionKind"] = isApproval ? "Approval" : "Question",
            ["interactionOptions"] = JsonSerializer.Serialize(options, JsonOptions),
        };
        if (GetOptionalString(root, "placeholder") is { Length: > 0 } placeholder)
        {
            payload["interactionPlaceholder"] = placeholder;
        }

        Emit(
            context,
            isApproval ? CompanionRunEventKind.ApprovalRequested : CompanionRunEventKind.QuestionRequested,
            isApproval ? RunStatus.WaitingForApproval : RunStatus.WaitingForAnswer,
            prompt,
            isApproval ? "等待授权" : "等待回答",
            payload);
    }

    private async Task FinalizeAfterAgentEndAsync(RunContext context, long generation)
    {
        try
        {
            await Task.Delay(500, context.Lifetime.Token).ConfigureAwait(false);
            if (!context.IsGeneration(generation) || context.IsTerminal || context.SettledEventReceived)
            {
                return;
            }

            var state = await SendCommandAsync(
                context,
                new Dictionary<string, object?> { ["type"] = "get_state" },
                context.Lifetime.Token).ConfigureAwait(false);
            if (!state.TryGetProperty("data", out var data))
            {
                return;
            }

            var isStreaming = data.TryGetProperty("isStreaming", out var streaming) && streaming.GetBoolean();
            var pending = data.TryGetProperty("pendingMessageCount", out var pendingElement)
                ? pendingElement.GetInt32()
                : context.PendingMessages;
            if (!context.IsGeneration(generation) || isStreaming || pending > 0 || context.SettledEventReceived || !context.TrySetTerminal())
            {
                return;
            }

            var terminalPayload = ReadSessionPayload(data);
            await CompleteTerminalRunAsync(context, terminalPayload, "agent-end-state-fallback").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.ExpectedStop)
        {
        }
        catch (Exception exception)
        {
            if (!context.IsTerminal)
            {
                FailRun(context, $"确认 Pi Run 完成状态失败：{exception.Message}", "settle-check-error");
            }
        }
    }

    private async Task FinalizeAfterAgentSettledAsync(RunContext context, long generation)
    {
        try
        {
            if (!context.IsGeneration(generation) || !context.TrySetTerminal())
            {
                return;
            }

            await CompleteTerminalRunAsync(context, [], "agent-settled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.ExpectedStop)
        {
        }
        catch (Exception exception)
        {
            if (!context.IsTerminal)
            {
                FailRun(context, $"处理 Pi agent_settled 失败：{exception.Message}", "agent-settled-error");
            }
        }
    }

    private async Task CompleteTerminalRunAsync(
        RunContext context,
        Dictionary<string, string> terminalPayload,
        string settlementSource)
    {
        try
        {
            var entriesResult = await ReadSessionEntriesAsync(
                context,
                context.Request.PiEntryCursor,
                context.Lifetime.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(entriesResult.LeafId))
            {
                terminalPayload["piEntryCursor"] = entriesResult.LeafId;
            }
        }
        catch (Exception exception)
        {
            terminalPayload["sessionSyncWarning"] = exception.Message;
        }

        var terminal = GetTerminalOutcome(context);
        terminalPayload["exitReason"] = terminal.ExitReason;
        terminalPayload["settlementSource"] = settlementSource;
        if (!string.IsNullOrWhiteSpace(context.AgentErrorMessage))
        {
            terminalPayload["errorMessage"] = context.AgentErrorMessage;
        }
        if (terminal.Status == RunStatus.Completed && TryRetainWarmContext(context))
        {
            context.ExpectedStop = false;
        }
        else
        {
            context.ExpectedStop = true;
            ClearCurrent(context);
            StopProcess(context);
            await WaitForShutdownAsync(context).ConfigureAwait(false);
        }
        Emit(context, terminal.Kind, terminal.Status, terminal.Activity, terminal.Summary, terminalPayload);
        context.Completion.TrySetResult();
    }

    private bool TryRetainWarmContext(RunContext context)
    {
        RunContext? evicted = null;
        lock (_gate)
        {
            if (_disposed ||
                !_active.TryGetValue(context.Request.RunId, out var active) ||
                !ReferenceEquals(active, context) ||
                !CanReuse(context, context.ReuseKey))
            {
                return false;
            }

            _active.Remove(context.Request.RunId);
            context.LastUsedAt = DateTimeOffset.UtcNow;
            _warm.Add(context);
            var idleCapacity = MaximumWorkerCount - _active.Count;
            if (_warm.Count > idleCapacity)
            {
                evicted = _warm.OrderBy(candidate => candidate.LastUsedAt).First();
                _warm.Remove(evicted);
                evicted.ExpectedStop = true;
            }
        }

        if (evicted is not null)
        {
            StopProcess(evicted);
            _ = WaitForShutdownAsync(evicted);
        }

        return true;
    }

    private static (CompanionRunEventKind Kind, RunStatus Status, string Activity, string Summary, string ExitReason)
        GetTerminalOutcome(RunContext context)
    {
        if (context.AbortRequested)
        {
            return (
                CompanionRunEventKind.RunInterrupted,
                RunStatus.Interrupted,
                "任务已停止",
                "已按你的要求停止",
                "user-abort");
        }

        if (context.HasAgentError)
        {
            var errorMessage = context.AgentErrorMessage;
            return (
                CompanionRunEventKind.RunFailed,
                RunStatus.Failed,
                string.IsNullOrWhiteSpace(errorMessage)
                    ? "Pi Agent 返回错误，但未提供具体原因"
                    : $"Pi Agent 返回错误：{errorMessage}",
                string.IsNullOrWhiteSpace(errorMessage)
                    ? "任务失败"
                    : $"任务失败：{Truncate(errorMessage, 240)}",
                "agent-error");
        }

        return (
            CompanionRunEventKind.RunSettled,
            RunStatus.Completed,
            "Pi Agent 已完成且消息队列为空",
            "任务已完成",
            "agent-settled");
    }

    private async Task ForceAbortAfterTimeoutAsync(RunContext context)
    {
        try
        {
            await context.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
        }

        if (!context.TrySetTerminal())
        {
            return;
        }

        context.ExpectedStop = true;
        StopProcess(context);
        await WaitForShutdownAsync(context).ConfigureAwait(false);
        Emit(
            context,
            CompanionRunEventKind.RunInterrupted,
            RunStatus.Interrupted,
            "停止请求未及时响应，已强制结束",
            "停止响应较慢，已为你强制结束",
            new Dictionary<string, string> { ["exitReason"] = "abort-timeout" });
        context.Completion.TrySetResult();
        ClearCurrent(context);
    }

    private void FailRun(RunContext context, string activity, string exitReason)
    {
        if (!context.TrySetTerminal())
        {
            return;
        }

        Emit(
            context,
            CompanionRunEventKind.RunFailed,
            RunStatus.Failed,
            activity,
            "Pi RPC 运行失败",
            new Dictionary<string, string> { ["exitReason"] = exitReason });
        context.Completion.TrySetResult();
        context.ExpectedStop = true;
        StopProcess(context);
        ClearCurrent(context);
    }

    private void PublishSessionState(RunContext context, JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data))
        {
            return;
        }

        context.SupportsNativeImages = ModelSupportsNativeImages(data);
        var payload = ReadSessionPayload(data);
        if (payload.Count == 0)
        {
            return;
        }

        context.SessionPayload = payload;

        PublishSessionPayload(context, payload);
    }

    private void PublishCachedSessionState(RunContext context)
    {
        if (context.SessionPayload.Count > 0)
        {
            PublishSessionPayload(context, new Dictionary<string, string>(context.SessionPayload));
        }
    }

    private void PublishSessionPayload(RunContext context, Dictionary<string, string> payload)
    {
        EmitStartupPhase(
            context,
            CompanionRunEventKind.QueueChanged,
            "session-ready",
            "Pi Session 已就绪",
            "Pi Session 已就绪",
            payload);
    }

    private static Dictionary<string, string> ReadSessionPayload(JsonElement data)
    {
        var payload = new Dictionary<string, string>();
        var sessionId = GetOptionalString(data, "sessionId");
        var sessionPath = GetOptionalString(data, "sessionFile");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            payload["piSessionId"] = sessionId;
        }

        if (!string.IsNullOrWhiteSpace(sessionPath))
        {
            payload["piSessionPath"] = sessionPath;
        }

        return payload;
    }

    private async Task ReconcileSessionEntriesAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Request.PiEntryCursor))
        {
            return;
        }

        SessionEntriesResult result;
        try
        {
            result = await ReadSessionEntriesAsync(context, context.Request.PiEntryCursor, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            Emit(
                context,
                CompanionRunEventKind.WarningRaised,
                context.CurrentStatus,
                $"Pi Session 增量对账失败：{exception.Message}",
                "Session 历史暂时无法对账",
                new Dictionary<string, string> { ["warningCode"] = "session-reconcile-failed" });
            return;
        }

        var knownMessages = (context.Request.KnownAssistantMessages ?? [])
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToHashSet(StringComparer.Ordinal);
        var recovered = 0;
        foreach (var entry in result.Entries)
        {
            if (GetOptionalString(entry, "type") != "message" ||
                !entry.TryGetProperty("message", out var message) ||
                GetOptionalString(message, "role") != "assistant")
            {
                continue;
            }

            var text = ExtractAssistantText(message);
            if (string.IsNullOrWhiteSpace(text) || knownMessages.Contains(text))
            {
                continue;
            }

            knownMessages.Add(text);
            recovered++;
            Emit(
                context,
                CompanionRunEventKind.AssistantMessageCompleted,
                context.CurrentStatus,
                "已从 Pi Session 恢复一条未同步的回答",
                "已恢复上次运行的回答",
                new Dictionary<string, string>
                {
                    ["finalText"] = text,
                    ["reconciled"] = "true",
                    ["piEntryId"] = GetOptionalString(entry, "id") ?? string.Empty,
                });
        }

        if (!string.IsNullOrWhiteSpace(result.LeafId) &&
            !string.Equals(result.LeafId, context.Request.PiEntryCursor, StringComparison.Ordinal))
        {
            Emit(
                context,
                CompanionRunEventKind.SessionSynchronized,
                context.CurrentStatus,
                recovered > 0 ? $"已从 Pi Session 恢复 {recovered} 条回答" : "Pi Session 增量对账完成",
                recovered > 0 ? "历史回答已恢复" : "Session 已同步",
                new Dictionary<string, string>
                {
                    ["piEntryCursor"] = result.LeafId,
                    ["recoveredMessageCount"] = recovered.ToString(),
                });
        }
    }

    private async Task<SessionEntriesResult> ReadSessionEntriesAsync(
        RunContext context,
        string? since,
        CancellationToken cancellationToken)
    {
        var command = new Dictionary<string, object?> { ["type"] = "get_entries" };
        if (!string.IsNullOrWhiteSpace(since))
        {
            command["since"] = since;
        }

        var response = await SendCommandAsync(context, command, cancellationToken).ConfigureAwait(false);
        if (!response.TryGetProperty("data", out var data))
        {
            return new SessionEntriesResult([], null);
        }

        var entries = data.TryGetProperty("entries", out var entriesElement) && entriesElement.ValueKind == JsonValueKind.Array
            ? entriesElement.EnumerateArray().Select(entry => entry.Clone()).ToArray()
            : [];
        return new SessionEntriesResult(entries, GetOptionalString(data, "leafId"));
    }

    private async Task<IReadOnlyList<NativeImage>> PrepareNativeImagesAsync(
        RunContext context,
        CancellationToken cancellationToken)
    {
        if (!context.SupportsNativeImages || context.Request.Attachments is not { Count: > 0 })
        {
            return [];
        }

        var images = new List<NativeImage>();
        var skipped = 0;
        long totalBytes = 0;
        foreach (var attachment in context.Request.Attachments)
        {
            if (images.Count >= MaximumNativeImageCount)
            {
                skipped++;
                continue;
            }

            if (!NativeImageMimeTypes.TryGetValue(Path.GetExtension(attachment), out var mimeType) ||
                !File.Exists(attachment) ||
                !IsTrustedAttachmentPath(context.Request, attachment))
            {
                continue;
            }

            var fileInfo = new FileInfo(attachment);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaximumNativeImageBytes ||
                totalBytes + fileInfo.Length > MaximumNativeImageTotalBytes)
            {
                skipped++;
                continue;
            }

            await using var stream = new FileStream(
                attachment,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != fileInfo.Length || stream.Length > int.MaxValue)
            {
                skipped++;
                continue;
            }

            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            images.Add(new NativeImage(attachment, mimeType, Convert.ToBase64String(bytes)));
            totalBytes += bytes.Length;
        }

        if (skipped > 0)
        {
            Emit(
                context,
                CompanionRunEventKind.WarningRaised,
                context.CurrentStatus,
                $"有 {skipped} 张图片超过原生图像输入大小限制，将保留为路径附件",
                "部分图片将由 Agent 按路径读取",
                new Dictionary<string, string> { ["warningCode"] = "native-image-limit" });
        }

        return images;
    }

    private static bool ModelSupportsNativeImages(JsonElement stateData)
    {
        if (!stateData.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object ||
            !model.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return input.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.String && item.GetString() == "image");
    }

    private static bool IsTrustedAttachmentPath(AgentRunRequest request, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var roots = new[] { request.WorkingDirectory, request.ReadOnlyAttachmentRoot }
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Cast<string>()
                .Select(Path.GetFullPath);
            foreach (var root in roots)
            {
                if (IsWithinRoot(fullPath, root) && !HasReparsePointBelowRoot(fullPath, root))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        return false;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointBelowRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var current = path;
        while (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || !IsWithinRoot(parent, normalizedRoot))
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private void Emit(
        RunContext context,
        CompanionRunEventKind kind,
        RunStatus status,
        string activity,
        string summary,
        IReadOnlyDictionary<string, string>? extraPayload = null)
    {
        if (context.SuppressEvents ||
            (kind == CompanionRunEventKind.RunQueued && context.Request.InitialSequence > 0))
        {
            return;
        }

        if (status == RunStatus.Running && kind != CompanionRunEventKind.InteractionResolved)
        {
            lock (context.InteractionGate)
            {
                if (context.PendingInteractions.FirstOrDefault() is { } pending)
                {
                    status = pending.IsApproval ? RunStatus.WaitingForApproval : RunStatus.WaitingForAnswer;
                }
            }
        }

        context.CurrentStatus = status;
        var payload = new Dictionary<string, string>
        {
            ["activity"] = activity,
            [status.IsActive() ? "activityStatus" : "summary"] = summary,
        };
        if (extraPayload is not null)
        {
            foreach (var (key, value) in extraPayload)
            {
                payload[key] = value;
            }
        }

        EventReceived?.Invoke(new CompanionRunEvent(
            Guid.NewGuid(),
            context.Request.TaskId,
            context.Request.RunId,
            Interlocked.Increment(ref context.Sequence),
            kind,
            DateTimeOffset.UtcNow,
            status,
            payload,
            "pi-rpc-v1"));
    }

    private void EmitStartupPhase(
        RunContext context,
        CompanionRunEventKind kind,
        string phase,
        string activity,
        string summary,
        IReadOnlyDictionary<string, string>? extraPayload = null)
    {
        var payload = new Dictionary<string, string>
        {
            ["startupPhase"] = phase,
        };
        if (kind != CompanionRunEventKind.RunStarted && !string.IsNullOrWhiteSpace(activity))
        {
            payload["startupDetail"] = activity;
        }
        if (extraPayload is not null)
        {
            foreach (var (key, value) in extraPayload)
            {
                payload[key] = value;
            }
        }

        var projectedActivity = kind == CompanionRunEventKind.RunStarted ? activity : string.Empty;
        Emit(context, kind, RunStatus.Starting, projectedActivity, summary, payload);
    }

    private RunContext RequireCurrent(Guid runId)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(runId, out var context) || context.IsTerminal)
            {
                throw new InvalidOperationException("指定的 Pi Run 当前未处于活动状态。");
            }

            return context;
        }
    }

    private void ClearCurrent(RunContext context)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(context.Request.RunId, out var active) &&
                ReferenceEquals(active, context))
            {
                _active.Remove(context.Request.RunId);
            }
        }
    }

    private static void StopProcess(RunContext context)
    {
        context.Lifetime.Cancel();
        try
        {
            context.Process?.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }

        context.Job?.Dispose();
        context.Job = null;
        try
        {
            if (context.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task WaitForShutdownAsync(RunContext context)
    {
        var tasks = new[] { context.StdoutTask, context.StderrTask, context.ExitTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            context.Process?.Dispose();
            context.Process = null;
            DeleteRuntimeFiles(context);
        }
    }

    private static string BuildPrompt(AgentRunRequest request, IReadOnlySet<string> nativeImagePaths)
    {
        if (request.Attachments is not { Count: > 0 })
        {
            return request.Prompt;
        }

        var builder = new StringBuilder(request.Prompt);
        var pathAttachments = request.Attachments.Where(path => !nativeImagePaths.Contains(path)).ToArray();
        if (nativeImagePaths.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("用户附加了以下图片；图片内容已通过原生图像输入一并提供：");
            foreach (var attachment in request.Attachments.Where(nativeImagePaths.Contains))
            {
                builder.Append("- ").AppendLine(attachment);
            }
        }

        if (pathAttachments.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("用户附加了以下只读附件。请使用读取工具查看附件内容后再回答，不要仅根据文件名或路径推测：");
            foreach (var attachment in pathAttachments)
            {
                builder.Append("- ").AppendLine(attachment);
            }
        }

        return builder.ToString();
    }

    private static bool IsDefaultModel(string model) =>
        string.IsNullOrWhiteSpace(model) ||
        model.Equals("Pi 默认模型", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("Pi RPC", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("Demo Agent", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePermissionMode(string? permissionMode) =>
        permissionMode?.Trim().ToLowerInvariant() switch
        {
            "read-only" => "read-only",
            "standard" => "standard",
            "full-access" => "full-access",
            _ => "standard",
        };

    private static string NormalizeThinkingLevel(string thinkingLevel) => thinkingLevel.Trim().ToLowerInvariant() switch
    {
        "低" or "low" => "low",
        "中" or "medium" => "medium",
        "高" or "high" => "high",
        "关闭" or "off" => "off",
        "最小" or "minimal" => "minimal",
        "超高" or "xhigh" => "xhigh",
        "最大" or "max" => "max",
        _ => "medium",
    };

    private static bool HasAgentError(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (GetOptionalString(message, "role") == "assistant" &&
                GetOptionalString(message, "stopReason") == "error")
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetAgentErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? errorMessage = null;
        foreach (var message in messages.EnumerateArray())
        {
            if (GetOptionalString(message, "role") == "assistant" &&
                GetOptionalString(message, "stopReason") == "error")
            {
                errorMessage = NormalizeAgentErrorMessage(
                    GetOptionalString(message, "errorMessage")) ?? errorMessage;
            }
        }

        return errorMessage;
    }

    private static string? NormalizeAgentErrorMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value.Trim(), 1200);
    }

    private static Dictionary<string, string> CreateToolPayload(JsonElement root, string output)
    {
        var payload = new Dictionary<string, string>
        {
            ["toolName"] = GetOptionalString(root, "toolName") ?? "tool",
            ["toolOutput"] = Truncate(output, ToolOutputMaximumLength),
        };
        if (GetOptionalString(root, "toolCallId") is { Length: > 0 } toolCallId)
        {
            payload["toolCallId"] = toolCallId;
        }

        if (root.TryGetProperty("args", out var args))
        {
            var target = GetToolTarget(args);
            if (!string.IsNullOrWhiteSpace(target))
            {
                payload["toolInput"] = Truncate(target, 800);
            }
        }

        return payload;
    }

    private static string ExtractToolOutput(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var result))
        {
            return string.Empty;
        }

        if (result.ValueKind == JsonValueKind.String)
        {
            return result.GetString() ?? string.Empty;
        }

        return ExtractContentText(result);
    }

    private static void RememberToolStart(RunContext context, JsonElement root)
    {
        var toolCallId = GetOptionalString(root, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var toolName = GetOptionalString(root, "toolName") ?? "tool";
        var argumentsJson = root.TryGetProperty("args", out var args)
            ? args.GetRawText()
            : "{}";
        context.ToolStarts[toolCallId] = new ToolStart(toolName, argumentsJson, DateTimeOffset.UtcNow);
    }

    private void PublishToolExecution(RunContext context, JsonElement root, bool isError)
    {
        var toolCallId = GetOptionalString(root, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        context.ToolStarts.TryRemove(toolCallId, out var started);
        var toolName = GetOptionalString(root, "toolName") ?? started?.ToolName ?? "tool";
        var argumentsJson = root.TryGetProperty("args", out var args)
            ? args.GetRawText()
            : started?.ArgumentsJson ?? "{}";
        var resultJson = root.TryGetProperty("result", out var result)
            ? result.GetRawText()
            : "{}";
        try
        {
            ToolExecutionCompleted?.Invoke(new AgentToolExecution(
                context.Request.TaskId,
                context.Request.RunId,
                toolCallId,
                toolName,
                argumentsJson,
                resultJson,
                isError,
                started?.StartedAt ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Emit(
                context,
                CompanionRunEventKind.WarningRaised,
                context.CurrentStatus,
                $"工具证据采集失败：{exception.Message}",
                "部分执行证据不可用");
        }
    }

    private static string DescribeTool(JsonElement root, string phase)
    {
        var toolName = GetOptionalString(root, "toolName") ?? "tool";
        if (root.TryGetProperty("args", out var args))
        {
            var target = GetToolTarget(args);
            if (!string.IsNullOrWhiteSpace(target))
            {
                return $"{toolName} {phase}：{Truncate(target, 240)}";
            }
        }

        return $"{toolName} {phase}";
    }

    private static string? GetToolTarget(JsonElement args) =>
        GetOptionalString(args, "command") ??
        GetOptionalString(args, "path") ??
        GetOptionalString(args, "pattern") ??
        GetOptionalString(args, "query");

    private static string DescribeToolProgress(JsonElement root)
    {
        var toolName = GetOptionalString(root, "toolName") ?? "tool";
        if (root.TryGetProperty("partialResult", out var result))
        {
            var text = ExtractContentText(result);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return $"{toolName}：{Truncate(text, 600)}";
            }
        }

        return $"{toolName} 正在运行";
    }

    private static string ExtractAssistantText(JsonElement message) => ExtractContentText(message);

    private static string ExtractContentText(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (GetOptionalString(item, "type") == "text" && GetOptionalString(item, "text") is { } text)
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private static string GetString(JsonElement element, string propertyName) =>
        GetOptionalString(element, propertyName) ?? throw new JsonException($"Pi RPC 字段 {propertyName} 缺失。");

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName) =>
        element.TryGetProperty(objectName, out var nested) ? GetOptionalString(nested, propertyName) : null;

    private static bool GetOptionalBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static int? GetOptionalInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetOptionalInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var value)
            ? value
            : null;

    private static double? GetOptionalDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var value)
            ? value
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";

    private sealed class RunContext
    {
        private int _terminal;
        private int _settledEventReceived;
        private int _autoRetryActive;
        private int _retryAbortRequested;
        private int _status = (int)RunStatus.Draft;
        private long _generation = 1;

        public RunContext(
            AgentRunRequest request,
            string reuseKey,
            string runIdentityPath,
            string runtimeContextPath)
        {
            Request = request;
            ReuseKey = reuseKey;
            RunIdentityPath = runIdentityPath;
            RuntimeContextPath = runtimeContextPath;
            Sequence = request.InitialSequence;
        }

        public AgentRunRequest Request { get; private set; }
        public AgentRunRequest? PreviousRequest { get; private set; }
        public string ReuseKey { get; }
        public string RunIdentityPath { get; }
        public string RuntimeContextPath { get; }
        public string PermissionToken { get; private set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
        public Process? Process { get; set; }
        public WindowsJobObject? Job { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> PendingResponses { get; } = new();
        public ConcurrentDictionary<string, ToolStart> ToolStarts { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource Completion { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StdoutTask { get; set; }
        public Task? StderrTask { get; set; }
        public Task? ExitTask { get; set; }
        public object InteractionGate { get; } = new();
        public List<PendingInteraction> PendingInteractions { get; } = [];
        public HashSet<string> ResolvingInteractionIds { get; } = new(StringComparer.Ordinal);
        public long RequestId;
        public long Sequence;
        public int PendingMessages;
        public bool AgentStarted { get; set; }
        public bool HasAgentError { get; set; }
        public string? AgentErrorMessage { get; set; }
        public bool AbortRequested { get; set; }
        public bool ExpectedStop { get; set; }
        public bool SuppressEvents { get; set; }
        public bool IsTerminal => Volatile.Read(ref _terminal) != 0;
        public bool SettledEventReceived => Volatile.Read(ref _settledEventReceived) != 0;
        public bool AutoRetryActive => Volatile.Read(ref _autoRetryActive) != 0;
        public long Generation => Volatile.Read(ref _generation);
        public Dictionary<string, string> SessionPayload { get; set; } = [];
        public bool SupportsNativeImages { get; set; }
        public RunStatus CurrentStatus
        {
            get => (RunStatus)Volatile.Read(ref _status);
            set => Volatile.Write(ref _status, (int)value);
        }

        public bool TrySetTerminal() => Interlocked.Exchange(ref _terminal, 1) == 0;

        public void MarkSettledEventReceived() => Volatile.Write(ref _settledEventReceived, 1);

        public void MarkAutoRetryStarted()
        {
            Volatile.Write(ref _retryAbortRequested, 0);
            Volatile.Write(ref _autoRetryActive, 1);
        }

        public void MarkRetryAbortRequested() => Volatile.Write(ref _retryAbortRequested, 1);

        public void ClearRetryAbortRequested() => Volatile.Write(ref _retryAbortRequested, 0);

        public bool CompleteAutoRetry()
        {
            Volatile.Write(ref _autoRetryActive, 0);
            return Interlocked.Exchange(ref _retryAbortRequested, 0) != 0;
        }

        public bool IsGeneration(long generation) => Generation == generation;

        public void BeginRun(AgentRunRequest request)
        {
            PreviousRequest = Request;
            Request = request;
            PermissionToken = Guid.NewGuid().ToString("N");
            LastUsedAt = DateTimeOffset.UtcNow;
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingResponses.Clear();
            ToolStarts.Clear();
            lock (InteractionGate)
            {
                PendingInteractions.Clear();
                ResolvingInteractionIds.Clear();
            }

            Sequence = request.InitialSequence;
            PendingMessages = 0;
            AgentStarted = false;
            HasAgentError = false;
            AgentErrorMessage = null;
            AbortRequested = false;
            ExpectedStop = false;
            SuppressEvents = false;
            Volatile.Write(ref _terminal, 0);
            Volatile.Write(ref _settledEventReceived, 0);
            Volatile.Write(ref _autoRetryActive, 0);
            Volatile.Write(ref _retryAbortRequested, 0);
            Volatile.Write(ref _status, (int)RunStatus.Draft);
            Interlocked.Increment(ref _generation);
        }
    }

    private sealed record PendingInteraction(
        string Id,
        string Method,
        string? DefaultValue,
        IReadOnlyList<string> Options,
        bool IsApproval);

    private sealed record ToolStart(string ToolName, string ArgumentsJson, DateTimeOffset StartedAt);

    private sealed record SessionEntriesResult(IReadOnlyList<JsonElement> Entries, string? LeafId);

    private sealed record NativeImage(string Path, string MimeType, string Base64Data);

    private sealed record RuntimeContextDocument(
        int SchemaVersion,
        long Generation,
        Guid TaskId,
        Guid RunId,
        string WorkingDirectory,
        string PermissionMode,
        string PermissionToken,
        IReadOnlyList<string> ReadOnlyRoots,
        IReadOnlyList<string> SkillReadOnlyRoots,
        IReadOnlyList<string> SkillReadOnlyFiles,
        string ScopeKind,
        string WorkspaceTrustStatus);

    private sealed record SkillReadAccess(
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> Files,
        string WorkspaceTrustStatus);
}
