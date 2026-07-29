namespace PiCompanion.Application.Tasks;

public sealed record GeneralChatWorkspace(
    string RootDirectory,
    string WorkingDirectory,
    string ArtifactDirectory);

public sealed class GeneralChatWorkspaceService
{
    private readonly string _rootDirectory;

    public GeneralChatWorkspaceService(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public static GeneralChatWorkspaceService CreateDefault()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion");
        return new GeneralChatWorkspaceService(Path.Combine(dataDirectory, "general-chat"));
    }

    public GeneralChatWorkspace GetOrCreate(Guid taskId)
    {
        var root = Path.Combine(_rootDirectory, taskId.ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var artifacts = Path.Combine(root, "published");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(artifacts);
        return new GeneralChatWorkspace(root, workspace, artifacts);
    }

    public string GetArtifactDirectory(Guid taskId) =>
        Path.Combine(_rootDirectory, taskId.ToString("N"), "published");

    public string GetWorkingDirectory(Guid taskId) =>
        Path.Combine(_rootDirectory, taskId.ToString("N"), "workspace");

    public void DeleteTask(Guid taskId) =>
        DeleteDirectory(Path.Combine(_rootDirectory, taskId.ToString("N")));

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
