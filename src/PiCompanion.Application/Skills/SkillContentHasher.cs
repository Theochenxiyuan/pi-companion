using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiCompanion.Application.Skills;

public sealed record SkillContentFingerprint(
    string Hash,
    int FileCount,
    long TotalSize,
    DateTimeOffset LastModifiedAt);

public static class SkillContentHasher
{
    private const int MaximumFileCount = 4_096;
    private const long MaximumTotalSize = 128L * 1024 * 1024;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, CachedFingerprint> Cache = new(PathComparer);

    /// <summary>
    /// Hashes every file. Use for integrity checks before importing or removing content.
    /// </summary>
    public static SkillContentFingerprint Inspect(string contentPath, bool isSingleFile)
    {
        var (_, files) = CollectFiles(contentPath, isSingleFile);
        return ComputeFingerprint(files);
    }

    /// <summary>
    /// Reuses the previous fingerprint while the file list, sizes and write times are unchanged.
    /// </summary>
    public static SkillContentFingerprint InspectCached(string contentPath, bool isSingleFile)
    {
        var (fullPath, files) = CollectFiles(contentPath, isSingleFile);
        var cacheKey = $"{(isSingleFile ? 'f' : 'd')}|{fullPath}";
        var signature = ComputeMetadataSignature(files);
        if (Cache.TryGetValue(cacheKey, out var cached) &&
            string.Equals(cached.Signature, signature, StringComparison.Ordinal))
        {
            return cached.Fingerprint;
        }

        var fingerprint = ComputeFingerprint(files);
        Cache[cacheKey] = new CachedFingerprint(signature, fingerprint);
        return fingerprint;
    }

    private static (string FullPath, IReadOnlyList<ContentFile> Files) CollectFiles(
        string contentPath,
        bool isSingleFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        var fullPath = Path.GetFullPath(contentPath);
        var files = isSingleFile
            ? InspectSingleFile(fullPath)
            : InspectDirectory(fullPath);
        if (files.Count > MaximumFileCount)
        {
            throw new InvalidDataException(
                $"技能包含超过 {MaximumFileCount} 个文件，未生成内容指纹。");
        }

        var totalSize = files.Sum(static file => file.Length);
        if (totalSize > MaximumTotalSize)
        {
            throw new InvalidDataException(
                $"技能内容超过 {MaximumTotalSize / 1024 / 1024} MB，未生成内容指纹。");
        }

        return (fullPath, files);
    }

    private static SkillContentFingerprint ComputeFingerprint(IReadOnlyList<ContentFile> files)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalSize = 0;
        var lastModifiedAt = DateTimeOffset.MinValue;
        foreach (var file in files.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            var info = new FileInfo(file.FullPath);
            EnsureOrdinaryFile(info);
            if (totalSize + info.Length > MaximumTotalSize)
            {
                throw new InvalidDataException(
                    $"技能内容超过 {MaximumTotalSize / 1024 / 1024} MB，未生成内容指纹。");
            }
            using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var fileHash = SHA256.HashData(stream);
            Append(aggregate, file.RelativePath);
            Append(aggregate, info.Length.ToString(CultureInfo.InvariantCulture));
            aggregate.AppendData(fileHash);
            aggregate.AppendData([0]);
            totalSize += info.Length;
            var modified = new DateTimeOffset(info.LastWriteTimeUtc);
            if (modified > lastModifiedAt)
            {
                lastModifiedAt = modified;
            }
        }

        return new SkillContentFingerprint(
            Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            files.Count,
            totalSize,
            lastModifiedAt);
    }

    private static string ComputeMetadataSignature(IReadOnlyList<ContentFile> files)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            Append(aggregate, file.RelativePath);
            Append(aggregate, file.Length.ToString(CultureInfo.InvariantCulture));
            Append(aggregate, file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    private static IReadOnlyList<ContentFile> InspectSingleFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("技能文件不存在。", fullPath);
        }

        var info = new FileInfo(fullPath);
        EnsureOrdinaryFile(info);
        return [new ContentFile(fullPath, "SKILL.md", info.Length, info.LastWriteTimeUtc)];
    }

    private static IReadOnlyList<ContentFile> InspectDirectory(string fullPath)
    {
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"技能目录不存在：{fullPath}");
        }

        var root = new DirectoryInfo(fullPath);
        EnsureOrdinaryDirectory(root);
        var files = new List<ContentFile>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法读取技能目录：{directory.FullName}", exception);
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"技能内容包含链接或重解析点：{entry.FullName}");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                    continue;
                }

                if (entry is FileInfo file)
                {
                    files.Add(new ContentFile(
                        file.FullName,
                        Path.GetRelativePath(fullPath, file.FullName).Replace('\\', '/'),
                        file.Length,
                        file.LastWriteTimeUtc));
                }
            }

            if (files.Count > MaximumFileCount)
            {
                throw new InvalidDataException(
                    $"技能包含超过 {MaximumFileCount} 个文件，未生成内容指纹。");
            }
        }

        return files;
    }

    private static void EnsureOrdinaryFile(FileInfo file)
    {
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"技能文件是链接或重解析点：{file.FullName}");
        }
    }

    private static void EnsureOrdinaryDirectory(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"技能目录是链接或重解析点：{directory.FullName}");
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private sealed record ContentFile(
        string FullPath,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record CachedFingerprint(string Signature, SkillContentFingerprint Fingerprint);
}
