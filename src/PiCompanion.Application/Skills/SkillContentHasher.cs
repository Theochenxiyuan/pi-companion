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

    public static SkillContentFingerprint Inspect(string contentPath, bool isSingleFile)
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

    private static IReadOnlyList<ContentFile> InspectSingleFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("技能文件不存在。", fullPath);
        }

        var info = new FileInfo(fullPath);
        EnsureOrdinaryFile(info);
        return [new ContentFile(fullPath, "SKILL.md")];
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
                        Path.GetRelativePath(fullPath, file.FullName).Replace('\\', '/')));
                }
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

    private sealed record ContentFile(string FullPath, string RelativePath);
}
