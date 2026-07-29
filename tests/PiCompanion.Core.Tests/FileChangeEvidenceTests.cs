using PiCompanion.Core.Evidence;

namespace PiCompanion.Core.Tests;

public sealed class FileChangeEvidenceTests
{
    [Fact]
    public void TextDiff_ExposesAddedAndDeletedLineCounts()
    {
        var evidence = CreateEvidence(
            "diff --git a/sample.txt b/sample.txt\n" +
            "--- a/sample.txt\n" +
            "+++ b/sample.txt\n" +
            "@@ -1,2 +1,3 @@\n" +
            "-old\n" +
            "+new\n" +
            "+another\n" +
            " context\n");

        Assert.Equal(2, evidence.AddedLines);
        Assert.Equal(1, evidence.DeletedLines);
    }

    [Fact]
    public void BinaryChange_DoesNotExposeTextLineCounts()
    {
        var evidence = CreateEvidence("@@ -1 +1 @@\n-old\n+new\n", isBinary: true);

        Assert.Equal(0, evidence.AddedLines);
        Assert.Equal(0, evidence.DeletedLines);
    }

    private static FileChangeEvidence CreateEvidence(string diff, bool isBinary = false) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "C:\\workspace\\sample.txt",
        "sample.txt",
        FileChangeKind.Modified,
        EvidenceConfidence.Confirmed,
        "BackupComparison",
        null,
        true,
        "before",
        "after",
        10,
        12,
        null,
        isBinary,
        diff,
        false,
        RecoveryAvailability.Available,
        null,
        DateTimeOffset.UtcNow);
}
