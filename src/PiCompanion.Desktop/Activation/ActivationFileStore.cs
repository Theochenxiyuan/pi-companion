using System.IO;
using PiCompanion.Core.Activation;

namespace PiCompanion.Desktop.Activation;

internal static class ActivationFileStore
{
    private const string ActivationFileArgument = "--activation-file";

    public static ExplorerActivationRequest? ReadFromArguments(IReadOnlyList<string> arguments)
    {
        var argumentIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], ActivationFileArgument, StringComparison.OrdinalIgnoreCase))
            {
                argumentIndex = index;
                break;
            }
        }

        if (argumentIndex < 0)
        {
            return null;
        }

        if (argumentIndex + 1 >= arguments.Count)
        {
            throw new InvalidDataException("--activation-file 缺少文件路径。");
        }

        var path = ValidatePath(arguments[argumentIndex + 1]);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > ExplorerActivationProtocol.MaximumPayloadBytes)
            {
                throw new InvalidDataException("Explorer 临时激活文件不存在或大小无效。");
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Explorer 临时激活文件不能是重解析点。");
            }

            return ExplorerActivationCodec.Deserialize(File.ReadAllBytes(path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ValidatePath(string path)
    {
        var activationDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion",
            "activations"));
        var candidate = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetDirectoryName(candidate),
                activationDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Explorer 临时激活文件不在受信任目录中。");
        }

        var relativePath = Path.GetRelativePath(activationDirectory, candidate);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Explorer 临时激活文件不在受信任目录中。");
        }

        if (!string.Equals(Path.GetExtension(candidate), ".json", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(Path.GetFileNameWithoutExtension(candidate), out _))
        {
            throw new InvalidDataException("Explorer 临时激活文件名无效。");
        }

        return candidate;
    }
}
