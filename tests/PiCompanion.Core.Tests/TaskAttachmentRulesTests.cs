using PiCompanion.Core.Tasks;

namespace PiCompanion.Core.Tests;

public sealed class TaskAttachmentRulesTests
{
    [Fact]
    public void NormalizeAndValidate_NormalizesAndDeduplicatesFilesAndFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
        var file = Path.Combine(root, "context.txt");
        File.WriteAllText(file, "context");
        try
        {
            var normalized = TaskAttachmentRules.NormalizeAndValidate(
                [file, file.ToUpperInvariant(), folder + Path.DirectorySeparatorChar]);

            Assert.Equal([file, folder], normalized);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NormalizeAndValidate_RejectsMoreThanMaximumCount()
    {
        var paths = Enumerable.Range(0, TaskAttachmentRules.MaximumCount + 1)
            .Select(index => Path.Combine(Path.GetTempPath(), $"attachment-{index}.txt"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => TaskAttachmentRules.NormalizeAndValidate(paths));

        Assert.Contains(TaskAttachmentRules.MaximumCount.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAndValidate_RejectsUnavailableAttachment()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "PiCompanionTests",
            Guid.NewGuid().ToString("N"),
            "missing.txt");

        var exception = Assert.Throws<InvalidOperationException>(
            () => TaskAttachmentRules.NormalizeAndValidate([missing]));

        Assert.Contains("附件不可用", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing.txt", exception.Message, StringComparison.Ordinal);
    }
}
