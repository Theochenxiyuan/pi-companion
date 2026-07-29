namespace PiCompanion.Application.Files;

public static class WorkspacePathPolicy
{
    public static bool TryResolveCandidate(
        string workingDirectory,
        string candidate,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            var target = Path.GetFullPath(candidate);
            var relative = Path.GetRelativePath(root, target);
            if (relative != "." &&
                (relative.Equals("..", StringComparison.Ordinal) ||
                 relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 Path.IsPathFullyQualified(relative)))
            {
                return false;
            }

            fullPath = target;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
