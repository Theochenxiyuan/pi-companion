using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using System.Text.Json;

namespace PiCompanion.Core.Tests;

public sealed class TaskProjectionTests
{
    [Fact]
    public void LocalMessageQueue_NormalizesEditsAndPreservesPersistedOrder()
    {
        var projection = new TaskProjection(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test",
            "C:\\work",
            "Pi",
            "high");
        var later = new LocalQueuedMessage(Guid.NewGuid(), " later ", DateTimeOffset.UtcNow);
        var earlier = new LocalQueuedMessage(Guid.NewGuid(), " earlier ", later.CreatedAt.AddMinutes(-1));

        projection.RestoreLocalQueuedMessages([later, earlier]);
        var added = projection.AddLocalQueuedMessage("  added  ", [Environment.CurrentDirectory]);
        projection.UpdateLocalQueuedMessage(added.Id, "  edited  ", [Environment.CurrentDirectory]);
        projection.MoveLocalQueuedMessage(added.Id, 0);
        var removed = projection.RemoveLocalQueuedMessage(earlier.Id);

        Assert.Equal(earlier.Id, removed.Id);
        Assert.Equal(["edited", "later"], projection.LocalQueuedMessages.Select(item => item.Message));
        Assert.Equal(Environment.CurrentDirectory, Assert.Single(projection.LocalQueuedMessages[0].Attachments!));
    }

    [Fact]
    public void Apply_UsesStrictSequenceAndIgnoresDuplicates()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Demo", "高");
        var runEvent = CreateEvent(taskId, runId, 1, RunStatus.Running, "读取目录");

        var firstApply = projection.Apply(runEvent);
        var duplicateApply = projection.Apply(runEvent);

        Assert.True(firstApply);
        Assert.False(duplicateApply);
        Assert.Equal(1, projection.LastSequence);
        Assert.Equal(RunStatus.Running, projection.Status);
        Assert.Single(projection.Activities);
    }

    [Fact]
    public void Apply_IgnoresEventsFromAnotherRun()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Demo", "高");

        var applied = projection.Apply(CreateEvent(taskId, Guid.NewGuid(), 1, RunStatus.Running, "错误运行"));

        Assert.False(applied);
        Assert.Equal(RunStatus.Draft, projection.Status);
        Assert.Empty(projection.Activities);
    }

    [Fact]
    public void Apply_StartupPhaseUpdatesReplaceActivityStatusWithoutOverwritingSummary()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");
        var initialTranscriptCount = projection.Transcript.Count;

        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            1,
            CompanionRunEventKind.RunStarted,
            DateTimeOffset.UtcNow,
            RunStatus.Starting,
            new Dictionary<string, string>
            {
                ["activity"] = "已启动 Pi RPC",
                ["summary"] = "正在连接 Pi RPC",
                ["startupPhase"] = "rpc-connecting",
            }));
        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            2,
            CompanionRunEventKind.QueueChanged,
            DateTimeOffset.UtcNow,
            RunStatus.Starting,
            new Dictionary<string, string>
            {
                ["activity"] = string.Empty,
                ["summary"] = "正在恢复 Pi Session",
                ["startupPhase"] = "session-restoring",
            }));
        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            3,
            CompanionRunEventKind.QueueChanged,
            DateTimeOffset.UtcNow,
            RunStatus.Starting,
            new Dictionary<string, string>
            {
                ["activity"] = string.Empty,
                ["summary"] = "正在配置 Pi Session",
                ["startupPhase"] = "session-configuring",
            }));

        Assert.Equal("正在配置 Pi Session", projection.ActivityStatus);
        Assert.Empty(projection.Summary);
        Assert.Single(projection.Activities);
        Assert.Equal("已启动 Pi RPC", projection.Activities[0].Text);
        Assert.Equal(initialTranscriptCount, projection.Transcript.Count);
    }

    [Fact]
    public void Apply_BoundsActivityProjection()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Demo", "高");

        for (var sequence = 1; sequence <= 50; sequence++)
        {
            projection.Apply(CreateEvent(taskId, runId, sequence, RunStatus.Running, $"事件 {sequence}"));
        }

        Assert.Equal(40, projection.Activities.Count);
        Assert.Equal(11, projection.Activities[0].Sequence);
        Assert.Equal(50, projection.LastSequence);
    }

    [Fact]
    public void Apply_CoalescesAssistantDeltasWithoutCreatingActivities()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(CreateAssistantEvent(taskId, runId, 1, CompanionRunEventKind.AssistantMessageStarted));
        projection.Apply(CreateAssistantEvent(taskId, runId, 2, CompanionRunEventKind.AssistantTextDelta, "你"));
        projection.Apply(CreateAssistantEvent(taskId, runId, 3, CompanionRunEventKind.AssistantTextDelta, "好"));
        projection.Apply(CreateAssistantEvent(taskId, runId, 4, CompanionRunEventKind.AssistantThinkingDelta, "思考"));

        Assert.Equal("你好", projection.AssistantText);
        Assert.Single(projection.Activities);
        Assert.Equal(CompanionRunEventKind.AssistantMessageStarted, projection.Activities[0].Kind);
        Assert.Equal(3, projection.Transcript.Count);
        Assert.Equal("你好", projection.Transcript.Single(block => block.Kind == TranscriptBlockKind.AssistantMessage).Content);
        Assert.Equal("思考", projection.Transcript.Single(block => block.Kind == TranscriptBlockKind.Thinking).Content);
    }

    [Fact]
    public void Apply_UsesCompletedAssistantTextAsFinalSnapshot()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(CreateAssistantEvent(taskId, runId, 1, CompanionRunEventKind.AssistantTextDelta, "partial"));
        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            2,
            CompanionRunEventKind.AssistantMessageCompleted,
            DateTimeOffset.UtcNow,
            RunStatus.Running,
            new Dictionary<string, string>
            {
                ["activity"] = "Agent completed a response",
                ["finalText"] = "complete answer",
            }));

        Assert.Equal("complete answer", projection.AssistantText);
        Assert.Equal("complete answer", projection.FinalAnswer);
        Assert.Single(projection.Activities);
    }

    [Fact]
    public void Apply_UpdatesOneToolBlockAcrossItsLifecycle()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high", prompt: "检查文件");

        projection.Apply(CreateToolEvent(taskId, runId, 1, CompanionRunEventKind.ToolStarted, "read 开始", "正在运行"));
        projection.Apply(CreateToolEvent(taskId, runId, 2, CompanionRunEventKind.ToolCompleted, "read 完成", "执行完成"));

        var tool = Assert.Single(projection.Transcript, block => block.Kind == TranscriptBlockKind.Tool);
        Assert.Equal(TranscriptBlockStatus.Completed, tool.Status);
        Assert.Equal("README.md", tool.Input);
        Assert.Equal("执行完成", tool.Output);
        Assert.Equal(1, tool.FirstSequence);
        Assert.Equal(2, tool.LastSequence);
    }

    [Fact]
    public void Apply_ProjectsWebSearchAsItsOwnActivityKind()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(CreateToolEvent(
            taskId,
            runId,
            1,
            CompanionRunEventKind.ToolStarted,
            "网络搜索 开始",
            string.Empty,
            "web_search"));
        projection.Apply(CreateToolEvent(
            taskId,
            runId,
            2,
            CompanionRunEventKind.ToolCompleted,
            "网络搜索 完成",
            "搜索结果",
            "web_search"));

        var search = Assert.Single(projection.Transcript, block => block.Kind == TranscriptBlockKind.WebSearch);
        Assert.Equal("web_search", search.Title);
        Assert.Equal(TranscriptBlockStatus.Completed, search.Status);
        Assert.DoesNotContain(projection.Transcript, block => block.Kind == TranscriptBlockKind.Tool);
    }

    [Fact]
    public void Apply_AddsSteerAsANewUserMessage()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high", prompt: "初始任务");

        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            1,
            CompanionRunEventKind.UserMessageAdded,
            DateTimeOffset.UtcNow,
            RunStatus.Running,
            new Dictionary<string, string>
            {
                ["message"] = "只检查配置文件",
                ["delivery"] = "steer",
            }));

        var messages = projection.Transcript.Where(block => block.Kind == TranscriptBlockKind.UserMessage).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("初始任务", messages[0].Content);
        Assert.Equal("只检查配置文件", messages[1].Content);
        Assert.Contains("调整方向", messages[1].Title);
    }

    [Fact]
    public void Apply_ProjectsInteractionOptionsAndQueueContents()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            1,
            CompanionRunEventKind.QuestionRequested,
            DateTimeOffset.UtcNow,
            RunStatus.WaitingForAnswer,
            new Dictionary<string, string>
            {
                ["activity"] = "选择下一步",
                ["interactionId"] = "question-1",
                ["interactionMethod"] = "select",
                ["interactionOptions"] = JsonSerializer.Serialize(new[] { "A", "B" }),
            }));
        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            2,
            CompanionRunEventKind.QueueChanged,
            DateTimeOffset.UtcNow,
            RunStatus.WaitingForAnswer,
            new Dictionary<string, string>
            {
                ["steeringQueue"] = JsonSerializer.Serialize(new[] { "现在处理" }),
                ["followUpQueue"] = JsonSerializer.Serialize(new[] { "稍后处理" }),
            }));
        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            3,
            CompanionRunEventKind.InteractionResolved,
            DateTimeOffset.UtcNow,
            RunStatus.Running,
            new Dictionary<string, string>
            {
                ["interactionId"] = "question-1",
                ["approved"] = "false",
                ["response"] = "A",
            }));

        var interaction = Assert.Single(projection.Transcript, block => block.Kind == TranscriptBlockKind.Interaction);
        Assert.Equal("Question", interaction.InteractionKind);
        Assert.Equal(["A", "B"], interaction.InteractionOptions);
        Assert.Equal(TranscriptBlockStatus.Cancelled, interaction.Status);
        Assert.Null(interaction.Output);
        Assert.Equal(["现在处理"], projection.PendingSteering);
        Assert.Equal(["稍后处理"], projection.PendingFollowUps);
    }

    [Fact]
    public void Apply_DoesNotRenderSuccessfulSessionRecoveryAsTranscriptNotice()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            1,
            CompanionRunEventKind.RecoveryAvailable,
            DateTimeOffset.UtcNow,
            RunStatus.Starting,
            new Dictionary<string, string>
            {
                ["activity"] = "已恢复上次 Pi Session",
                ["summary"] = "Session 已恢复",
            }));

        Assert.DoesNotContain(projection.Transcript, block => block.Kind == TranscriptBlockKind.Notice);
    }

    [Fact]
    public void Apply_UpdatesCompactionAndRetryNoticeBlocksAcrossTheirLifecycle()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(CreateLifecycleEvent(taskId, runId, 1, CompanionRunEventKind.CompactionStarted, "正在压缩", true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 2, CompanionRunEventKind.CompactionCompleted, "压缩完成", true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 3, CompanionRunEventKind.AutoRetryStarted, "等待重试", true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 4, CompanionRunEventKind.AutoRetryCompleted, "重试失败", false));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 5, CompanionRunEventKind.AutoRetryStarted, "再次等待重试", true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 6, CompanionRunEventKind.AutoRetryCompleted, "重试已取消", false, cancelled: true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 7, CompanionRunEventKind.SummarizationRetryStarted, "等待摘要重试", true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 8, CompanionRunEventKind.SummarizationRetryProgressed, "正在重试摘要", true));
        projection.Apply(CreateLifecycleEvent(taskId, runId, 9, CompanionRunEventKind.SummarizationRetryCompleted, "摘要重试结束", true));

        var notices = projection.Transcript.Where(block => block.Kind == TranscriptBlockKind.Notice).ToArray();
        Assert.Collection(
            notices,
            compaction =>
            {
                Assert.Equal("上下文压缩", compaction.Title);
                Assert.Equal(TranscriptBlockStatus.Completed, compaction.Status);
                Assert.Equal("压缩完成", compaction.Content);
            },
            retry =>
            {
                Assert.Equal("自动重试", retry.Title);
                Assert.Equal(TranscriptBlockStatus.Failed, retry.Status);
                Assert.Equal("重试失败", retry.Content);
            },
            cancelledRetry =>
            {
                Assert.Equal("自动重试", cancelledRetry.Title);
                Assert.Equal(TranscriptBlockStatus.Cancelled, cancelledRetry.Status);
                Assert.Equal("重试已取消", cancelledRetry.Content);
            },
            summarizationRetry =>
            {
                Assert.Equal("摘要重试", summarizationRetry.Title);
                Assert.Equal(TranscriptBlockStatus.Completed, summarizationRetry.Status);
                Assert.Equal("摘要重试结束", summarizationRetry.Content);
            });
    }

    [Fact]
    public void Apply_PresentsRestartInterruptionWithoutInternalTermsOrNoticeCard()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projection = new TaskProjection(taskId, runId, "test", "C:\\work", "Pi", "high");

        projection.Apply(new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            1,
            CompanionRunEventKind.RunInterrupted,
            DateTimeOffset.UtcNow,
            RunStatus.Interrupted,
            new Dictionary<string, string>
            {
                ["activity"] = "应用重启时检测到未完成的 Pi Run",
                ["summary"] = "上次运行已中断，可从保留的 Session 继续",
                ["exitReason"] = "application-restart",
            }));

        Assert.Equal("已停止", projection.Status.ToDisplayText());
        Assert.Empty(projection.Summary);
        Assert.Equal("应用关闭时任务仍在进行，你可以继续提问", projection.RuntimeStatusDetail);
        Assert.DoesNotContain(projection.Transcript, block => block.Kind == TranscriptBlockKind.Notice);
    }

    [Fact]
    public void AiSummaryStatus_TracksOnlyTheExplicitGenerationLifecycle()
    {
        var projection = new TaskProjection(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test",
            "C:\\work",
            "Pi",
            "high");

        Assert.Equal(AiSummaryStatus.NotRequested, projection.AiSummaryStatus);

        projection.BeginAiSummaryGeneration();
        Assert.Equal(AiSummaryStatus.Generating, projection.AiSummaryStatus);

        projection.FailAiSummaryGeneration();
        Assert.Equal(AiSummaryStatus.Failed, projection.AiSummaryStatus);

        projection.BeginAiSummaryGeneration();
        projection.SetSummary("  Generated   summary.  ");
        Assert.Equal(AiSummaryStatus.Available, projection.AiSummaryStatus);
        Assert.Equal("Generated summary.", projection.Summary);
    }

    private static CompanionRunEvent CreateEvent(
        Guid taskId,
        Guid runId,
        long sequence,
        RunStatus status,
        string activity) => new(
            Guid.NewGuid(),
            taskId,
            runId,
            sequence,
            CompanionRunEventKind.ToolProgressed,
            DateTimeOffset.UtcNow,
            status,
            new Dictionary<string, string>
            {
                ["activity"] = activity,
                ["summary"] = activity,
            });

    private static CompanionRunEvent CreateAssistantEvent(
        Guid taskId,
        Guid runId,
        long sequence,
        CompanionRunEventKind kind,
        string? delta = null)
    {
        var payload = new Dictionary<string, string>
        {
            ["activity"] = delta ?? "Agent started a response",
        };
        if (delta is not null)
        {
            payload["delta"] = delta;
        }

        return new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            sequence,
            kind,
            DateTimeOffset.UtcNow,
            RunStatus.Running,
            payload);
    }

    private static CompanionRunEvent CreateToolEvent(
        Guid taskId,
        Guid runId,
        long sequence,
        CompanionRunEventKind kind,
        string activity,
        string output,
        string toolName = "read") => new(
            Guid.NewGuid(),
            taskId,
            runId,
            sequence,
            kind,
            DateTimeOffset.UtcNow,
            RunStatus.Running,
            new Dictionary<string, string>
            {
                ["activity"] = activity,
                ["toolCallId"] = "tool-1",
                ["toolName"] = toolName,
                ["toolInput"] = "README.md",
                ["toolOutput"] = output,
            });

    private static CompanionRunEvent CreateLifecycleEvent(
        Guid taskId,
        Guid runId,
        long sequence,
        CompanionRunEventKind kind,
        string activity,
        bool success,
        bool cancelled = false) => new(
            Guid.NewGuid(),
            taskId,
            runId,
            sequence,
            kind,
            DateTimeOffset.UtcNow,
            RunStatus.Running,
            new Dictionary<string, string>
            {
                ["activity"] = activity,
                ["summary"] = activity,
                ["success"] = success ? "true" : "false",
                ["cancelled"] = cancelled ? "true" : "false",
            });
}
