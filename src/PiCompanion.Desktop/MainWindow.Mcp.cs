using System.IO;
using System.Text.Json;
using PiCompanion.Application.PiRpc;

namespace PiCompanion.Desktop;

public partial class MainWindow
{
    private readonly PiMcpService _mcp = PiMcpService.CreateDefault();
    private readonly Dictionary<string, CancellationTokenSource> _mcpLogins = new(StringComparer.Ordinal);

    private async Task HandleMcpRequestAsync(JsonElement payload)
    {
        var request = payload.Deserialize<PiMcpRequest>(JsonOptions) ?? throw new InvalidOperationException("服务请求无效。");
        string? workspaceDirectory = null;
        var trusted = false;
        if (request.WorkspaceId is Guid workspaceId)
        {
            var workspace = _coordinator.Workspaces.FirstOrDefault(value => value.Id == workspaceId) ??
                throw new InvalidOperationException("工作区已不可用，请重新选择。");
            workspaceDirectory = workspace.WorkingDirectory;
            trusted = _piProjectTrust.GetStatus(workspaceDirectory).Status == "trusted";
        }
        using var cancellation = new CancellationTokenSource();
        if (request.Action == "login")
        {
            if (_mcpLogins.Count > 0) throw new InvalidOperationException("请先完成或取消当前登录。");
            _mcpLogins.Add(request.RequestId, cancellation);
        }
        try
        {
            var snapshot = await _mcp.RunAsync(request, workspaceDirectory, trusted,
                url => Dispatcher.Invoke(() =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                        throw new InvalidOperationException("登录地址无效。");
                    OpenExternalLink(url);
                    PostMessage("McpLoginProgress", new { request.RequestId, phase = "waiting" });
                }), cancellation.Token);
            if (request.Action != "list")
                _coordinator.InvalidateRuntimeResources(request.Scope == "project" ? workspaceDirectory : null);
            PostMessage("McpResult", new { request.RequestId, request.WorkspaceId, request.Action, succeeded = true, snapshot, message = "" });
        }
        catch (OperationCanceledException)
        {
            PostMessage("McpResult", new { request.RequestId, request.WorkspaceId, request.Action, succeeded = false, canceled = true, message = "登录已取消。" });
        }
        catch (Exception exception)
        {
            PostMessage("McpResult", new { request.RequestId, request.WorkspaceId, request.Action, succeeded = false, message = exception.Message });
        }
        finally { _mcpLogins.Remove(request.RequestId); }
    }

    private void CancelMcpLogin(JsonElement payload)
    {
        if (_mcpLogins.TryGetValue(ReadString(payload, "requestId"), out var cancellation)) cancellation.Cancel();
    }
}
