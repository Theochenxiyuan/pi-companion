using System.Threading;
using System.Windows;
using System.IO;
using System.Runtime.CompilerServices;
using PiCompanion.Application.Demo;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Settings;
using PiCompanion.Application.Skills;
using PiCompanion.Application.Tasks;
using PiCompanion.Core.Activation;
using PiCompanion.Desktop.Activation;
using PiCompanion.Desktop.Shell;

namespace PiCompanion.Desktop;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "Local\\PiCompanion.Desktop.Stage4";
    private Mutex? _instanceMutex;
    private ActivationPipeServer? _activationServer;
    private DesktopShell? _shell;
    private TaskCoordinator? _coordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        ProcessEnvironmentBootstrap.EnsureWindowsDirectoryEnvironment();
        base.OnStartup(e);

        ExplorerActivationRequest? initialActivation;
        try
        {
            initialActivation = ActivationFileStore.ReadFromArguments(e.Args);
            if (initialActivation is null && e.Args.Contains("--explorer-preview", StringComparer.OrdinalIgnoreCase))
            {
                initialActivation = ExplorerActivationFactory.CreatePreview();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteLocalLog("activation-file-error.log", exception.ToString());
            Shutdown(-1);
            return;
        }

        _instanceMutex = new Mutex(true, InstanceMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            if (initialActivation is not null &&
                !ActivationPipeClient.TrySend(initialActivation, TimeSpan.FromSeconds(2)))
            {
                WriteLocalLog("activation-forwarding-error.log", "现有实例未在超时内接受 Explorer 激活请求。");
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "Pi Companion", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            var eventStore = SqliteRunEventStore.CreateDefault();
            var settings = new AppSettingsService(eventStore);
            var skillDiscovery = new SkillDiscoveryService();
            var piConfiguration = PiConfigurationService.CreateDefault();
            _coordinator = new TaskCoordinator(
                PiRpcBackend.CreateDefault(skillDiscovery),
                eventStore,
                metadataGenerator: PiTaskMetadataGenerator.CreateDefault(),
                taskSettingsResolver: () => settings.Current.Tasks,
                attachmentStaging: AttachmentStagingService.CreateDefault(),
                generalChatWorkspaces: GeneralChatWorkspaceService.CreateDefault());
            _shell = new DesktopShell(
                _coordinator,
                settings,
                piConfiguration,
                skillDiscovery);
            _activationServer = new ActivationPipeServer(
                request => _ = Dispatcher.InvokeAsync(() => _shell?.HandleExplorerActivation(request)),
                exception => WriteLocalLog("activation-pipe-error.log", exception.ToString()));
            _activationServer.Start();
            _shell.Start(
                initialActivation,
                startInBackground: e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            WriteLocalLog("startup-error.log", exception.ToString());
            System.Windows.MessageBox.Show(
                $"桌面外壳启动失败：{exception.Message}\n\n详情已写入本地启动日志。",
                "Pi Companion",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationServer?.Dispose();
        _shell?.Dispose();
        _coordinator?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void WriteLocalLog(string fileName, string content)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PiCompanion",
                "logs");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, fileName), content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

}

internal static class ProcessEnvironmentBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() => EnsureWindowsDirectoryEnvironment();

    internal static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var windowsDirectory = Environment.GetEnvironmentVariable("windir", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            Environment.SetEnvironmentVariable("windir", windowsDirectory, EnvironmentVariableTarget.Process);
        }
    }
}
