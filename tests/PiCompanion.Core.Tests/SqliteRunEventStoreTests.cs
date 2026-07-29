using Microsoft.Data.Sqlite;
using PiCompanion.Application.Persistence;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Core.Tests;

public sealed class SqliteRunEventStoreTests
{
    [Fact]
    public void Workspaces_PersistWithoutTasksAndSurviveTaskDeletion()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var workingDirectory = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var store = new SqliteRunEventStore(databasePath);

            var created = store.CreateWorkspace(workingDirectory);
            Assert.Equal(0, created.TaskCount);
            Assert.Equal(created.Id, store.CreateWorkspace(workingDirectory).Id);

            var reopened = new SqliteRunEventStore(databasePath);
            Assert.Equal(created.Id, Assert.Single(reopened.GetWorkspaces()).Id);

            var taskId = Guid.NewGuid();
            reopened.CreateRun(
                new TaskProjection(taskId, Guid.NewGuid(), "Workspace task", workingDirectory, "Pi", "high"),
                "Use the workspace");
            Assert.Equal(1, Assert.Single(reopened.GetWorkspaces()).TaskCount);

            reopened.MoveTaskToRecycleBin(taskId);
            reopened.DeleteTaskPermanently(taskId);
            var emptyWorkspace = Assert.Single(reopened.GetWorkspaces());
            Assert.Equal(created.Id, emptyWorkspace.Id);
            Assert.Equal(0, emptyWorkspace.TaskCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkspacePresentation_PersistsAndFlowsIntoTaskHistory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var workingDirectory = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var store = new SqliteRunEventStore(databasePath);
            var workspace = store.CreateWorkspace(workingDirectory);

            var updated = store.UpdateWorkspacePresentation(
                workspace.Id,
                "  Companion Core  ",
                "code",
                "violet");
            Assert.Equal("Companion Core", updated.Name);
            Assert.Equal("Companion Core", updated.DisplayName);
            Assert.Equal("code", updated.IconKey);
            Assert.Equal("violet", updated.ColorKey);

            var taskId = Guid.NewGuid();
            store.CreateRun(
                new TaskProjection(taskId, Guid.NewGuid(), "Workspace task", workingDirectory, "Pi", "high"),
                "Use the workspace");

            var reopened = new SqliteRunEventStore(databasePath);
            var persisted = Assert.Single(reopened.GetWorkspaces());
            Assert.Equal("Companion Core", persisted.Name);
            Assert.Equal("code", persisted.IconKey);
            Assert.Equal("violet", persisted.ColorKey);
            var history = Assert.Single(reopened.QueryTasks(new TaskHistoryQuery(Search: "Companion Core")));
            Assert.Equal(workspace.Id, history.WorkspaceId);
            Assert.Equal(taskId, history.TaskId);

            var reset = reopened.UpdateWorkspacePresentation(workspace.Id, null, "folder", "blue");
            Assert.Null(reset.DisplayName);
            Assert.Equal("workspace", reset.Name);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HiddenWorkspace_HidesItsTasksAndAddingTheDirectoryRestoresThem()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var workingDirectory = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var store = new SqliteRunEventStore(databasePath);
            var workspace = store.CreateWorkspace(workingDirectory);
            var taskId = Guid.NewGuid();
            store.CreateRun(
                new TaskProjection(taskId, Guid.NewGuid(), "Workspace task", workingDirectory, "Pi", "high"),
                "Use the workspace");

            store.HideWorkspace(workspace.Id);

            Assert.Empty(store.GetWorkspaces());
            Assert.Empty(store.QueryTasks(new TaskHistoryQuery()));

            var restored = store.CreateWorkspace(workingDirectory);

            Assert.Equal(workspace.Id, restored.Id);
            Assert.Equal(taskId, Assert.Single(store.QueryTasks(new TaskHistoryQuery())).TaskId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Initialize_BackfillsIndependentWorkspacesForExistingTasks()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var workingDirectory = Directory.CreateDirectory(Path.Combine(root, "legacy-workspace")).FullName;
            var taskId = Guid.NewGuid();
            var store = new SqliteRunEventStore(databasePath);
            store.CreateRun(
                new TaskProjection(taskId, Guid.NewGuid(), "Legacy task", workingDirectory, "Pi", "high"),
                "Legacy");

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE tasks SET workspace_id = NULL WHERE id = $taskId;
                    DELETE FROM workspaces;
                    DELETE FROM schema_migrations WHERE version = 13;
                    """;
                command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
                command.ExecuteNonQuery();
            }

            var migrated = new SqliteRunEventStore(databasePath);
            var workspace = Assert.Single(migrated.GetWorkspaces());
            Assert.Equal(Path.GetFullPath(workingDirectory), workspace.WorkingDirectory);
            Assert.Equal(1, workspace.TaskCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GeneralChatScopeAndPublishedArtifactsPersistAcrossRestart()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var storagePath = Path.Combine(root, "published.csv");
            File.WriteAllText(storagePath, "value\n42\n");
            store.CreateRun(
                new TaskProjection(
                    taskId,
                    runId,
                    "General",
                    root,
                    "provider/model",
                    "high",
                    scopeKind: TaskScopeKind.GeneralChat),
                "create a file");
            var artifact = new TaskArtifact(
                Guid.NewGuid(),
                taskId,
                runId,
                "published.csv",
                storagePath,
                "text/csv",
                new FileInfo(storagePath).Length,
                "abc123",
                DateTimeOffset.UtcNow);
            store.UpsertTaskArtifact(artifact);

            var reopened = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var restored = Assert.Single(reopened.RestoreTaskRuns(taskId));
            Assert.Equal(TaskScopeKind.GeneralChat, restored.ScopeKind);
            Assert.Equal(artifact, Assert.Single(restored.Artifacts));
            Assert.Equal(TaskScopeKind.GeneralChat, Assert.Single(reopened.GetRecentTasks()).ScopeKind);
            Assert.Equal(artifact, reopened.GetTaskArtifact(artifact.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionStatisticsCache_PersistsForMatchingRunSequenceAndCascadesWithTask()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var store = new SqliteRunEventStore(databasePath);
            store.CreateRun(new TaskProjection(taskId, runId, "任务", root, "provider/model", "high"), "任务");
            var statistics = CreateSessionStatistics();
            var updatedAt = new DateTimeOffset(2026, 7, 23, 12, 34, 56, TimeSpan.Zero);

            store.UpsertSessionStatisticsCache(new SessionStatisticsCacheEntry(
                taskId,
                runId,
                0,
                statistics,
                updatedAt));

            var reopenedStore = new SqliteRunEventStore(databasePath);
            var restored = reopenedStore.GetSessionStatisticsCache(taskId, runId, 0);
            Assert.NotNull(restored);
            Assert.Equal(statistics, restored.Statistics);
            Assert.Equal(updatedAt, restored.UpdatedAt);
            Assert.Null(reopenedStore.GetSessionStatisticsCache(taskId, runId, 1));

            reopenedStore.MoveTaskToRecycleBin(taskId);
            reopenedStore.DeleteTaskPermanently(taskId);
            Assert.Null(reopenedStore.GetSessionStatisticsCache(taskId, runId, 0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateTaskExecutionDefaults_PersistsPreferenceWithoutChangingRunModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var store = new SqliteRunEventStore(databasePath);
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(taskId, runId, "任务", root, "provider/original", "low"), "任务");
            var activityAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE tasks SET updated_at = $updatedAt WHERE id = $taskId;";
                command.Parameters.AddWithValue("$updatedAt", activityAt.ToString("O"));
                command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
                command.ExecuteNonQuery();
            }

            store.UpdateTaskExecutionDefaults(taskId, "provider/next", "high");

            var restored = Assert.Single(store.RestoreTaskRuns(taskId));
            Assert.Equal("provider/original", restored.Model);
            Assert.Equal("low", restored.ThinkingLevel);
            Assert.Equal("provider/next", restored.PreferredModel);
            Assert.Equal("high", restored.PreferredThinkingLevel);
            Assert.Equal(activityAt, Assert.Single(store.GetRecentTasks()).UpdatedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreLatestProjection_ReplaysEventsAndIgnoresDuplicateSequence()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var projection = new TaskProjection(
                taskId,
                runId,
                "检查工程",
                root,
                "Pi 默认模型",
                "高",
                [Path.Combine(root, "README.md")],
                permissionMode: "standard");
            store.CreateRun(projection, "检查工程");
            store.AppendRunEvent(CreateEvent(taskId, runId, 1, CompanionRunEventKind.RunQueued, RunStatus.Queued, "排队"));
            store.AppendRunEvent(CreateEvent(taskId, runId, 2, CompanionRunEventKind.AssistantMessageCompleted, RunStatus.Running, "最终回答", "答案"));
            store.AppendRunEvent(CreateEvent(taskId, runId, 2, CompanionRunEventKind.RunFailed, RunStatus.Failed, "重复"));
            store.AppendRunEvent(CreateEvent(taskId, runId, 3, CompanionRunEventKind.RunSettled, RunStatus.Completed, "完成"));

            var restored = store.RestoreLatestProjection();

            Assert.NotNull(restored);
            Assert.Equal(taskId, restored.TaskId);
            Assert.Equal(runId, restored.RunId);
            Assert.Equal(3, restored.LastSequence);
            Assert.Equal(RunStatus.Completed, restored.Status);
            Assert.Equal("答案", restored.FinalAnswer);
            Assert.Empty(restored.Summary);
            Assert.Equal("完成", restored.RuntimeStatusDetail);
            Assert.Equal("检查工程", restored.Prompt);
            Assert.Equal("检查工程", restored.Transcript[0].Content);
            Assert.Single(restored.Attachments);
            Assert.Equal("standard", restored.PermissionMode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PurgeExpiredTasks_RemovesOldHistoryAndRecycleBinButKeepsMostRecentTask()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var store = new SqliteRunEventStore(databasePath);
            var oldTaskId = Guid.NewGuid();
            var recentTaskId = Guid.NewGuid();
            var recycledTaskId = Guid.NewGuid();

            CreateCompletedTask(store, oldTaskId, Guid.NewGuid(), root, "Old history");
            CreateCompletedTask(store, recentTaskId, Guid.NewGuid(), root, "Recent history");
            CreateCompletedTask(store, recycledTaskId, Guid.NewGuid(), root, "Old recycled");
            store.MoveTaskToRecycleBin(recycledTaskId);

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE tasks SET updated_at = '2000-01-01T00:00:00.0000000Z' WHERE id = $oldTaskId;
                    UPDATE tasks SET deleted_at = '2000-01-01T00:00:00.0000000Z', updated_at = '2000-01-01T00:00:00.0000000Z' WHERE id = $recycledTaskId;
                    UPDATE recycle_bin SET deleted_at = '2000-01-01T00:00:00.0000000Z' WHERE task_id = $recycledTaskId;
                    """;
                command.Parameters.AddWithValue("$oldTaskId", oldTaskId.ToString("D"));
                command.Parameters.AddWithValue("$recycledTaskId", recycledTaskId.ToString("D"));
                command.ExecuteNonQuery();
            }

            var cutoff = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            store.PurgeExpiredTasks(cutoff, cutoff);

            Assert.Null(store.RestoreProjection(oldTaskId));
            Assert.NotNull(store.RestoreProjection(recentTaskId));
            Assert.DoesNotContain(store.QueryTasks(new TaskHistoryQuery(IncludeDeleted: true)), task => task.TaskId == recycledTaskId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreLatestProjection_MarksActiveRunInterruptedOnce()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(taskId, runId, "任务", root, "Pi 默认模型", "中"), "任务");
            store.AppendRunEvent(CreateEvent(taskId, runId, 1, CompanionRunEventKind.RunStarted, RunStatus.Running, "运行"));

            var first = store.RestoreLatestProjection();
            var second = store.RestoreLatestProjection();

            Assert.NotNull(first);
            Assert.Equal(RunStatus.Interrupted, first.Status);
            Assert.Equal(2, first.LastSequence);
            Assert.NotNull(second);
            Assert.Equal(RunStatus.Interrupted, second.Status);
            Assert.Equal(2, second.LastSequence);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreLatestProjection_MarksEveryActiveRunInterrupted()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var firstTaskId = Guid.NewGuid();
            var secondTaskId = Guid.NewGuid();
            var firstRunId = Guid.NewGuid();
            var secondRunId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(firstTaskId, firstRunId, "First", root, "Pi", "medium"), "First");
            store.AppendRunEvent(CreateEvent(firstTaskId, firstRunId, 1, CompanionRunEventKind.RunStarted, RunStatus.Running, "Running"));
            store.CreateRun(new TaskProjection(secondTaskId, secondRunId, "Second", root, "Pi", "medium"), "Second");
            store.AppendRunEvent(CreateEvent(secondTaskId, secondRunId, 1, CompanionRunEventKind.RunQueued, RunStatus.Queued, "Queued"));

            _ = store.RestoreLatestProjection();

            Assert.Equal(RunStatus.Interrupted, store.RestoreProjection(firstTaskId)?.Status);
            Assert.Equal(RunStatus.Interrupted, store.RestoreProjection(secondTaskId)?.Status);
            Assert.Equal(2, store.RestoreProjection(firstTaskId)?.LastSequence);
            Assert.Equal(2, store.RestoreProjection(secondTaskId)?.LastSequence);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreTaskRuns_ReplaysCompleteConversationInRunOrder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var firstRunId = Guid.NewGuid();
            var secondRunId = Guid.NewGuid();
            var firstAttachment = Path.Combine(root, "first-run.txt");
            File.WriteAllText(firstAttachment, "first");

            store.CreateRun(
                new TaskProjection(taskId, firstRunId, "Conversation", root, "model-one", "low", [firstAttachment]),
                "First prompt");
            store.AppendRunEvent(CreateEvent(
                taskId,
                firstRunId,
                1,
                CompanionRunEventKind.AssistantMessageCompleted,
                RunStatus.Running,
                "First answer",
                "First answer"));
            store.AppendRunEvent(CreateEvent(
                taskId,
                firstRunId,
                2,
                CompanionRunEventKind.RunSettled,
                RunStatus.Completed,
                "First completed"));

            store.CreateRun(
                new TaskProjection(taskId, secondRunId, "Conversation", root, "model-two", "high"),
                "Second prompt");
            store.AppendRunEvent(CreateEvent(
                taskId,
                secondRunId,
                1,
                CompanionRunEventKind.AssistantMessageCompleted,
                RunStatus.Running,
                "Second answer",
                "Second answer"));
            store.AppendRunEvent(CreateEvent(
                taskId,
                secondRunId,
                2,
                CompanionRunEventKind.RunSettled,
                RunStatus.Completed,
                "Second completed"));

            var restored = store.RestoreTaskRuns(taskId);

            Assert.Collection(
                restored,
                first =>
                {
                    Assert.Equal(firstRunId, first.RunId);
                    Assert.Equal("First prompt", first.Prompt);
                    Assert.Equal("model-one", first.Model);
                    Assert.Equal("low", first.ThinkingLevel);
                    Assert.Equal("First answer", first.FinalAnswer);
                    Assert.Equal([firstAttachment], first.Attachments);
                },
                second =>
                {
                    Assert.Equal(secondRunId, second.RunId);
                    Assert.Equal("Second prompt", second.Prompt);
                    Assert.Equal("model-two", second.Model);
                    Assert.Equal("high", second.ThinkingLevel);
                    Assert.Equal("Second answer", second.FinalAnswer);
                    Assert.Empty(second.Attachments);
                });
            Assert.Equal(secondRunId, store.RestoreProjection(taskId)?.RunId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetLatestSessionPath_ReturnsPersistedPiSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var sessionPath = Path.Combine(root, "session.jsonl");
            store.CreateRun(new TaskProjection(taskId, runId, "任务", root, "Pi 默认模型", "中"), "任务");
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
                    ["activity"] = "运行",
                    ["summary"] = "运行",
                    ["piSessionId"] = "session-1",
                    ["piSessionPath"] = sessionPath,
                    ["piEntryCursor"] = "entry-42",
                }));

            Assert.Equal(sessionPath, store.GetLatestSessionPath(taskId));
            Assert.Equal("entry-42", store.GetLatestPiEntryCursor(taskId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetRecentTasks_OrdersTasksAndRestoreProjectionLoadsSelectedTask()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var store = new SqliteRunEventStore(databasePath);
            var firstTaskId = Guid.NewGuid();
            var firstRunId = Guid.NewGuid();
            var secondTaskId = Guid.NewGuid();
            var secondRunId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            store.CreateRun(
                new TaskProjection(firstTaskId, firstRunId, "First task", root, "Pi", "high"),
                "First prompt");
            store.AppendRunEvent(CreateEvent(
                firstTaskId,
                firstRunId,
                1,
                CompanionRunEventKind.RunSettled,
                RunStatus.Completed,
                "First summary",
                timestamp: now.AddMinutes(-1)));
            store.UpdateRunSummary(firstTaskId, firstRunId, "First summary");

            store.CreateRun(
                new TaskProjection(secondTaskId, secondRunId, "Second task", root, "Pi", "high"),
                "Second prompt");
            store.AppendRunEvent(CreateEvent(
                secondTaskId,
                secondRunId,
                1,
                CompanionRunEventKind.RunSettled,
                RunStatus.Completed,
                "Second summary",
                timestamp: now));
            store.UpdateRunSummary(secondTaskId, secondRunId, "Second summary");

            var reopenedStore = new SqliteRunEventStore(databasePath);
            var recentTasks = reopenedStore.GetRecentTasks();
            var restoredFirst = reopenedStore.RestoreProjection(firstTaskId);

            Assert.Equal(2, recentTasks.Count);
            Assert.Equal(secondTaskId, recentTasks[0].TaskId);
            Assert.Equal(root, recentTasks[0].WorkingDirectory);
            Assert.Equal("Second summary", recentTasks[0].Summary);
            Assert.Equal(firstTaskId, recentTasks[1].TaskId);
            Assert.NotNull(restoredFirst);
            Assert.Equal(firstTaskId, restoredFirst.TaskId);
            Assert.Equal(firstRunId, restoredFirst.RunId);
            Assert.Equal("First prompt", restoredFirst.Prompt);
            Assert.Equal("First summary", restoredFirst.Summary);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Initialize_RebuildsLegacyOpenedAtOnceFromActualRunActivity()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var activityAt = DateTimeOffset.UtcNow;
            var store = new SqliteRunEventStore(databasePath);
            store.CreateRun(new TaskProjection(taskId, runId, "Activity order", root, "Pi", "high"), "Prompt");
            activityAt = DateTimeOffset.UtcNow;
            store.AppendRunEvent(CreateEvent(
                taskId,
                runId,
                1,
                CompanionRunEventKind.RunSettled,
                RunStatus.Completed,
                "Completed",
                timestamp: activityAt));

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE FROM schema_migrations WHERE version = 7;
                    UPDATE tasks SET updated_at = $legacyOpenedAt WHERE id = $taskId;
                    """;
                command.Parameters.AddWithValue("$legacyOpenedAt", activityAt.AddDays(1).ToString("O"));
                command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
                command.ExecuteNonQuery();
            }

            var migratedStore = new SqliteRunEventStore(databasePath);
            Assert.Equal(activityAt, Assert.Single(migratedStore.GetRecentTasks()).UpdatedAt);

            migratedStore.RenameTask(taskId, "Renamed after migration");
            var renamedAt = Assert.Single(migratedStore.GetRecentTasks()).UpdatedAt;
            var reopenedStore = new SqliteRunEventStore(databasePath);
            Assert.Equal(renamedAt, Assert.Single(reopenedStore.GetRecentTasks()).UpdatedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MoveTaskToRecycleBin_RefillsRecentTasksUpToLimit()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var now = DateTimeOffset.UtcNow;
            var tasks = Enumerable.Range(0, 3)
                .Select(index => (TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), Index: index))
                .ToArray();

            foreach (var task in tasks)
            {
                store.CreateRun(
                    new TaskProjection(task.TaskId, task.RunId, $"Task {task.Index}", root, "Pi", "high"),
                    $"Prompt {task.Index}");
                store.AppendRunEvent(CreateEvent(
                    task.TaskId,
                    task.RunId,
                    1,
                    CompanionRunEventKind.RunSettled,
                    RunStatus.Completed,
                    $"Summary {task.Index}",
                    timestamp: now.AddMinutes(task.Index)));
            }

            store.MoveTaskToRecycleBin(tasks[2].TaskId);

            var recentTasks = store.GetRecentTasks(limit: 2);
            Assert.Equal(2, recentTasks.Count);
            Assert.Equal([tasks[1].TaskId, tasks[0].TaskId], recentTasks.Select(task => task.TaskId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("CompletedUnacknowledged", RunStatus.Completed, CompanionRunEventKind.RunSettled)]
    [InlineData("FailedUnacknowledged", RunStatus.Failed, CompanionRunEventKind.RunFailed)]
    [InlineData("Interrupted", RunStatus.Interrupted, CompanionRunEventKind.RunInterrupted)]
    public void Initialize_MigratesLegacyAcknowledgementWithoutLosingRunOutcome(
        string legacyStatus,
        RunStatus expectedStatus,
        CompanionRunEventKind terminalKind)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var store = new SqliteRunEventStore(databasePath);
            store.CreateRun(new TaskProjection(taskId, runId, "Legacy task", root, "Pi", "high"), "Legacy prompt");
            store.AppendRunEvent(CreateEvent(
                taskId,
                runId,
                1,
                terminalKind,
                expectedStatus,
                "Original result"));

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE run_events SET status = $legacyStatus WHERE run_id = $runId;
                    UPDATE runs
                    SET status = 'Acknowledged', last_event_sequence = 2, settled_at = $acknowledgedAt
                    WHERE id = $runId;
                    UPDATE tasks
                    SET status = 'Acknowledged', summary = 'Result acknowledged', updated_at = $acknowledgedAt
                    WHERE id = $taskId;
                    DELETE FROM schema_migrations WHERE version = 12;
                    INSERT INTO run_events (
                        event_id, task_id, run_id, sequence, kind, timestamp, status, payload_json, source_version)
                    VALUES (
                        $eventId, $taskId, $runId, 2, 'QueueChanged', $acknowledgedAt, 'Acknowledged',
                        '{"activity":"Result acknowledged","summary":"Result acknowledged"}',
                        'pi-companion-command-v1');
                    """;
                command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
                command.Parameters.AddWithValue("$runId", runId.ToString("D"));
                command.Parameters.AddWithValue("$legacyStatus", legacyStatus);
                command.Parameters.AddWithValue("$acknowledgedAt", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
                command.ExecuteNonQuery();
            }

            var migratedStore = new SqliteRunEventStore(databasePath);
            var restored = migratedStore.RestoreProjection(taskId);
            var history = Assert.Single(migratedStore.GetRecentTasks());

            Assert.NotNull(restored);
            Assert.Equal(expectedStatus, restored.Status);
            Assert.Equal(1, restored.LastSequence);
            Assert.Empty(restored.Summary);
            Assert.Equal("Original result", restored.RuntimeStatusDetail);
            Assert.Equal(expectedStatus, history.Status);
            Assert.Empty(history.Summary);

            using var verification = new SqliteConnection($"Data Source={databasePath}");
            verification.Open();
            using var verifyCommand = verification.CreateCommand();
            verifyCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM run_events
                WHERE status IN ('Acknowledged', 'CompletedUnacknowledged', 'FailedUnacknowledged');
                """;
            Assert.Equal(0L, (long)verifyCommand.ExecuteScalar()!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InteractionRequests_AreMaterializedResolvedAndReplayable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(taskId, runId, "授权测试", root, "Pi", "high"), "授权测试");
            store.AppendRunEvent(new CompanionRunEvent(
                Guid.NewGuid(),
                taskId,
                runId,
                1,
                CompanionRunEventKind.ApprovalRequested,
                DateTimeOffset.UtcNow,
                RunStatus.WaitingForApproval,
                new Dictionary<string, string>
                {
                    ["activity"] = "运行 dotnet test",
                    ["summary"] = "等待授权",
                    ["interactionId"] = "permission-1",
                    ["interactionMethod"] = "select",
                    ["interactionOptions"] = "[\"允许一次\",\"拒绝\"]",
                }));
            store.AppendRunEvent(new CompanionRunEvent(
                Guid.NewGuid(),
                taskId,
                runId,
                2,
                CompanionRunEventKind.InteractionResolved,
                DateTimeOffset.UtcNow,
                RunStatus.Running,
                new Dictionary<string, string>
                {
                    ["activity"] = "已授权",
                    ["summary"] = "继续运行",
                    ["interactionId"] = "permission-1",
                    ["approved"] = "true",
                    ["response"] = "允许一次",
                }));

            var interaction = Assert.Single(store.GetInteractionRequests(runId));
            var restored = store.RestoreProjection(taskId);

            Assert.Equal("Approval", interaction.Kind);
            Assert.Equal("Approved", interaction.Status);
            Assert.Equal("允许一次", interaction.Response);
            Assert.NotNull(interaction.ResolvedAt);
            var replayed = Assert.Single(restored!.Transcript, block => block.Kind == TranscriptBlockKind.Interaction);
            Assert.Equal(TranscriptBlockStatus.Completed, replayed.Status);
            Assert.Equal("允许一次", replayed.Output);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InterruptedRun_CancelsPendingInteractionEvidenceAndTranscript()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            store.CreateRun(new TaskProjection(taskId, runId, "中断测试", root, "Pi", "high"), "中断测试");
            store.AppendRunEvent(new CompanionRunEvent(
                Guid.NewGuid(),
                taskId,
                runId,
                1,
                CompanionRunEventKind.QuestionRequested,
                DateTimeOffset.UtcNow,
                RunStatus.WaitingForAnswer,
                new Dictionary<string, string>
                {
                    ["activity"] = "等待回答",
                    ["summary"] = "等待回答",
                    ["interactionId"] = "question-interrupted",
                    ["interactionMethod"] = "input",
                    ["interactionOptions"] = "[]",
                }));
            store.AppendRunEvent(CreateEvent(
                taskId,
                runId,
                2,
                CompanionRunEventKind.RunInterrupted,
                RunStatus.Interrupted,
                "运行中断"));

            var evidence = Assert.Single(store.GetInteractionRequests(runId));
            var restored = store.RestoreProjection(taskId);
            var interaction = Assert.Single(restored!.Transcript, block => block.Kind == TranscriptBlockKind.Interaction);

            Assert.Equal("Cancelled", evidence.Status);
            Assert.NotNull(evidence.ResolvedAt);
            Assert.Equal(TranscriptBlockStatus.Cancelled, interaction.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TaskManagement_SearchesFiltersRenamesAndManagesRecycleBin()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var completedTaskId = Guid.NewGuid();
            var completedRunId = Guid.NewGuid();
            var failedTaskId = Guid.NewGuid();
            var failedRunId = Guid.NewGuid();

            store.CreateRun(
                new TaskProjection(completedTaskId, completedRunId, "Alpha docs", root, "Pi", "high"),
                "Document Alpha");
            store.AppendRunEvent(CreateEvent(
                completedTaskId,
                completedRunId,
                1,
                CompanionRunEventKind.RunSettled,
                RunStatus.Completed,
                "Documentation completed"));
            store.CreateRun(
                new TaskProjection(failedTaskId, failedRunId, "Beta bug", root, "Pi", "high"),
                "Fix Beta");
            store.AppendRunEvent(CreateEvent(
                failedTaskId,
                failedRunId,
                1,
                CompanionRunEventKind.RunFailed,
                RunStatus.Failed,
                "Build failed"));

            var searchResult = Assert.Single(store.QueryTasks(new TaskHistoryQuery(Search: "docs")));
            var failedResult = Assert.Single(store.QueryTasks(new TaskHistoryQuery(Statuses: [RunStatus.Failed])));
            Assert.Equal(completedTaskId, searchResult.TaskId);
            Assert.Equal(failedTaskId, failedResult.TaskId);
            Assert.Equal(
                completedTaskId,
                Assert.Single(store.QueryTasks(new TaskHistoryQuery(Limit: 1, Offset: 1))).TaskId);

            store.RenameTask(completedTaskId, "  Alpha documentation  ");
            Assert.Equal(
                "Alpha documentation",
                Assert.Single(store.QueryTasks(new TaskHistoryQuery(Search: "documentation"))).Title);

            store.MoveTaskToRecycleBin(completedTaskId);
            Assert.DoesNotContain(store.QueryTasks(new TaskHistoryQuery()), task => task.TaskId == completedTaskId);
            var recycled = Assert.Single(store.QueryTasks(new TaskHistoryQuery(IncludeDeleted: true)));
            Assert.Equal(completedTaskId, recycled.TaskId);
            Assert.NotNull(recycled.DeletedAt);
            Assert.Null(store.RestoreProjection(completedTaskId));

            store.RestoreTaskFromRecycleBin(completedTaskId);
            Assert.Empty(store.QueryTasks(new TaskHistoryQuery(IncludeDeleted: true)));
            Assert.Equal("Alpha documentation", store.RestoreProjection(completedTaskId)?.Title);

            store.MoveTaskToRecycleBin(completedTaskId);
            store.DeleteTaskPermanently(completedTaskId);
            Assert.Empty(store.QueryTasks(new TaskHistoryQuery(IncludeDeleted: true)));
            Assert.Null(store.RestoreProjection(completedTaskId));

            store.MoveTaskToRecycleBin(failedTaskId);
            store.EmptyRecycleBin();
            Assert.Empty(store.QueryTasks(new TaskHistoryQuery(IncludeDeleted: true)));
            Assert.Empty(store.QueryTasks(new TaskHistoryQuery()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static CompanionRunEvent CreateEvent(
        Guid taskId,
        Guid runId,
        long sequence,
        CompanionRunEventKind kind,
        RunStatus status,
        string summary,
        string? finalText = null,
        DateTimeOffset? timestamp = null)
    {
        var payload = new Dictionary<string, string>
        {
            ["activity"] = summary,
            ["summary"] = summary,
        };
        if (finalText is not null)
        {
            payload["finalText"] = finalText;
        }

        return new CompanionRunEvent(
            Guid.NewGuid(),
            taskId,
            runId,
            sequence,
            kind,
            timestamp ?? DateTimeOffset.UtcNow,
            status,
            payload);
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

    private static void CreateCompletedTask(
        SqliteRunEventStore store,
        Guid taskId,
        Guid runId,
        string root,
        string title)
    {
        store.CreateRun(new TaskProjection(taskId, runId, title, root, "Pi", "high"), title);
        store.AppendRunEvent(CreateEvent(
            taskId,
            runId,
            1,
            CompanionRunEventKind.RunSettled,
            RunStatus.Completed,
            "Completed"));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
