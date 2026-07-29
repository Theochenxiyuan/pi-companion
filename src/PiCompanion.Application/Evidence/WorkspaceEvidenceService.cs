using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.Files;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Evidence;

namespace PiCompanion.Application.Evidence;

public sealed partial class WorkspaceEvidenceService : IWorkspaceEvidenceService
{
    private const int MaximumWatcherCandidates = 512;
    private const int MaximumOutputSummaryCharacters = 12_000;
    private const int MaximumDiffInputBytes = 1024 * 1024;
    private const int MaximumDiffCharacters = 256 * 1024;
    private readonly object _gate = new();
    private readonly IRunEventStore _store;
    private readonly string _backupDirectory;
    private readonly string _recoveryTrashDirectory;
    private readonly Dictionary<Guid, ActiveRun> _activeRuns = [];
    private bool _disposed;

    public WorkspaceEvidenceService(
        IRunEventStore store,
        string backupDirectory,
        string recoveryTrashDirectory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _recoveryTrashDirectory = Path.GetFullPath(recoveryTrashDirectory);
        Directory.CreateDirectory(_backupDirectory);
        Directory.CreateDirectory(_recoveryTrashDirectory);
    }

    public static WorkspaceEvidenceService CreateDefault(IRunEventStore store)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion");
        return new WorkspaceEvidenceService(
            store,
            Path.Combine(dataDirectory, "backups"),
            Path.Combine(dataDirectory, "recovery-trash"));
    }

    public event Action<Guid>? EvidenceChanged;

    public void BeginRun(Guid taskId, Guid runId, string workingDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var root = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{root}");
        }

        lock (_gate)
        {
            if (_activeRuns.Remove(runId, out var previous))
            {
                previous.StopWatcher();
            }
            var git = CaptureGit(root);
            var metadata = new RunEvidenceMetadata(
                runId,
                taskId,
                root,
                DateTimeOffset.UtcNow,
                git.IsRepository,
                git.Root,
                git.Head,
                null,
                git.Status,
                null,
                false,
                false,
                false,
                null);
            _store.UpsertRunEvidenceMetadata(metadata);
            var active = new ActiveRun(metadata, ParseGitStatus(git.Status));
            _activeRuns.Add(runId, active);
            active.StartWatcher();
        }

        EvidenceChanged?.Invoke(runId);
    }

    public void RecordToolExecution(AgentToolExecution execution)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metadata = _store.GetRunEvidenceMetadata(execution.RunId);
        if (metadata is null)
        {
            return;
        }

        if (string.Equals(execution.ToolName, "bash", StringComparison.OrdinalIgnoreCase))
        {
            RecordCommand(metadata, execution);
            lock (_gate)
            {
                if (_activeRuns.TryGetValue(execution.RunId, out var active))
                {
                    active.ShellObserved = true;
                }
            }
        }
        else if (execution.ToolName is "edit" or "write")
        {
            RecordFileTool(metadata, execution);
        }

        EvidenceChanged?.Invoke(execution.RunId);
    }

    public void FinalizeRun(Guid runId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metadata = _store.GetRunEvidenceMetadata(runId);
        if (metadata is null || metadata.Finalized)
        {
            return;
        }

        IReadOnlyDictionary<string, WatcherChangeTypes> candidates = new Dictionary<string, WatcherChangeTypes>();
        IReadOnlyDictionary<string, string> statusBefore = ParseGitStatus(metadata.GitStatusBefore);
        var watcherOverflowed = metadata.WatcherOverflowed;
        var shellObserved = metadata.ShellObserved;
        lock (_gate)
        {
            if (_activeRuns.Remove(runId, out var active))
            {
                active.StopWatcher();
                candidates = active.Candidates.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                watcherOverflowed |= active.WatcherOverflowed;
                shellObserved |= active.ShellObserved;
                statusBefore = active.GitStatusBefore;
            }
        }

        var gitAfter = CaptureGit(metadata.WorkingDirectory);
        var statusAfter = ParseGitStatus(gitAfter.Status);
        MaterializeObservedChanges(metadata, statusBefore, statusAfter, candidates);

        var finalized = metadata with
        {
            HeadAfter = gitAfter.Head,
            GitStatusAfter = gitAfter.Status,
            Finalized = true,
            WatcherOverflowed = watcherOverflowed,
            ShellObserved = shellObserved,
            FinalizedAt = DateTimeOffset.UtcNow,
        };
        _store.UpsertRunEvidenceMetadata(finalized);
        _store.ReplaceEvidenceWarnings(runId, BuildWarnings(finalized, statusBefore));
        EvidenceChanged?.Invoke(runId);
    }

    public RunEvidenceSnapshot GetRunEvidence(Guid runId) => _store.GetRunEvidence(runId);

    public FileDiffEvidence? GetFileDiff(Guid fileChangeId)
    {
        var file = _store.GetFileChange(fileChangeId);
        return file is null
            ? null
            : new FileDiffEvidence(
                file.Id,
                file.RunId,
                file.Path,
                file.DiffText,
                file.IsBinary,
                file.DiffTruncated,
                file.Source);
    }

    public RecoveryResult RestoreFile(Guid fileChangeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var file = _store.GetFileChange(fileChangeId) ??
            throw new InvalidOperationException("未找到文件变化证据。");
        var metadata = _store.GetRunEvidenceMetadata(file.RunId) ??
            throw new InvalidOperationException("未找到对应 Run 的证据基线。");
        if (file.Recovery != RecoveryAvailability.Available)
        {
            return SaveRecoveryOutcome(file, false, file.Recovery, "该文件没有可用的确定性恢复基线，未执行恢复。");
        }
        try
        {
        var target = EnsureWorkspacePath(file.Path, metadata.WorkingDirectory);
        if (HasReparsePointBelowRoot(metadata.WorkingDirectory, target))
        {
            return SaveRecoveryOutcome(file, false, RecoveryAvailability.Conflict, "文件已变成链接或重解析点，已停止恢复。");
        }

        if (string.IsNullOrWhiteSpace(file.AfterHash) || !File.Exists(target))
        {
            return SaveRecoveryOutcome(file, false, RecoveryAvailability.Conflict, "当前文件不存在或缺少 Agent 修改后的 Hash。");
        }

        if (!CurrentFileMatches(file, target))
        {
            return SaveRecoveryOutcome(file, false, RecoveryAvailability.Conflict, "文件在 Agent 修改后又发生了变化，未覆盖当前内容。");
        }

        if (!file.ExistedBefore)
        {
            if (!CurrentFileMatches(file, target) || HasReparsePointBelowRoot(metadata.WorkingDirectory, target))
            {
                return SaveRecoveryOutcome(file, false, RecoveryAvailability.Conflict, "文件在恢复确认后又发生了变化，未移动当前内容。");
            }

            var relative = Path.GetRelativePath(metadata.WorkingDirectory, target);
            var destination = Path.Combine(
                _recoveryTrashDirectory,
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture),
                relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(target, destination);
            return SaveRecoveryOutcome(file, true, RecoveryAvailability.Recovered, $"新建文件已移至恢复暂存区：{destination}");
        }

        if (string.IsNullOrWhiteSpace(file.BackupObjectPath) || !File.Exists(file.BackupObjectPath))
        {
            return SaveRecoveryOutcome(file, false, RecoveryAvailability.Unavailable, "修改前备份不存在，无法确定性恢复。");
        }

        using var original = new FileStream(file.BackupObjectPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        if (!string.Equals(originalHash, file.BeforeHash, StringComparison.OrdinalIgnoreCase))
        {
            return SaveRecoveryOutcome(file, false, RecoveryAvailability.Unavailable, "修改前备份校验失败，未写入目标文件。");
        }

        original.Position = 0;

        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.restore");
        try
        {
            using (var restored = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                original.CopyTo(restored);
                restored.Flush(flushToDisk: true);
            }
            if (!CurrentFileMatches(file, target) || HasReparsePointBelowRoot(metadata.WorkingDirectory, target))
            {
                return SaveRecoveryOutcome(file, false, RecoveryAvailability.Conflict, "文件在恢复确认后又发生了变化，未覆盖当前内容。");
            }

            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return SaveRecoveryOutcome(file, true, RecoveryAvailability.Recovered, "已恢复 Agent 修改前的文件内容。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SaveRecoveryOutcome(file, false, RecoveryAvailability.Conflict, $"恢复未完成：{exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            DisposeActiveRuns();
            _disposed = true;
        }
    }

    private void RecordCommand(RunEvidenceMetadata metadata, AgentToolExecution execution)
    {
        using var arguments = JsonDocument.Parse(execution.ArgumentsJson);
        using var result = JsonDocument.Parse(execution.ResultJson);
        var command = GetOptionalString(arguments.RootElement, "command") ?? string.Empty;
        var output = ExtractTextContent(result.RootElement);
        var cancelled = execution.IsError && ContainsAny(output, "aborted", "cancelled", "canceled", "已取消", "中止");
        var timedOut = execution.IsError && ContainsAny(output, "timed out", "timeout", "超时");
        var exitCode = execution.IsError ? ParseExitCode(output) : 0;
        var (isTest, framework) = ClassifyTestCommand(command);
        var status = !isTest
            ? TestEvidenceStatus.NotRun
            : cancelled || timedOut || exitCode is null
                ? TestEvidenceStatus.Unknown
                : exitCode == 0
                    ? TestEvidenceStatus.Passed
                    : TestEvidenceStatus.Failed;
        var id = StableGuid($"{execution.RunId:D}|command|{execution.ToolCallId}");
        var fullOutputPath = TryGetNestedString(result.RootElement, "details", "fullOutputPath");
        var evidence = new CommandExecutionEvidence(
            id,
            execution.RunId,
            execution.ToolCallId,
            command,
            metadata.WorkingDirectory,
            execution.StartedAt,
            execution.CompletedAt - execution.StartedAt,
            exitCode,
            cancelled,
            timedOut,
            Truncate(output, MaximumOutputSummaryCharacters),
            fullOutputPath,
            isTest,
            framework,
            status);
        _store.UpsertCommandExecution(evidence);
        if (isTest)
        {
            _store.UpsertTestResult(new TestResultEvidence(
                StableGuid($"{execution.RunId:D}|test|{execution.ToolCallId}"),
                execution.RunId,
                id,
                command,
                framework ?? "unknown",
                status,
                exitCode,
                execution.CompletedAt));
        }
    }

    private void RecordFileTool(RunEvidenceMetadata metadata, AgentToolExecution execution)
    {
        using var arguments = JsonDocument.Parse(execution.ArgumentsJson);
        using var result = JsonDocument.Parse(execution.ResultJson);
        var requestedPath = GetOptionalString(arguments.RootElement, "path");
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return;
        }

        var target = EnsureWorkspacePath(
            Path.IsPathFullyQualified(requestedPath)
                ? requestedPath
                : Path.Combine(metadata.WorkingDirectory, requestedPath),
            metadata.WorkingDirectory);
        var manifest = ReadBackupManifest(execution.RunId)
            .Where(record => ManifestMatchesTarget(record, target))
            .OrderBy(record => record.BackedUpAt)
            .ToArray();
        var firstBackup = manifest.FirstOrDefault();
        var existing = _store.GetRunEvidence(execution.RunId).Files
            .FirstOrDefault(file => string.Equals(file.Path, target, StringComparison.OrdinalIgnoreCase));
        var baselineCaptured = existing?.Confidence == EvidenceConfidence.Confirmed || firstBackup is not null;
        var existsAfter = File.Exists(target);
        var after = existsAfter ? CaptureFile(target) : CapturedFile.Missing;
        var afterHash = existsAfter ? after.Hash : null;
        var existedBefore = existing?.ExistedBefore ?? firstBackup?.Existed ?? firstBackup?.Sha256 is not null;
        var beforeHash = existing?.BeforeHash ?? firstBackup?.Sha256;
        var beforeSize = existing?.BeforeSize ?? firstBackup?.Size;
        var backupObjectPath = existing?.BackupObjectPath ??
            (!IsSha256(beforeHash)
                ? null
                : Path.Combine(_backupDirectory, "objects", beforeHash![..2], beforeHash));
        if (existing is null && firstBackup is null && execution.IsError && !existsAfter)
        {
            return;
        }
        if (existing is null && existedBefore && existsAfter &&
            !string.IsNullOrWhiteSpace(beforeHash) &&
            string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var before = CapturedFile.Missing;
        if (existedBefore && !string.IsNullOrWhiteSpace(backupObjectPath) && File.Exists(backupObjectPath))
        {
            before = CaptureFile(backupObjectPath);
        }

        var relative = Path.GetRelativePath(metadata.WorkingDirectory, target);
        var nativePatch = TryGetNestedString(result.RootElement, "details", "patch");
        var diff = existing is null && execution.ToolName == "edit" && !string.IsNullOrWhiteSpace(nativePatch)
            ? NormalizeNativePatch(nativePatch)
            : baselineCaptured
                ? BuildCapturedDiff(before, after, relative)
                : new DiffBuildResult(null, after.IsBinary, false);
        var kind = !baselineCaptured
            ? FileChangeKind.Unknown
            : !existedBefore && existsAfter
            ? FileChangeKind.Added
            : existedBefore && !existsAfter
                ? FileChangeKind.Deleted
                : FileChangeKind.Modified;
        var recovery = baselineCaptured && existsAfter && (!existedBefore || !string.IsNullOrWhiteSpace(backupObjectPath) && File.Exists(backupObjectPath))
            ? RecoveryAvailability.Available
            : RecoveryAvailability.Unavailable;
        var evidence = new FileChangeEvidence(
            existing?.Id ?? StableGuid($"{execution.RunId:D}|file|{target.ToUpperInvariant()}"),
            execution.RunId,
            target,
            relative,
            kind,
            baselineCaptured ? EvidenceConfidence.Confirmed : EvidenceConfidence.Observed,
            execution.ToolName == "edit" && existing is null && !string.IsNullOrWhiteSpace(nativePatch)
                ? "PiEditPatch"
                : "BackupComparison",
            execution.ToolCallId,
            existedBefore,
            beforeHash,
            afterHash,
            beforeSize,
            existsAfter ? after.Size : null,
            backupObjectPath,
            diff.IsBinary,
            diff.Text,
            diff.Truncated,
            recovery,
            recovery == RecoveryAvailability.Available ? "当前内容 Hash 匹配时可恢复" : "没有可靠的修改前内容",
            execution.CompletedAt);
        _store.UpsertFileChange(evidence);
    }

    private void MaterializeObservedChanges(
        RunEvidenceMetadata metadata,
        IReadOnlyDictionary<string, string> statusBefore,
        IReadOnlyDictionary<string, string> statusAfter,
        IReadOnlyDictionary<string, WatcherChangeTypes> candidates)
    {
        var confirmed = _store.GetRunEvidence(metadata.RunId).Files
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, status) in statusAfter)
        {
            if (!WorkspacePathPolicy.TryResolveCandidate(
                    metadata.WorkingDirectory,
                    Path.Combine(metadata.WorkingDirectory, relativePath),
                    out var target))
            {
                continue;
            }

            if (confirmed.ContainsKey(target))
            {
                continue;
            }

            var exists = File.Exists(target);
            var captured = exists ? CaptureFile(target) : CapturedFile.Missing;
            var gitDiff = GetGitDiff(metadata.WorkingDirectory, relativePath, status, captured);
            var change = new FileChangeEvidence(
                StableGuid($"{metadata.RunId:D}|file|{target.ToUpperInvariant()}"),
                metadata.RunId,
                target,
                relativePath,
                GitChangeKind(status, exists),
                candidates.ContainsKey(target)
                    ? EvidenceConfidence.Observed
                    : statusBefore.ContainsKey(relativePath)
                        ? EvidenceConfidence.PreExisting
                        : EvidenceConfidence.Observed,
                "GitWorkspace",
                null,
                statusBefore.ContainsKey(relativePath),
                null,
                exists ? captured.Hash : null,
                null,
                exists ? captured.Size : null,
                null,
                captured.IsBinary || gitDiff.IsBinary,
                gitDiff.Text,
                gitDiff.Truncated,
                RecoveryAvailability.Unavailable,
                "Shell/Git 变化没有修改前字节备份",
                DateTimeOffset.UtcNow);
            _store.UpsertFileChange(change);
            confirmed[target] = change;
        }

        foreach (var (target, changeType) in candidates)
        {
            if (confirmed.ContainsKey(target) || !IsInsideWorkspace(target, metadata.WorkingDirectory))
            {
                continue;
            }

            var exists = File.Exists(target);
            if (Directory.Exists(target))
            {
                continue;
            }

            var captured = exists ? CaptureFile(target) : CapturedFile.Missing;
            _store.UpsertFileChange(new FileChangeEvidence(
                StableGuid($"{metadata.RunId:D}|file|{target.ToUpperInvariant()}"),
                metadata.RunId,
                target,
                Path.GetRelativePath(metadata.WorkingDirectory, target),
                ClassifyWatcherChange(changeType, exists),
                EvidenceConfidence.Observed,
                "FileSystemWatcher",
                null,
                false,
                null,
                exists ? captured.Hash : null,
                null,
                exists ? captured.Size : null,
                null,
                captured.IsBinary,
                null,
                captured.DiffUnavailable,
                RecoveryAvailability.Unavailable,
                "Watcher 只能证明观察到路径变化，不能提供完整修改前内容",
                DateTimeOffset.UtcNow));
        }
    }

    private static FileChangeKind ClassifyWatcherChange(WatcherChangeTypes changeType, bool exists)
    {
        // FileSystemWatcher can report Created, Changed, and Deleted for the
        // same path while an installer stages and atomically replaces content.
        // The final filesystem state is authoritative: a path that still
        // exists must never be presented as deleted.
        if (!exists) return FileChangeKind.Deleted;
        if (changeType.HasFlag(WatcherChangeTypes.Created)) return FileChangeKind.Added;
        if (changeType.HasFlag(WatcherChangeTypes.Changed) ||
            changeType.HasFlag(WatcherChangeTypes.Deleted))
        {
            return FileChangeKind.Modified;
        }

        return FileChangeKind.Unknown;
    }

    private IReadOnlyList<EvidenceWarning> BuildWarnings(
        RunEvidenceMetadata metadata,
        IReadOnlyDictionary<string, string> statusBefore)
    {
        var warnings = new List<EvidenceWarning>();
        void Add(string code, string message) => warnings.Add(new EvidenceWarning(
            StableGuid($"{metadata.RunId:D}|warning|{code}"),
            metadata.RunId,
            code,
            message,
            DateTimeOffset.UtcNow));

        if (metadata.ShellObserved)
        {
            Add("shell-coverage", "Shell 可以绕过 edit/write；工作目录外、忽略文件和未被 Watcher 捕获的变化可能不完整。");
        }
        if (metadata.WatcherOverflowed)
        {
            Add("watcher-overflow", "文件监视不可用、缓冲区溢出或候选超过上限，变化列表可能不完整。");
        }
        if (statusBefore.Count > 0)
        {
            Add("git-dirty-baseline", "Run 开始前 Git 工作区已有变化；这些变化不会自动归因给 Agent。");
        }
        if (_store.GetRunEvidence(metadata.RunId).Files.Any(file =>
                file.ToolCallId is not null && file.Confidence != EvidenceConfidence.Confirmed))
        {
            Add("tool-baseline-missing", "检测到 edit/write 结果，但没有可靠的修改前备份；Diff 或恢复能力可能不完整。");
        }
        if (!string.Equals(metadata.HeadBefore, metadata.HeadAfter, StringComparison.Ordinal))
        {
            Add("git-head-changed", "Run 执行期间 Git HEAD 发生变化，Git 基线归属可能不准确。");
        }

        return warnings;
    }

    private RecoveryResult SaveRecoveryOutcome(
        FileChangeEvidence file,
        bool succeeded,
        RecoveryAvailability status,
        string message)
    {
        var updated = file with { Recovery = status, RecoveryMessage = message, UpdatedAt = DateTimeOffset.UtcNow };
        _store.UpsertFileChange(updated);
        _store.AppendRecoveryAction(new RecoveryActionEvidence(
            Guid.NewGuid(),
            file.RunId,
            file.Id,
            file.Path,
            succeeded ? "Succeeded" : status.ToString(),
            message,
            DateTimeOffset.UtcNow));
        EvidenceChanged?.Invoke(file.RunId);
        return new RecoveryResult(succeeded, status, message, updated);
    }

    private IReadOnlyList<BackupManifestRecord> ReadBackupManifest(Guid runId)
    {
        var path = Path.Combine(_backupDirectory, "manifests", $"{runId:D}.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var records = new List<BackupManifestRecord>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<BackupManifestRecord>(line, JsonOptions) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }

    private static bool ManifestMatchesTarget(BackupManifestRecord record, string target)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(record.OriginalPath) &&
                   string.Equals(Path.GetFullPath(record.OriginalPath), target, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static GitCapture CaptureGit(string workingDirectory)
    {
        var root = RunGit(workingDirectory, "rev-parse", "--show-toplevel");
        if (!root.Succeeded || string.IsNullOrWhiteSpace(root.Output))
        {
            return new GitCapture(false, null, null, null);
        }

        var head = RunGit(workingDirectory, "rev-parse", "--verify", "HEAD");
        var status = RunGit(workingDirectory, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".");
        return new GitCapture(
            true,
            root.Output.Trim(),
            head.Succeeded ? head.Output.Trim() : null,
            status.Succeeded ? status.Output : null);
    }

    private static DiffBuildResult GetGitDiff(
        string workingDirectory,
        string relativePath,
        string status,
        CapturedFile captured)
    {
        if (status.StartsWith("??", StringComparison.Ordinal) && File.Exists(Path.Combine(workingDirectory, relativePath)))
        {
            return captured.DiffUnavailable
                ? new DiffBuildResult(null, captured.IsBinary, true)
                : UnifiedDiffBuilder.Build([], captured.DiffBytes, relativePath);
        }

        var diff = RunGit(workingDirectory, "diff", "--no-ext-diff", "--no-textconv", "--binary", "HEAD", "--", relativePath);
        if (!diff.Succeeded || string.IsNullOrWhiteSpace(diff.Output))
        {
            return new DiffBuildResult(null, false, false);
        }

        var isBinary = diff.Output.Contains("GIT binary patch", StringComparison.Ordinal) ||
                       diff.Output.Contains("Binary files ", StringComparison.Ordinal);
        return diff.Output.Length > MaximumDiffCharacters
            ? new DiffBuildResult(diff.Output[..MaximumDiffCharacters], isBinary, true)
            : new DiffBuildResult(diff.Output, isBinary, false);
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] arguments)
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
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.fsmonitor=false");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("diff.external=");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitCommandResult(false, string.Empty);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return new GitCommandResult(process.ExitCode == 0, stdout.Result);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new GitCommandResult(false, string.Empty);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseGitStatus(string? status)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(status))
        {
            return result;
        }

        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4)
            {
                continue;
            }

            var code = entry[..2];
            var path = entry[3..];
            result[path] = code;
            if ((code.Contains('R') || code.Contains('C')) && index + 1 < entries.Length)
            {
                result[entries[++index]] = "D ";
            }
        }

        return result;
    }

    private static FileChangeKind GitChangeKind(string status, bool exists)
    {
        if (!exists || status.Contains('D')) return FileChangeKind.Deleted;
        if (status.StartsWith("??", StringComparison.Ordinal) || status.Contains('A')) return FileChangeKind.Added;
        if (status.Contains('R')) return FileChangeKind.Renamed;
        if (status.Contains('M')) return FileChangeKind.Modified;
        return FileChangeKind.Unknown;
    }

    private static (bool IsTest, string? Framework) ClassifyTestCommand(string command)
    {
        var normalized = command.Trim();
        if (DotnetTestRegex().IsMatch(normalized)) return (true, "dotnet");
        if (JavaScriptTestRegex().IsMatch(normalized)) return (true, "javascript");
        if (JavaScriptRunnerTestRegex().IsMatch(normalized)) return (true, "javascript");
        if (PytestRegex().IsMatch(normalized)) return (true, "pytest");
        if (CargoTestRegex().IsMatch(normalized)) return (true, "cargo");
        if (GoTestRegex().IsMatch(normalized)) return (true, "go");
        if (JavaTestRegex().IsMatch(normalized)) return (true, "java");
        return (false, null);
    }

    private static int? ParseExitCode(string output)
    {
        var match = ExitCodeRegex().Match(output);
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
    }

    private static string ExtractTextContent(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            content.EnumerateArray()
                .Where(item => GetOptionalString(item, "type") == "text")
                .Select(item => GetOptionalString(item, "text"))
                .Where(text => !string.IsNullOrEmpty(text)));
    }

    private static string? TryGetNestedString(JsonElement root, string parentName, string propertyName) =>
        root.TryGetProperty(parentName, out var parent) && parent.ValueKind == JsonValueKind.Object
            ? GetOptionalString(parent, propertyName)
            : null;

    private static string? GetOptionalString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string EnsureWorkspacePath(string candidate, string workingDirectory)
    {
        if (!WorkspacePathPolicy.TryResolveCandidate(workingDirectory, candidate, out var target))
        {
            throw new InvalidOperationException($"证据路径位于工作目录外：{candidate}");
        }

        return target;
    }

    private static bool IsInsideWorkspace(string target, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(target));
        return relative == "." ||
               (!relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                 !Path.IsPathFullyQualified(relative));
    }

    private static bool HasReparsePointBelowRoot(string workingDirectory, string target)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        var relative = Path.GetRelativePath(root, Path.GetFullPath(target));
        if (!IsInsideWorkspace(target, root))
        {
            return true;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CurrentFileMatches(FileChangeEvidence file, string target)
    {
        try
        {
            return File.Exists(target) &&
                   !string.IsNullOrWhiteSpace(file.AfterHash) &&
                   string.Equals(ComputeHash(target), file.AfterHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static CapturedFile CaptureFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = stream.Length;
        if (size <= MaximumDiffInputBytes)
        {
            var bytes = new byte[checked((int)size)];
            stream.ReadExactly(bytes);
            return new CapturedFile(
                bytes,
                ComputeHash(bytes),
                size,
                IsBinary(bytes),
                false);
        }

        var sample = new byte[Math.Min(8192, checked((int)Math.Min(size, int.MaxValue)))];
        stream.ReadExactly(sample);
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new CapturedFile([], hash, size, IsBinary(sample), true);
    }

    private static DiffBuildResult BuildCapturedDiff(CapturedFile before, CapturedFile after, string relativePath) =>
        before.DiffUnavailable || after.DiffUnavailable
            ? new DiffBuildResult(null, before.IsBinary || after.IsBinary, true)
            : UnifiedDiffBuilder.Build(before.DiffBytes, after.DiffBytes, relativePath);

    private static DiffBuildResult NormalizeNativePatch(string patch) =>
        patch.Length > MaximumDiffCharacters
            ? new DiffBuildResult(patch[..MaximumDiffCharacters], false, true)
            : new DiffBuildResult(patch, false, false);

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsBinary(byte[] bytes) =>
        bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : $"{value[..maximum]}…";

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private void DisposeActiveRuns()
    {
        foreach (var active in _activeRuns.Values)
        {
            active.StopWatcher();
        }
        _activeRuns.Clear();
    }

    [GeneratedRegex(@"(?:^|[;&|()]\s*)dotnet\s+test(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotnetTestRegex();

    [GeneratedRegex(@"(?:^|[;&|()]\s*)(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?test(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptTestRegex();

    [GeneratedRegex(@"(?:^|[;&|()]\s*)(?:node\s+--test|(?:npx|pnpm\s+exec|yarn\s+dlx)\s+(?:vitest|jest|mocha)|(?:vitest|jest|mocha)(?:\s|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptRunnerTestRegex();

    [GeneratedRegex(@"(?:^|[;&|()]\s*)(?:python\s+-m\s+)?pytest(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PytestRegex();

    [GeneratedRegex(@"(?:^|[;&|()]\s*)cargo\s+test(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CargoTestRegex();

    [GeneratedRegex(@"(?:^|[;&|()]\s*)go\s+test(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoTestRegex();

    [GeneratedRegex(@"(?:^|[;&|()]\s*)(?:mvn(?:\.cmd)?\s+test|(?:gradle|gradlew)(?:\.bat)?\s+test)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaTestRegex();

    [GeneratedRegex(@"Command exited with code\s+(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitCodeRegex();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record BackupManifestRecord(
        string RunId,
        string ToolCallId,
        string OriginalPath,
        string? Sha256,
        long? Size,
        DateTimeOffset BackedUpAt,
        bool? Existed)
    {
        public bool ExistedBefore => Existed ?? Sha256 is not null;
    }

    private sealed record GitCapture(bool IsRepository, string? Root, string? Head, string? Status);

    private sealed record GitCommandResult(bool Succeeded, string Output);

    private sealed record CapturedFile(
        byte[] DiffBytes,
        string? Hash,
        long Size,
        bool IsBinary,
        bool DiffUnavailable)
    {
        public static CapturedFile Missing { get; } = new([], null, 0, false, false);
    }

    private sealed class ActiveRun
    {
        private FileSystemWatcher? _watcher;
        private int _watcherOverflowed;
        private readonly WorkspaceEvidenceIgnorePolicy _ignorePolicy;

        public ActiveRun(RunEvidenceMetadata metadata, IReadOnlyDictionary<string, string> gitStatusBefore)
        {
            Metadata = metadata;
            GitStatusBefore = gitStatusBefore;
            _ignorePolicy = new WorkspaceEvidenceIgnorePolicy(metadata.WorkingDirectory);
        }

        public RunEvidenceMetadata Metadata { get; }
        public IReadOnlyDictionary<string, string> GitStatusBefore { get; }
        public ConcurrentDictionary<string, WatcherChangeTypes> Candidates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool WatcherOverflowed => Volatile.Read(ref _watcherOverflowed) != 0;
        public bool ShellObserved { get; set; }

        public void StartWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(Metadata.WorkingDirectory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Deleted += OnChanged;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += (_, _) => Interlocked.Exchange(ref _watcherOverflowed, 1);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _watcher?.Dispose();
                _watcher = null;
                Interlocked.Exchange(ref _watcherOverflowed, 1);
            }
        }

        public void StopWatcher()
        {
            if (_watcher is null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        private void OnChanged(object sender, FileSystemEventArgs eventArgs) => Add(eventArgs.FullPath, eventArgs.ChangeType);

        private void OnRenamed(object sender, RenamedEventArgs eventArgs)
        {
            Add(eventArgs.OldFullPath, WatcherChangeTypes.Deleted);
            Add(eventArgs.FullPath, WatcherChangeTypes.Created);
        }

        private void Add(string path, WatcherChangeTypes changeType)
        {
            if (_ignorePolicy.IsIgnored(path))
            {
                return;
            }

            if (Candidates.Count >= MaximumWatcherCandidates && !Candidates.ContainsKey(path))
            {
                Interlocked.Exchange(ref _watcherOverflowed, 1);
                return;
            }

            Candidates.AddOrUpdate(path, changeType, (_, existing) => existing | changeType);
        }
    }
}
