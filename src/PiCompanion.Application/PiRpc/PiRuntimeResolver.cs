namespace PiCompanion.Application.PiRpc;

public sealed record PiRuntimeCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string RuntimePath);

public sealed class PiRuntimeResolver(
    string? explicitRuntimePath = null,
    string? baseDirectory = null,
    string? nodeExecutablePath = null,
    IReadOnlyList<string>? globalRuntimeRoots = null)
{
    public const string RuntimePathEnvironmentVariable = "PI_COMPANION_PI_PATH";
    public const string NodePathEnvironmentVariable = "PI_COMPANION_NODE_PATH";
    public const string DevelopmentMarkerFileName = "PiCompanion.Development";

    private readonly string? _explicitRuntimePath = explicitRuntimePath;
    private readonly string _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
    private readonly string? _nodeExecutablePath = nodeExecutablePath;
    private readonly IReadOnlyList<string>? _globalRuntimeRoots = globalRuntimeRoots;
    private readonly object _resolveGate = new();
    private PiRuntimeCommand? _resolved;

    public PiRuntimeCommand Resolve()
    {
        lock (_resolveGate)
        {
            return _resolved ??= ResolveCore();
        }
    }

    private PiRuntimeCommand ResolveCore()
    {
        var configuredPath = FirstNonEmpty(
            _explicitRuntimePath,
            Environment.GetEnvironmentVariable(RuntimePathEnvironmentVariable));
        if (configuredPath is not null)
        {
            return CreateCommand(Path.GetFullPath(configuredPath), allowPathNode: true);
        }

        var privateRoot = Path.Combine(_baseDirectory, "PiRuntime");
        var candidates = new[]
        {
            Path.Combine(privateRoot, "pi.exe"),
            Path.Combine(privateRoot, "dist", "pi.exe"),
            Path.Combine(privateRoot, "dist", "cli.js"),
            Path.Combine(privateRoot, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js"),
            Path.Combine(privateRoot, "node_modules", "@mariozechner", "pi-coding-agent", "dist", "cli.js"),
        };
        if (IsDevelopmentBuild())
        {
            var globalRuntime = FindGlobalRuntime();
            if (globalRuntime is not null)
            {
                return CreateCommand(globalRuntime, allowPathNode: true);
            }
        }

        var runtimePath = candidates.FirstOrDefault(File.Exists);
        if (runtimePath is null)
        {
            var message = IsDevelopmentBuild()
                ? $"开发版未找到本机 Pi Runtime。请全局安装 @earendil-works/pi-coding-agent，或设置 {RuntimePathEnvironmentVariable}。"
                : $"未找到应用私有 Pi Runtime。开发环境请显式设置 {RuntimePathEnvironmentVariable} 为 pi.exe 或 dist\\cli.js；正式发布版不会回退到用户全局 Pi。";
            throw new FileNotFoundException(
                message,
                privateRoot);
        }

        return CreateCommand(runtimePath, allowPathNode: false);
    }

    private bool IsDevelopmentBuild() =>
        File.Exists(Path.Combine(_baseDirectory, DevelopmentMarkerFileName));

    private string? FindGlobalRuntime()
    {
        var roots = _globalRuntimeRoots ?? EnumerateGlobalRuntimeRoots();
        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullRoot = Path.GetFullPath(root);
            var candidates = new[]
            {
                Path.Combine(fullRoot, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js"),
                Path.Combine(fullRoot, "node_modules", "@mariozechner", "pi-coding-agent", "dist", "cli.js"),
            };
            var runtime = candidates.FirstOrDefault(File.Exists);
            if (runtime is not null)
            {
                return runtime;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> EnumerateGlobalRuntimeRoots()
    {
        var roots = new List<string>();
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            roots.Add(Path.Combine(applicationData, "npm"));
        }

        var npmPrefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        if (!string.IsNullOrWhiteSpace(npmPrefix))
        {
            roots.Add(npmPrefix);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return roots;
    }

    private PiRuntimeCommand CreateCommand(string runtimePath, bool allowPathNode)
    {
        if (!File.Exists(runtimePath))
        {
            throw new FileNotFoundException("配置的 Pi Runtime 不存在。", runtimePath);
        }

        var extension = Path.GetExtension(runtimePath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new PiRuntimeCommand(runtimePath, [], runtimePath);
        }

        if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Pi Runtime 必须是原生 .exe 或 Node.js cli.js；不直接启动全局 .cmd/.ps1 shim。");
        }

        var configuredNode = FirstNonEmpty(
            _nodeExecutablePath,
            Environment.GetEnvironmentVariable(NodePathEnvironmentVariable));
        var privateNode = Path.Combine(FindPrivateRoot(runtimePath), "node.exe");
        var node = configuredNode ?? (File.Exists(privateNode) ? privateNode : null);
        if (node is null && allowPathNode)
        {
            node = "node.exe";
        }

        if (node is null)
        {
            throw new FileNotFoundException(
                $"应用私有 Pi Runtime 缺少 node.exe。开发环境可显式设置 {NodePathEnvironmentVariable}。",
                privateNode);
        }

        if (Path.IsPathFullyQualified(node) && !File.Exists(node))
        {
            throw new FileNotFoundException("配置的 Node.js Runtime 不存在。", node);
        }

        return new PiRuntimeCommand(node, [runtimePath], runtimePath);
    }

    private static string FindPrivateRoot(string runtimePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(runtimePath)!);
        while (directory.Parent is not null &&
               !directory.Name.Equals("PiRuntime", StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }

        return directory.FullName;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
