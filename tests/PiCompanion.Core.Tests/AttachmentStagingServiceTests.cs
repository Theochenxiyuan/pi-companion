using PiCompanion.Application.Tasks;

namespace PiCompanion.Core.Tests;

public sealed class AttachmentStagingServiceTests
{
    [Fact]
    public void StageForRun_CopiesOnlyOutsideAttachmentsIntoAnIsolatedRunDirectory()
    {
        var root = CreateTemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var cache = Path.Combine(root, "cache");
        var insideFile = Path.Combine(workspace, "inside.txt");
        var outsideFile = Path.Combine(outside, "image.png");
        File.WriteAllText(insideFile, "inside");
        File.WriteAllText(outsideFile, "outside");
        try
        {
            var taskId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var service = new AttachmentStagingService(cache);

            var result = service.StageForRun(taskId, runId, workspace, [insideFile, outsideFile]);

            Assert.Equal(insideFile, result.RuntimePaths[0]);
            Assert.NotEqual(outsideFile, result.RuntimePaths[1]);
            Assert.Equal([insideFile, outsideFile], result.PersistentPaths);
            Assert.StartsWith(result.ReadOnlyRoot, result.RuntimePaths[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runId.ToString("N"), result.RuntimePaths[1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("outside", File.ReadAllText(result.RuntimePaths[1]));
            Assert.Equal(outsideFile, Path.GetFullPath(outsideFile));

            service.DeleteTask(taskId);
            Assert.False(Directory.Exists(result.ReadOnlyRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StageForRun_PromotesTransientClipboardFilesIntoTaskOwnedAssets()
    {
        var root = CreateTemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var transientRoot = Directory.CreateDirectory(Path.Combine(root, "clipboard-attachments")).FullName;
        var transientImage = Path.Combine(transientRoot, "clipboard.png");
        File.WriteAllText(transientImage, "image");
        try
        {
            var taskId = Guid.NewGuid();
            var service = new AttachmentStagingService(
                Path.Combine(root, "attachments"),
                transientRoot);

            var result = service.StageForRun(
                taskId,
                Guid.NewGuid(),
                workspace,
                [transientImage]);

            var persistentPath = Assert.Single(result.PersistentPaths);
            Assert.Equal(persistentPath, Assert.Single(result.RuntimePaths));
            Assert.StartsWith(result.ReadOnlyRoot, persistentPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}",
                persistentPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("image", File.ReadAllText(persistentPath));
            Assert.False(File.Exists(transientImage));

            service.DeleteTask(taskId);
            Assert.False(File.Exists(persistentPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StageForRun_CopiesOutsideDirectoriesRecursively()
    {
        var root = CreateTemporaryDirectory();
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside", "folder")).FullName;
        File.WriteAllText(Path.Combine(outside, "note.txt"), "folder content");
        try
        {
            var service = new AttachmentStagingService(Path.Combine(root, "cache"));
            var result = service.StageForRun(Guid.NewGuid(), Guid.NewGuid(), workspace, [outside]);

            Assert.Equal("folder content", File.ReadAllText(Path.Combine(result.RuntimePaths[0], "note.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
