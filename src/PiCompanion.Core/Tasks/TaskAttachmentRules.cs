namespace PiCompanion.Core.Tasks;

public static class TaskAttachmentRules
{
    public const int MaximumCount = 64;

    public static IReadOnlyList<string> NormalizeAndValidate(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return [];
        }

        var normalized = paths
            .Select(NormalizeAbsolutePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > MaximumCount)
        {
            throw new InvalidOperationException($"最多支持 {MaximumCount} 个附件。");
        }

        var unavailable = normalized.FirstOrDefault(
            path => !File.Exists(path) && !Directory.Exists(path));
        if (unavailable is not null)
        {
            throw new InvalidOperationException($"附件不可用：{GetDisplayName(unavailable)}");
        }

        return normalized;
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("附件路径必须是绝对路径。");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("附件路径无效。", exception);
        }
    }

    private static string GetDisplayName(string path)
    {
        var displayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(displayName) ? path : displayName;
    }
}
