using System.Diagnostics;
using System.Text.Json;
using PiCompanion.Application.PiRpc;

namespace PiCompanion.Core.Tests;

public sealed class PiMcpServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"companion-mcp-service-{Guid.NewGuid():N}");

    private async Task<PiMcpService> CreateServiceAsync()
    {
        Directory.CreateDirectory(_root);
        var runtime = Path.Combine(_root, "cli.js");
        var helper = Path.Combine(_root, "helper.mjs");
        await File.WriteAllTextAsync(runtime, "", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(helper, """
            import { createInterface } from 'node:readline';
            import { spawn } from 'node:child_process';
            import { writeFileSync } from 'node:fs';
            import { join } from 'node:path';
            const lines = createInterface({ input: process.stdin });
            const iterator = lines[Symbol.asyncIterator]();
            const request = JSON.parse((await iterator.next()).value);
            const send = value => process.stdout.write(`${JSON.stringify(value)}\n`);
            let revision = request.cwd;
            if (request.action === 'login') {
              if (request.name === 'cancel') {
                const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
                writeFileSync(join(request.cwd, 'child-pid.txt'), String(child.pid));
              }
              send({ kind: 'event', url: 'http://127.0.0.1/authorize' });
              const callback = JSON.parse((await iterator.next()).value);
              revision = callback.redirectUrl;
            }
            send({ kind: 'result', snapshot: { servers: [], globalRevision: revision, projectRevision: null, projectTrusted: request.projectTrusted } });
            lines.close();
            process.stdin.destroy();
            """, TestContext.Current.CancellationToken);
        return new PiMcpService(new PiRuntimeResolver(runtime, _root, OperatingSystem.IsWindows() ? "node.exe" : "node"), helper);
    }

    [Fact]
    public async Task LoginForwardsAuthorizationAndAcceptsThePastedCallback()
    {
        var service = await CreateServiceAsync();
        var authorization = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = service.RunAsync(new PiMcpRequest("login", "login", Name: "remote"), _root, true,
            url => authorization.TrySetResult(url), TestContext.Current.CancellationToken);
        Assert.Equal("http://127.0.0.1/authorize", await authorization.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        const string callback = "http://127.0.0.1:1234/callback?code=test&state=state";
        await service.SubmitRedirectAsync("login", callback);
        var result = await login;
        Assert.Equal(callback, result.GlobalRevision);
        Assert.True(result.ProjectTrusted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitRedirectAsync("login", callback));
    }

    [Fact]
    public async Task CancelingLoginStopsItsChildProcessAndReleasesTheOperationGate()
    {
        var service = await CreateServiceAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var authorization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = service.RunAsync(new PiMcpRequest("cancel", "login", Name: "cancel"), _root, false,
            _ => authorization.TrySetResult(), cancellation.Token);
        await authorization.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "child-pid.txt"), TestContext.Current.CancellationToken));
        using var child = Process.GetProcessById(childId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var result = await service.RunAsync(new PiMcpRequest("next", "list"), _root, false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(_root, result.GlobalRevision);
    }

    [Fact]
    public async Task ProjectRequestsRequireAResolvedWorkspace()
    {
        var service = await CreateServiceAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new PiMcpRequest("project", "save", "project"), null, false,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
