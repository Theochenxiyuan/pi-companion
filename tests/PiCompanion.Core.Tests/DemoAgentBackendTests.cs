using PiCompanion.Application.Demo;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;

namespace PiCompanion.Core.Tests;

public sealed class DemoAgentBackendTests
{
    [Fact]
    public async Task SuccessScenario_EndsWithSettledEventAndStrictSequences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var backend = new DemoAgentBackend(TimeSpan.Zero);
        var events = new List<CompanionRunEvent>();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.EventReceived += runEvent =>
        {
            events.Add(runEvent);
            if (runEvent.Kind == CompanionRunEventKind.RunSettled)
            {
                settled.TrySetResult();
            }
        };

        await backend.StartRunAsync(CreateRequest(DemoRunMode.Success), cancellationToken);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        Assert.Equal(CompanionRunEventKind.RunQueued, events[0].Kind);
        Assert.Equal(CompanionRunEventKind.RunSettled, events[^1].Kind);
        Assert.Equal(RunStatus.Completed, events[^1].Status);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(runEvent => runEvent.Sequence));
    }

    [Fact]
    public async Task InteractiveScenario_WaitsForApprovalBeforeSettling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var backend = new DemoAgentBackend(TimeSpan.FromMilliseconds(1));
        var eventKinds = new List<CompanionRunEventKind>();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(DemoRunMode.InteractiveSuccess);
        backend.EventReceived += runEvent =>
        {
            eventKinds.Add(runEvent.Kind);
            if (runEvent.Kind == CompanionRunEventKind.ApprovalRequested)
            {
                _ = backend.ResolveInteractionAsync(runEvent.RunId, new InteractionResolution(true), cancellationToken);
            }
            else if (runEvent.Kind == CompanionRunEventKind.RunSettled)
            {
                settled.TrySetResult();
            }
        };

        await backend.StartRunAsync(request, cancellationToken);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        var requestedIndex = eventKinds.IndexOf(CompanionRunEventKind.ApprovalRequested);
        var resolvedIndex = eventKinds.IndexOf(CompanionRunEventKind.InteractionResolved);
        var settledIndex = eventKinds.IndexOf(CompanionRunEventKind.RunSettled);
        Assert.True(requestedIndex >= 0);
        Assert.True(resolvedIndex > requestedIndex);
        Assert.True(settledIndex > resolvedIndex);
    }

    [Fact]
    public async Task FailureScenario_PreservesFailureAsFinalStatus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var backend = new DemoAgentBackend(TimeSpan.Zero);
        CompanionRunEvent? finalEvent = null;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.EventReceived += runEvent =>
        {
            if (runEvent.Kind == CompanionRunEventKind.RunFailed)
            {
                finalEvent = runEvent;
                failed.TrySetResult();
            }
        };

        await backend.StartRunAsync(CreateRequest(DemoRunMode.Failure), cancellationToken);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        Assert.NotNull(finalEvent);
        Assert.Equal(RunStatus.Failed, finalEvent.Status);
    }

    private static AgentRunRequest CreateRequest(DemoRunMode mode) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Demo test",
        "Run the test scenario",
        Environment.CurrentDirectory,
        "Demo Agent",
        "高",
        mode.ToString());
}
