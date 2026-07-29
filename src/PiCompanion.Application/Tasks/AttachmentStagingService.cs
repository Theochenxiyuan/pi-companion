namespace PiCompanion.Application.Tasks;

public sealed record StagedAttachments(
    IReadOnlyList<string> RuntimePaths,
    IReadOnlyList<string> PersistentPaths,
    string ReadOnlyRoot);

public sealed class AttachmentStagingService
{
    public const int MaximumStagedFileCount = 4096;
    public const long MaximumStagedBytes = 256L * 1024 * 1024;

    private readonly string _rootDirectory;
    private readonly string? _transientAttachmentDirectory;

    public AttachmentStagingService(
        string rootDirectory,
        string? transientAttachmentDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _transientAttachmentDirectory = string.IsNullOrWhiteSpace(transientAttachmentDirectory)
            ? null
            : Path.GetFullPath(transientAttachmentDirectory);
    }

    public static AttachmentStagingService CreateDefault()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion");
        return new AttachmentStagingService(
            Path.Combine(dataDirectory, "attachments"),
            Path.Combine(dataDirectory, "clipboard-attachments"));
    }

    public StagedAttachments StageForRun(
        Guid taskId,
        Guid runId,
        string workingDirectory,
        IReadOnlyList<string> attachments,
        bool alwaysSnapshot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(attachments);

        var workspace = Path.GetFullPath(workingDirectory);
        var taskRoot = Path.Combine(_rootDirectory, taskId.ToString("N"));
        var runRoot = Path.Combine(taskRoot, runId.ToString("N"));
        var assetRoot = Path.Combine(taskRoot, "assets");
        var runtimePaths = new List<string>(attachments.Count);
        var persistentPaths = new List<string>(attachments.Count);
        var promotedSources = new List<string>();
        var promotedDestinations = new List<string>();
        var stagedFileCount = 0;
        long stagedBytes = 0;

        try
        {
            for (var index = 0; index < attachments.Count; index++)
            {
                var source = Path.GetFullPath(attachments[index]);
                if (_transientAttachmentDirectory is not null &&
                    IsInside(source, _transientAttachmentDirectory) &&
                    File.Exists(source))
                {
                    Directory.CreateDirectory(assetRoot);
                    var assetDestination = Path.Combine(
                        assetRoot,
                        $"{runId:N}-{GetSafeName(source, index)}");
                    CopyFile(source, assetDestination, ref stagedFileCount, ref stagedBytes);
                    runtimePaths.Add(assetDestination);
                    persistentPaths.Add(assetDestination);
                    promotedSources.Add(source);
                    promotedDestinations.Add(assetDestination);
                    continue;
                }

                if (IsInside(source, taskRoot))
                {
                    runtimePaths.Add(source);
                    persistentPaths.Add(source);
                    continue;
                }

                if (!alwaysSnapshot && IsInside(source, workspace))
                {
                    runtimePaths.Add(source);
                    persistentPaths.Add(source);
                    continue;
                }

                Directory.CreateDirectory(runRoot);
                var name = GetSafeName(source, index);
                var destination = Path.Combine(runRoot, name);
                if (File.Exists(source))
                {
                    CopyFile(source, destination, ref stagedFileCount, ref stagedBytes);
                }
                else if (Directory.Exists(source))
                {
                    CopyDirectory(source, destination, ref stagedFileCount, ref stagedBytes);
                }
                else
                {
                    throw new InvalidOperationException($"附件不可用：{Path.GetFileName(source)}");
                }

                runtimePaths.Add(destination);
                persistentPaths.Add(source);
            }
        }
        catch
        {
            DeleteDirectory(runRoot);
            foreach (var destination in promotedDestinations)
            {
                DeleteFile(destination);
            }
            throw;
        }

        foreach (var source in promotedSources)
        {
            DeleteFile(source);
        }

        return new StagedAttachments(runtimePaths, persistentPaths, taskRoot);
    }

    public void DeleteTask(Guid taskId) =>
        DeleteDirectory(Path.Combine(_rootDirectory, taskId.ToString("N")));

    public void Clear()
    {
        DeleteDirectory(_rootDirectory);
    }

    private static void CopyDirectory(
        string source,
        string destination,
        ref int fileCount,
        ref long byteCount)
    {
        RejectReparsePoint(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectReparsePoint(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target, ref fileCount, ref byteCount);
            }
            else
            {
                CopyFile(entry, target, ref fileCount, ref byteCount);
            }
        }
    }

    private static void CopyFile(
        string source,
        string destination,
        ref int fileCount,
        ref long byteCount)
    {
        RejectReparsePoint(source);
        var length = new FileInfo(source).Length;
        fileCount++;
        byteCount = checked(byteCount + length);
        if (fileCount > MaximumStagedFileCount || byteCount > MaximumStagedBytes)
        {
            throw new InvalidOperationException("目录外附件过大，最多暂存 4096 个文件或 256 MB。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"目录外附件不能包含链接：{Path.GetFileName(path)}");
        }
    }

    private static bool IsInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathFullyQualified(relative));
    }

    private static string GetSafeName(string source, int index)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "attachment";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return $"{index + 1:D2}-{name}";
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}
