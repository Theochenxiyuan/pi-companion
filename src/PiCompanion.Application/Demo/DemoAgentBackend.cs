using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;

namespace PiCompanion.Application.Demo;

public sealed class DemoAgentBackend(TimeSpan? stepDelay = null) : IAgentBackend, IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _stepDelay = stepDelay ?? TimeSpan.FromMilliseconds(520);
    private readonly Dictionary<Guid, DemoRunContext> _runs = [];
    private bool _disposed;

    public event Action<CompanionRunEvent>? EventReceived;

    public event Action<AgentToolExecution>? ToolExecutionCompleted
    {
        add { }
        remove { }
    }

    public Task StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var context = new DemoRunContext(
            request,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        context.Sequence = request.InitialSequence;
        lock (_gate)
        {
            if (_runs.ContainsKey(request.RunId))
            {
                context.Cancellation.Dispose();
                throw new InvalidOperationException("The requested demo run is already active.");
            }

            _runs.Add(request.RunId, context);
        }

        _ = RunScenarioAsync(context);
        return Task.CompletedTask;
    }

    public Task SteerAsync(Guid runId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = RequireRun(runId);
        Emit(
            context,
            CompanionRunEventKind.QueueChanged,
            RunStatus.Running,
            $"已立即调整方向：{message}",
            "Agent 已接收新的执行方向");
        return Task.CompletedTask;
    }

    public Task FollowUpAsync(Guid runId, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = RequireRun(runId);
        Emit(
            context,
            CompanionRunEventKind.QueueChanged,
            CurrentStatus(context),
            $"已排队后续任务：{message}",
            "后续任务将在当前运行结束后开始");
        return Task.CompletedTask;
    }

    public Task ResolveInteractionAsync(
        Guid runId,
        InteractionResolution resolution,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = RequireRun(runId);
        TaskCompletionSource<InteractionResolution>? interaction;
        lock (_gate)
        {
            interaction = context.Interaction;
        }

        interaction?.TrySetResult(resolution);
        return Task.CompletedTask;
    }

    public Task AbortAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = RequireRun(runId);
        Emit(
            context,
            CompanionRunEventKind.QueueChanged,
            RunStatus.Cancelling,
            "正在停止模拟任务",
            "已发送 Abort");
        context.Cancellation.Cancel();
        return Task.CompletedTask;
    }

    public Task AbortRetryAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = RequireRun(runId);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DemoRunContext[] contexts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            contexts = _runs.Values.ToArray();
            _runs.Clear();
        }

        foreach (var context in contexts)
        {
            context.Cancellation.Cancel();
            context.Cancellation.Dispose();
        }
    }

    private async Task RunScenarioAsync(DemoRunContext context)
    {
        try
        {
            if (context.Request.InitialSequence == 0)
            {
                Emit(context, CompanionRunEventKind.RunQueued, RunStatus.Queued, "任务已进入并发队列", "等待 Demo Agent");
            }
            await PauseAsync(context.Cancellation.Token);

            Emit(context, CompanionRunEventKind.RunStarted, RunStatus.Starting, "正在创建独立 Agent 进程", "正在启动");
            await PauseAsync(context.Cancellation.Token);

            Emit(context, CompanionRunEventKind.AssistantMessageStarted, RunStatus.Running, "Agent 开始分析任务", "正在理解目标");
            await PauseAsync(context.Cancellation.Token);

            Emit(context, CompanionRunEventKind.AssistantThinkingDelta, RunStatus.Running, "已规划模拟任务的实现步骤", "已生成执行计划");
            await PauseAsync(context.Cancellation.Token);

            Emit(context, CompanionRunEventKind.ToolStarted, RunStatus.Running, "读取工作目录并检查工程结构", "正在检查文件");
            await PauseAsync(context.Cancellation.Token);

            if (context.Request.Mode == DemoRunMode.InteractiveSuccess.ToString())
            {
                var interaction = new TaskCompletionSource<InteractionResolution>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    context.Interaction = interaction;
                }

                Emit(
                    context,
                    CompanionRunEventKind.ApprovalRequested,
                    RunStatus.WaitingForApproval,
                    "请求运行只读构建检查",
                    "需要你的授权才能继续");

                var resolution = await interaction.Task.WaitAsync(context.Cancellation.Token);
                Emit(
                    context,
                    CompanionRunEventKind.InteractionResolved,
                    RunStatus.Running,
                    resolution.Approved ? "已允许本次构建检查" : "用户拒绝了构建检查",
                    resolution.Approved ? "继续执行" : "操作已拒绝");

                if (!resolution.Approved)
                {
                    Emit(context, CompanionRunEventKind.RunFailed, RunStatus.Failed, "任务因授权被拒绝而停止", "模拟任务未完成");
                    return;
                }

                await PauseAsync(context.Cancellation.Token);
            }

            if (context.Request.Mode == DemoRunMode.Failure.ToString())
            {
                Emit(context, CompanionRunEventKind.ToolFailed, RunStatus.Running, "模拟构建命令返回退出码 1", "构建检查失败");
                await PauseAsync(context.Cancellation.Token);
                Emit(context, CompanionRunEventKind.RunFailed, RunStatus.Failed, "已保留错误详情与恢复入口", "模拟任务失败");
                return;
            }

            Emit(context, CompanionRunEventKind.ToolCompleted, RunStatus.Running, "工作目录检查完成", "正在汇总结果");
            await PauseAsync(context.Cancellation.Token);
            Emit(context, CompanionRunEventKind.TestExecutionDetected, RunStatus.Running, "模拟测试：3 项通过", "自动化检查通过");
            await PauseAsync(context.Cancellation.Token);
            Emit(context, CompanionRunEventKind.AssistantMessageCompleted, RunStatus.Running, "最终回答已生成", "正在收尾");
            await PauseAsync(context.Cancellation.Token);
            Emit(context, CompanionRunEventKind.RunSettled, RunStatus.Completed, "Demo Agent 已 settled", "模拟任务完成");
        }
        catch (OperationCanceledException)
        {
            Emit(context, CompanionRunEventKind.RunInterrupted, RunStatus.Interrupted, "任务已停止", "已按你的要求停止");
        }
        finally
        {
            lock (_gate)
            {
                _runs.Remove(context.Request.RunId);
            }
            context.Cancellation.Dispose();
        }
    }

    private Task PauseAsync(CancellationToken cancellationToken) =>
        Task.Delay(_stepDelay, cancellationToken);

    private void Emit(
        DemoRunContext context,
        CompanionRunEventKind kind,
        RunStatus status,
        string activity,
        string summary)
    {
        var sequence = Interlocked.Increment(ref context.Sequence);
        EventReceived?.Invoke(new CompanionRunEvent(
            Guid.NewGuid(),
            context.Request.TaskId,
            context.Request.RunId,
            sequence,
            kind,
            DateTimeOffset.UtcNow,
            status,
            new Dictionary<string, string>
            {
                ["activity"] = activity,
                ["summary"] = summary,
            }));
    }

    private RunStatus CurrentStatus(DemoRunContext context)
    {
        lock (_gate)
        {
            return context.Interaction is null ? RunStatus.Running : RunStatus.WaitingForApproval;
        }
    }

    private DemoRunContext RequireRun(Guid runId)
    {
        lock (_gate)
        {
            return _runs.GetValueOrDefault(runId)
                ?? throw new InvalidOperationException("The requested demo run is not active.");
        }
    }

    private sealed class DemoRunContext(
        AgentRunRequest request,
        CancellationTokenSource cancellation)
    {
        public AgentRunRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<InteractionResolution>? Interaction { get; set; }
        public long Sequence;
    }
}
