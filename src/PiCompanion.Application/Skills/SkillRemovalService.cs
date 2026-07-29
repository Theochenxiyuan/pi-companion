using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiCompanion.Application.Skills;

public sealed record SkillRemovalResult(
    string InstallationId,
    string RecoveryPath,
    string Message);

public sealed class SkillRemovalService
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string CreateInstallationId(DiscoveredSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var path = Path.GetFullPath(skill.CanonicalPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant();
    }

    public static bool CanRemove(DiscoveredSkill skill, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Origins.All(static origin =>
                !string.Equals(origin.Source, "pi", StringComparison.Ordinal)))
        {
            reason = "只有 Pi 专属目录中的技能可以卸载。";
            return false;
        }

        if (skill.Origins.Any(static origin => origin.IsCompatibilityLink))
        {
            reason = "Pi 目录中的入口是兼容链接，请通过创建该链接的技能安装器管理。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(skill.ContentHash))
        {
            reason = "无法校验当前技能内容，不能安全卸载。";
            return false;
        }

        if (!TryResolvePiRoot(skill, out _, out reason))
        {
            return false;
        }

        reason = null;
        return true;
    }

    public SkillRemovalResult Remove(DiscoveredSkill skill, string expectedContentHash)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);
        if (!CanRemove(skill, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        if (!string.Equals(
                skill.ContentHash,
                expectedContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("技能内容已变化，请刷新详情后重试。");
        }

        if (!TryResolvePiRoot(skill, out var rootPath, out reason))
        {
            throw new InvalidOperationException(reason);
        }

        var targetPath = Path.GetFullPath(skill.InstallPath);
        EnsureNoReparsePoints(rootPath, targetPath);
        var current = SkillContentHasher.Inspect(targetPath, skill.IsSingleFile);
        if (!string.Equals(
                current.Hash,
                expectedContentHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("技能内容在确认后发生了变化，未执行卸载。");
        }

        var trashRoot = Path.Combine(rootPath, ".pi-companion-trash");
        EnsureTrashRoot(trashRoot);
        var removalDirectory = Path.Combine(
            trashRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(removalDirectory);
        var destination = Path.Combine(removalDirectory, Path.GetFileName(targetPath));
        var installationId = CreateInstallationId(skill);
        var manifestPath = Path.Combine(removalDirectory, "removal.json");
        try
        {
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new RemovalManifest(
                        installationId,
                        skill.Name,
                        targetPath,
                        expectedContentHash,
                        DateTimeOffset.UtcNow),
                    new JsonSerializerOptions { WriteIndented = true }));
            if (skill.IsSingleFile)
            {
                File.Move(targetPath, destination);
            }
            else
            {
                Directory.Move(targetPath, destination);
            }
        }
        catch
        {
            TryDeleteRemovalDirectory(removalDirectory);
            throw;
        }

        return new SkillRemovalResult(
            installationId,
            removalDirectory,
            $"已卸载技能“{skill.Name}”，原内容已移至可恢复位置：{removalDirectory}");
    }

    private static bool TryResolvePiRoot(
        DiscoveredSkill skill,
        out string rootPath,
        out string? reason)
    {
        var targetPath = Path.GetFullPath(skill.InstallPath);
        foreach (var origin in skill.Origins.Where(static origin =>
                     string.Equals(origin.Source, "pi", StringComparison.Ordinal)))
        {
            var candidateRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(origin.RootPath));
            if (!IsStrictDescendant(candidateRoot, targetPath))
            {
                continue;
            }

            var skillFile = Path.GetFullPath(skill.FilePath);
            var targetMatches = skill.IsSingleFile
                ? string.Equals(targetPath, skillFile, PathComparison)
                : IsDescendantOrSame(targetPath, skillFile);
            if (!targetMatches)
            {
                continue;
            }

            rootPath = candidateRoot;
            reason = null;
            return true;
        }

        rootPath = string.Empty;
        reason = "技能路径不在可验证的 Pi 专属技能根目录内。";
        return false;
    }

    private static bool IsStrictDescendant(string rootPath, string targetPath)
    {
        var relative = Path.GetRelativePath(rootPath, targetPath);
        return relative.Length > 0 &&
               relative != "." &&
               !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsDescendantOrSame(string rootPath, string targetPath)
    {
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(rootPath),
                Path.TrimEndingDirectorySeparator(targetPath),
                PathComparison))
        {
            return true;
        }

        return IsStrictDescendant(rootPath, targetPath);
    }

    private static void EnsureNoReparsePoints(string rootPath, string targetPath)
    {
        var relative = Path.GetRelativePath(rootPath, targetPath);
        var current = rootPath;
        foreach (var segment in relative
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(static value => value.Length > 0))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"技能路径包含链接或重解析点：{current}");
            }
        }
    }

    private static void EnsureTrashRoot(string trashRoot)
    {
        if (Directory.Exists(trashRoot))
        {
            var info = new DirectoryInfo(trashRoot);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("技能恢复目录不能是链接或重解析点。");
            }

            return;
        }

        Directory.CreateDirectory(trashRoot);
    }

    private static void TryDeleteRemovalDirectory(string removalDirectory)
    {
        try
        {
            if (Directory.Exists(removalDirectory))
            {
                Directory.Delete(removalDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record RemovalManifest(
        string InstallationId,
        string SkillName,
        string OriginalPath,
        string ContentHash,
        DateTimeOffset RemovedAt);
}
