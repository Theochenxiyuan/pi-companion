using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PiCompanion.Application.PiRpc;

public sealed record PiMcpRequest(
    string RequestId,
    string Action,
    string Scope = "global",
    Guid? WorkspaceId = null,
    string? Name = null,
    string? OriginalName = null,
    JsonElement? Config = null,
    JsonElement? Servers = null,
    string? Revision = null,
    bool Enabled = true,
    bool Check = false);

public sealed record PiMcpTool(string Name, string Description);
public sealed record PiMcpServer(string Name, string Scope, JsonElement Config, string State, IReadOnlyList<PiMcpTool> Tools, string? Error,
    bool IsOverride = false, bool Inherited = false, JsonElement? OverrideConfig = null);
public sealed record PiMcpSnapshot(IReadOnlyList<PiMcpServer> Servers, string GlobalRevision, string? ProjectRevision, bool ProjectTrusted);

public sealed class PiMcpService(PiRuntimeResolver runtimeResolver, string helperPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Process> _logins = new(StringComparer.Ordinal);

    public static PiMcpService CreateDefault() => new(new PiRuntimeResolver(), Path.Combine(AppContext.BaseDirectory, "PiExtension", "pi-mcp.mjs"));

    public async Task SubmitRedirectAsync(string requestId, string redirectUrl)
    {
        if (!_logins.TryGetValue(requestId, out var process)) throw new InvalidOperationException("登录已结束，请重新登录。");
        if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
            throw new InvalidOperationException("请粘贴浏览器中的完整回调地址。");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { redirectUrl }, JsonOptions));
        await process.StandardInput.FlushAsync();
    }

    public async Task<PiMcpSnapshot> RunAsync(
        PiMcpRequest request,
        string? workspaceDirectory,
        bool projectTrusted,
        Action<string>? onAuthorizationUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 100 ||
            request.Action is not ("list" or "save" or "import" or "toggle" or "delete" or "login" or "logout") ||
            request.Scope is not ("global" or "project"))
            throw new InvalidOperationException("服务请求无效。");
        if (request.Scope == "project" && workspaceDirectory is null) throw new InvalidOperationException("请选择工作区。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = runtimeResolver.Resolve();
            if (!runtime.RuntimePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || !File.Exists(helperPath))
                throw new InvalidOperationException("无法启动服务管理，请重新安装应用。");
            var startInfo = new ProcessStartInfo
            {
                FileName = runtime.FileName, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true),
                WorkingDirectory = workspaceDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            startInfo.ArgumentList.Add(Path.GetFullPath(helperPath));
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("服务管理启动失败。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Action == "login" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(90));
            var token = timeout.Token;
            var errorOutput = process.StandardError.ReadToEndAsync(CancellationToken.None);
            PiMcpSnapshot? snapshot = null;
            string? error = null;
            try
            {
                if (request.Action == "login") _logins[request.RequestId] = process;
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    piEntry = runtime.RuntimePath, cwd = startInfo.WorkingDirectory,
                    workspaceSelected = workspaceDirectory is not null, projectTrusted,
                    request.Action, request.Scope, request.Name, request.OriginalName, request.Config,
                    request.Servers, request.Revision, request.Enabled, request.Check,
                }, JsonOptions).AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
                if (request.Action != "login") process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var frame = JsonDocument.Parse(line);
                    var root = frame.RootElement;
                    switch (root.GetProperty("kind").GetString())
                    {
                        case "event": onAuthorizationUrl?.Invoke(root.GetProperty("url").GetString()!); break;
                        case "result": snapshot = root.GetProperty("snapshot").Deserialize<PiMcpSnapshot>(JsonOptions); break;
                        case "error": error = root.GetProperty("message").GetString(); break;
                    }
                }
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                if (process.ExitCode != 0 || snapshot is null)
                    throw new InvalidOperationException(error ?? "服务操作失败，请检查连接设置。");
                return snapshot;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(request.Action == "login" ? "登录等待超时，请重试。" : "服务连接超时，请重试。");
            }
            finally
            {
                _logins.TryRemove(request.RequestId, out _);
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await errorOutput.ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
}
