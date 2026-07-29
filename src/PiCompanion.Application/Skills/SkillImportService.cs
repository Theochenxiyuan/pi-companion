using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PiCompanion.Application.Skills;

public sealed record SkillImportFile(
    string RelativePath,
    long Size,
    string Kind);

public sealed record SkillImportSourceInspection(
    string Token,
    string Name,
    string? Description,
    string SourceKind,
    string ContentHash,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<SkillImportFile> Files,
    IReadOnlyList<string> ScriptFiles,
    IReadOnlyList<string> ExecutableFiles);

public sealed record SkillImportPreparation(
    string Token,
    string SourceToken,
    string Name,
    string? Description,
    string Scope,
    Guid? WorkspaceId,
    string? WorkspaceName,
    string? WorkspacePath,
    string TargetPath,
    string SourceKind,
    string ContentHash,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<SkillImportFile> Files,
    IReadOnlyList<string> ScriptFiles,
    IReadOnlyList<string> ExecutableFiles,
    bool RequiresProjectTrust,
    string TrustStatus)
{
    public bool RequiresConfirmation =>
        RequiresProjectTrust || ScriptFiles.Count > 0 || ExecutableFiles.Count > 0;
}

public sealed record SkillImportResult(
    string Name,
    string TargetPath,
    string Scope,
    Guid? WorkspaceId);

public sealed class SkillImportException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Imports one local folder or ZIP directly into a Pi native skill directory.
/// Content is validated in a hidden sibling staging directory and becomes
/// visible through one atomic directory move.
/// </summary>
public sealed class SkillImportService
{
    public const string MarkerFileName = ".pi-companion-skill.json";
    private const int MarkerVersion = 1;
    private const int MaximumFileCount = 2_000;
    private const long MaximumFileBytes = 25L * 1024 * 1024;
    private const long MaximumPackageBytes = 100L * 1024 * 1024;
    private static readonly string[] ScriptExtensions =
    [
        ".bat", ".cmd", ".js", ".mjs", ".cjs", ".ps1", ".py", ".rb", ".sh", ".pl",
    ];
    private static readonly string[] ExecutableExtensions =
    [
        ".exe", ".com", ".dll", ".msi", ".scr",
    ];
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _userProfile;
    private readonly string _previewRoot;
    private readonly Dictionary<string, InspectedSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreparedImport> _prepared = new(StringComparer.Ordinal);

    public SkillImportService(string? userProfile = null, string? previewRoot = null)
    {
        _userProfile = Path.GetFullPath(userProfile ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _previewRoot = Path.GetFullPath(previewRoot ?? Path.Combine(
            Path.GetTempPath(),
            "PiCompanion",
            "skill-import-preview",
            Guid.NewGuid().ToString("N")));
    }

    public SkillImportSourceInspection InspectDirectory(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
        {
            throw new SkillImportException("所选技能目录不存在。");
        }

        lock (_gate)
        {
            return InspectSource(
                "folder",
                staging => CopyDirectorySafely(source, staging));
        }
    }

    public SkillImportSourceInspection InspectArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var source = Path.GetFullPath(archivePath);
        if (!File.Exists(source))
        {
            throw new SkillImportException("所选技能压缩包不存在。");
        }
        if (!string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new SkillImportException("只支持 ZIP 技能压缩包。");
        }

        lock (_gate)
        {
            return InspectSource(
                "zip",
                staging => ExtractArchiveSafely(source, staging));
        }
    }

    public SkillImportPreparation PrepareDirectory(
        string sourceDirectory,
        string scope,
        SkillDiscoveryWorkspace? workspace,
        PiProjectTrustSnapshot? trust)
    {
        var source = InspectDirectory(sourceDirectory);
        try
        {
            return PrepareSource(source.Token, scope, workspace, trust);
        }
        catch
        {
            CancelSource(source.Token);
            throw;
        }
    }

    public SkillImportPreparation PrepareArchive(
        string archivePath,
        string scope,
        SkillDiscoveryWorkspace? workspace,
        PiProjectTrustSnapshot? trust)
    {
        var source = InspectArchive(archivePath);
        try
        {
            return PrepareSource(source.Token, scope, workspace, trust);
        }
        catch
        {
            CancelSource(source.Token);
            throw;
        }
    }

    public SkillImportPreparation PrepareSource(
        string sourceToken,
        string scope,
        SkillDiscoveryWorkspace? workspace,
        PiProjectTrustSnapshot? trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceToken);
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceToken, out var source))
            {
                throw new SkillImportException("技能来源预览已失效，请重新选择。");
            }

            return Prepare(source, scope, workspace, trust);
        }
    }

    public SkillImportResult Commit(string token, Action? afterMove = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_gate)
        {
            if (!_prepared.TryGetValue(token, out var prepared))
            {
                throw new SkillImportException("技能导入已失效，请重新选择。");
            }
            if (!Directory.Exists(prepared.StagingPath))
            {
                _prepared.Remove(token);
                throw new SkillImportException("技能导入暂存内容已丢失，请重新选择。");
            }
            if (Directory.Exists(prepared.TargetPath) || File.Exists(prepared.TargetPath))
            {
                throw new SkillImportException($"目标技能已存在，未进行覆盖：{prepared.TargetPath}");
            }
            EnsureNoReparsePoints(prepared.AnchorPath, prepared.RootPath);
            var current = SkillContentHasher.Inspect(
                prepared.StagingPath,
                isSingleFile: false);
            if (!string.Equals(
                    current.Hash,
                    prepared.StagedContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SkillImportException("技能内容在确认后发生了变化，请重新选择。");
            }

            Directory.Move(prepared.StagingPath, prepared.TargetPath);
            try
            {
                afterMove?.Invoke();
            }
            catch
            {
                if (!Directory.Exists(prepared.StagingPath) &&
                    Directory.Exists(prepared.TargetPath))
                {
                    Directory.Move(prepared.TargetPath, prepared.StagingPath);
                }
                throw;
            }

            _prepared.Remove(token);
            RemoveSource(prepared.SourceToken);
            return new SkillImportResult(
                prepared.Name,
                prepared.TargetPath,
                prepared.Scope,
                prepared.WorkspaceId);
        }
    }

    public void Cancel(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_gate)
        {
            if (!_prepared.Remove(token, out var prepared))
            {
                return;
            }

            TryDeleteStaging(prepared.StagingPath, prepared.RootPath);
        }
    }

    public void CancelSource(string sourceToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceToken);
        lock (_gate)
        {
            foreach (var preparation in _prepared.Values
                         .Where(item => string.Equals(
                             item.SourceToken,
                             sourceToken,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                _prepared.Remove(preparation.Token);
                TryDeleteStaging(preparation.StagingPath, preparation.RootPath);
            }

            RemoveSource(sourceToken);
        }
    }

    private SkillImportSourceInspection InspectSource(
        string sourceKind,
        Action<string> stageContent)
    {
        Directory.CreateDirectory(_previewRoot);
        EnsureNoReparsePoints(_previewRoot, _previewRoot);
        var token = Guid.NewGuid().ToString("N");
        var staging = CombineUnderRoot(_previewRoot, token);
        var keepStaging = false;
        try
        {
            stageContent(staging);
            var skillFile = Path.Combine(staging, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                throw new SkillImportException("技能根目录必须包含 SKILL.md。");
            }
            if (File.Exists(Path.Combine(staging, MarkerFileName)))
            {
                throw new SkillImportException(
                    $"技能包含保留文件 {MarkerFileName}，不能导入。");
            }

            var inspection = SkillDiscoveryService.InspectSkillFile(skillFile);
            ValidateInspection(inspection);
            ValidateSkillName(inspection.Name);
            var files = InspectFiles(staging);
            var contentHash = SkillContentHasher.Inspect(
                staging,
                isSingleFile: false).Hash;
            var source = new InspectedSource(
                token,
                inspection.Name,
                inspection.Description,
                sourceKind,
                contentHash,
                staging,
                files);
            _sources.Add(token, source);
            keepStaging = true;
            return CreateSourceInspection(source);
        }
        catch (SkillImportException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or NotSupportedException)
        {
            throw new SkillImportException($"无法检查技能：{exception.Message}", exception);
        }
        finally
        {
            if (!keepStaging)
            {
                TryDeleteStaging(staging, _previewRoot);
            }
        }
    }

    private SkillImportPreparation Prepare(
        InspectedSource source,
        string scope,
        SkillDiscoveryWorkspace? workspace,
        PiProjectTrustSnapshot? trust)
    {
        var paths = ResolveTargetRoot(scope, workspace);
        Directory.CreateDirectory(paths.Root);
        EnsureNoReparsePoints(paths.Anchor, paths.Root);
        CleanupStaleStaging(paths.Root);

        var staging = CombineUnderRoot(paths.Root, $".pi-companion-import-{Guid.NewGuid():N}");
        var keepStaging = false;
        try
        {
            if (!Directory.Exists(source.StagingPath))
            {
                throw new SkillImportException("技能来源预览已失效，请重新选择。");
            }
            var currentSourceHash = SkillContentHasher.Inspect(
                source.StagingPath,
                isSingleFile: false).Hash;
            if (!string.Equals(
                    currentSourceHash,
                    source.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SkillImportException(
                    "技能来源在预览后发生了变化，请重新选择。");
            }

            CopyDirectorySafely(source.StagingPath, staging);
            var skillFile = Path.Combine(staging, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                throw new SkillImportException("技能根目录必须包含 SKILL.md。");
            }
            if (File.Exists(Path.Combine(staging, MarkerFileName)))
            {
                throw new SkillImportException($"技能包含保留文件 {MarkerFileName}，不能导入。");
            }

            var inspection = SkillDiscoveryService.InspectSkillFile(skillFile);
            ValidateInspection(inspection);
            ValidateSkillName(inspection.Name);
            var stagedSourceHash = SkillContentHasher.Inspect(
                staging,
                isSingleFile: false).Hash;
            if (!string.Equals(
                    stagedSourceHash,
                    source.ContentHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    inspection.Name,
                    source.Name,
                    StringComparison.Ordinal))
            {
                throw new SkillImportException(
                    "技能来源在预览后发生了变化，请重新选择。");
            }
            var target = CombineUnderRoot(paths.Root, inspection.Name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new SkillImportException($"目标技能已存在，未进行覆盖：{target}");
            }

            var files = InspectFiles(staging);
            var scripts = files
                .Where(static file => file.Kind == "script")
                .Select(static file => file.RelativePath)
                .ToArray();
            var executables = files
                .Where(static file => file.Kind == "executable")
                .Select(static file => file.RelativePath)
                .ToArray();
            var marker = new ImportMarker(
                MarkerVersion,
                inspection.Name,
                inspection.ContentHash!,
                source.SourceKind,
                scope,
                workspace?.Id,
                workspace?.WorkingDirectory,
                DateTimeOffset.UtcNow);
            File.WriteAllText(
                Path.Combine(staging, MarkerFileName),
                $"{JsonSerializer.Serialize(marker, JsonOptions)}{Environment.NewLine}",
                new UTF8Encoding(false));
            var stagedContentHash = SkillContentHasher.Inspect(
                staging,
                isSingleFile: false).Hash;

            var token = Guid.NewGuid().ToString("N");
            var prepared = new PreparedImport(
                token,
                source.Token,
                inspection.Name,
                scope,
                workspace?.Id,
                workspace?.Name,
                workspace?.WorkingDirectory,
                paths.Anchor,
                paths.Root,
                target,
                staging,
                stagedContentHash);
            _prepared.Add(token, prepared);

            keepStaging = true;
            var requiresTrust = scope == "workspace" &&
                !string.Equals(trust?.Status, "trusted", StringComparison.Ordinal);
            return new SkillImportPreparation(
                token,
                source.Token,
                inspection.Name,
                inspection.Description,
                scope,
                workspace?.Id,
                workspace?.Name,
                workspace?.WorkingDirectory,
                target,
                source.SourceKind,
                inspection.ContentHash!,
                files.Count,
                files.Sum(static file => file.Size),
                files,
                scripts,
                executables,
                requiresTrust,
                scope == "workspace" ? trust?.Status ?? "undecided" : "not-required");
        }
        catch (SkillImportException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or NotSupportedException)
        {
            throw new SkillImportException($"无法导入技能：{exception.Message}", exception);
        }
        finally
        {
            if (!keepStaging)
            {
                TryDeleteStaging(staging, paths.Root);
            }
        }
    }

    private (string Anchor, string Root) ResolveTargetRoot(
        string scope,
        SkillDiscoveryWorkspace? workspace)
    {
        if (scope == "global")
        {
            if (workspace is not null)
            {
                throw new SkillImportException("全局导入不能指定工作区。");
            }

            return (_userProfile, Path.Combine(_userProfile, ".pi", "agent", "skills"));
        }
        if (scope != "workspace" || workspace is null)
        {
            throw new SkillImportException("工作区导入必须指定有效工作区。");
        }

        var workspacePath = Path.GetFullPath(workspace.WorkingDirectory);
        if (!Directory.Exists(workspacePath))
        {
            throw new SkillImportException($"目标工作区不存在：{workspacePath}");
        }

        return (workspacePath, Path.Combine(workspacePath, ".pi", "skills"));
    }

    private static void ValidateInspection(SkillFileInspection inspection)
    {
        if (!inspection.IsAvailable || string.IsNullOrWhiteSpace(inspection.ContentHash))
        {
            var reason = inspection.Diagnostics.FirstOrDefault()?.Message ??
                "SKILL.md 缺少可加载的 name 或 description。";
            throw new SkillImportException($"技能校验失败：{reason}");
        }

        var blocking = inspection.Diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Code is "frontmatter-invalid" or "name-invalid" or "name-too-long" or
                "description-required" or "description-too-long" or "content-inspection-failed");
        if (blocking is not null)
        {
            throw new SkillImportException($"技能校验失败：{blocking.Message}");
        }
    }

    private static void ValidateSkillName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new SkillImportException("技能名不能安全映射为安装目录。");
        }
    }

    private static void CopyDirectorySafely(string sourceRoot, string destinationRoot)
    {
        RejectReparsePoint(sourceRoot);
        if (!File.Exists(Path.Combine(sourceRoot, "SKILL.md")))
        {
            throw new SkillImportException("所选目录不是技能根目录：缺少 SKILL.md。");
        }

        Directory.CreateDirectory(destinationRoot);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        var count = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            var (source, destination) = pending.Pop();
            foreach (var entry in new DirectoryInfo(source)
                         .EnumerateFileSystemInfos()
                         .OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                RejectReparsePoint(entry.FullName);
                var target = Path.Combine(destination, entry.Name);
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry.FullName, target));
                    continue;
                }
                if (entry is not FileInfo file)
                {
                    throw new SkillImportException($"不支持的技能目录项：{entry.FullName}");
                }

                ValidateFileLimits(++count, file.Length, ref totalBytes);
                using var input = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(
                    target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
    }

    private static void ExtractArchiveSafely(string archivePath, string destinationRoot)
    {
        var extractionRoot = $"{destinationRoot}-extracted";
        Directory.CreateDirectory(extractionRoot);
        try
        {
            var count = 0;
            long totalBytes = 0;
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var relativePath = NormalizeArchivePath(entry.FullName);
                    if (relativePath.Length == 0)
                    {
                        continue;
                    }
                    if (IsArchiveLink(entry))
                    {
                        throw new SkillImportException(
                            $"压缩包包含不允许的链接：{entry.FullName}");
                    }

                    var destination = Path.GetFullPath(Path.Combine(
                        extractionRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    EnsureInside(extractionRoot, destination);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    ValidateFileLimits(++count, entry.Length, ref totalBytes);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open();
                    using var output = new FileStream(
                        destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }
            }

            var skillFiles = Directory.EnumerateFiles(
                    extractionRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    "SKILL.md",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (skillFiles.Length == 0)
            {
                throw new SkillImportException("压缩包中没有找到 SKILL.md。");
            }
            if (skillFiles.Length > 1)
            {
                throw new SkillImportException(
                    "一个压缩包只能导入一个技能；当前发现多个 SKILL.md。");
            }

            var skillRoot = Path.GetDirectoryName(skillFiles[0])!;
            var outsideFile = Directory.EnumerateFiles(
                    extractionRoot,
                    "*",
                    SearchOption.AllDirectories)
                .FirstOrDefault(path => !IsInsideOrEqual(skillRoot, path));
            if (outsideFile is not null)
            {
                throw new SkillImportException(
                    $"压缩包在技能根目录外包含文件：{Path.GetRelativePath(extractionRoot, outsideFile)}");
            }

            Directory.Move(skillRoot, destinationRoot);
        }
        finally
        {
            if (Directory.Exists(extractionRoot))
            {
                Directory.Delete(extractionRoot, recursive: true);
            }
        }
    }

    private static IReadOnlyList<SkillImportFile> InspectFiles(string contentRoot)
    {
        var result = new List<SkillImportFile>();
        foreach (var path in Directory.EnumerateFiles(
                     contentRoot,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(path => Path.GetRelativePath(contentRoot, path), StringComparer.Ordinal))
        {
            RejectReparsePoint(path);
            var relative = Path.GetRelativePath(contentRoot, path).Replace('\\', '/');
            var info = new FileInfo(path);
            var extension = Path.GetExtension(path);
            var kind = ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? "executable"
                : ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
                  relative.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)
                    ? "script"
                    : "file";
            result.Add(new SkillImportFile(relative, info.Length, kind));
        }

        return result;
    }

    private static SkillImportSourceInspection CreateSourceInspection(
        InspectedSource source)
    {
        var scripts = source.Files
            .Where(static file => file.Kind == "script")
            .Select(static file => file.RelativePath)
            .ToArray();
        var executables = source.Files
            .Where(static file => file.Kind == "executable")
            .Select(static file => file.RelativePath)
            .ToArray();
        return new SkillImportSourceInspection(
            source.Token,
            source.Name,
            source.Description,
            source.SourceKind,
            source.ContentHash,
            source.Files.Count,
            source.Files.Sum(static file => file.Size),
            source.Files,
            scripts,
            executables);
    }

    private void RemoveSource(string sourceToken)
    {
        if (!_sources.Remove(sourceToken, out var source))
        {
            return;
        }

        TryDeleteStaging(source.StagingPath, _previewRoot);
    }

    private static void ValidateFileLimits(int count, long bytes, ref long totalBytes)
    {
        if (count > MaximumFileCount)
        {
            throw new SkillImportException($"技能文件数量超过上限 {MaximumFileCount}。");
        }
        if (bytes < 0 || bytes > MaximumFileBytes)
        {
            throw new SkillImportException(
                $"技能中的单个文件超过上限 {MaximumFileBytes / 1024 / 1024} MB。");
        }

        totalBytes = checked(totalBytes + bytes);
        if (totalBytes > MaximumPackageBytes)
        {
            throw new SkillImportException(
                $"技能总大小超过上限 {MaximumPackageBytes / 1024 / 1024} MB。");
        }
    }

    private static string NormalizeArchivePath(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }
        if (Path.IsPathRooted(value) ||
            normalized.Split('/').Any(segment =>
                segment is "." or ".." ||
                segment.Contains(':', StringComparison.Ordinal)))
        {
            throw new SkillImportException($"压缩包包含不安全路径：{value}");
        }

        return normalized;
    }

    private static bool IsArchiveLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new SkillImportException($"技能包含不允许的链接或 junction：{path}");
        }
    }

    private static void EnsureNoReparsePoints(string anchor, string target)
    {
        var fullAnchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor));
        var fullTarget = Path.GetFullPath(target);
        if (!IsInsideOrEqual(fullAnchor, fullTarget))
        {
            throw new SkillImportException($"技能目标路径越出允许范围：{fullTarget}");
        }

        var current = fullAnchor;
        foreach (var segment in Path.GetRelativePath(fullAnchor, fullTarget)
                     .Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new SkillImportException($"技能目标路径包含链接或 junction：{current}");
            }
        }
    }

    private static string CombineUnderRoot(string root, string name)
    {
        var result = Path.GetFullPath(Path.Combine(root, name));
        EnsureInside(root, result);
        return result;
    }

    private static void EnsureInside(string root, string path)
    {
        if (!IsInside(root, path))
        {
            throw new SkillImportException($"路径逃逸技能导入边界：{path}");
        }
    }

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(
            $"{fullRoot}{Path.DirectorySeparatorChar}",
            PathComparison);
    }

    private static bool IsInsideOrEqual(string root, string path) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            PathComparison) ||
        IsInside(root, path);

    private void CleanupStaleStaging(string root)
    {
        HashSet<string> active;
        lock (_gate)
        {
            active = _prepared.Values
                .Select(static item => item.StagingPath)
                .ToHashSet(
                    OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal);
        }
        foreach (var path in Directory.EnumerateDirectories(
                     root,
                     ".pi-companion-import-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (!active.Contains(path))
            {
                TryDeleteStaging(path, root);
            }
        }
    }

    private static void TryDeleteStaging(string path, string expectedRoot)
    {
        if (!IsInside(expectedRoot, path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record PreparedImport(
        string Token,
        string SourceToken,
        string Name,
        string Scope,
        Guid? WorkspaceId,
        string? WorkspaceName,
        string? WorkspacePath,
        string AnchorPath,
        string RootPath,
        string TargetPath,
        string StagingPath,
        string StagedContentHash);

    private sealed record InspectedSource(
        string Token,
        string Name,
        string? Description,
        string SourceKind,
        string ContentHash,
        string StagingPath,
        IReadOnlyList<SkillImportFile> Files);

    private sealed record ImportMarker(
        int Version,
        string Name,
        string SourceContentHash,
        string SourceKind,
        string Scope,
        Guid? WorkspaceId,
        string? WorkspacePath,
        DateTimeOffset ImportedAt);
}
