using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace PiCompanion.Application.Files;

internal sealed class WorkspaceFileIgnorePolicy
{
    private const string BuiltInSource = "built-in";
    private static readonly string[] ProjectIgnoreFileNames = [".gitignore", ".ignore", ".fdignore"];
    private static readonly HashSet<string> IgnoreFileNames =
        [.. ProjectIgnoreFileNames, ".piignore"];
    private static readonly HashSet<string> HardIgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
    };
    private static readonly HashSet<string> GeneratedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bower_components",
        ".pnpm-store",
        ".npm",
        ".venv",
        "venv",
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        ".ruff_cache",
        ".tox",
        ".nox",
        ".gradle",
        ".dart_tool",
        ".terraform",
        ".cache",
        ".astro",
        ".next",
        ".nuxt",
        ".svelte-kit",
        ".vite",
        ".turbo",
    };
    private static readonly HashSet<string> GeneratedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    };

    private readonly string _workspace;
    private readonly ConcurrentDictionary<string, DirectoryRules> _rules =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public WorkspaceFileIgnorePolicy(string workspace)
    {
        _workspace = Path.GetFullPath(workspace);
    }

    public WorkspaceFileIgnoreMatch Match(string path)
    {
        string fullPath;
        string relativePath;
        try
        {
            fullPath = Path.GetFullPath(path);
            relativePath = Path.GetRelativePath(_workspace, fullPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return WorkspaceFileIgnoreMatch.Ignored(BuiltInSource);
        }

        if (relativePath == ".." ||
            Path.IsPathFullyQualified(relativePath) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return WorkspaceFileIgnoreMatch.Ignored(BuiltInSource);
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return WorkspaceFileIgnoreMatch.Visible;
        }

        if (HardIgnoredDirectories.Overlaps(segments))
        {
            return WorkspaceFileIgnoreMatch.Ignored(BuiltInSource);
        }

        var containingDirectory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? _workspace;
        if (IgnoreFileNames.Contains(Path.GetFileName(fullPath)))
        {
            _rules.TryRemove(containingDirectory, out _);
        }

        var directories = GetContainingDirectories(containingDirectory);
        var builtInIgnored = ContainsGeneratedPath(segments) || IsGeneratedFile(segments[^1]);
        WorkspaceFileIgnoreMatch match = builtInIgnored
            ? WorkspaceFileIgnoreMatch.Ignored(BuiltInSource)
            : WorkspaceFileIgnoreMatch.Visible;
        if (!builtInIgnored)
        {
            foreach (var directory in directories)
            {
                match = ApplyRules(match, GetRules(directory).Project, directory, fullPath);
            }
        }

        foreach (var directory in directories)
        {
            match = ApplyRules(match, GetRules(directory).Pi, directory, fullPath);
        }

        return match;
    }

    private static WorkspaceFileIgnoreMatch ApplyRules(
        WorkspaceFileIgnoreMatch current,
        IReadOnlyList<IgnoreRule> rules,
        string directory,
        string fullPath)
    {
        var relativeFromDirectory = ToPosix(Path.GetRelativePath(directory, fullPath));
        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(relativeFromDirectory))
            {
                current = rule.Negated
                    ? WorkspaceFileIgnoreMatch.Visible
                    : WorkspaceFileIgnoreMatch.Ignored(rule.Source);
            }
        }

        return current;
    }

    private IReadOnlyList<string> GetContainingDirectories(string containingDirectory)
    {
        var result = new List<string>();
        var current = Path.GetFullPath(containingDirectory);
        while (true)
        {
            var relative = Path.GetRelativePath(_workspace, current);
            if (relative == ".." ||
                Path.IsPathFullyQualified(relative) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                break;
            }

            result.Add(current);
            if (PathEquals(current, _workspace))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || PathEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        result.Reverse();
        return result;
    }

    private DirectoryRules GetRules(string directory) =>
        _rules.GetOrAdd(directory, LoadRules);

    private static DirectoryRules LoadRules(string directory)
    {
        var project = new List<IgnoreRule>();
        foreach (var fileName in ProjectIgnoreFileNames)
        {
            LoadRulesFromFile(Path.Combine(directory, fileName), fileName, project);
        }

        var pi = new List<IgnoreRule>();
        LoadRulesFromFile(Path.Combine(directory, ".piignore"), ".piignore", pi);
        return new DirectoryRules(project, pi);
    }

    private static void LoadRulesFromFile(string path, string source, List<IgnoreRule> rules)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (TryCreateRule(line, source, out var rule))
                {
                    rules.Add(rule);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            // Ignore files are best-effort metadata for the browser.
        }
    }

    private static bool TryCreateRule(string line, string source, out IgnoreRule rule)
    {
        rule = default;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 ||
            (trimmed.StartsWith("#", StringComparison.Ordinal) &&
             !trimmed.StartsWith("\\#", StringComparison.Ordinal)))
        {
            return false;
        }

        var pattern = line.TrimEnd();
        var negated = false;
        if (pattern.StartsWith("!", StringComparison.Ordinal))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        if (pattern.StartsWith("/", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        pattern = pattern.Replace('\\', '/');
        if (pattern.Length == 0)
        {
            return false;
        }

        rule = new IgnoreRule(CreateIgnoreRegex(pattern), negated, source);
        return true;
    }

    private static Regex CreateIgnoreRegex(string pattern)
    {
        var directoryOnly = pattern.EndsWith("/", StringComparison.Ordinal);
        if (directoryOnly)
        {
            pattern = pattern[..^1];
        }

        var hasSlash = pattern.Contains('/', StringComparison.Ordinal);
        var builder = new StringBuilder();
        builder.Append(hasSlash ? "^" : "(?:^|/)");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        builder.Append(directoryOnly ? "(?:/.*)?$" : "(?:$|/.*$)");
        return new Regex(
            builder.ToString(),
            RegexOptions.CultureInvariant |
            (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    private static bool ContainsGeneratedPath(IReadOnlyList<string> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (GeneratedDirectories.Contains(segments[index]) ||
                index + 1 < segments.Count &&
                segments[index].Equals(".yarn", StringComparison.OrdinalIgnoreCase) &&
                (segments[index + 1].Equals("cache", StringComparison.OrdinalIgnoreCase) ||
                 segments[index + 1].Equals("unplugged", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGeneratedFile(string fileName) =>
        GeneratedFileNames.Contains(fileName) ||
        fileName.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ToPosix(string path) => path.Replace('\\', '/');

    private readonly record struct IgnoreRule(Regex Pattern, bool Negated, string Source);

    private sealed record DirectoryRules(
        IReadOnlyList<IgnoreRule> Project,
        IReadOnlyList<IgnoreRule> Pi);
}

internal readonly record struct WorkspaceFileIgnoreMatch(bool IsIgnored, string? Source)
{
    public static WorkspaceFileIgnoreMatch Visible { get; } = new(false, null);

    public static WorkspaceFileIgnoreMatch Ignored(string source) => new(true, source);
}
