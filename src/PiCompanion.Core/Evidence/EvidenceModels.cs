namespace PiCompanion.Core.Evidence;

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Unknown,
}

public enum EvidenceConfidence
{
    Confirmed,
    Observed,
    PreExisting,
    Unknown,
}

public enum TestEvidenceStatus
{
    Passed,
    Failed,
    NotRun,
    Unknown,
}

public enum RecoveryAvailability
{
    Unavailable,
    Available,
    Conflict,
    Recovered,
}

public sealed record RunEvidenceMetadata(
    Guid RunId,
    Guid TaskId,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    bool IsGitRepository,
    string? GitRoot,
    string? HeadBefore,
    string? HeadAfter,
    string? GitStatusBefore,
    string? GitStatusAfter,
    bool Finalized,
    bool WatcherOverflowed,
    bool ShellObserved,
    DateTimeOffset? FinalizedAt);

public sealed record FileChangeEvidence(
    Guid Id,
    Guid RunId,
    string Path,
    string RelativePath,
    FileChangeKind Kind,
    EvidenceConfidence Confidence,
    string Source,
    string? ToolCallId,
    bool ExistedBefore,
    string? BeforeHash,
    string? AfterHash,
    long? BeforeSize,
    long? AfterSize,
    string? BackupObjectPath,
    bool IsBinary,
    string? DiffText,
    bool DiffTruncated,
    RecoveryAvailability Recovery,
    string? RecoveryMessage,
    DateTimeOffset UpdatedAt)
{
    public bool HasDiff => !string.IsNullOrWhiteSpace(DiffText);
    public int AddedLines => CountChangedLines('+');
    public int DeletedLines => CountChangedLines('-');

    private int CountChangedLines(char marker)
    {
        if (IsBinary || string.IsNullOrWhiteSpace(DiffText))
        {
            return 0;
        }

        var count = 0;
        var insideHunk = false;
        using var reader = new StringReader(DiffText);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                insideHunk = true;
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                insideHunk = false;
                continue;
            }

            if (insideHunk && line.Length > 0 && line[0] == marker)
            {
                count++;
            }
        }

        return count;
    }
}

public sealed record CommandExecutionEvidence(
    Guid Id,
    Guid RunId,
    string ToolCallId,
    string Command,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int? ExitCode,
    bool Cancelled,
    bool TimedOut,
    string OutputSummary,
    string? FullOutputPath,
    bool IsTest,
    string? DetectedFramework,
    TestEvidenceStatus Status);

public sealed record TestResultEvidence(
    Guid Id,
    Guid RunId,
    Guid CommandExecutionId,
    string Command,
    string Framework,
    TestEvidenceStatus Status,
    int? ExitCode,
    DateTimeOffset CompletedAt);

public sealed record EvidenceWarning(
    Guid Id,
    Guid RunId,
    string Code,
    string Message,
    DateTimeOffset CreatedAt);

public sealed record RecoveryActionEvidence(
    Guid Id,
    Guid RunId,
    Guid FileChangeId,
    string Path,
    string Outcome,
    string Message,
    DateTimeOffset CreatedAt);

public sealed record RunEvidenceSnapshot(
    Guid RunId,
    bool Finalized,
    bool IsGitRepository,
    string? GitRoot,
    string? HeadBefore,
    string? HeadAfter,
    TestEvidenceStatus TestStatus,
    IReadOnlyList<FileChangeEvidence> Files,
    IReadOnlyList<CommandExecutionEvidence> Commands,
    IReadOnlyList<TestResultEvidence> Tests,
    IReadOnlyList<EvidenceWarning> Warnings)
{
    public static RunEvidenceSnapshot Empty(Guid runId) => new(
        runId,
        false,
        false,
        null,
        null,
        null,
        TestEvidenceStatus.NotRun,
        [],
        [],
        [],
        []);
}

public sealed record FileDiffEvidence(
    Guid FileChangeId,
    Guid RunId,
    string Path,
    string? DiffText,
    bool IsBinary,
    bool Truncated,
    string Source);

public sealed record RecoveryResult(
    bool Succeeded,
    RecoveryAvailability Status,
    string Message,
    FileChangeEvidence FileChange);
