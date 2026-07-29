namespace PiCompanion.Application.Files;

public sealed class WorkspaceFileBrowser
{
    public const int MaximumSearchResults = 200;
    public const int MaximumSearchEntries = 100_000;

    public WorkspaceDirectoryListing ReadDirectory(
        string workingDirectory,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        var root = RequireWorkspace(workingDirectory);
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var candidate = normalizedRelativePath.Length == 0
            ? root
            : Path.Combine(root, normalizedRelativePath);
        if (!WorkspacePathPolicy.TryResolveCandidate(root, candidate, out var directory) ||
            !Directory.Exists(directory) ||
            HasReparsePointBelowRoot(root, directory))
        {
            throw new InvalidOperationException("请求的目录不在当前工作区内，或目录已不可用。");
        }

        var entries = new List<WorkspaceFileEntry>();
        var ignorePolicy = new WorkspaceFileIgnorePolicy(root);
        var inaccessibleEntries = 0;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(path);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                    var childRelativePath = ToWebRelativePath(Path.GetRelativePath(root, path));
                    var ignore = ignorePolicy.Match(path);
                    entries.Add(new WorkspaceFileEntry(
                        Path.GetFileName(path),
                        childRelativePath,
                        isDirectory,
                        isDirectory && !isReparsePoint && HasAnyChild(path),
                        isReparsePoint,
                        ignore.IsIgnored,
                        ignore.Source));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    inaccessibleEntries++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法读取目录：{normalizedRelativePath}", exception);
        }

        entries.Sort(WorkspaceFileEntryComparer.Instance);
        return new WorkspaceDirectoryListing(
            root,
            ToWebRelativePath(normalizedRelativePath),
            entries,
            inaccessibleEntries);
    }

    public WorkspaceFileSearchResult Search(
        string workingDirectory,
        string query,
        CancellationToken cancellationToken = default,
        bool includeIgnored = false)
    {
        var root = RequireWorkspace(workingDirectory);
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0)
        {
            return new WorkspaceFileSearchResult(root, normalizedQuery, [], false, 0, 0);
        }

        var results = new List<WorkspaceFileEntry>();
        var ignorePolicy = new WorkspaceFileIgnorePolicy(root);
        var directories = new Stack<string>();
        directories.Push(root);
        var visitedEntries = 0;
        var inaccessibleEntries = 0;
        var truncated = false;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                inaccessibleEntries++;
                continue;
            }

            try
            {
                foreach (var path in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visitedEntries++;
                    if (visitedEntries > MaximumSearchEntries)
                    {
                        truncated = true;
                        directories.Clear();
                        break;
                    }

                    try
                    {
                        var attributes = File.GetAttributes(path);
                        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                        var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                        var name = Path.GetFileName(path);
                        var relativePath = ToWebRelativePath(Path.GetRelativePath(root, path));
                        var ignore = ignorePolicy.Match(path);
                        if ((includeIgnored || !ignore.IsIgnored) &&
                            (name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                             relativePath.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)))
                        {
                            results.Add(new WorkspaceFileEntry(
                                name,
                                relativePath,
                                isDirectory,
                                isDirectory && !isReparsePoint && HasAnyChild(path),
                                isReparsePoint,
                                ignore.IsIgnored,
                                ignore.Source));
                            if (results.Count >= MaximumSearchResults)
                            {
                                truncated = true;
                                directories.Clear();
                                break;
                            }
                        }

                        if (isDirectory && !isReparsePoint && (includeIgnored || !ignore.IsIgnored))
                        {
                            directories.Push(path);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        inaccessibleEntries++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                inaccessibleEntries++;
            }
        }

        results.Sort(WorkspaceFileEntryComparer.Instance);
        return new WorkspaceFileSearchResult(
            root,
            normalizedQuery,
            results,
            truncated,
            visitedEntries,
            inaccessibleEntries);
    }

    public string ResolveExistingPath(string workingDirectory, string relativePath)
    {
        var root = RequireWorkspace(workingDirectory);
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (normalizedRelativePath.Length == 0 ||
            !WorkspacePathPolicy.TryResolveCandidate(root, Path.Combine(root, normalizedRelativePath), out var target) ||
            !File.Exists(target) && !Directory.Exists(target))
        {
            throw new InvalidOperationException("文件不在当前工作区内，或文件已不存在。");
        }

        return target;
    }

    private static string RequireWorkspace(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{root}");
        }

        return root;
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalized) || normalized.StartsWith(Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException("文件路径必须相对于当前工作区。");
        }

        return normalized.TrimStart(Path.DirectorySeparatorChar);
    }

    private static string ToWebRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static bool HasAnyChild(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasReparsePointBelowRoot(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
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

    private sealed class WorkspaceFileEntryComparer : IComparer<WorkspaceFileEntry>
    {
        public static WorkspaceFileEntryComparer Instance { get; } = new();

        public int Compare(WorkspaceFileEntry? left, WorkspaceFileEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var kind = right.IsDirectory.CompareTo(left.IsDirectory);
            return kind != 0
                ? kind
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }
    }
}

public sealed record WorkspaceFileEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    bool HasChildren,
    bool IsReparsePoint,
    bool IsIgnored = false,
    string? IgnoreSource = null);

public sealed record WorkspaceDirectoryListing(
    string WorkingDirectory,
    string RelativePath,
    IReadOnlyList<WorkspaceFileEntry> Entries,
    int InaccessibleEntries);

public sealed record WorkspaceFileSearchResult(
    string WorkingDirectory,
    string Query,
    IReadOnlyList<WorkspaceFileEntry> Entries,
    bool Truncated,
    int VisitedEntries,
    int InaccessibleEntries);
