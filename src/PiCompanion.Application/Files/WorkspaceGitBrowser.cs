using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PiCompanion.Application.Evidence;

namespace PiCompanion.Application.Files;

public sealed class WorkspaceGitBrowser
{
    private const int MaximumDiffCharacters = 256 * 1024;
    private const int MaximumUntrackedDiffBytes = 4 * 1024 * 1024;
    private const int MaximumCommitMessageDiffCharacters = 128 * 1024;
    private const int MaximumActionPaths = 500;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(90);

    public WorkspaceGitSnapshot Read(string workingDirectory)
    {
        var workspace = RequireWorkspace(workingDirectory);
        var repository = TryOpenRepository(workspace);
        if (repository is null)
        {
            return new WorkspaceGitSnapshot(
                workspace,
                false,
                null,
                null,
                false,
                [],
                "None",
                false,
                [],
                null);
        }

        var status = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", repository.PathSpec);
        if (!status.Succeeded)
        {
            throw GitFailure("读取 Git 工作区状态", status);
        }

        var branchResult = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "symbolic-ref", "--quiet", "--short", "HEAD");
        var headResult = branchResult.Succeeded
            ? branchResult
            : RunGit(repository.Root, false, ReadTimeout, "rev-parse", "--short", "HEAD");
        var branch = headResult.Succeeded ? headResult.Output.Trim() : "未提交";
        var stats = ReadDiffStats(repository.Root, workspace, repository.PathSpec);
        var entries = ParseStatus(status.Output, repository.Root, workspace, stats);
        var branches = ReadBranches(repository.Root, branchResult.Succeeded ? branch : null);

        return new WorkspaceGitSnapshot(
            workspace,
            true,
            repository.Root,
            branch,
            !branchResult.Succeeded,
            branches,
            ReadOperationState(repository.Root),
            repository.CanManageBranches,
            entries,
            ReadStagedFingerprint(repository.Root));
    }

    public WorkspaceGitCommitMessageContext ReadCommitMessageContext(string workingDirectory)
    {
        var repository = RequireRepository(workingDirectory);
        var staged = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "diff",
            "--cached",
            "--name-only",
            "-z",
            "--no-renames",
            "--diff-filter=ACDMRTUXB");
        if (!staged.Succeeded)
        {
            throw GitFailure("检查暂存区", staged);
        }

        var repositoryPaths = staged.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (repositoryPaths.Length == 0)
        {
            throw new InvalidOperationException("暂存区没有可用于生成提交信息的更改。");
        }

        var relativePaths = new List<string>(repositoryPaths.Length);
        var outsideWorkspace = new List<string>();
        foreach (var path in repositoryPaths)
        {
            if (TryMapToWorkspace(repository.Root, repository.Workspace, path, out var relativePath))
            {
                relativePaths.Add(relativePath);
            }
            else if (outsideWorkspace.Count < 5)
            {
                outsideWorkspace.Add(path);
            }
        }
        if (outsideWorkspace.Count > 0)
        {
            throw new InvalidOperationException(
                $"仓库中还有当前工作目录外的已暂存文件，无法生成提交信息：{string.Join("、", outsideWorkspace)}");
        }

        var diff = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "diff",
            "--cached",
            "--no-ext-diff",
            "--no-textconv",
            "--no-renames",
            "--unified=3",
            "--",
            repository.PathSpec);
        if (!diff.Succeeded)
        {
            throw GitFailure("读取暂存区 Diff", diff);
        }

        var diffText = diff.Output;
        var truncated = diffText.Length > MaximumCommitMessageDiffCharacters;
        if (truncated)
        {
            diffText = diffText[..MaximumCommitMessageDiffCharacters];
        }

        var history = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "log",
            "-n20",
            "--format=%s",
            "--",
            repository.PathSpec);
        var recentSubjects = history.Succeeded
            ? history.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var branch = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "symbolic-ref",
            "--quiet",
            "--short",
            "HEAD");

        return new WorkspaceGitCommitMessageContext(
            repository.Workspace,
            Path.GetFileName(repository.Root),
            branch.Succeeded ? branch.Output.Trim() : "HEAD",
            ReadStagedFingerprint(repository.Root),
            relativePaths,
            recentSubjects,
            diffText,
            truncated);
    }

    public WorkspaceGitHistorySnapshot ReadHistory(
        string workingDirectory,
        int offset = 0,
        int limit = 25)
    {
        var repository = RequireRepository(workingDirectory);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);
        var head = RunGit(repository.Root, false, ReadTimeout, "rev-parse", "--verify", "HEAD");
        if (!head.Succeeded)
        {
            return new WorkspaceGitHistorySnapshot(repository.Workspace, [], false);
        }

        var history = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "log",
            $"--skip={offset}",
            $"-n{limit + 1}",
            "--date=iso-strict",
            "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%P%x1e",
            "--",
            repository.PathSpec);
        if (!history.Succeeded)
        {
            throw GitFailure("读取提交历史", history);
        }

        var entries = new List<WorkspaceGitCommit>();
        foreach (var record in history.Output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.TrimStart('\r', '\n').Split('\x1f');
            if (fields.Length < 7 ||
                !DateTimeOffset.TryParse(
                    fields[4],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp))
            {
                continue;
            }

            entries.Add(new WorkspaceGitCommit(
                fields[0],
                fields[1],
                fields[5],
                fields[2],
                fields[3],
                timestamp,
                fields[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        var hasMore = entries.Count > limit;
        return new WorkspaceGitHistorySnapshot(
            repository.Workspace,
            entries.Take(limit).ToArray(),
            hasMore);
    }

    public WorkspaceGitDiff ReadDiff(string workingDirectory, string relativePath)
    {
        var snapshot = Read(workingDirectory);
        if (!snapshot.IsRepository || string.IsNullOrWhiteSpace(snapshot.RepositoryRoot))
        {
            throw new InvalidOperationException("当前工作目录不是 Git 仓库。");
        }

        var entry = snapshot.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException("该文件已不在当前 Git 变更列表中。");
        if (!WorkspacePathPolicy.TryResolveCandidate(
                snapshot.WorkingDirectory,
                Path.Combine(snapshot.WorkingDirectory, FromGitPath(entry.RelativePath)),
                out var target))
        {
            throw new InvalidOperationException("Git Diff 路径不在当前工作区内。");
        }

        if (entry.IsUntracked)
        {
            return BuildUntrackedDiff(snapshot.WorkingDirectory, entry.RelativePath, target);
        }

        var pathSpecs = new List<string>
        {
            ToGitPath(Path.GetRelativePath(snapshot.RepositoryRoot, target)),
        };
        if (!string.IsNullOrWhiteSpace(entry.OriginalRelativePath) &&
            WorkspacePathPolicy.TryResolveCandidate(
                snapshot.WorkingDirectory,
                Path.Combine(snapshot.WorkingDirectory, FromGitPath(entry.OriginalRelativePath)),
                out var originalTarget))
        {
            pathSpecs.Add(ToGitPath(Path.GetRelativePath(snapshot.RepositoryRoot, originalTarget)));
        }

        var stagedArguments = new List<string>
        {
            "diff", "--cached", "--no-ext-diff", "--no-textconv", "--binary", "--find-renames", "--",
        };
        stagedArguments.AddRange(pathSpecs);
        var workingArguments = new List<string>
        {
            "diff", "--no-ext-diff", "--no-textconv", "--binary", "--find-renames", "--",
        };
        workingArguments.AddRange(pathSpecs);
        var staged = RunGit(snapshot.RepositoryRoot, false, ReadTimeout, stagedArguments);
        var working = RunGit(snapshot.RepositoryRoot, false, ReadTimeout, workingArguments);
        var sections = new[]
        {
            staged.Succeeded ? staged.Output.TrimEnd() : string.Empty,
            working.Succeeded ? working.Output.TrimEnd() : string.Empty,
        }.Where(section => section.Length > 0);
        var diff = string.Join(Environment.NewLine, sections);
        return CreateDiff(snapshot.WorkingDirectory, entry.RelativePath, diff);
    }

    public WorkspaceGitCommitDiff ReadCommitDiff(string workingDirectory, string commitHash)
    {
        var repository = RequireRepository(workingDirectory);
        var hash = NormalizeCommitHash(commitHash);
        var verify = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "cat-file",
            "-e",
            $"{hash}^{{commit}}");
        if (!verify.Succeeded)
        {
            throw new InvalidOperationException("该提交不存在或不是有效提交。");
        }

        var metadata = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "show",
            "-s",
            "--format=%h%x00%s",
            hash);
        var parts = metadata.Output.TrimEnd('\r', '\n').Split('\0', 2);
        var shortHash = parts.ElementAtOrDefault(0) ?? hash[..Math.Min(hash.Length, 8)];
        var subject = parts.ElementAtOrDefault(1) ?? string.Empty;
        var changedFiles = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "diff-tree",
            "--root",
            "--no-commit-id",
            "--name-status",
            "-z",
            "--find-renames",
            hash,
            "--",
            repository.PathSpec);
        if (!changedFiles.Succeeded)
        {
            throw GitFailure("读取提交文件列表", changedFiles);
        }

        var fileMetadata = ParseCommitFileMetadata(
            changedFiles.Output,
            repository.Root,
            repository.Workspace);
        var patch = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "show",
            "--format=",
            "--patch",
            "--no-ext-diff",
            "--no-textconv",
            "--binary",
            "--find-renames",
            hash,
            "--",
            repository.PathSpec);
        if (!patch.Succeeded)
        {
            throw GitFailure("读取提交 Diff", patch);
        }

        var sections = SplitCommitPatchSections(patch.Output.TrimEnd());
        var canUseCombinedPatch = sections.Count == fileMetadata.Count;
        var files = new List<WorkspaceGitCommitFileDiff>(fileMetadata.Count);
        for (var index = 0; index < fileMetadata.Count; index++)
        {
            var file = fileMetadata[index];
            var section = canUseCombinedPatch
                ? sections[index]
                : ReadCommitFilePatch(repository, hash, file);
            var diff = CreateDiff(repository.Workspace, file.RelativePath, section);
            var (addedLines, deletedLines) = diff.IsBinary
                ? ((int?)null, (int?)null)
                : CountDiffLines(section);
            files.Add(new WorkspaceGitCommitFileDiff(
                file.RelativePath,
                file.OriginalRelativePath,
                file.Kind,
                addedLines,
                deletedLines,
                diff.IsBinary ? null : diff.DiffText,
                diff.IsBinary,
                diff.Truncated));
        }

        return new WorkspaceGitCommitDiff(
            repository.Workspace,
            hash,
            shortHash,
            subject,
            files,
            files.Any(file => file.Truncated));
    }

    public void Stage(string workingDirectory, IReadOnlyList<string> relativePaths)
    {
        var snapshot = Read(workingDirectory);
        var pathSpecs = ResolveActionPathSpecs(snapshot, relativePaths, entry => entry.IsUnstaged);
        var result = RunGit(
            snapshot.RepositoryRoot!,
            true,
            WriteTimeout,
            BuildArguments("add", "-A", "--", pathSpecs));
        if (!result.Succeeded)
        {
            throw GitFailure("暂存文件", result);
        }
    }

    public void Unstage(string workingDirectory, IReadOnlyList<string> relativePaths)
    {
        var snapshot = Read(workingDirectory);
        var pathSpecs = ResolveActionPathSpecs(snapshot, relativePaths, entry => entry.IsStaged);
        var hasHead = RunGit(
            snapshot.RepositoryRoot!,
            false,
            ReadTimeout,
            "rev-parse",
            "--verify",
            "HEAD").Succeeded;
        var arguments = hasHead
            ? BuildArguments("reset", "-q", "HEAD", "--", pathSpecs)
            : BuildArguments("rm", "--cached", "-r", "--ignore-unmatch", "--", pathSpecs);
        var result = RunGit(snapshot.RepositoryRoot!, true, WriteTimeout, arguments);
        if (!result.Succeeded)
        {
            throw GitFailure("取消暂存文件", result);
        }
    }

    public string Commit(string workingDirectory, string message)
    {
        var repository = RequireRepository(workingDirectory);
        var normalizedMessage = NormalizeCommitMessage(message);
        var staged = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "diff",
            "--cached",
            "--name-only",
            "-z",
            "--no-renames",
            "--diff-filter=ACDMRTUXB");
        if (!staged.Succeeded)
        {
            throw GitFailure("检查暂存区", staged);
        }

        var stagedPaths = staged.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (stagedPaths.Length == 0)
        {
            throw new InvalidOperationException("暂存区没有可提交的更改。");
        }

        var outsideWorkspace = stagedPaths
            .Where(path => !TryMapToWorkspace(repository.Root, repository.Workspace, path, out _))
            .Take(5)
            .ToArray();
        if (outsideWorkspace.Length > 0)
        {
            throw new InvalidOperationException(
                $"仓库中还有当前工作目录外的已暂存文件，已阻止提交：{string.Join("、", outsideWorkspace)}");
        }

        var result = RunGit(
            repository.Root,
            true,
            WriteTimeout,
            "commit",
            "-m",
            normalizedMessage);
        if (!result.Succeeded)
        {
            throw GitFailure("创建提交", result);
        }

        var head = RunGit(repository.Root, false, ReadTimeout, "rev-parse", "HEAD");
        if (!head.Succeeded || string.IsNullOrWhiteSpace(head.Output))
        {
            throw new InvalidOperationException("提交已执行，但无法读取新的提交 ID。");
        }

        return head.Output.Trim();
    }

    public void SwitchBranch(string workingDirectory, string branchName)
    {
        var repository = RequireBranchRepository(workingDirectory);
        RequireNoOperation(repository);
        RequireCleanRepository(repository);
        var branch = RequireLocalBranch(repository.Root, branchName);
        var result = RunGit(repository.Root, true, WriteTimeout, "switch", branch);
        if (!result.Succeeded)
        {
            throw GitFailure("切换本地分支", result);
        }
    }

    public void CreateBranch(string workingDirectory, string branchName)
    {
        var repository = RequireBranchRepository(workingDirectory);
        RequireNoOperation(repository);
        RequireCleanRepository(repository);
        var branch = NormalizeBranchName(branchName);
        var valid = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "check-ref-format",
            "--branch",
            branch);
        if (!valid.Succeeded)
        {
            throw new InvalidOperationException("分支名称不符合 Git 规则。");
        }

        if (ReadBranches(repository.Root, null).Any(candidate =>
                string.Equals(candidate.Name, branch, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("同名本地分支已经存在。");
        }

        var result = RunGit(repository.Root, true, WriteTimeout, "switch", "-c", branch);
        if (!result.Succeeded)
        {
            throw GitFailure("创建本地分支", result);
        }
    }

    public void UpdateBranch(string workingDirectory, string strategy, string sourceBranch)
    {
        var repository = RequireBranchRepository(workingDirectory);
        RequireNoOperation(repository);
        RequireCleanRepository(repository);
        var branch = RequireLocalBranch(repository.Root, sourceBranch);
        var current = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "symbolic-ref",
            "--quiet",
            "--short",
            "HEAD");
        if (!current.Succeeded)
        {
            throw new InvalidOperationException("Detached HEAD 状态下不能更新分支。");
        }

        if (string.Equals(current.Output.Trim(), branch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("请选择另一个本地分支。");
        }

        GitCommandResult result;
        if (string.Equals(strategy, "merge", StringComparison.OrdinalIgnoreCase))
        {
            result = RunGit(repository.Root, true, WriteTimeout, "merge", "--no-ff", "--no-edit", branch);
        }
        else if (string.Equals(strategy, "rebase", StringComparison.OrdinalIgnoreCase))
        {
            result = RunGit(repository.Root, true, WriteTimeout, "rebase", branch);
        }
        else
        {
            throw new InvalidOperationException("不支持的分支更新方式。");
        }

        if (!result.Succeeded)
        {
            throw GitFailure(strategy.Equals("merge", StringComparison.OrdinalIgnoreCase) ? "合并分支" : "变基分支", result);
        }
    }

    public void AbortOperation(string workingDirectory)
    {
        var repository = RequireBranchRepository(workingDirectory);
        var operation = ReadOperationState(repository.Root);
        var result = operation switch
        {
            "Merge" => RunGit(repository.Root, true, WriteTimeout, "merge", "--abort"),
            "Rebase" => RunGit(repository.Root, true, WriteTimeout, "rebase", "--abort"),
            _ => throw new InvalidOperationException("当前没有可中止的合并或变基操作。"),
        };
        if (!result.Succeeded)
        {
            throw GitFailure("中止 Git 操作", result);
        }
    }

    private static IReadOnlyList<WorkspaceGitEntry> ParseStatus(
        string output,
        string repositoryRoot,
        string workspace,
        IReadOnlyDictionary<string, GitLineStats> stats)
    {
        var result = new List<WorkspaceGitEntry>();
        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 4) continue;
            var status = record[..2];
            var repositoryPath = record[3..];
            string? originalRepositoryPath = null;
            if ((status.Contains('R') || status.Contains('C')) && index + 1 < records.Length)
            {
                originalRepositoryPath = records[++index];
            }

            if (!TryMapToWorkspace(repositoryRoot, workspace, repositoryPath, out var relativePath)) continue;
            string? originalRelativePath = null;
            if (!string.IsNullOrWhiteSpace(originalRepositoryPath))
            {
                TryMapToWorkspace(repositoryRoot, workspace, originalRepositoryPath, out originalRelativePath);
            }

            var lineStats = stats.GetValueOrDefault(relativePath);
            if (!string.IsNullOrWhiteSpace(originalRelativePath) &&
                stats.TryGetValue(originalRelativePath, out var originalStats))
            {
                lineStats = lineStats.Add(originalStats);
            }

            var isUntracked = status == "??";
            if (isUntracked && lineStats == default &&
                WorkspacePathPolicy.TryResolveCandidate(
                    workspace,
                    Path.Combine(workspace, FromGitPath(relativePath)),
                    out var untrackedPath))
            {
                lineStats = ReadUntrackedStats(untrackedPath);
            }

            result.Add(new WorkspaceGitEntry(
                relativePath,
                originalRelativePath,
                status,
                status[0].ToString(),
                status[1].ToString(),
                ChangeKind(status),
                status[0] is not (' ' or '?'),
                status[1] != ' ' || isUntracked,
                isUntracked,
                lineStats.IsBinary,
                lineStats.Added,
                lineStats.Deleted));
        }

        result.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return result;
    }

    private static IReadOnlyDictionary<string, GitLineStats> ReadDiffStats(
        string repositoryRoot,
        string workspace,
        string pathSpec)
    {
        var result = new Dictionary<string, GitLineStats>(StringComparer.OrdinalIgnoreCase);
        MergeStats(RunGit(
            repositoryRoot,
            false,
            ReadTimeout,
            "diff", "--numstat", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--", pathSpec));
        MergeStats(RunGit(
            repositoryRoot,
            false,
            ReadTimeout,
            "diff", "--cached", "--numstat", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--", pathSpec));
        return result;

        void MergeStats(GitCommandResult command)
        {
            if (!command.Succeeded) return;
            foreach (var record in command.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var firstTab = record.IndexOf('\t');
                var secondTab = firstTab < 0 ? -1 : record.IndexOf('\t', firstTab + 1);
                if (firstTab < 1 || secondTab < 0) continue;
                if (!TryMapToWorkspace(repositoryRoot, workspace, record[(secondTab + 1)..], out var relativePath)) continue;
                var binary = record[..firstTab] == "-" || record[(firstTab + 1)..secondTab] == "-";
                var added = binary || !int.TryParse(record[..firstTab], out var parsedAdded) ? 0 : parsedAdded;
                var deleted = binary || !int.TryParse(record[(firstTab + 1)..secondTab], out var parsedDeleted) ? 0 : parsedDeleted;
                result[relativePath] = result.GetValueOrDefault(relativePath).Add(new GitLineStats(added, deleted, binary));
            }
        }
    }

    private static IReadOnlyList<WorkspaceGitBranch> ReadBranches(string repositoryRoot, string? currentBranch)
    {
        var result = RunGit(
            repositoryRoot,
            false,
            ReadTimeout,
            "for-each-ref",
            "--sort=refname",
            "--format=%(refname:short)%00%(objectname:short)%00%(contents:subject)",
            "refs/heads");
        if (!result.Succeeded) return [];

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\0', 3))
            .Where(fields => fields.Length >= 2 && !string.IsNullOrWhiteSpace(fields[0]))
            .Select(fields => new WorkspaceGitBranch(
                fields[0],
                fields[1],
                fields.ElementAtOrDefault(2) ?? string.Empty,
                string.Equals(fields[0], currentBranch, StringComparison.Ordinal)))
            .ToArray();
    }

    private static GitLineStats ReadUntrackedStats(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumUntrackedDiffBytes ||
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return default;
            }

            var bytes = File.ReadAllBytes(path);
            var isBinary = bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);
            if (isBinary) return new GitLineStats(0, 0, true);
            var lines = bytes.Count(value => value == (byte)'\n');
            if (bytes.Length > 0 && bytes[^1] != (byte)'\n') lines++;
            return new GitLineStats(lines, 0, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static WorkspaceGitDiff BuildUntrackedDiff(string workspace, string relativePath, string target)
    {
        try
        {
            var info = new FileInfo(target);
            if (!info.Exists || File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
            {
                return new WorkspaceGitDiff(workspace, relativePath, null, false, false);
            }

            if (info.Length > MaximumUntrackedDiffBytes)
            {
                return new WorkspaceGitDiff(workspace, relativePath, null, false, true);
            }

            var diff = UnifiedDiffBuilder.Build([], File.ReadAllBytes(target), relativePath);
            return new WorkspaceGitDiff(workspace, relativePath, diff.Text, diff.IsBinary, diff.Truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法读取未跟踪文件的 Diff。", exception);
        }
    }

    private static IReadOnlyList<CommitFileMetadata> ParseCommitFileMetadata(
        string output,
        string repositoryRoot,
        string workspace)
    {
        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<CommitFileMetadata>();
        for (var index = 0; index < records.Length;)
        {
            var status = records[index++];
            if (status.Length == 0 || index >= records.Length)
            {
                throw new InvalidOperationException("提交文件列表格式无效。");
            }

            string? originalRepositoryPath = null;
            string repositoryPath;
            if (status[0] is 'R' or 'C')
            {
                if (index + 1 >= records.Length)
                {
                    throw new InvalidOperationException("提交重命名记录格式无效。");
                }

                originalRepositoryPath = records[index++];
                repositoryPath = records[index++];
            }
            else
            {
                repositoryPath = records[index++];
            }

            var hasCurrentPath = TryMapToWorkspace(
                repositoryRoot,
                workspace,
                repositoryPath,
                out var relativePath);
            var originalRelativePath = string.Empty;
            var hasOriginalPath = !string.IsNullOrWhiteSpace(originalRepositoryPath) &&
                                  TryMapToWorkspace(
                                      repositoryRoot,
                                      workspace,
                                      originalRepositoryPath,
                                      out originalRelativePath);
            if (!hasCurrentPath && !hasOriginalPath)
            {
                continue;
            }

            if (!hasCurrentPath)
            {
                relativePath = originalRelativePath;
            }

            result.Add(new CommitFileMetadata(
                repositoryPath,
                originalRepositoryPath,
                relativePath,
                hasOriginalPath ? originalRelativePath : null,
                ChangeKind(status)));
        }

        return result;
    }

    private static IReadOnlyList<string> SplitCommitPatchSections(string patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return [];
        }

        var starts = new List<int>();
        var searchStart = 0;
        while (searchStart < patch.Length)
        {
            var index = patch.IndexOf("diff --git ", searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            if (index == 0 || patch[index - 1] == '\n')
            {
                starts.Add(index);
            }

            searchStart = index + 1;
        }

        var result = new List<string>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] : patch.Length;
            result.Add(patch[start..end].TrimEnd());
        }

        return result;
    }

    private static string ReadCommitFilePatch(
        RepositoryContext repository,
        string hash,
        CommitFileMetadata file)
    {
        var arguments = new List<string>
        {
            "show",
            "--format=",
            "--patch",
            "--no-ext-diff",
            "--no-textconv",
            "--binary",
            "--find-renames",
            hash,
            "--",
            file.RepositoryPath,
        };
        if (!string.IsNullOrWhiteSpace(file.OriginalRepositoryPath))
        {
            arguments.Add(file.OriginalRepositoryPath);
        }

        var result = RunGit(repository.Root, false, ReadTimeout, arguments);
        if (!result.Succeeded)
        {
            throw GitFailure($"读取 {file.RelativePath} 的提交 Diff", result);
        }

        return result.Output.TrimEnd();
    }

    private static (int? Added, int? Deleted) CountDiffLines(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return (0, 0);
        }

        var added = 0;
        var deleted = 0;
        var insideHunk = false;
        using var reader = new StringReader(diff);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                insideHunk = false;
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                insideHunk = true;
            }
            else if (insideHunk && line.StartsWith('+'))
            {
                added++;
            }
            else if (insideHunk && line.StartsWith('-'))
            {
                deleted++;
            }
        }

        return (added, deleted);
    }

    private static WorkspaceGitDiff CreateDiff(string workspace, string relativePath, string diff)
    {
        var isBinary = diff.Contains("GIT binary patch", StringComparison.Ordinal) ||
                       diff.Contains("Binary files ", StringComparison.Ordinal);
        var truncated = diff.Length > MaximumDiffCharacters;
        return new WorkspaceGitDiff(
            workspace,
            relativePath,
            truncated ? diff[..MaximumDiffCharacters] : diff,
            isBinary,
            truncated);
    }

    private static IReadOnlyList<string> ResolveActionPathSpecs(
        WorkspaceGitSnapshot snapshot,
        IReadOnlyList<string> relativePaths,
        Func<WorkspaceGitEntry, bool> predicate)
    {
        if (!snapshot.IsRepository || string.IsNullOrWhiteSpace(snapshot.RepositoryRoot))
        {
            throw new InvalidOperationException("当前工作目录不是 Git 仓库。");
        }

        var requested = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0 || requested.Length > MaximumActionPaths)
        {
            throw new InvalidOperationException($"请选择 1–{MaximumActionPaths} 个文件。");
        }

        var pathSpecs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relativePath in requested)
        {
            var entry = snapshot.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !predicate(entry))
            {
                throw new InvalidOperationException($"文件状态已经变化，请刷新后重试：{relativePath}");
            }

            AddPath(entry.RelativePath);
            if (!string.IsNullOrWhiteSpace(entry.OriginalRelativePath))
            {
                AddPath(entry.OriginalRelativePath);
            }
        }

        return pathSpecs.ToArray();

        void AddPath(string relativePath)
        {
            if (!WorkspacePathPolicy.TryResolveCandidate(
                    snapshot.WorkingDirectory,
                    Path.Combine(snapshot.WorkingDirectory, FromGitPath(relativePath)),
                    out var target))
            {
                throw new InvalidOperationException("Git 文件路径不在当前工作区内。");
            }

            pathSpecs.Add(ToGitPath(Path.GetRelativePath(snapshot.RepositoryRoot, target)));
        }
    }

    private static RepositoryContext RequireRepository(string workingDirectory) =>
        TryOpenRepository(RequireWorkspace(workingDirectory)) ??
        throw new InvalidOperationException("当前工作目录不是 Git 仓库。");

    private static RepositoryContext RequireBranchRepository(string workingDirectory)
    {
        var repository = RequireRepository(workingDirectory);
        if (!repository.CanManageBranches)
        {
            throw new InvalidOperationException("工作目录只是仓库的一部分；请打开仓库根目录后再操作本地分支。");
        }

        return repository;
    }

    private static RepositoryContext? TryOpenRepository(string workspace)
    {
        var rootResult = RunGit(workspace, false, ReadTimeout, "rev-parse", "--show-toplevel");
        if (!rootResult.Succeeded || string.IsNullOrWhiteSpace(rootResult.Output))
        {
            return null;
        }

        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootResult.Output.Trim()));
        if (!WorkspacePathPolicy.TryResolveCandidate(repositoryRoot, workspace, out _))
        {
            return null;
        }

        var relative = Path.GetRelativePath(repositoryRoot, workspace);
        return new RepositoryContext(
            workspace,
            repositoryRoot,
            relative == "." ? "." : ToGitPath(relative),
            string.Equals(repositoryRoot, workspace, StringComparison.OrdinalIgnoreCase));
    }

    private static string RequireLocalBranch(string repositoryRoot, string branchName)
    {
        var branch = NormalizeBranchName(branchName);
        if (!ReadBranches(repositoryRoot, null).Any(candidate =>
                string.Equals(candidate.Name, branch, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("所选本地分支不存在。");
        }

        return branch;
    }

    private static void RequireNoOperation(RepositoryContext repository)
    {
        var operation = ReadOperationState(repository.Root);
        if (operation != "None")
        {
            throw new InvalidOperationException($"请先完成或中止当前 {operation} 操作。");
        }
    }

    private static void RequireCleanRepository(RepositoryContext repository)
    {
        var status = RunGit(
            repository.Root,
            false,
            ReadTimeout,
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=normal");
        if (!status.Succeeded)
        {
            throw GitFailure("检查 Git 工作区状态", status);
        }

        if (status.Output.Length > 0)
        {
            throw new InvalidOperationException("切换或更新分支前，请先提交或处理仓库中的全部更改。");
        }
    }

    private static string ReadOperationState(string repositoryRoot)
    {
        var gitDirectory = RunGit(
            repositoryRoot,
            false,
            ReadTimeout,
            "rev-parse",
            "--absolute-git-dir");
        if (!gitDirectory.Succeeded || string.IsNullOrWhiteSpace(gitDirectory.Output))
        {
            return "None";
        }

        var path = Path.GetFullPath(gitDirectory.Output.Trim());
        if (Directory.Exists(Path.Combine(path, "rebase-merge")) ||
            Directory.Exists(Path.Combine(path, "rebase-apply")))
        {
            return "Rebase";
        }

        return File.Exists(Path.Combine(path, "MERGE_HEAD")) ? "Merge" : "None";
    }

    private static string ReadStagedFingerprint(string repositoryRoot)
    {
        var raw = RunGit(
            repositoryRoot,
            false,
            ReadTimeout,
            "diff",
            "--cached",
            "--raw",
            "-z",
            "--no-renames");
        if (!raw.Succeeded)
        {
            throw GitFailure("读取暂存区指纹", raw);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.Output)))
            .ToLowerInvariant();
    }

    private static bool TryMapToWorkspace(
        string repositoryRoot,
        string workspace,
        string repositoryPath,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (!WorkspacePathPolicy.TryResolveCandidate(
                workspace,
                Path.Combine(repositoryRoot, FromGitPath(repositoryPath)),
                out var target))
        {
            return false;
        }

        var relative = Path.GetRelativePath(workspace, target);
        if (relative == ".") return false;
        relativePath = ToGitPath(relative);
        return true;
    }

    private static string RequireWorkspace(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{workspace}");
        }

        return workspace;
    }

    private static string NormalizeCommitMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = message.Trim();
        if (result.Length == 0)
        {
            throw new InvalidOperationException("请输入提交信息。");
        }

        if (result.Length > 4000 || result.Contains('\0'))
        {
            throw new InvalidOperationException("提交信息无效或超过 4000 个字符。");
        }

        return result;
    }

    private static string NormalizeCommitHash(string commitHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitHash);
        var result = commitHash.Trim();
        if (result.Length is < 4 or > 64 || !result.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("提交 ID 无效。");
        }

        return result;
    }

    private static string NormalizeBranchName(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        var result = branchName.Trim();
        if (result.Length > 255 || result.Contains('\0'))
        {
            throw new InvalidOperationException("分支名称无效。");
        }

        return result;
    }

    private static string ChangeKind(string status)
    {
        if (status.Contains('D')) return "Deleted";
        if (status == "??" || status.Contains('A')) return "Added";
        if (status.Contains('R')) return "Renamed";
        if (status.Contains('C')) return "Copied";
        if (status.Contains('U')) return "Unmerged";
        return "Modified";
    }

    private static string FromGitPath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);

    private static string ToGitPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static IReadOnlyList<string> BuildArguments(
        string first,
        string second,
        string third,
        IReadOnlyList<string> remaining)
    {
        var result = new List<string> { first, second, third };
        result.AddRange(remaining);
        return result;
    }

    private static IReadOnlyList<string> BuildArguments(
        string first,
        string second,
        string third,
        string fourth,
        IReadOnlyList<string> remaining)
    {
        var result = new List<string> { first, second, third, fourth };
        result.AddRange(remaining);
        return result;
    }

    private static IReadOnlyList<string> BuildArguments(
        string first,
        string second,
        string third,
        string fourth,
        string fifth,
        IReadOnlyList<string> remaining)
    {
        var result = new List<string> { first, second, third, fourth, fifth };
        result.AddRange(remaining);
        return result;
    }

    private static GitCommandResult RunGit(
        string workingDirectory,
        bool write,
        TimeSpan timeout,
        params string[] arguments) =>
        RunGit(workingDirectory, write, timeout, (IEnumerable<string>)arguments);

    private static GitCommandResult RunGit(
        string workingDirectory,
        bool write,
        TimeSpan timeout,
        IEnumerable<string> arguments)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.Environment["GIT_OPTIONAL_LOCKS"] = write ? "1" : "0";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_EDITOR"] = "true";
            startInfo.Environment["GIT_SEQUENCE_EDITOR"] = "true";
            startInfo.Environment["GIT_MERGE_AUTOEDIT"] = "no";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.fsmonitor=false");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.quotepath=false");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("diff.external=");
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            process = Process.Start(startInfo);
            if (process is null) return new GitCommandResult(-1, string.Empty, "无法启动 git。");
            using var cancellation = new CancellationTokenSource(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
            process.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return new GitCommandResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new GitCommandResult(-1, string.Empty, "Git 操作超时。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new GitCommandResult(-1, string.Empty, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Best effort after a timeout.
        }
    }

    private static InvalidOperationException GitFailure(string action, GitCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        detail = detail.Trim();
        if (detail.Length > 4000) detail = detail[..4000];
        return new InvalidOperationException(
            detail.Length == 0 ? $"{action}失败。" : $"{action}失败：{detail}");
    }

    private sealed record RepositoryContext(
        string Workspace,
        string Root,
        string PathSpec,
        bool CanManageBranches);

    private sealed record CommitFileMetadata(
        string RepositoryPath,
        string? OriginalRepositoryPath,
        string RelativePath,
        string? OriginalRelativePath,
        string Kind);

    private readonly record struct GitCommandResult(int ExitCode, string Output, string Error)
    {
        public bool Succeeded => ExitCode == 0;
    }

    private readonly record struct GitLineStats(int Added, int Deleted, bool IsBinary)
    {
        public GitLineStats Add(GitLineStats other) =>
            new(Added + other.Added, Deleted + other.Deleted, IsBinary || other.IsBinary);
    }
}

public sealed record WorkspaceGitEntry(
    string RelativePath,
    string? OriginalRelativePath,
    string Status,
    string IndexStatus,
    string WorkingTreeStatus,
    string Kind,
    bool IsStaged,
    bool IsUnstaged,
    bool IsUntracked,
    bool IsBinary,
    int AddedLines,
    int DeletedLines);

public sealed record WorkspaceGitBranch(
    string Name,
    string ShortHash,
    string Subject,
    bool IsCurrent);

public sealed record WorkspaceGitSnapshot(
    string WorkingDirectory,
    bool IsRepository,
    string? RepositoryRoot,
    string? Branch,
    bool IsDetached,
    IReadOnlyList<WorkspaceGitBranch> Branches,
    string OperationState,
    bool CanManageBranches,
    IReadOnlyList<WorkspaceGitEntry> Entries,
    string? StagedFingerprint = null);

public sealed record WorkspaceGitCommitMessageContext(
    string WorkingDirectory,
    string RepositoryName,
    string Branch,
    string StagedFingerprint,
    IReadOnlyList<string> RelativePaths,
    IReadOnlyList<string> RecentSubjects,
    string DiffText,
    bool Truncated);

public sealed record WorkspaceGitCommit(
    string Hash,
    string ShortHash,
    string Subject,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> Parents);

public sealed record WorkspaceGitHistorySnapshot(
    string WorkingDirectory,
    IReadOnlyList<WorkspaceGitCommit> Entries,
    bool HasMore);

public sealed record WorkspaceGitDiff(
    string WorkingDirectory,
    string RelativePath,
    string? DiffText,
    bool IsBinary,
    bool Truncated);

public sealed record WorkspaceGitCommitDiff(
    string WorkingDirectory,
    string Hash,
    string ShortHash,
    string Subject,
    IReadOnlyList<WorkspaceGitCommitFileDiff> Files,
    bool Truncated);

public sealed record WorkspaceGitCommitFileDiff(
    string RelativePath,
    string? OriginalRelativePath,
    string Status,
    int? AddedLines,
    int? DeletedLines,
    string? DiffText,
    bool IsBinary,
    bool Truncated);
