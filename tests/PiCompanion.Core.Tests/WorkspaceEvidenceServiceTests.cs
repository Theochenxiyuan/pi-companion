using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiCompanion.Application.Evidence;
using PiCompanion.Application.Files;
using PiCompanion.Application.Persistence;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Evidence;

namespace PiCompanion.Core.Tests;

public sealed class WorkspaceEvidenceServiceTests
{
    [Fact]
    public void WorkspacePathPolicy_RejectsWindowsDevicePathsWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PiCompanionEvidenceTests", "workspace"));
        var candidate = Path.Combine(workspace, "nul");

        Assert.False(WorkspacePathPolicy.TryResolveCandidate(workspace, candidate, out _));
    }

    [Fact]
    public void EditEvidence_UsesNativePatchAndRestoresOnlyWhenCurrentHashMatches()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var target = Path.Combine(workspace, "sample.txt");
            File.WriteAllText(target, "old\n", new UTF8Encoding(false));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, backups, Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            WriteBackup(backups, runId, "edit-1", target, File.ReadAllBytes(target), existed: true);
            File.WriteAllText(target, "new\n", new UTF8Encoding(false));

            service.RecordToolExecution(new AgentToolExecution(
                taskId,
                runId,
                "edit-1",
                "edit",
                JsonSerializer.Serialize(new { path = "sample.txt" }),
                JsonSerializer.Serialize(new
                {
                    content = new[] { new { type = "text", text = "updated" } },
                    details = new { diff = "-old\n+new", patch = "--- a/sample.txt\n+++ b/sample.txt\n@@ -1 +1 @@\n-old\n+new\n" },
                }),
                false,
                DateTimeOffset.UtcNow.AddMilliseconds(-20),
                DateTimeOffset.UtcNow));
            service.FinalizeRun(runId);

            var evidence = store.GetRunEvidence(runId);
            var file = Assert.Single(evidence.Files, item => item.Confidence == EvidenceConfidence.Confirmed);
            Assert.Equal("PiEditPatch", file.Source);
            Assert.Contains("--- a/sample.txt", file.DiffText);
            Assert.Equal(RecoveryAvailability.Available, file.Recovery);

            var restored = service.RestoreFile(file.Id);
            Assert.True(restored.Succeeded);
            Assert.Equal("old\n", File.ReadAllText(target));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void RestoreFile_DetectsChangesMadeAfterTheAgent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var target = Path.Combine(workspace, "sample.txt");
            File.WriteAllText(target, "before", new UTF8Encoding(false));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, backups, Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            WriteBackup(backups, runId, "write-1", target, File.ReadAllBytes(target), existed: true);
            File.WriteAllText(target, "agent", new UTF8Encoding(false));
            service.RecordToolExecution(ToolExecution(taskId, runId, "write-1", "write", "sample.txt"));
            var file = Assert.Single(store.GetRunEvidence(runId).Files, item => item.Confidence == EvidenceConfidence.Confirmed);
            File.WriteAllText(target, "user", new UTF8Encoding(false));

            var result = service.RestoreFile(file.Id);

            Assert.False(result.Succeeded);
            Assert.Equal(RecoveryAvailability.Conflict, result.Status);
            Assert.Equal("user", File.ReadAllText(target));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void MissingToolBaseline_IsObservedAndCannotBeRestoredAsIfItWereANewFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var target = Path.Combine(workspace, "sample.txt");
            File.WriteAllText(target, "before", new UTF8Encoding(false));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, Path.Combine(root, "backups"), Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            File.WriteAllText(target, "agent", new UTF8Encoding(false));
            service.RecordToolExecution(ToolExecution(taskId, runId, "write-without-manifest", "write", "sample.txt"));
            service.FinalizeRun(runId);

            var snapshot = store.GetRunEvidence(runId);
            var file = Assert.Single(snapshot.Files, item => item.ToolCallId == "write-without-manifest");
            Assert.Equal(EvidenceConfidence.Observed, file.Confidence);
            Assert.Equal(FileChangeKind.Unknown, file.Kind);
            Assert.Equal(RecoveryAvailability.Unavailable, file.Recovery);
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "tool-baseline-missing");

            var result = service.RestoreFile(file.Id);

            Assert.False(result.Succeeded);
            Assert.True(File.Exists(target));
            Assert.Equal("agent", File.ReadAllText(target));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void BashEvidence_ClassifiesTestsFromTheRealProcessExitContract()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, Path.Combine(root, "backups"), Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            service.RecordToolExecution(new AgentToolExecution(
                taskId,
                runId,
                "bash-1",
                "bash",
                JsonSerializer.Serialize(new { command = "dotnet test --configuration Release" }),
                JsonSerializer.Serialize(new
                {
                    content = new[] { new { type = "text", text = "Failed\n\nCommand exited with code 1" } },
                    details = new { },
                }),
                true,
                DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow));
            service.FinalizeRun(runId);

            var snapshot = store.GetRunEvidence(runId);
            var command = Assert.Single(snapshot.Commands);
            Assert.Equal(1, command.ExitCode);
            Assert.True(command.IsTest);
            Assert.Equal(TestEvidenceStatus.Failed, snapshot.TestStatus);
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "shell-coverage");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ConcurrentRuns_KeepIndependentEvidenceWatchers()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstWorkspace = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var secondWorkspace = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(
                store,
                Path.Combine(root, "backups"),
                Path.Combine(root, "trash"));
            var firstTaskId = Guid.NewGuid();
            var secondTaskId = Guid.NewGuid();
            var firstRunId = Guid.NewGuid();
            var secondRunId = Guid.NewGuid();
            service.BeginRun(firstTaskId, firstRunId, firstWorkspace);
            service.BeginRun(secondTaskId, secondRunId, secondWorkspace);

            service.RecordToolExecution(BashExecution(firstTaskId, firstRunId, "bash-first"));
            service.RecordToolExecution(BashExecution(secondTaskId, secondRunId, "bash-second"));
            service.FinalizeRun(firstRunId);
            service.FinalizeRun(secondRunId);

            Assert.True(store.GetRunEvidenceMetadata(firstRunId)?.ShellObserved);
            Assert.True(store.GetRunEvidenceMetadata(secondRunId)?.ShellObserved);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }

        static AgentToolExecution BashExecution(Guid taskId, Guid runId, string toolCallId) =>
            new(
                taskId,
                runId,
                toolCallId,
                "bash",
                JsonSerializer.Serialize(new { command = "dotnet --info" }),
                JsonSerializer.Serialize(new
                {
                    content = new[] { new { type = "text", text = "Command exited with code 0" } },
                    details = new { },
                }),
                false,
                DateTimeOffset.UtcNow.AddMilliseconds(-20),
                DateTimeOffset.UtcNow);
    }

    [Fact]
    public void NewFileEvidence_RestoresByMovingTheFileToRecoveryTrash()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var trash = Path.Combine(root, "trash");
            var target = Path.Combine(workspace, "created.txt");
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, backups, trash);
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            WriteMissingBackup(backups, runId, "write-new", target);
            File.WriteAllText(target, "created by agent", new UTF8Encoding(false));
            service.RecordToolExecution(ToolExecution(taskId, runId, "write-new", "write", "created.txt"));

            var file = Assert.Single(store.GetRunEvidence(runId).Files, item => item.Confidence == EvidenceConfidence.Confirmed);
            Assert.Equal(FileChangeKind.Added, file.Kind);
            Assert.False(file.ExistedBefore);
            Assert.Equal(RecoveryAvailability.Available, file.Recovery);

            var result = service.RestoreFile(file.Id);

            Assert.True(result.Succeeded);
            Assert.False(File.Exists(target));
            Assert.Single(Directory.GetFiles(trash, "created.txt", SearchOption.AllDirectories));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task WatcherOnlyNewFile_IsClassifiedAsAdded()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, Path.Combine(root, "backups"), Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);

            var target = Path.Combine(workspace, "watcher-created.txt");
            await File.WriteAllTextAsync(target, "new", TestContext.Current.CancellationToken);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            service.FinalizeRun(runId);

            var file = Assert.Single(store.GetRunEvidence(runId).Files, item => item.RelativePath == "watcher-created.txt");
            Assert.Equal("FileSystemWatcher", file.Source);
            Assert.Equal(FileChangeKind.Added, file.Kind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task WatcherInstallStyleReplacement_UsesFinalStateInsteadOfTransientDelete()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(
                store,
                Path.Combine(root, "backups"),
                Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);

            var installDirectory = Directory.CreateDirectory(
                Path.Combine(workspace, ".agents", "skills", "sample")).FullName;
            var stablePath = Path.Combine(installDirectory, "SKILL.md");
            var replacedPath = Path.Combine(installDirectory, "reference.md");
            await File.WriteAllTextAsync(
                stablePath,
                "stable",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                replacedPath,
                "staged",
                TestContext.Current.CancellationToken);
            await Task.Delay(150, TestContext.Current.CancellationToken);

            File.Delete(replacedPath);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                replacedPath,
                "installed",
                TestContext.Current.CancellationToken);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            service.FinalizeRun(runId);

            var files = store.GetRunEvidence(runId).Files
                .Where(item => item.Source == "FileSystemWatcher")
                .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(FileChangeKind.Added, files[Path.Combine(
                ".agents",
                "skills",
                "sample",
                "SKILL.md")].Kind);
            var replaced = files[Path.Combine(
                ".agents",
                "skills",
                "sample",
                "reference.md")];
            Assert.Equal(FileChangeKind.Added, replaced.Kind);
            Assert.NotNull(replaced.AfterHash);
            Assert.True(File.Exists(replacedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task NonGitWatcher_FiltersGeneratedAndProjectIgnoredChangesBeforeMaterializingEvidence()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "dist/\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(workspace, ".ignore"), "logs/\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(workspace, ".fdignore"), "*.tmp\n", new UTF8Encoding(false));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(
                store,
                Path.Combine(root, "backups"),
                Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);

            var nodeModules = Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "dep")).FullName;
            var dist = Directory.CreateDirectory(Path.Combine(workspace, "dist")).FullName;
            var logs = Directory.CreateDirectory(Path.Combine(workspace, "logs")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(workspace, "src")).FullName;
            var astro = Directory.CreateDirectory(Path.Combine(workspace, ".astro")).FullName;
            var workflows = Directory.CreateDirectory(Path.Combine(workspace, ".github", "workflows")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(nodeModules, "index.js"),
                "dependency",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(dist, "bundle.js"),
                "generated",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(logs, "run.log"),
                "generated",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "scratch.tmp"),
                "generated",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(source, "feature.cs"),
                "meaningful",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(astro, "types.d.ts"),
                "generated",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workflows, "ci.yml"),
                "meaningful configuration",
                TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            service.FinalizeRun(runId);

            var files = store.GetRunEvidence(runId).Files
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(
                [
                    Path.Combine(".github", "workflows", "ci.yml"),
                    Path.Combine("src", "feature.cs"),
                ],
                files.Select(file => file.RelativePath));
            Assert.All(files, file => Assert.Equal("FileSystemWatcher", file.Source));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PiIgnore_CanExplicitlyIncludeABuiltInGeneratedPath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            File.WriteAllText(
                Path.Combine(workspace, ".piignore"),
                "!node_modules/kept.js\n",
                new UTF8Encoding(false));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(
                store,
                Path.Combine(root, "backups"),
                Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);

            var nodeModules = Directory.CreateDirectory(Path.Combine(workspace, "node_modules")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(nodeModules, "ignored.js"),
                "ignored",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(nodeModules, "kept.js"),
                "kept",
                TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            service.FinalizeRun(runId);

            var file = Assert.Single(store.GetRunEvidence(runId).Files);
            Assert.Equal(Path.Combine("node_modules", "kept.js"), file.RelativePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ExplicitWriteEvidence_BypassesWatcherIgnorePolicy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(
                store,
                backups,
                Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);

            var targetDirectory = Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "manual")).FullName;
            var target = Path.Combine(targetDirectory, "explicit.js");
            WriteMissingBackup(backups, runId, "write-ignored", target);
            File.WriteAllText(target, "explicit", new UTF8Encoding(false));
            service.RecordToolExecution(
                ToolExecution(taskId, runId, "write-ignored", "write", Path.GetRelativePath(workspace, target)));
            service.FinalizeRun(runId);

            var file = Assert.Single(store.GetRunEvidence(runId).Files);
            Assert.Equal(Path.Combine("node_modules", "manual", "explicit.js"), file.RelativePath);
            Assert.Equal("BackupComparison", file.Source);
            Assert.Equal(EvidenceConfidence.Confirmed, file.Confidence);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void LargeFileEvidence_HashesAndRestoresWithoutGeneratingAnUnboundedDiff()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var target = Path.Combine(workspace, "large.bin");
            var original = Enumerable.Repeat((byte)'a', 1024 * 1024 + 1).ToArray();
            var changed = original.ToArray();
            changed[^1] = (byte)'b';
            File.WriteAllBytes(target, original);
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, backups, Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            WriteBackup(backups, runId, "write-large", target, original, existed: true);
            File.WriteAllBytes(target, changed);
            service.RecordToolExecution(ToolExecution(taskId, runId, "write-large", "write", "large.bin"));

            var file = Assert.Single(store.GetRunEvidence(runId).Files, item => item.Confidence == EvidenceConfidence.Confirmed);
            Assert.True(file.DiffTruncated);
            Assert.Null(file.DiffText);
            Assert.Equal(changed.LongLength, file.AfterSize);

            Assert.True(service.RestoreFile(file.Id).Succeeded);
            Assert.Equal(original, File.ReadAllBytes(target));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void GitWorkspaceEvidence_CapturesHeadStatusDiffAndShellCoverageWarning()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            if (!RunGit(workspace, "init") ||
                !RunGit(workspace, "config", "user.email", "tests@example.invalid") ||
                !RunGit(workspace, "config", "user.name", "Pi Companion Tests"))
            {
                return;
            }

            var target = Path.Combine(workspace, "tracked.txt");
            File.WriteAllText(target, "before\n", new UTF8Encoding(false));
            Assert.True(RunGit(workspace, "add", "tracked.txt"));
            Assert.True(RunGit(workspace, "commit", "-m", "baseline"));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(store, Path.Combine(root, "backups"), Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);
            File.WriteAllText(target, "after\n", new UTF8Encoding(false));
            service.RecordToolExecution(new AgentToolExecution(
                taskId,
                runId,
                "bash-git",
                "bash",
                JsonSerializer.Serialize(new { command = "powershell -Command Set-Content tracked.txt after" }),
                JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "ok" } }, details = new { } }),
                false,
                DateTimeOffset.UtcNow.AddMilliseconds(-10),
                DateTimeOffset.UtcNow));
            service.FinalizeRun(runId);

            var snapshot = store.GetRunEvidence(runId);
            Assert.True(snapshot.IsGitRepository);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.HeadBefore));
            Assert.Equal(snapshot.HeadBefore, snapshot.HeadAfter);
            var file = Assert.Single(snapshot.Files, item => item.RelativePath == "tracked.txt");
            Assert.Equal("GitWorkspace", file.Source);
            Assert.Contains("-before", file.DiffText);
            Assert.Contains("+after", file.DiffText);
            Assert.Contains(snapshot.Warnings, warning => warning.Code == "shell-coverage");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task GitWorkspaceWatcher_DoesNotReintroduceGitIgnoredFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            if (!RunGit(workspace, "init") ||
                !RunGit(workspace, "config", "user.email", "tests@example.invalid") ||
                !RunGit(workspace, "config", "user.name", "Pi Companion Tests"))
            {
                return;
            }

            File.WriteAllText(Path.Combine(workspace, ".gitignore"), "generated/\n", new UTF8Encoding(false));
            var tracked = Path.Combine(workspace, "tracked.txt");
            File.WriteAllText(tracked, "before\n", new UTF8Encoding(false));
            Assert.True(RunGit(workspace, "add", ".gitignore", "tracked.txt"));
            Assert.True(RunGit(workspace, "commit", "-m", "baseline"));
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            using var service = new WorkspaceEvidenceService(
                store,
                Path.Combine(root, "backups"),
                Path.Combine(root, "trash"));
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            service.BeginRun(taskId, runId, workspace);

            var generated = Directory.CreateDirectory(Path.Combine(workspace, "generated")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(generated, "artifact.txt"),
                "ignored",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                tracked,
                "after\n",
                TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            service.FinalizeRun(runId);

            var file = Assert.Single(store.GetRunEvidence(runId).Files);
            Assert.Equal("tracked.txt", file.RelativePath);
            Assert.Equal("GitWorkspace", file.Source);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(root);
        }
    }

    private static AgentToolExecution ToolExecution(
        Guid taskId,
        Guid runId,
        string toolCallId,
        string toolName,
        string path) => new(
        taskId,
        runId,
        toolCallId,
        toolName,
        JsonSerializer.Serialize(new { path }),
        JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "ok" } }, details = new { } }),
        false,
        DateTimeOffset.UtcNow.AddMilliseconds(-10),
        DateTimeOffset.UtcNow);

    private static void WriteBackup(
        string backups,
        Guid runId,
        string toolCallId,
        string target,
        byte[] content,
        bool existed)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var objectDirectory = Directory.CreateDirectory(Path.Combine(backups, "objects", hash[..2])).FullName;
        File.WriteAllBytes(Path.Combine(objectDirectory, hash), content);
        var manifestDirectory = Directory.CreateDirectory(Path.Combine(backups, "manifests")).FullName;
        File.AppendAllText(
            Path.Combine(manifestDirectory, $"{runId:D}.jsonl"),
            JsonSerializer.Serialize(new
            {
                runId = runId.ToString("D"),
                toolCallId,
                originalPath = target,
                sha256 = hash,
                size = content.LongLength,
                backedUpAt = DateTimeOffset.UtcNow,
                existed,
            }) + "\n",
            new UTF8Encoding(false));
    }

    private static void WriteMissingBackup(string backups, Guid runId, string toolCallId, string target)
    {
        var manifestDirectory = Directory.CreateDirectory(Path.Combine(backups, "manifests")).FullName;
        File.AppendAllText(
            Path.Combine(manifestDirectory, $"{runId:D}.jsonl"),
            JsonSerializer.Serialize(new
            {
                runId = runId.ToString("D"),
                toolCallId,
                originalPath = target,
                size = 0,
                backedUpAt = DateTimeOffset.UtcNow,
                existed = false,
            }) + "\n",
            new UTF8Encoding(false));
    }

    private static bool RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiCompanionEvidenceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PiCompanionEvidenceTests"));
        var target = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(testRoot, target);
        if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean a directory outside the evidence test root: {target}");
        }

        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(target, recursive: true);
    }
}
