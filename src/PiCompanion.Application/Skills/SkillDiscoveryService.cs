using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiCompanion.Application.Skills;

public sealed record SkillDiscoveryWorkspace(
    Guid Id,
    string Name,
    string WorkingDirectory,
    string TrustStatus = "trusted",
    string? TrustDecisionPath = null,
    bool TrustInherited = false);

public sealed record SkillWorkspaceTrust(
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspacePath,
    string Status,
    string? DecisionPath,
    bool Inherited);

public sealed record SkillFileInspection(
    string Name,
    string? Description,
    string? Version,
    string? License,
    bool IsAvailable,
    bool DisableModelInvocation,
    string? ContentHash,
    int FileCount,
    long TotalSize,
    IReadOnlyList<SkillDiagnostic> Diagnostics);

public sealed record SkillDiscoverySnapshot(
    DateTimeOffset ScannedAt,
    IReadOnlyList<DiscoveredSkill> Skills,
    IReadOnlyList<SkillScanLocation> Locations,
    IReadOnlyList<SkillDiagnostic> Diagnostics,
    IReadOnlyList<SkillWorkspaceTrust> WorkspaceTrust);

public sealed record DiscoveredSkill(
    string Id,
    string Name,
    string? Description,
    string? Version,
    string? License,
    IReadOnlyDictionary<string, string> Metadata,
    string FilePath,
    string BaseDirectory,
    string CanonicalPath,
    string InstallPath,
    bool IsSingleFile,
    string? ContentHash,
    int FileCount,
    long TotalSize,
    DateTimeOffset? LastModifiedAt,
    bool IsAvailable,
    bool DisableModelInvocation,
    bool IsGloballyEffective,
    IReadOnlyList<Guid> EffectiveWorkspaceIds,
    IReadOnlyList<SkillOrigin> Origins,
    IReadOnlyList<SkillDiagnostic> Diagnostics);

public sealed record SkillOrigin(
    string Scope,
    string Source,
    string RootPath,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? WorkspacePath,
    bool Inherited,
    string InstallPath,
    bool IsCompatibilityLink,
    string? LinkTarget);

public sealed record SkillScanLocation(
    string Id,
    string Scope,
    string Source,
    string Path,
    string Status,
    int SkillCount,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? WorkspacePath,
    bool Inherited,
    string? Message);

public sealed record SkillDiagnostic(
    string Code,
    string Severity,
    string Message,
    string Path,
    string? WinnerPath = null,
    Guid? WorkspaceId = null,
    string? WorkspaceName = null);

/// <summary>
/// Read-only discovery for Pi 0.83 native skill locations. This service never
/// creates, changes, copies, or removes skill files.
/// </summary>
public sealed class SkillDiscoveryService
{
    private const int MaximumNameLength = 64;
    private const int MaximumDescriptionLength = 1024;
    private static readonly string[] IgnoreFileNames = [".gitignore", ".ignore", ".fdignore"];
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _userProfile;

    public SkillDiscoveryService(string? userProfile = null)
    {
        _userProfile = Path.GetFullPath(userProfile ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public static SkillFileInspection InspectSkillFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var skillDirectory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidDataException("无法解析 SKILL.md 所在目录。");
        var containingRoot = Path.GetDirectoryName(skillDirectory) ?? skillDirectory;
        var skill = ParseSkill(fullPath, containingRoot);
        return new SkillFileInspection(
            skill.Name,
            skill.Description,
            skill.Version,
            skill.License,
            skill.IsAvailable,
            skill.DisableModelInvocation,
            skill.ContentHash,
            skill.FileCount,
            skill.TotalSize,
            skill.Diagnostics);
    }

    public SkillDiscoverySnapshot Discover(
        IReadOnlyList<SkillDiscoveryWorkspace>? workspaces = null)
    {
        var state = new DiscoveryState();
        var globalPiRoot = new ScanRoot(
            Path.Combine(_userProfile, ".pi", "agent", "skills"),
            Scope: "global",
            Source: "pi",
            IncludeRootMarkdown: true);
        var globalAgentsRoot = new ScanRoot(
            Path.Combine(_userProfile, ".agents", "skills"),
            Scope: "global",
            Source: "agents",
            IncludeRootMarkdown: false);

        // Build shared Agent installations from their physical location first so
        // a Pi compatibility link can attach another origin without becoming
        // the representative path. Pi candidates still retain load precedence.
        var globalAgentCandidates = Scan(globalAgentsRoot, state);
        var globalPiCandidates = Scan(globalPiRoot, state);
        var globalCandidates = globalPiCandidates
            .Concat(globalAgentCandidates)
            .ToArray();

        var normalizedWorkspaces = NormalizeWorkspaces(workspaces ?? [], state);
        var workspaceCandidates = new Dictionary<Guid, List<Candidate>>();
        foreach (var workspace in normalizedWorkspaces)
        {
            var agentCandidates = new List<Candidate>();
            foreach (var agentsRoot in EnumerateAncestorAgentsRoots(workspace.WorkingDirectory))
            {
                if (PathComparer.Equals(Path.GetFullPath(agentsRoot), Path.GetFullPath(globalAgentsRoot.Path)))
                {
                    continue;
                }

                agentCandidates.AddRange(Scan(new ScanRoot(
                    agentsRoot,
                    Scope: "workspace",
                    Source: "agents",
                    IncludeRootMarkdown: false,
                    Workspace: workspace,
                    Inherited: !PathComparer.Equals(
                        Path.GetDirectoryName(Path.GetDirectoryName(agentsRoot))!,
                        workspace.WorkingDirectory)), state));
            }

            var piCandidates = Scan(new ScanRoot(
                Path.Combine(workspace.WorkingDirectory, ".pi", "skills"),
                Scope: "workspace",
                Source: "pi",
                IncludeRootMarkdown: true,
                Workspace: workspace), state);
            workspaceCandidates[workspace.Id] = piCandidates
                .Concat(agentCandidates)
                .ToList();
        }

        ResolveGlobalPrecedence(globalCandidates, state);
        foreach (var workspace in normalizedWorkspaces)
        {
            ResolveWorkspacePrecedence(
                workspace,
                workspaceCandidates[workspace.Id],
                globalCandidates,
                state);
        }

        var skills = state.Builders.Values
            .Select(static builder => builder.Build())
            .OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static skill => skill.FilePath, PathComparer)
            .ToArray();
        var diagnostics = state.ScanDiagnostics
            .Concat(skills.SelectMany(static skill => skill.Diagnostics))
            .ToArray();

        return new SkillDiscoverySnapshot(
            DateTimeOffset.UtcNow,
            skills,
            state.Locations,
            diagnostics,
            normalizedWorkspaces.Select(static workspace => new SkillWorkspaceTrust(
                workspace.Id,
                workspace.Name,
                workspace.WorkingDirectory,
                workspace.TrustStatus,
                workspace.TrustDecisionPath,
                workspace.TrustInherited)).ToArray());
    }

    private static IReadOnlyList<SkillDiscoveryWorkspace> NormalizeWorkspaces(
        IReadOnlyList<SkillDiscoveryWorkspace> workspaces,
        DiscoveryState state)
    {
        var result = new List<SkillDiscoveryWorkspace>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var workspace in workspaces)
        {
            try
            {
                var path = Path.GetFullPath(workspace.WorkingDirectory);
                if (!seen.Add(path))
                {
                    continue;
                }

                result.Add(workspace with
                {
                    Name = string.IsNullOrWhiteSpace(workspace.Name)
                        ? Path.GetFileName(path)
                        : workspace.Name.Trim(),
                    WorkingDirectory = path,
                });
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                state.ScanDiagnostics.Add(new SkillDiagnostic(
                    "path-inaccessible",
                    "warning",
                    $"无法解析已登记工作区路径：{exception.Message}",
                    workspace.WorkingDirectory,
                    WorkspaceId: workspace.Id,
                    WorkspaceName: workspace.Name));
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateAncestorAgentsRoots(string workingDirectory)
    {
        var current = Path.GetFullPath(workingDirectory);
        var gitRoot = FindGitRoot(current);
        while (true)
        {
            yield return Path.Combine(current, ".agents", "skills");
            if (gitRoot is not null && PathComparer.Equals(current, gitRoot))
            {
                yield break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathComparer.Equals(parent, current))
            {
                yield break;
            }

            current = parent;
        }
    }

    private static string? FindGitRoot(string start)
    {
        var current = Path.GetFullPath(start);
        while (true)
        {
            if (Directory.Exists(Path.Combine(current, ".git")) ||
                File.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathComparer.Equals(parent, current))
            {
                return null;
            }

            current = parent;
        }
    }

    private static IReadOnlyList<Candidate> Scan(ScanRoot root, DiscoveryState state)
    {
        var candidates = new List<Candidate>();
        var rootPath = Path.GetFullPath(root.Path);
        if (!Directory.Exists(rootPath))
        {
            state.Locations.Add(CreateLocation(root, rootPath, "missing", 0, null));
            return candidates;
        }

        var rawSkills = new List<RawSkill>();
        var diagnosticsBefore = state.ScanDiagnostics.Count;
        try
        {
            ScanDirectory(
                rootPath,
                rootPath,
                root.IncludeRootMarkdown,
                root,
                [],
                new HashSet<string>(PathComparer),
                rawSkills,
                state.ScanDiagnostics);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            var inaccessibleMessage = $"无法读取技能目录：{exception.Message}";
            state.ScanDiagnostics.Add(new SkillDiagnostic(
                "path-inaccessible",
                "warning",
                inaccessibleMessage,
                rootPath,
                WorkspaceId: root.Workspace?.Id,
                WorkspaceName: root.Workspace?.Name));
            state.Locations.Add(CreateLocation(root, rootPath, "inaccessible", 0, inaccessibleMessage));
            return candidates;
        }

        foreach (var raw in rawSkills)
        {
            var bindingKey = string.Join(
                '\0',
                raw.CanonicalPath,
                root.Workspace?.Id.ToString("D") ?? "global",
                root.Source,
                rootPath);
            if (!state.Bindings.Add(bindingKey))
            {
                continue;
            }

            if (!state.Builders.TryGetValue(raw.CanonicalPath, out var builder))
            {
                builder = new SkillBuilder(raw);
                state.Builders.Add(raw.CanonicalPath, builder);
            }

            builder.Origins.Add(new SkillOrigin(
                root.Scope,
                root.Source,
                rootPath,
                root.Workspace?.Id,
                root.Workspace?.Name,
                root.Workspace?.WorkingDirectory,
                root.Inherited,
                raw.InstallPath,
                raw.CompatibilityLinkTarget is not null,
                raw.CompatibilityLinkTarget));
            candidates.Add(new Candidate(builder, root));
        }

        var status = rawSkills.Count == 0 ? "empty" : "loaded";
        var message = state.ScanDiagnostics.Count > diagnosticsBefore
            ? "部分路径无法访问；已返回其余扫描结果。"
            : null;
        state.Locations.Add(CreateLocation(root, rootPath, status, rawSkills.Count, message));
        return candidates;
    }

    private static SkillScanLocation CreateLocation(
        ScanRoot root,
        string path,
        string status,
        int count,
        string? message) => new(
        $"{root.Workspace?.Id.ToString("D") ?? "global"}:{root.Source}:{path}",
        root.Scope,
        root.Source,
        path,
        status,
        count,
        root.Workspace?.Id,
        root.Workspace?.Name,
        root.Workspace?.WorkingDirectory,
        root.Inherited,
        message);

    private static void ScanDirectory(
        string directory,
        string rootDirectory,
        bool includeRootMarkdown,
        ScanRoot root,
        IReadOnlyList<IgnoreRule> inheritedRules,
        HashSet<string> visitedDirectories,
        List<RawSkill> skills,
        List<SkillDiagnostic> diagnostics)
    {
        var canonicalDirectory = CanonicalizeExistingPath(directory);
        if (!visitedDirectories.Add(canonicalDirectory))
        {
            return;
        }

        var rules = LoadIgnoreRules(directory, rootDirectory, inheritedRules);
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            if (PathComparer.Equals(
                    Path.GetFullPath(directory),
                    Path.GetFullPath(rootDirectory)))
            {
                throw;
            }

            diagnostics.Add(new SkillDiagnostic(
                "path-inaccessible",
                "warning",
                $"无法读取技能路径：{exception.Message}",
                directory,
                WorkspaceId: root.Workspace?.Id,
                WorkspaceName: root.Workspace?.Name));
            return;
        }

        var skillFile = entries.FirstOrDefault(static entry =>
            string.Equals(entry.Name, "SKILL.md", StringComparison.Ordinal) &&
            IsFile(entry));
        if (skillFile is not null && !IsIgnored(skillFile.FullName, false, rootDirectory, rules))
        {
            skills.Add(ParseSkill(skillFile.FullName, rootDirectory, root));
            return;
        }

        var isRoot = PathComparer.Equals(
            Path.GetFullPath(directory),
            Path.GetFullPath(rootDirectory));
        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith(".", StringComparison.Ordinal) ||
                string.Equals(entry.Name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var isDirectory = IsDirectory(entry);
            var isFile = IsFile(entry);
            if (IsIgnored(entry.FullName, isDirectory, rootDirectory, rules))
            {
                continue;
            }

            if (isDirectory)
            {
                ScanDirectory(
                    entry.FullName,
                    rootDirectory,
                    includeRootMarkdown: false,
                    root,
                    rules,
                    visitedDirectories,
                    skills,
                    diagnostics);
                continue;
            }

            if (isFile &&
                isRoot &&
                includeRootMarkdown &&
                entry.Name.EndsWith(".md", StringComparison.Ordinal))
            {
                skills.Add(ParseSkill(entry.FullName, rootDirectory, root));
            }
        }
    }

    private static IReadOnlyList<IgnoreRule> LoadIgnoreRules(
        string directory,
        string rootDirectory,
        IReadOnlyList<IgnoreRule> inherited)
    {
        List<IgnoreRule>? rules = null;
        var relativeDirectory = ToPosix(Path.GetRelativePath(rootDirectory, directory));
        var prefix = relativeDirectory == "." ? string.Empty : $"{relativeDirectory}/";
        foreach (var fileName in IgnoreFileNames)
        {
            var ignorePath = Path.Combine(directory, fileName);
            if (!File.Exists(ignorePath))
            {
                continue;
            }

            try
            {
                foreach (var line in File.ReadLines(ignorePath))
                {
                    if (TryCreateIgnoreRule(line, prefix, out var rule))
                    {
                        rules ??= [.. inherited];
                        rules.Add(rule);
                    }
                }
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                // Pi 0.83 treats unreadable ignore files as best-effort.
            }
        }

        return rules ?? inherited;
    }

    private static bool TryCreateIgnoreRule(string line, string prefix, out IgnoreRule rule)
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

        pattern = $"{prefix}{pattern}".Replace('\\', '/');
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

    private static bool IsIgnored(
        string path,
        bool isDirectory,
        string rootDirectory,
        IReadOnlyList<IgnoreRule> rules)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        var relative = ToPosix(Path.GetRelativePath(rootDirectory, path));
        if (isDirectory)
        {
            relative += "/";
        }

        var ignored = false;
        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(relative))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static RawSkill ParseSkill(
        string filePath,
        string rootDirectory,
        ScanRoot? root = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        var canonicalPath = CanonicalizeExistingPath(fullPath);
        var baseDirectory = Path.GetDirectoryName(fullPath)!;
        var fallbackName = Path.GetFileName(baseDirectory);
        var isSingleFile =
            !string.Equals(Path.GetFileName(fullPath), "SKILL.md", StringComparison.Ordinal) ||
            PathComparer.Equals(baseDirectory, Path.GetFullPath(rootDirectory));
        var installPath = isSingleFile ? fullPath : baseDirectory;
        var compatibilityLinkTarget = TryResolveCompatibilityLink(
            installPath,
            canonicalPath,
            isSingleFile,
            root);
        var inspectionPath = compatibilityLinkTarget ?? installPath;
        var diagnostics = new List<SkillDiagnostic>();
        SkillContentFingerprint? fingerprint = null;
        try
        {
            fingerprint = SkillContentHasher.Inspect(inspectionPath, isSingleFile);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new SkillDiagnostic(
                "content-inspection-failed",
                "warning",
                $"无法生成技能内容指纹：{exception.Message}",
                fullPath));
        }

        ParsedFrontmatter frontmatter;
        try
        {
            frontmatter = ParseFrontmatter(File.ReadAllText(fullPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new SkillDiagnostic(
                "frontmatter-invalid",
                "warning",
                exception.Message,
                fullPath));
            return new RawSkill(
                canonicalPath,
                fullPath,
                baseDirectory,
                fallbackName,
                null,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                installPath,
                isSingleFile,
                fingerprint?.Hash,
                fingerprint?.FileCount ?? 0,
                fingerprint?.TotalSize ?? 0,
                fingerprint?.LastModifiedAt,
                IsAvailable: false,
                DisableModelInvocation: false,
                compatibilityLinkTarget,
                diagnostics);
        }

        var name = string.IsNullOrWhiteSpace(frontmatter.Name)
            ? fallbackName
            : frontmatter.Name;
        if (name.Length > MaximumNameLength)
        {
            diagnostics.Add(new SkillDiagnostic(
                "name-too-long",
                "warning",
                $"name 超过 {MaximumNameLength} 个字符（当前 {name.Length}）。",
                fullPath));
        }

        if (!Regex.IsMatch(name, "^[a-z0-9-]+$", RegexOptions.CultureInvariant))
        {
            diagnostics.Add(new SkillDiagnostic(
                "name-invalid",
                "warning",
                "name 只能包含小写字母、数字和连字符。",
                fullPath));
        }

        if (name.StartsWith("-", StringComparison.Ordinal) ||
            name.EndsWith("-", StringComparison.Ordinal))
        {
            diagnostics.Add(new SkillDiagnostic(
                "name-invalid",
                "warning",
                "name 不能以连字符开头或结尾。",
                fullPath));
        }

        if (name.Contains("--", StringComparison.Ordinal))
        {
            diagnostics.Add(new SkillDiagnostic(
                "name-invalid",
                "warning",
                "name 不能包含连续连字符。",
                fullPath));
        }

        var description = frontmatter.Description;
        var isAvailable = !string.IsNullOrWhiteSpace(description);
        if (!isAvailable)
        {
            diagnostics.Add(new SkillDiagnostic(
                "description-required",
                "warning",
                "description 为必填字段；Pi 不会加载此技能。",
                fullPath));
        }
        else if (description!.Length > MaximumDescriptionLength)
        {
            diagnostics.Add(new SkillDiagnostic(
                "description-too-long",
                "warning",
                $"description 超过 {MaximumDescriptionLength} 个字符（当前 {description.Length}）。",
                fullPath));
        }

        return new RawSkill(
            canonicalPath,
            fullPath,
            baseDirectory,
            name,
            description,
            frontmatter.Version,
            frontmatter.License,
            frontmatter.Metadata,
            installPath,
            isSingleFile,
            fingerprint?.Hash,
            fingerprint?.FileCount ?? 0,
            fingerprint?.TotalSize ?? 0,
            fingerprint?.LastModifiedAt,
            isAvailable,
            frontmatter.DisableModelInvocation,
            compatibilityLinkTarget,
            diagnostics);
    }

    private static string? TryResolveCompatibilityLink(
        string installPath,
        string canonicalPath,
        bool isSingleFile,
        ScanRoot? root)
    {
        if (root is null ||
            root.Source != "pi" ||
            isSingleFile ||
            !PathComparer.Equals(
                Path.GetDirectoryName(Path.GetFullPath(installPath)),
                Path.GetFullPath(root.Path)))
        {
            return null;
        }

        var linkDirectory = new DirectoryInfo(installPath);
        if (!linkDirectory.Exists ||
            !linkDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        string expectedTarget;
        if (root.Workspace is not null)
        {
            expectedTarget = Path.Combine(
                root.Workspace.WorkingDirectory,
                ".agents",
                "skills",
                linkDirectory.Name);
        }
        else
        {
            var piDirectory = Directory.GetParent(Path.GetFullPath(root.Path))?.Parent?.Parent;
            if (piDirectory is null)
            {
                return null;
            }

            expectedTarget = Path.Combine(
                piDirectory.FullName,
                ".agents",
                "skills",
                linkDirectory.Name);
        }

        if (!Directory.Exists(expectedTarget))
        {
            return null;
        }

        var resolvedTarget = Path.GetDirectoryName(canonicalPath);
        var canonicalExpectedTarget = CanonicalizeExistingPath(expectedTarget);
        return resolvedTarget is not null &&
               PathComparer.Equals(resolvedTarget, canonicalExpectedTarget)
            ? canonicalExpectedTarget
            : null;
    }

    private static ParsedFrontmatter ParseFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (!normalized.StartsWith("---", StringComparison.Ordinal))
        {
            return new ParsedFrontmatter(
                null,
                null,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                false);
        }

        var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return new ParsedFrontmatter(
                null,
                null,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                false);
        }

        var yaml = normalized[4..endIndex];
        string? name = null;
        string? description = null;
        string? version = null;
        string? license = null;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var disableModelInvocation = false;
        var lines = yaml.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = CountIndent(line);
            if (indent > 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException($"无法解析 frontmatter 第 {index + 1} 行。");
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value is "|" or ">")
            {
                var blockLines = new List<string>();
                var next = index + 1;
                while (next < lines.Length)
                {
                    var blockLine = lines[next];
                    if (!string.IsNullOrWhiteSpace(blockLine) && CountIndent(blockLine) <= indent)
                    {
                        break;
                    }

                    blockLines.Add(blockLine);
                    next++;
                }

                var nonEmptyIndent = blockLines
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(CountIndent)
                    .DefaultIfEmpty(indent + 1)
                    .Min();
                var values = blockLines.Select(item =>
                    item.Length >= nonEmptyIndent ? item[nonEmptyIndent..] : string.Empty);
                value = value == ">"
                    ? string.Join(" ", values.Select(static item => item.Trim()).Where(static item => item.Length > 0))
                    : string.Join("\n", values).TrimEnd();
                index = next - 1;
            }
            else
            {
                value = ParseYamlString(value, key);
            }

            metadata[key] = value;
            switch (key)
            {
                case "name":
                    name = value.Length == 0 ? null : value;
                    break;
                case "description":
                    description = value.Length == 0 ? null : value;
                    break;
                case "version":
                    version = value.Length == 0 ? null : value;
                    break;
                case "license":
                    license = value.Length == 0 ? null : value;
                    break;
                case "disable-model-invocation":
                    disableModelInvocation = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return new ParsedFrontmatter(
            name,
            description,
            version,
            license,
            metadata,
            disableModelInvocation);
    }

    private static string ParseYamlString(string value, string key)
    {
        if (value.Length == 0 || value is "null" or "Null" or "NULL" or "~")
        {
            return string.Empty;
        }

        if (value.StartsWith('"'))
        {
            if (!value.EndsWith('"') || value.Length == 1)
            {
                throw new InvalidDataException($"frontmatter 字段 {key} 包含未闭合的双引号。");
            }

            try
            {
                return JsonSerializer.Deserialize<string>(value) ??
                    throw new InvalidDataException($"frontmatter 字段 {key} 不是字符串。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"frontmatter 字段 {key} 不是有效字符串。", exception);
            }
        }

        if (value.StartsWith('\''))
        {
            if (!value.EndsWith('\'') || value.Length == 1)
            {
                throw new InvalidDataException($"frontmatter 字段 {key} 包含未闭合的单引号。");
            }

            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        var commentIndex = value.IndexOf(" #", StringComparison.Ordinal);
        return (commentIndex >= 0 ? value[..commentIndex] : value).Trim();
    }

    private static int CountIndent(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static void ResolveGlobalPrecedence(
        IReadOnlyList<Candidate> candidates,
        DiscoveryState state)
    {
        var byName = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer);
        foreach (var candidate in candidates)
        {
            if (!candidate.Builder.IsAvailable || !paths.Add(candidate.Builder.CanonicalPath))
            {
                continue;
            }

            if (byName.TryGetValue(candidate.Builder.Name, out var winner))
            {
                AddCollision(candidate.Builder, winner.Builder, null, state);
            }
            else
            {
                byName.Add(candidate.Builder.Name, candidate);
                candidate.Builder.IsGloballyEffective = true;
            }
        }
    }

    private static void ResolveWorkspacePrecedence(
        SkillDiscoveryWorkspace workspace,
        IReadOnlyList<Candidate> projectCandidates,
        IReadOnlyList<Candidate> globalCandidates,
        DiscoveryState state)
    {
        var projectTrusted = string.Equals(
            workspace.TrustStatus,
            "trusted",
            StringComparison.Ordinal);
        if (!projectTrusted)
        {
            foreach (var candidate in projectCandidates)
            {
                AddUntrustedWorkspaceDiagnostic(candidate.Builder, workspace);
            }
        }

        var byName = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer);
        foreach (var candidate in (projectTrusted ? projectCandidates : []).Concat(globalCandidates))
        {
            if (!candidate.Builder.IsAvailable || !paths.Add(candidate.Builder.CanonicalPath))
            {
                continue;
            }

            if (byName.TryGetValue(candidate.Builder.Name, out var winner))
            {
                if (candidate.Root.Scope == "global" && winner.Root.Scope == "global")
                {
                    continue;
                }

                AddCollision(candidate.Builder, winner.Builder, workspace, state);
            }
            else
            {
                byName.Add(candidate.Builder.Name, candidate);
                candidate.Builder.EffectiveWorkspaceIds.Add(workspace.Id);
            }
        }
    }

    private static void AddUntrustedWorkspaceDiagnostic(
        SkillBuilder skill,
        SkillDiscoveryWorkspace workspace)
    {
        var key = $"workspace-untrusted\0{workspace.Id:D}\0{skill.CanonicalPath}";
        if (!skill.DiagnosticKeys.Add(key))
        {
            return;
        }

        skill.Diagnostics.Add(new SkillDiagnostic(
            "workspace-untrusted",
            "warning",
            $"工作区“{workspace.Name}”尚未受 Pi 信任；该工作区中的技能不会被加载。",
            skill.FilePath,
            WorkspaceId: workspace.Id,
            WorkspaceName: workspace.Name));
    }

    private static void AddCollision(
        SkillBuilder loser,
        SkillBuilder winner,
        SkillDiscoveryWorkspace? workspace,
        DiscoveryState state)
    {
        var key = $"name-collision\0{workspace?.Id.ToString("D") ?? "global"}\0{winner.CanonicalPath}";
        if (!loser.DiagnosticKeys.Add(key))
        {
            return;
        }

        var context = workspace is null
            ? "全局范围"
            : $"工作区“{workspace.Name}”";
        loser.Diagnostics.Add(new SkillDiagnostic(
            "name-collision",
            "warning",
            $"{context}中的 name \"{loser.Name}\" 冲突；Pi 0.83 保留优先级更高的 {winner.FilePath}。",
            loser.FilePath,
            winner.FilePath,
            workspace?.Id,
            workspace?.Name));
    }

    private static bool IsDirectory(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo)
            {
                return true;
            }

            return entry.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                   entry.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return false;
        }
    }

    private static bool IsFile(FileSystemInfo entry)
    {
        try
        {
            if (entry is FileInfo)
            {
                return true;
            }

            return entry.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                   entry.ResolveLinkTarget(returnFinalTarget: true) is FileInfo;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return false;
        }
    }

    private static string CanonicalizeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ??
            throw new InvalidOperationException($"路径没有根目录：{fullPath}");
        var current = root;
        foreach (var segment in fullPath[root.Length..]
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(static segment => segment.Length > 0))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : new FileInfo(next);
            try
            {
                current = info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next
                    : next;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                current = next;
            }
        }

        return Path.GetFullPath(current);
    }

    private static string ToPosix(string path) => path.Replace('\\', '/');

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private sealed class DiscoveryState
    {
        public Dictionary<string, SkillBuilder> Builders { get; } = new(PathComparer);
        public HashSet<string> Bindings { get; } = new(PathComparer);
        public List<SkillScanLocation> Locations { get; } = [];
        public List<SkillDiagnostic> ScanDiagnostics { get; } = [];
    }

    private sealed class SkillBuilder(RawSkill skill)
    {
        public string CanonicalPath { get; } = skill.CanonicalPath;
        public string FilePath { get; } = skill.FilePath;
        public string BaseDirectory { get; } = skill.BaseDirectory;
        public string Name { get; } = skill.Name;
        public string? Description { get; } = skill.Description;
        public string? Version { get; } = skill.Version;
        public string? License { get; } = skill.License;
        public IReadOnlyDictionary<string, string> Metadata { get; } = skill.Metadata;
        public string InstallPath { get; } = skill.InstallPath;
        public bool IsSingleFile { get; } = skill.IsSingleFile;
        public string? ContentHash { get; } = skill.ContentHash;
        public int FileCount { get; } = skill.FileCount;
        public long TotalSize { get; } = skill.TotalSize;
        public DateTimeOffset? LastModifiedAt { get; } = skill.LastModifiedAt;
        public bool IsAvailable { get; } = skill.IsAvailable;
        public bool DisableModelInvocation { get; } = skill.DisableModelInvocation;
        public bool IsGloballyEffective { get; set; }
        public HashSet<Guid> EffectiveWorkspaceIds { get; } = [];
        public List<SkillOrigin> Origins { get; } = [];
        public List<SkillDiagnostic> Diagnostics { get; } = [.. skill.Diagnostics];
        public HashSet<string> DiagnosticKeys { get; } = [];

        public DiscoveredSkill Build() => new(
            CanonicalPath,
            Name,
            Description,
            Version,
            License,
            Metadata,
            FilePath,
            BaseDirectory,
            CanonicalPath,
            InstallPath,
            IsSingleFile,
            ContentHash,
            FileCount,
            TotalSize,
            LastModifiedAt,
            IsAvailable,
            DisableModelInvocation,
            IsGloballyEffective,
            EffectiveWorkspaceIds.Order().ToArray(),
            Origins,
            Diagnostics);
    }

    private sealed record RawSkill(
        string CanonicalPath,
        string FilePath,
        string BaseDirectory,
        string Name,
        string? Description,
        string? Version,
        string? License,
        IReadOnlyDictionary<string, string> Metadata,
        string InstallPath,
        bool IsSingleFile,
        string? ContentHash,
        int FileCount,
        long TotalSize,
        DateTimeOffset? LastModifiedAt,
        bool IsAvailable,
        bool DisableModelInvocation,
        string? CompatibilityLinkTarget,
        IReadOnlyList<SkillDiagnostic> Diagnostics);

    private sealed record Candidate(SkillBuilder Builder, ScanRoot Root);

    private sealed record ScanRoot(
        string Path,
        string Scope,
        string Source,
        bool IncludeRootMarkdown,
        SkillDiscoveryWorkspace? Workspace = null,
        bool Inherited = false);

    private sealed record ParsedFrontmatter(
        string? Name,
        string? Description,
        string? Version,
        string? License,
        IReadOnlyDictionary<string, string> Metadata,
        bool DisableModelInvocation);

    private readonly record struct IgnoreRule(Regex Pattern, bool Negated);
}
