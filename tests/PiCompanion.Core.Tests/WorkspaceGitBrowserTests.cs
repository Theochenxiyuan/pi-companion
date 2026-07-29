using System.Diagnostics;
using System.Text;
using PiCompanion.Application.Files;

namespace PiCompanion.Core.Tests;

public sealed class WorkspaceGitBrowserTests
{
    [Fact]
    public void Read_ReportsTrackedAndUntrackedChangesWithLineStatistics()
    {
        var root = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "before\nchanged\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "new.txt"), "first\nsecond\n", new UTF8Encoding(false));
            var browser = new WorkspaceGitBrowser();

            var snapshot = browser.Read(root);

            Assert.True(snapshot.IsRepository);
            Assert.Equal("main", snapshot.Branch);
            var tracked = Assert.Single(snapshot.Entries, entry => entry.RelativePath == "tracked.txt");
            Assert.Equal("Modified", tracked.Kind);
            Assert.True(tracked.IsUnstaged);
            Assert.Equal(1, tracked.AddedLines);
            Assert.Equal(0, tracked.DeletedLines);
            var untracked = Assert.Single(snapshot.Entries, entry => entry.RelativePath == "new.txt");
            Assert.True(untracked.IsUntracked);
            Assert.Equal(2, untracked.AddedLines);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ReadDiff_ReturnsTrackedAndUntrackedUnifiedDiffsAndRejectsTraversal()
    {
        var root = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "after\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "new.txt"), "new\n", new UTF8Encoding(false));
            var browser = new WorkspaceGitBrowser();

            var tracked = browser.ReadDiff(root, "tracked.txt");
            var untracked = browser.ReadDiff(root, "new.txt");

            Assert.Contains("-before", tracked.DiffText);
            Assert.Contains("+after", tracked.DiffText);
            Assert.Contains("+++ b/new.txt", untracked.DiffText);
            Assert.Contains("+new", untracked.DiffText);
            Assert.Throws<InvalidOperationException>(() => browser.ReadDiff(root, "../outside.txt"));
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void StageUnstageCommitAndHistory_CompleteTheLocalCommitFlow()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "after\n", new UTF8Encoding(false));

            browser.Stage(root, ["tracked.txt"]);
            var staged = Assert.Single(browser.Read(root).Entries);
            Assert.True(staged.IsStaged);
            Assert.False(staged.IsUnstaged);

            browser.Unstage(root, ["tracked.txt"]);
            var unstaged = Assert.Single(browser.Read(root).Entries);
            Assert.False(unstaged.IsStaged);
            Assert.True(unstaged.IsUnstaged);

            browser.Stage(root, ["tracked.txt"]);
            var commitHash = browser.Commit(root, "update tracked file");
            var history = browser.ReadHistory(root);
            var commit = Assert.Single(history.Entries, entry => entry.Hash == commitHash);
            Assert.Equal("update tracked file", commit.Subject);
            Assert.Empty(browser.Read(root).Entries);

            var diff = browser.ReadCommitDiff(root, commitHash);
            Assert.Equal("update tracked file", diff.Subject);
            var file = Assert.Single(diff.Files);
            Assert.Equal("tracked.txt", file.RelativePath);
            Assert.Equal("Modified", file.Status);
            Assert.Equal(1, file.AddedLines);
            Assert.Equal(1, file.DeletedLines);
            Assert.Contains("-before", file.DiffText);
            Assert.Contains("+after", file.DiffText);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ReadCommitMessageContext_UsesOnlyStagedChangesAndTracksIndexFingerprint()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "staged\n", new UTF8Encoding(false));
            browser.Stage(root, ["tracked.txt"]);
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "unstaged after staging\n", new UTF8Encoding(false));

            var before = browser.Read(root);
            var context = browser.ReadCommitMessageContext(root);

            Assert.Equal(before.StagedFingerprint, context.StagedFingerprint);
            Assert.Equal("main", context.Branch);
            Assert.Contains("tracked.txt", context.RelativePaths);
            Assert.Contains("+staged", context.DiffText);
            Assert.DoesNotContain("unstaged after staging", context.DiffText);
            Assert.Contains("initial", context.RecentSubjects);

            File.WriteAllText(Path.Combine(root, "tracked.txt"), "second staged state\n", new UTF8Encoding(false));
            browser.Stage(root, ["tracked.txt"]);
            Assert.NotEqual(context.StagedFingerprint, browser.Read(root).StagedFingerprint);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ReadHistory_LoadsCommitPagesWithoutOverlap()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            for (var index = 1; index <= 4; index++)
            {
                File.WriteAllText(
                    Path.Combine(root, "tracked.txt"),
                    $"change {index}\n",
                    new UTF8Encoding(false));
                browser.Stage(root, ["tracked.txt"]);
                browser.Commit(root, $"change {index}");
            }

            var first = browser.ReadHistory(root, offset: 0, limit: 2);
            var second = browser.ReadHistory(root, offset: 2, limit: 2);
            var final = browser.ReadHistory(root, offset: 4, limit: 2);

            Assert.Equal(2, first.Entries.Count);
            Assert.True(first.HasMore);
            Assert.Equal(2, second.Entries.Count);
            Assert.True(second.HasMore);
            Assert.Single(final.Entries);
            Assert.False(final.HasMore);
            Assert.Empty(first.Entries.Select(commit => commit.Hash).Intersect(
                second.Entries.Select(commit => commit.Hash)));
            Assert.Empty(second.Entries.Select(commit => commit.Hash).Intersect(
                final.Entries.Select(commit => commit.Hash)));
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ReadCommitDiff_GroupsTextRenameAndBinaryChangesByFile()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            File.WriteAllText(Path.Combine(root, "rename-source.txt"), "move me\n", new UTF8Encoding(false));
            browser.Stage(root, ["rename-source.txt"]);
            browser.Commit(root, "add rename source");

            File.WriteAllText(Path.Combine(root, "tracked.txt"), "after\n", new UTF8Encoding(false));
            File.Move(
                Path.Combine(root, "rename-source.txt"),
                Path.Combine(root, "renamed file.txt"));
            File.WriteAllBytes(Path.Combine(root, "asset.bin"), [0, 1, 2, 3, 0, 255]);

            var paths = browser.Read(root).Entries.Select(entry => entry.RelativePath).ToArray();
            browser.Stage(root, paths);
            var commitHash = browser.Commit(root, "mixed changes");

            var diff = browser.ReadCommitDiff(root, commitHash);

            Assert.Equal(3, diff.Files.Count);
            var modified = Assert.Single(diff.Files, file => file.RelativePath == "tracked.txt");
            Assert.Equal("Modified", modified.Status);
            Assert.Equal(1, modified.AddedLines);
            Assert.Equal(1, modified.DeletedLines);
            Assert.False(modified.IsBinary);
            Assert.Contains("+after", modified.DiffText);

            var renamed = Assert.Single(diff.Files, file => file.RelativePath == "renamed file.txt");
            Assert.Equal("rename-source.txt", renamed.OriginalRelativePath);
            Assert.Equal("Renamed", renamed.Status);
            Assert.Equal(0, renamed.AddedLines);
            Assert.Equal(0, renamed.DeletedLines);

            var binary = Assert.Single(diff.Files, file => file.RelativePath == "asset.bin");
            Assert.Equal("Added", binary.Status);
            Assert.True(binary.IsBinary);
            Assert.Null(binary.AddedLines);
            Assert.Null(binary.DeletedLines);
            Assert.Null(binary.DiffText);
            Assert.False(diff.Truncated);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ReadCommitDiff_TruncatesEachFileWithoutHidingLaterFiles()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            File.WriteAllText(
                Path.Combine(root, "a-large.txt"),
                new string('x', 300 * 1024),
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "z-small.txt"), "small\n", new UTF8Encoding(false));
            browser.Stage(root, ["a-large.txt", "z-small.txt"]);
            var commitHash = browser.Commit(root, "large and small files");

            var diff = browser.ReadCommitDiff(root, commitHash);

            var large = Assert.Single(diff.Files, file => file.RelativePath == "a-large.txt");
            Assert.True(large.Truncated);
            Assert.Equal(256 * 1024, large.DiffText?.Length);
            var small = Assert.Single(diff.Files, file => file.RelativePath == "z-small.txt");
            Assert.False(small.Truncated);
            Assert.Contains("+small", small.DiffText);
            Assert.True(diff.Truncated);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ReadCommitDiff_IncludesFilesFromTheRootCommit()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            var commit = Assert.Single(browser.ReadHistory(root).Entries);

            var diff = browser.ReadCommitDiff(root, commit.Hash);

            var file = Assert.Single(diff.Files);
            Assert.Equal("tracked.txt", file.RelativePath);
            Assert.Equal("Added", file.Status);
            Assert.Contains("+before", file.DiffText);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void Commit_RejectsStagedChangesOutsideTheSelectedWorkspace()
    {
        var root = CreateRepository();
        try
        {
            var workspace = Path.Combine(root, "src");
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "inside.txt"), "inside\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "outside.txt"), "outside\n", new UTF8Encoding(false));
            RunGit(root, "add", "outside.txt");

            var browser = new WorkspaceGitBrowser();
            browser.Stage(workspace, ["inside.txt"]);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                browser.Commit(workspace, "scoped commit"));
            Assert.Contains("工作目录外", exception.Message);
            Assert.False(browser.Read(workspace).CanManageBranches);
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void LocalBranchOperations_CreateSwitchAndMergeWithoutRemoteAccess()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            browser.CreateBranch(root, "feature/local-git");
            File.WriteAllText(Path.Combine(root, "feature.txt"), "feature\n", new UTF8Encoding(false));
            browser.Stage(root, ["feature.txt"]);
            browser.Commit(root, "add local feature");

            browser.SwitchBranch(root, "main");
            Assert.Equal("main", browser.Read(root).Branch);
            browser.UpdateBranch(root, "merge", "feature/local-git");

            Assert.True(File.Exists(Path.Combine(root, "feature.txt")));
            Assert.Contains(
                browser.ReadHistory(root).Entries,
                commit => commit.Subject == "add local feature");
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    [Fact]
    public void ConflictingMerge_ExposesAndAbortsTheNativeGitOperation()
    {
        var root = CreateRepository();
        try
        {
            var browser = new WorkspaceGitBrowser();
            browser.CreateBranch(root, "feature/conflict");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "feature\n", new UTF8Encoding(false));
            browser.Stage(root, ["tracked.txt"]);
            browser.Commit(root, "feature change");

            browser.SwitchBranch(root, "main");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "main\n", new UTF8Encoding(false));
            browser.Stage(root, ["tracked.txt"]);
            browser.Commit(root, "main change");

            Assert.Throws<InvalidOperationException>(() =>
                browser.UpdateBranch(root, "merge", "feature/conflict"));
            var conflicted = browser.Read(root);
            Assert.Equal("Merge", conflicted.OperationState);
            Assert.Contains(conflicted.Entries, entry => entry.Kind == "Unmerged");

            browser.AbortOperation(root);
            var restored = browser.Read(root);
            Assert.Equal("None", restored.OperationState);
            Assert.Empty(restored.Entries);
            Assert.Equal(
                "main\n",
                File.ReadAllText(Path.Combine(root, "tracked.txt")).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRepository(root);
        }
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionGitBrowserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "--initial-branch=main");
        RunGit(root, "config", "user.name", "Pi Companion Tests");
        RunGit(root, "config", "user.email", "tests@pi-companion.local");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "before\n", new UTF8Encoding(false));
        RunGit(root, "add", "tracked.txt");
        RunGit(root, "commit", "-m", "initial");
        return root;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 git。");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stdout}{stderr}");
    }

    private static void DeleteRepository(string path)
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PiCompanionGitBrowserTests"));
        var target = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(testRoot, target);
        if (Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean a directory outside the Git browser test root: {target}");
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
