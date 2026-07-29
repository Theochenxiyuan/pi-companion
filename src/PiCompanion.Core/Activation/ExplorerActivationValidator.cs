namespace PiCompanion.Core.Activation;

public static class ExplorerActivationValidator
{
    private static readonly char[] InvalidPathCharacters = [.. Path.GetInvalidPathChars(), '\0'];

    public static ExplorerActivationRequest Normalize(ExplorerActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion != ExplorerActivationProtocol.Version)
        {
            throw new InvalidDataException($"不支持 Explorer 激活协议版本 {request.ProtocolVersion}。");
        }

        if (request.RequestId == Guid.Empty)
        {
            throw new InvalidDataException("Explorer 激活请求缺少 requestId。");
        }

        if (request.SelectedPaths is null)
        {
            throw new InvalidDataException("Explorer 激活请求缺少 selectedPaths。");
        }

        if (request.SelectedPaths.Count > ExplorerActivationProtocol.MaximumSelectedPathCount)
        {
            throw new InvalidDataException($"最多支持 {ExplorerActivationProtocol.MaximumSelectedPathCount} 个选中项。");
        }

        var workingDirectory = NormalizeAbsolutePath(request.WorkingDirectory, "workingDirectory");
        var selectedPaths = request.SelectedPaths
            .Select((path, index) => NormalizeAbsolutePath(path, $"selectedPaths[{index}]"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !string.Equals(path, workingDirectory, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (string.IsNullOrWhiteSpace(request.InvocationKind) || request.InvocationKind.Length > 64)
        {
            throw new InvalidDataException("Explorer 激活请求的 invocationKind 无效。");
        }

        if (request.Timestamp == default)
        {
            throw new InvalidDataException("Explorer 激活请求缺少 timestamp。");
        }

        return request with
        {
            WorkingDirectory = workingDirectory,
            SelectedPaths = selectedPaths,
            InvocationKind = request.InvocationKind.Trim(),
            Timestamp = request.Timestamp.ToUniversalTime(),
        };
    }

    private static string NormalizeAbsolutePath(string? path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(InvalidPathCharacters) >= 0)
        {
            throw new InvalidDataException($"Explorer 激活请求的 {fieldName} 无效。");
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException($"Explorer 激活请求的 {fieldName} 必须是绝对路径。");
            }

            var normalized = Path.GetFullPath(path);
            var root = Path.GetPathRoot(normalized);
            return string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : Path.TrimEndingDirectorySeparator(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Explorer 激活请求的 {fieldName} 无效。", exception);
        }
    }
}
