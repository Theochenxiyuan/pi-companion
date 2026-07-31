using Microsoft.Data.Sqlite;
using PiCompanion.Application.Demo;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.Settings;
using PiCompanion.Application.Tasks;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using System.Text.Json;

namespace PiCompanion.Core.Tests;

public sealed class DemoTaskCoordinatorTests
{
    [Fact]
    public async Task InvalidateRuntimeResources_BlocksRunningScopeAndReleasesIdleWorkers()
    {
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);
        var workspace = Path.GetTempPath();
        await coordinator.StartAsync(
            "running",
            workspace,
            "test/model",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => coordinator.InvalidateRuntimeResources(workspace));
        Assert.Throws<InvalidOperationException>(() => coordinator.InvalidateRuntimeResources(null));

        backend.SettleCurrentRun();
        coordinator.InvalidateRuntimeResources(workspace);
        coordinator.InvalidateRuntimeResources(null);

        Assert.Equal(
            [Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), null],
            backend.InvalidatedResourceWorkspaces);
    }

    [Fact]
    public async Task PrepareAsync_ForwardsComposerConfigurationWithoutCreatingATask()
    {
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);

        await coordinator.PrepareAsync(
            Environment.CurrentDirectory,
            "provider/model",
            "high",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new AgentPreparationRequest(Environment.CurrentDirectory, "provider/model", "high"),
            backend.LastPreparation);
        Assert.Null(coordinator.Current);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task SessionStatisticsCache_RestoresAfterRestartAndRejectsStaleSequence()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var store = new SqliteRunEventStore(databasePath);
            store.CreateRun(new TaskProjection(taskId, runId, "任务", root, "provider/model", "high"), "任务");
            var expected = CreateSessionStatistics();

            using (var coordinator = new TaskCoordinator(
                new RecordingBackend { SessionStatistics = expected },
                store))
            {
                Assert.Equal(
                    expected,
                    await coordinator.GetSessionStatisticsAsync(
                        cancellationToken: TestContext.Current.CancellationToken));
            }

            using (var restoredCoordinator = new TaskCoordinator(new RecordingBackend(), new SqliteRunEventStore(databasePath)))
            {
                Assert.Equal(
                    expected,
                    await restoredCoordinator.GetSessionStatisticsAsync(
                        cancellationToken: TestContext.Current.CancellationToken));
            }

            store.AppendRunEvent(new CompanionRunEvent(
                Guid.NewGuid(),
                taskId,
                runId,
                1,
                CompanionRunEventKind.RunStarted,
                DateTimeOffset.UtcNow,
                RunStatus.Running,
                new Dictionary<string, string>
                {
                    ["activity"] = "Started",
                    ["summary"] = "Running",
                }));
            using var staleCoordinator = new TaskCoordinator(new RecordingBackend(), new SqliteRunEventStore(databasePath));
            Assert.Null(await staleCoordinator.GetSessionStatisticsAsync(
                cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_PreservesOriginalAttachmentsButSendsStagedOutsideCopiesToTheBackend()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var outsideFile = Path.Combine(root, "outside.png");
        File.WriteAllText(outsideFile, "image");
        try
        {
            var backend = new RecordingBackend();
            var staging = new AttachmentStagingService(Path.Combine(root, "attachments"));
            using var coordinator = new TaskCoordinator(backend, attachmentStaging: staging);

            await coordinator.StartAsync(
                "inspect",
                workspace,
                "Demo Agent",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken,
                [outsideFile],
                permissionMode: "full-access");

            Assert.Equal(outsideFile, Assert.Single(coordinator.Current!.Attachments));
            var request = Assert.IsType<AgentRunRequest>(backend.LastRequest);
            var runtimeAttachment = Assert.Single(request.Attachments!);
            Assert.NotEqual(outsideFile, runtimeAttachment);
            Assert.StartsWith(request.ReadOnlyAttachmentRoot!, runtimeAttachment, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("image", File.ReadAllText(runtimeAttachment));
            Assert.Equal("full-access", coordinator.Current.PermissionMode);
            Assert.Equal("full-access", request.PermissionMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_PersistsPromotedClipboardAssetPathsOnTheTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var transientRoot = Directory.CreateDirectory(Path.Combine(root, "clipboard-attachments")).FullName;
        var clipboardImage = Path.Combine(transientRoot, "clipboard.png");
        File.WriteAllText(clipboardImage, "image");
        try
        {
            var backend = new RecordingBackend();
            using var coordinator = new TaskCoordinator(
                backend,
                attachmentStaging: new AttachmentStagingService(
                    Path.Combine(root, "attachments"),
                    transientRoot));

            await coordinator.StartAsync(
                string.Empty,
                workspace,
                "provider/vision-model",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken,
                [clipboardImage]);

            var persistentPath = Assert.Single(coordinator.Current!.Attachments);
            Assert.False(File.Exists(clipboardImage));
            Assert.True(File.Exists(persistentPath));
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}",
                persistentPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(persistentPath, Assert.Single(backend.LastRequest!.Attachments!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_GeneralChatCreatesManagedWorkspaceAndSnapshotsEveryAttachment()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var attachment = Path.Combine(root, "input.txt");
        File.WriteAllText(attachment, "input");
        try
        {
            var backend = new RecordingBackend();
            using var coordinator = new TaskCoordinator(
                backend,
                attachmentStaging: new AttachmentStagingService(Path.Combine(root, "attachments")),
                generalChatWorkspaces: new GeneralChatWorkspaceService(Path.Combine(root, "general-chat")));

            await coordinator.StartAsync(
                "summarize",
                null,
                "provider/model",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken,
                [attachment],
                scopeKind: TaskScopeKind.GeneralChat);

            var projection = Assert.IsType<TaskProjection>(coordinator.Current);
            var request = Assert.IsType<AgentRunRequest>(backend.LastRequest);
            Assert.Equal(TaskScopeKind.GeneralChat, projection.ScopeKind);
            Assert.Equal(TaskScopeKind.GeneralChat, request.ScopeKind);
            Assert.Equal("standard", request.PermissionMode);
            Assert.True(Directory.Exists(request.WorkingDirectory));
            Assert.True(Directory.Exists(request.ArtifactDirectory));
            Assert.StartsWith(Path.Combine(root, "general-chat"), request.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(attachment, Assert.Single(request.Attachments!));
            Assert.Equal("input", File.ReadAllText(Assert.Single(request.Attachments!)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteTaskPermanently_ReleasesGeneralChatWorkerBeforeDeletingWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backend = new RecordingBackend { SettleOnStart = true };
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var workspaces = new GeneralChatWorkspaceService(Path.Combine(root, "general-chat"));
            using var coordinator = new TaskCoordinator(
                backend,
                store,
                generalChatWorkspaces: workspaces);

            await coordinator.StartAsync(
                "answer directly",
                null,
                "provider/model",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken,
                scopeKind: TaskScopeKind.GeneralChat);

            var taskId = Assert.IsType<TaskProjection>(coordinator.Current).TaskId;
            var workingDirectory = workspaces.GetWorkingDirectory(taskId);
            Assert.True(Directory.Exists(workingDirectory));
            coordinator.MoveTaskToRecycleBin(taskId);

            coordinator.DeleteTaskPermanently(taskId);

            Assert.Contains(Path.GetFullPath(workingDirectory), backend.ReleasedWorkspaces);
            Assert.False(Directory.Exists(workingDirectory));
            Assert.DoesNotContain(coordinator.RecycleBinTasks, task => task.TaskId == taskId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PublishArtifactToolResultIsPersistedOnTheGeneralChatProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backend = new RecordingBackend();
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var workspaces = new GeneralChatWorkspaceService(Path.Combine(root, "general-chat"));
            using var coordinator = new TaskCoordinator(
                backend,
                store,
                attachmentStaging: new AttachmentStagingService(Path.Combine(root, "attachments")),
                generalChatWorkspaces: workspaces);

            await coordinator.StartAsync(
                "create report",
                null,
                "provider/model",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken,
                scopeKind: TaskScopeKind.GeneralChat);

            var request = Assert.IsType<AgentRunRequest>(backend.LastRequest);
            var artifactId = Guid.NewGuid();
            var runDirectory = Directory.CreateDirectory(Path.Combine(request.ArtifactDirectory!, request.RunId.ToString("D"))).FullName;
            var storagePath = Path.Combine(runDirectory, $"{artifactId:D}-report.csv");
            File.WriteAllText(storagePath, "value\n42\n");
            backend.PublishToolExecution(new AgentToolExecution(
                request.TaskId,
                request.RunId,
                "publish-1",
                "publish_artifact",
                """{"path":"report.csv"}""",
                JsonSerializer.Serialize(new
                {
                    details = new
                    {
                        artifact = new
                        {
                            id = artifactId,
                            path = storagePath,
                            displayName = "report.csv",
                            contentType = "text/csv",
                        },
                    },
                }),
                false,
                DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow));

            var artifact = Assert.Single(coordinator.Current!.Artifacts);
            Assert.Equal("report.csv", artifact.DisplayName);
            Assert.Equal(artifact, store.GetTaskArtifact(artifact.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_UsesTimestampedFallbackTitleInsteadOfFirstPrompt()
    {
        using var coordinator = new TaskCoordinator(new DemoAgentBackend(TimeSpan.FromSeconds(1)));

        await coordinator.StartAsync(
            "This prompt must not become the task title",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);

        Assert.Matches(@"^新任务 · \d{4}-\d{2}-\d{2} \d{2}:\d{2}$", coordinator.Current?.Title ?? string.Empty);
        Assert.DoesNotContain("This prompt", coordinator.Current?.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_SnapshotsExplorerAttachmentsIntoProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
        var file = Path.Combine(root, "one.txt");
        await File.WriteAllTextAsync(file, "attachment", TestContext.Current.CancellationToken);
        try
        {
            using var coordinator = new TaskCoordinator(new DemoAgentBackend(TimeSpan.FromSeconds(1)));
            var attachments = new[] { file, folder };

            await coordinator.StartAsync(
                "携带 Explorer 上下文",
                root,
                "Demo Agent",
                "高",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken,
                attachments);

            Assert.Equal(attachments, coordinator.Current?.Attachments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BeginNewTask_ClearsSettledProjection()
    {
        var backend = new DemoAgentBackend(TimeSpan.Zero);
        using var coordinator = new TaskCoordinator(backend);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetWasPublished = false;
        coordinator.ProjectionChanged += projection =>
        {
            if (projection?.Status == RunStatus.Completed)
            {
                settled.TrySetResult();
            }
            else if (projection is null)
            {
                resetWasPublished = true;
            }
        };

        await coordinator.StartAsync(
            "完成后开始一个新任务",
            Environment.CurrentDirectory,
            "Demo Agent",
            "高",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        coordinator.BeginNewTask();

        Assert.Null(coordinator.Current);
        Assert.True(resetWasPublished);
    }

    [Fact]
    public async Task BeginNewTask_LeavesActiveProjectionRunningInBackground()
    {
        var backend = new DemoAgentBackend(TimeSpan.FromSeconds(1));
        using var coordinator = new TaskCoordinator(backend);

        await coordinator.StartAsync(
            "仍在运行的任务",
            Environment.CurrentDirectory,
            "Demo Agent",
            "高",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);

        var activeRunId = Assert.IsType<TaskProjection>(coordinator.Current).RunId;

        coordinator.BeginNewTask();

        Assert.Null(coordinator.Current);
        Assert.Equal(activeRunId, Assert.Single(coordinator.ActiveTasks).RunId);
    }

    [Fact]
    public async Task ConcurrentTasks_RunInDifferentWorkspacesAndKeepSelectionIndependent()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"pi-companion-concurrency-{Guid.NewGuid():N}")).FullName;
        var firstWorkspace = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var secondWorkspace = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        var thirdWorkspace = Directory.CreateDirectory(Path.Combine(root, "third")).FullName;
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);
        try
        {
            await coordinator.StartAsync(
                "first",
                firstWorkspace,
                "Demo Agent",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken);
            var first = Assert.IsType<TaskProjection>(coordinator.Current);

            coordinator.BeginNewTask();
            await coordinator.StartAsync(
                "second",
                secondWorkspace,
                "Demo Agent",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken);
            var second = Assert.IsType<TaskProjection>(coordinator.Current);

            coordinator.BeginNewTask();
            await coordinator.StartAsync(
                "third",
                thirdWorkspace,
                "Demo Agent",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken);
            var third = Assert.IsType<TaskProjection>(coordinator.Current);

            Assert.Equal(2, backend.Requests.Count);
            Assert.Equal(RunStatus.Queued, third.Status);
            Assert.Equal(3, coordinator.ActiveTasks.Count);

            coordinator.SelectTask(first.TaskId);
            var focusedUpdates = new List<Guid>();
            var backgroundUpdates = new List<(Guid RunId, RunStatus Status)>();
            coordinator.ProjectionChanged += projection =>
            {
                if (projection is not null) focusedUpdates.Add(projection.RunId);
            };
            coordinator.TaskChanged += projection =>
                backgroundUpdates.Add((projection.RunId, projection.Status));
            backend.SettleRun(second.RunId);

            Assert.Equal(3, backend.Requests.Count);
            Assert.Equal(first.TaskId, coordinator.Current?.TaskId);
            Assert.Equal(RunStatus.Running, coordinator.Current?.Status);
            Assert.Equal(RunStatus.Running, third.Status);
            Assert.DoesNotContain(second.RunId, focusedUpdates);
            Assert.Contains((second.RunId, RunStatus.Completed), backgroundUpdates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentTasks_SerializeTheSameWorkspaceAndCanCancelQueuedRun()
    {
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);
        await coordinator.StartAsync(
            "first",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);

        coordinator.BeginNewTask();
        await coordinator.StartAsync(
            "second",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);

        Assert.Single(backend.Requests);
        Assert.Equal(RunStatus.Queued, coordinator.Current?.Status);

        await coordinator.AbortAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.Interrupted, coordinator.Current?.Status);
        Assert.Single(backend.Requests);
        Assert.Single(coordinator.ActiveTasks);
    }

    [Fact]
    public async Task StartAsync_RejectsChangingDirectoryForExistingTask()
    {
        var backend = new DemoAgentBackend(TimeSpan.Zero);
        using var coordinator = new TaskCoordinator(backend);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ProjectionChanged += projection =>
        {
            if (projection?.Status == RunStatus.Completed)
            {
                settled.TrySetResult();
            }
        };

        await coordinator.StartAsync(
            "First run",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var differentDirectory = Path.Combine(Path.GetTempPath(), "PiCompanionDifferentDirectory");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(
            "Second run",
            differentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken));

        Assert.Contains("不能更改工作目录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_AppendsFollowUpRunToCurrentConversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var coordinator = new TaskCoordinator(new DemoAgentBackend(TimeSpan.Zero), store);
            var firstSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedRuns = new HashSet<Guid>();
            coordinator.ProjectionChanged += projection =>
            {
                if (projection?.Status != RunStatus.Completed || !completedRuns.Add(projection.RunId))
                {
                    return;
                }

                if (completedRuns.Count == 1)
                {
                    firstSettled.TrySetResult();
                }
                else if (completedRuns.Count == 2)
                {
                    secondSettled.TrySetResult();
                }
            };

            await coordinator.StartAsync(
                "First prompt",
                root,
                "model-one",
                "low",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken);
            await firstSettled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            await coordinator.StartAsync(
                "Second prompt",
                root,
                "model-two",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken);
            await secondSettled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(2, coordinator.CurrentConversation.Count);
            Assert.Equal(
                ["First prompt", "Second prompt"],
                coordinator.CurrentConversation.Select(run => run.Prompt));
            Assert.Equal("Second prompt", coordinator.Current?.Prompt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AiMetadata_UpdatesTitleAndPersistsRunSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var generator = new StubTaskMetadataGenerator("AI generated title", "AI generated run summary");
            using var coordinator = new TaskCoordinator(
                new DemoAgentBackend(TimeSpan.Zero),
                store,
                metadataGenerator: generator,
                taskSettingsResolver: () => new TaskSettings(
                    true,
                    "provider/title-model",
                    true,
                    "provider/summary-model",
                    AiMetadataModel: "provider/metadata-model"));
            var metadataApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.ProjectionChanged += projection =>
            {
                if (projection is
                    {
                        Title: "AI generated title",
                        Summary: "AI generated run summary",
                    })
                {
                    metadataApplied.TrySetResult();
                }
            };

            await coordinator.StartAsync(
                "Explain the repository structure",
                root,
                "run-model",
                "high",
                DemoRunMode.Success,
                TestContext.Current.CancellationToken);
            await metadataApplied.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal("provider/metadata-model", generator.TitleModel);
            Assert.Equal("provider/metadata-model", generator.SummaryModel);
            Assert.Equal("AI generated title", coordinator.Current?.Title);
            Assert.Equal("AI generated run summary", coordinator.Current?.Summary);
            Assert.Equal(AiSummaryStatus.Available, coordinator.Current?.AiSummaryStatus);
            var restored = Assert.Single(store.RestoreTaskRuns(coordinator.Current!.TaskId));
            Assert.Equal("AI generated title", restored.Title);
            Assert.Equal("AI generated run summary", restored.Summary);
            Assert.Equal(AiSummaryStatus.Available, restored.AiSummaryStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AiSummaryStatus_IsGeneratingOnlyWhileTheGeneratorIsRunning()
    {
        var generator = new ControlledSummaryMetadataGenerator();
        using var coordinator = new TaskCoordinator(
            new DemoAgentBackend(TimeSpan.Zero),
            metadataGenerator: generator,
            taskSettingsResolver: () => new TaskSettings(
                false,
                string.Empty,
                true,
                "provider/summary-model"));
        var generating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ProjectionChanged += projection =>
        {
            if (projection is null)
            {
                return;
            }

            if (projection.AiSummaryStatus == AiSummaryStatus.Generating)
            {
                generating.TrySetResult();
            }
            else if (projection.AiSummaryStatus == AiSummaryStatus.Failed)
            {
                failed.TrySetResult();
            }
        };

        await coordinator.StartAsync(
            "Summarize this run",
            Path.GetTempPath(),
            "run-model",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        await generating.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(AiSummaryStatus.Generating, coordinator.Current?.AiSummaryStatus);
        Assert.Empty(coordinator.Current?.Summary ?? string.Empty);

        generator.CompleteSummary(null);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(AiSummaryStatus.Failed, coordinator.Current?.AiSummaryStatus);
        Assert.Empty(coordinator.Current?.Summary ?? string.Empty);
    }

    [Fact]
    public void SelectTask_RestoresRequestedPersistedTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var firstTaskId = Guid.NewGuid();
            var firstRunId = Guid.NewGuid();
            var firstFollowUpRunId = Guid.NewGuid();
            var secondTaskId = Guid.NewGuid();
            var secondRunId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(firstTaskId, firstRunId, "First", root, "Pi", "high"), "First");
            store.AppendRunEvent(SettledEvent(firstTaskId, firstRunId, DateTimeOffset.UtcNow.AddMinutes(-1)));
            store.CreateRun(new TaskProjection(firstTaskId, firstFollowUpRunId, "First", root, "Pi", "high"), "Follow up");
            store.AppendRunEvent(SettledEvent(firstTaskId, firstFollowUpRunId, DateTimeOffset.UtcNow.AddSeconds(-30)));
            store.CreateRun(new TaskProjection(secondTaskId, secondRunId, "Second", root, "Pi", "high"), "Second");
            store.AppendRunEvent(SettledEvent(secondTaskId, secondRunId, DateTimeOffset.UtcNow));

            using var coordinator = new TaskCoordinator(new DemoAgentBackend(TimeSpan.Zero), store);
            var recentBeforeSelection = coordinator.RecentTasks.ToArray();
            var selected = coordinator.SelectTask(firstTaskId);
            var recentAfterSelection = coordinator.RecentTasks.ToArray();

            Assert.Equal(firstTaskId, selected.TaskId);
            Assert.Equal(firstFollowUpRunId, selected.RunId);
            Assert.Equal(firstTaskId, coordinator.Current?.TaskId);
            Assert.Equal([firstRunId, firstFollowUpRunId], coordinator.CurrentConversation.Select(run => run.RunId));
            Assert.Equal([secondTaskId, firstTaskId], recentAfterSelection.Select(task => task.TaskId));
            Assert.Equal(
                recentBeforeSelection.Select(task => task.UpdatedAt),
                recentAfterSelection.Select(task => task.UpdatedAt));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TaskManagement_RenamesCurrentAndClearsItWhenMovedToRecycleBin()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(taskId, runId, "Original", root, "Pi", "high"), "Original");
            store.AppendRunEvent(SettledEvent(taskId, runId, DateTimeOffset.UtcNow));
            using var coordinator = new TaskCoordinator(new DemoAgentBackend(TimeSpan.Zero), store);

            coordinator.RenameTask(taskId, "Renamed task");
            Assert.Equal("Renamed task", coordinator.Current?.Title);
            Assert.Equal("Renamed task", Assert.Single(coordinator.HistoryTasks).Title);

            coordinator.MoveTaskToRecycleBin(taskId);
            Assert.Null(coordinator.Current);
            Assert.Empty(coordinator.HistoryTasks);
            Assert.Equal(taskId, Assert.Single(coordinator.RecycleBinTasks).TaskId);

            coordinator.RestoreTaskFromRecycleBin(taskId);
            Assert.Empty(coordinator.RecycleBinTasks);
            Assert.Equal(taskId, Assert.Single(coordinator.HistoryTasks).TaskId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalMessageQueue_DispatchesThroughTheSelectedPiChannelAndRemovesOnSuccess()
    {
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);

        var steer = coordinator.QueueLocalMessage("  adjust the current approach  ");
        var followUp = coordinator.QueueLocalMessage("run the tests afterwards");
        coordinator.UpdateLocalMessage(followUp.Id, "run all tests afterwards");

        await coordinator.DispatchLocalMessageAsync(
            steer.Id,
            "steer",
            TestContext.Current.CancellationToken);
        await coordinator.DispatchLocalMessageAsync(
            followUp.Id,
            "follow-up",
            TestContext.Current.CancellationToken);

        Assert.Equal("adjust the current approach", backend.LastSteerMessage);
        Assert.Equal("run all tests afterwards", backend.LastFollowUpMessage);
        Assert.Empty(coordinator.Current!.LocalQueuedMessages);
    }

    [Fact]
    public async Task LocalMessageQueue_PersistsAcrossCoordinatorRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            Guid queuedId;
            using (var first = new TaskCoordinator(new RecordingBackend(), store))
            {
                await first.StartAsync(
                    "Initial prompt",
                    root,
                    "Demo Agent",
                    "high",
                    DemoRunMode.Success,
                    TestContext.Current.CancellationToken);
                queuedId = first.QueueLocalMessage("keep this locally").Id;
            }

            using var restored = new TaskCoordinator(new RecordingBackend(), store);
            var queued = Assert.Single(restored.Current!.LocalQueuedMessages);
            Assert.Equal(queuedId, queued.Id);
            Assert.Equal("keep this locally", queued.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalMessageQueue_KeepsTheItemWhenPiDispatchFails()
    {
        var backend = new RecordingBackend { SteerException = new InvalidOperationException("Pi unavailable") };
        using var coordinator = new TaskCoordinator(backend);
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        var queued = coordinator.QueueLocalMessage("do not lose this");

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DispatchLocalMessageAsync(
            queued.Id,
            "steer",
            TestContext.Current.CancellationToken));

        Assert.Equal(queued.Id, Assert.Single(coordinator.Current!.LocalQueuedMessages).Id);
    }

    [Fact]
    public async Task LocalMessageQueue_CanSendARetainedItemAsANewRunAfterSettlement()
    {
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        var queued = coordinator.QueueLocalMessage("continue in a new run");
        backend.SettleCurrentRun();

        await coordinator.DispatchLocalMessageAsync(
            queued.Id,
            "new-run",
            TestContext.Current.CancellationToken);

        Assert.Equal("continue in a new run", backend.LastRequest?.Prompt);
        Assert.Equal(2, coordinator.CurrentConversation.Count);
        Assert.Empty(coordinator.Current!.LocalQueuedMessages);
    }

    [Fact]
    public async Task LocalMessageQueue_AutomaticallyStartsTheFirstItemAfterSuccessfulCompletion()
    {
        var backend = new RecordingBackend();
        var taskSettings = PiCompanionSettings.Default.Tasks with
        {
            AutoStartLocalQueueEnabled = true,
            AutoStartLocalQueueDelaySeconds = 0,
        };
        using var coordinator = new TaskCoordinator(
            backend,
            taskSettingsResolver: () => taskSettings);
        var nextRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ProjectionChanged += projection =>
        {
            if (projection?.Prompt == "first queued task" && projection.Status == RunStatus.Running)
            {
                nextRunStarted.TrySetResult();
            }
        };
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        coordinator.QueueLocalMessage("first queued task");
        coordinator.QueueLocalMessage("second queued task");

        backend.SettleCurrentRun();
        await nextRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal("first queued task", backend.LastRequest?.Prompt);
        Assert.Equal("second queued task", Assert.Single(coordinator.Current!.LocalQueuedMessages).Message);
    }

    [Fact]
    public async Task LocalMessageQueue_CancelStopsTheCurrentCountdownWithoutRemovingTheItem()
    {
        var backend = new RecordingBackend();
        var taskSettings = PiCompanionSettings.Default.Tasks with
        {
            AutoStartLocalQueueEnabled = true,
            AutoStartLocalQueueDelaySeconds = 15,
        };
        using var coordinator = new TaskCoordinator(
            backend,
            taskSettingsResolver: () => taskSettings);
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        var queued = coordinator.QueueLocalMessage("keep queued");
        backend.SettleCurrentRun();

        Assert.Equal(queued.Id, coordinator.Current?.LocalQueueAutoStartMessageId);
        coordinator.CancelLocalQueueAutoStart();

        Assert.Null(coordinator.Current?.LocalQueueAutoStartMessageId);
        Assert.Equal(queued.Id, Assert.Single(coordinator.Current!.LocalQueuedMessages).Id);
        Assert.Equal("Initial prompt", backend.LastRequest?.Prompt);
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Interrupted)]
    public async Task LocalMessageQueue_DoesNotAutoStartAfterUnsuccessfulRun(RunStatus status)
    {
        var backend = new RecordingBackend();
        var taskSettings = PiCompanionSettings.Default.Tasks with
        {
            AutoStartLocalQueueEnabled = true,
            AutoStartLocalQueueDelaySeconds = 0,
        };
        using var coordinator = new TaskCoordinator(
            backend,
            taskSettingsResolver: () => taskSettings);
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        var queued = coordinator.QueueLocalMessage("keep queued");

        backend.SettleCurrentRun(status);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Single(backend.Requests);
        Assert.Null(coordinator.Current?.LocalQueueAutoStartMessageId);
        Assert.Equal(queued.Id, Assert.Single(coordinator.Current!.LocalQueuedMessages).Id);
    }

    [Fact]
    public async Task LocalMessageQueue_AutoStartContinuesInQueueOrderWhenRunsFinishImmediately()
    {
        var backend = new RecordingBackend();
        var taskSettings = PiCompanionSettings.Default.Tasks with
        {
            AutoStartLocalQueueEnabled = true,
            AutoStartLocalQueueDelaySeconds = 0,
        };
        using var coordinator = new TaskCoordinator(
            backend,
            taskSettingsResolver: () => taskSettings);
        var queueDrained = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ProjectionChanged += projection =>
        {
            if (projection?.Prompt == "second queued task" &&
                projection.LocalQueuedMessages.Count == 0)
            {
                queueDrained.TrySetResult();
            }
        };
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        coordinator.QueueLocalMessage("first queued task");
        coordinator.QueueLocalMessage("second queued task");
        backend.SettleOnStart = true;

        backend.SettleCurrentRun();
        await queueDrained.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Initial prompt", "first queued task", "second queued task"],
            backend.Requests.Select(request => request.Prompt));
        Assert.Empty(coordinator.Current!.LocalQueuedMessages);
        Assert.Null(coordinator.Current.LocalQueueAutoStartMessageId);
    }

    [Fact]
    public async Task LocalMessageQueue_ItemsWithAttachmentsCannotUseSteerOrFollowUp()
    {
        var backend = new RecordingBackend();
        using var coordinator = new TaskCoordinator(backend);
        await coordinator.StartAsync(
            "Initial prompt",
            Environment.CurrentDirectory,
            "Demo Agent",
            "high",
            DemoRunMode.Success,
            TestContext.Current.CancellationToken);
        var queued = coordinator.QueueLocalMessage("future task", [Environment.CurrentDirectory]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.DispatchLocalMessageAsync(
                queued.Id,
                "steer",
                TestContext.Current.CancellationToken));

        Assert.Contains("只能作为新一轮", exception.Message, StringComparison.Ordinal);
        Assert.Equal(queued.Id, Assert.Single(coordinator.Current!.LocalQueuedMessages).Id);
    }

    private static CompanionRunEvent SettledEvent(Guid taskId, Guid runId, DateTimeOffset timestamp) => new(
        Guid.NewGuid(),
        taskId,
        runId,
        1,
        CompanionRunEventKind.RunSettled,
        timestamp,
        RunStatus.Completed,
        new Dictionary<string, string>
        {
            ["activity"] = "Completed",
            ["summary"] = "Completed",
        });

    private sealed class StubTaskMetadataGenerator(string title, string summary) : ITaskMetadataGenerator
    {
        public string? TitleModel { get; private set; }

        public string? SummaryModel { get; private set; }

        public Task<string?> GenerateTitleAsync(
            string prompt,
            string model,
            CancellationToken cancellationToken = default)
        {
            TitleModel = model;
            return Task.FromResult<string?>(title);
        }

        public Task<string?> GenerateRunSummaryAsync(
            RunSummarySource source,
            string model,
            CancellationToken cancellationToken = default)
        {
            SummaryModel = model;
            return Task.FromResult<string?>(summary);
        }

        public Task<string?> GenerateCommitMessageAsync(
            CommitMessageSource source,
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class ControlledSummaryMetadataGenerator : ITaskMetadataGenerator
    {
        private readonly TaskCompletionSource<string?> _summary = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteSummary(string? summary) => _summary.TrySetResult(summary);

        public Task<string?> GenerateTitleAsync(
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> GenerateRunSummaryAsync(
            RunSummarySource source,
            string model,
            CancellationToken cancellationToken = default) =>
            _summary.Task.WaitAsync(cancellationToken);

        public Task<string?> GenerateCommitMessageAsync(
            CommitMessageSource source,
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private static AgentSessionStatistics CreateSessionStatistics() =>
        new(
            "session-cache",
            null,
            2,
            4,
            3,
            3,
            6,
            1200,
            80,
            400,
            0,
            1680,
            0,
            new AgentContextUsage(27200, 272000, 10));

    private sealed class RecordingBackend : IAgentBackend, IAgentBackendPrewarmer,
        IAgentBackendWorkspaceReleaser, IAgentBackendResourceInvalidator, IAgentSessionStatisticsProvider
    {
        public List<AgentRunRequest> Requests { get; } = [];

        public AgentRunRequest? LastRequest { get; private set; }

        public AgentPreparationRequest? LastPreparation { get; private set; }

        public List<string> ReleasedWorkspaces { get; } = [];

        public List<string?> InvalidatedResourceWorkspaces { get; } = [];

        public string? LastSteerMessage { get; private set; }

        public string? LastFollowUpMessage { get; private set; }

        public Exception? SteerException { get; init; }

        public bool SettleOnStart { get; set; }

        public AgentSessionStatistics? SessionStatistics { get; init; }

        public event Action<CompanionRunEvent>? EventReceived;

        public event Action<AgentToolExecution>? ToolExecutionCompleted;

        public void PublishToolExecution(AgentToolExecution execution) =>
            ToolExecutionCompleted?.Invoke(execution);

        public Task StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            Requests.Add(request);
            EventReceived?.Invoke(new CompanionRunEvent(
                Guid.NewGuid(),
                request.TaskId,
                request.RunId,
                request.InitialSequence + 1,
                CompanionRunEventKind.RunStarted,
                DateTimeOffset.UtcNow,
                RunStatus.Running,
                new Dictionary<string, string>
                {
                    ["activity"] = "Started",
                    ["summary"] = "Running",
                }));
            if (SettleOnStart)
            {
                SettleCurrentRun();
            }
            return Task.CompletedTask;
        }

        public Task PrepareAsync(
            AgentPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastPreparation = request;
            return Task.CompletedTask;
        }

        public void ReleaseWorkspace(string workingDirectory) =>
            ReleasedWorkspaces.Add(Path.GetFullPath(workingDirectory));

        public void InvalidateIdleResources(string? workingDirectory = null) =>
            InvalidatedResourceWorkspaces.Add(
                string.IsNullOrWhiteSpace(workingDirectory)
                    ? null
                    : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)));

        public void SettleCurrentRun(RunStatus status = RunStatus.Completed)
        {
            var request = Assert.IsType<AgentRunRequest>(LastRequest);
            SettleRun(request.RunId, status);
        }

        public void SettleRun(Guid runId, RunStatus status = RunStatus.Completed)
        {
            var request = Assert.Single(Requests, candidate => candidate.RunId == runId);
            EventReceived?.Invoke(new CompanionRunEvent(
                Guid.NewGuid(),
                request.TaskId,
                request.RunId,
                request.InitialSequence + 2,
                status switch
                {
                    RunStatus.Failed => CompanionRunEventKind.RunFailed,
                    RunStatus.Interrupted => CompanionRunEventKind.RunInterrupted,
                    _ => CompanionRunEventKind.RunSettled,
                },
                DateTimeOffset.UtcNow,
                status,
                new Dictionary<string, string>
                {
                    ["activity"] = "Completed",
                    ["summary"] = "Completed",
                }));
        }

        public Task SteerAsync(Guid runId, string message, CancellationToken cancellationToken = default)
        {
            if (SteerException is not null)
            {
                return Task.FromException(SteerException);
            }

            LastSteerMessage = message;
            return Task.CompletedTask;
        }

        public Task FollowUpAsync(Guid runId, string message, CancellationToken cancellationToken = default)
        {
            LastFollowUpMessage = message;
            return Task.CompletedTask;
        }

        public Task ResolveInteractionAsync(
            Guid runId,
            InteractionResolution resolution,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AbortAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AbortRetryAsync(Guid runId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AgentSessionStatistics?> GetSessionStatisticsAsync(
            AgentSessionStatisticsRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionStatistics);
    }
}
