using System.Text;
using System.Text.Json;

namespace PiCompanion.Application.Skills;

public sealed record PiProjectTrustSnapshot(
    string Status,
    string WorkspacePath,
    string? DecisionPath,
    bool Inherited,
    string TrustStorePath);

public sealed class PiProjectTrustException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Reads and updates Pi's canonical-directory project trust store. Trust is a
/// project-wide Pi decision, so callers must obtain explicit user confirmation.
/// </summary>
public sealed class PiProjectTrustService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string _trustPath;

    public PiProjectTrustService(string? userProfile = null)
    {
        var profile = Path.GetFullPath(userProfile ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _trustPath = Path.Combine(profile, ".pi", "agent", "trust.json");
    }

    public PiProjectTrustSnapshot GetStatus(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var canonicalWorkspace = CanonicalizeDirectory(workspacePath);
        return WithTrustLock(() =>
        {
            var entries = ReadEntries();
            var current = canonicalWorkspace;
            while (true)
            {
                var match = entries.FirstOrDefault(entry => PathComparer.Equals(entry.Key, current));
                if (!string.IsNullOrWhiteSpace(match.Key) && match.Value is { } decision)
                {
                    return new PiProjectTrustSnapshot(
                        decision ? "trusted" : "declined",
                        canonicalWorkspace,
                        match.Key,
                        !PathComparer.Equals(match.Key, canonicalWorkspace),
                        _trustPath);
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || PathComparer.Equals(parent, current))
                {
                    break;
                }

                current = parent;
            }

            return new PiProjectTrustSnapshot(
                "undecided",
                canonicalWorkspace,
                null,
                false,
                _trustPath);
        });
    }

    public PiProjectTrustSnapshot Trust(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var canonicalWorkspace = CanonicalizeDirectory(workspacePath);
        WithTrustLock(() =>
        {
            var entries = ReadEntries();
            var existingKey = entries.Keys.FirstOrDefault(key =>
                PathComparer.Equals(key, canonicalWorkspace));
            if (existingKey is not null &&
                !string.Equals(existingKey, canonicalWorkspace, StringComparison.Ordinal))
            {
                entries.Remove(existingKey);
            }

            entries[canonicalWorkspace] = true;
            WriteEntries(entries);
            return true;
        });
        return GetStatus(canonicalWorkspace);
    }

    private T WithTrustLock<T>(Func<T> action)
    {
        var directory = Path.GetDirectoryName(_trustPath)!;
        Directory.CreateDirectory(directory);
        var lockPath = $"{_trustPath}.lock";
        string? candidate = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            candidate = Path.Combine(directory, $".pi-companion-trust-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(candidate);
            try
            {
                Directory.Move(candidate, lockPath);
                candidate = null;
                try
                {
                    return action();
                }
                finally
                {
                    Directory.Delete(lockPath, recursive: true);
                }
            }
            catch (IOException) when (Directory.Exists(lockPath))
            {
                Directory.Delete(candidate!, recursive: true);
                candidate = null;
                Thread.Sleep(20);
            }
        }

        if (candidate is not null && Directory.Exists(candidate))
        {
            Directory.Delete(candidate, recursive: true);
        }

        throw new PiProjectTrustException("Pi 项目信任存储正在被其他进程使用，请稍后重试。");
    }

    private Dictionary<string, bool?> ReadEntries()
    {
        if (!File.Exists(_trustPath))
        {
            return new Dictionary<string, bool?>(PathComparer);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_trustPath, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PiProjectTrustException("Pi 项目信任文件格式无效：根节点必须是对象。");
            }

            var result = new Dictionary<string, bool?>(PathComparer);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => throw new PiProjectTrustException(
                        $"Pi 项目信任文件格式无效：{property.Name} 的值必须是 true、false 或 null。"),
                };
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new PiProjectTrustException("无法解析 Pi 项目信任文件。", exception);
        }
    }

    private void WriteEntries(IReadOnlyDictionary<string, bool?> entries)
    {
        var sorted = entries
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        var temporaryPath = $"{_trustPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                $"{JsonSerializer.Serialize(sorted, JsonOptions)}{Environment.NewLine}",
                new UTF8Encoding(false));
            File.Move(temporaryPath, _trustPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string CanonicalizeDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            return fullPath;
        }

        try
        {
            var root = Path.GetPathRoot(fullPath) ??
                throw new PiProjectTrustException($"无法解析工作区根目录：{fullPath}");
            var current = Path.TrimEndingDirectorySeparator(root);
            foreach (var segment in Path.GetRelativePath(root, fullPath)
                         .Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var resolved = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                {
                    current = resolved.FullName;
                }
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return fullPath;
        }
    }
}
