using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace PiCompanion.Application.Evidence;

/// <summary>
/// Filters indirect file-system evidence. Explicit edit/write evidence and
/// Git status evidence bypass this policy because they have stronger provenance.
/// </summary>
internal sealed class WorkspaceEvidenceIgnorePolicy
{
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

    public WorkspaceEvidenceIgnorePolicy(string workspace)
    {
        _workspace = Path.GetFullPath(workspace);
    }

    public bool IsIgnored(string path)
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
            return true;
        }

        if (relativePath == ".." ||
            Path.IsPathFullyQualified(relativePath) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return true;
        }

        var segments = relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        if (HardIgnoredDirectories.Overlaps(segments))
        {
            return true;
        }

        var containingDirectory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? _workspace;
        if (IgnoreFileNames.Contains(Path.GetFileName(fullPath)))
        {
            _rules.TryRemove(containingDirectory, out _);
        }

        var directories = GetContainingDirectories(containingDirectory);
        var generatedDirectoryIndex = FindGeneratedDirectoryIndex(segments);
        var generatedFile = IsGeneratedFile(segments[^1]);
        if ((generatedDirectoryIndex >= 0 || generatedFile) &&
            !IsExplicitlyIncludedByPiIgnore(
                fullPath,
                generatedDirectoryIndex >= 0
                    ? directories.Take(generatedDirectoryIndex + 1)
                    : directories))
        {
            return true;
        }

        var ignored = false;
        foreach (var directory in directories)
        {
            var relativeFromDirectory = ToPosix(Path.GetRelativePath(directory, fullPath));
            foreach (var rule in GetRules(directory).Project)
            {
                if (rule.Pattern.IsMatch(relativeFromDirectory))
                {
                    ignored = !rule.Negated;
                }
            }
        }

        // .piignore is a task-evidence override and therefore has higher
        // precedence than Git/fd/ripgrep-compatible project ignore files.
        foreach (var directory in directories)
        {
            var relativeFromDirectory = ToPosix(Path.GetRelativePath(directory, fullPath));
            foreach (var rule in GetRules(directory).Pi)
            {
                if (rule.Pattern.IsMatch(relativeFromDirectory))
                {
                    ignored = !rule.Negated;
                }
            }
        }

        return ignored;
    }

    private bool IsExplicitlyIncludedByPiIgnore(string fullPath, IEnumerable<string> directories)
    {
        var included = false;
        foreach (var directory in directories)
        {
            var relativeFromDirectory = ToPosix(Path.GetRelativePath(directory, fullPath));
            foreach (var rule in GetRules(directory).Pi)
            {
                if (rule.Pattern.IsMatch(relativeFromDirectory))
                {
                    included = rule.Negated;
                }
            }
        }

        return included;
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
            LoadRulesFromFile(Path.Combine(directory, fileName), project);
        }

        var pi = new List<IgnoreRule>();
        LoadRulesFromFile(Path.Combine(directory, ".piignore"), pi);
        return new DirectoryRules(project, pi);
    }

    private static void LoadRulesFromFile(string path, List<IgnoreRule> rules)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (TryCreateRule(line, out var rule))
                {
                    rules.Add(rule);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            // Ignore files are best-effort evidence filters.
        }
    }

    private static bool TryCreateRule(string line, out IgnoreRule rule)
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

        rule = new IgnoreRule(CreateIgnoreRegex(pattern), negated);
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

    private static int FindGeneratedDirectoryIndex(IReadOnlyList<string> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (GeneratedDirectories.Contains(segments[index]) ||
                index + 1 < segments.Count &&
                segments[index].Equals(".yarn", StringComparison.OrdinalIgnoreCase) &&
                (segments[index + 1].Equals("cache", StringComparison.OrdinalIgnoreCase) ||
                 segments[index + 1].Equals("unplugged", StringComparison.OrdinalIgnoreCase)))
            {
                return index;
            }
        }

        return -1;
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

    private readonly record struct IgnoreRule(Regex Pattern, bool Negated);

    private sealed record DirectoryRules(
        IReadOnlyList<IgnoreRule> Project,
        IReadOnlyList<IgnoreRule> Pi);
}
