using System.Windows;
using PiCompanion.Application.Demo;
using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Settings;
using PiCompanion.Application.Skills;
using PiCompanion.Core.Activation;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using PiCompanion.Desktop.Design;
using PiCompanion.Desktop.Monitor;
using PiCompanion.Desktop.Localization;
using PiCompanion.Desktop.PromptComposer;
using PiCompanion.Desktop.Skills;
using PiCompanion.Desktop.Tray;

namespace PiCompanion.Desktop.Shell;

public sealed class DesktopShell : IDisposable
{
    private readonly MainWindow _chatWindow;
    private readonly PromptComposerWindow _composerWindow;
    private readonly MonitorWindow _monitorWindow;
    private readonly TrayIconService _trayIcon;
    private readonly AppSettingsService _settings;
    private readonly TaskCoordinator _coordinator;
    private readonly ThemeManager _themeManager;
    private readonly Dictionary<Guid, RunStatus> _lastRunStatuses = [];
    private bool _isExiting;

    public DesktopShell(
        TaskCoordinator coordinator,
        AppSettingsService settings,
        PiConfigurationService piConfiguration,
        SkillDiscoveryService skillDiscovery)
    {
        _coordinator = coordinator;
        _settings = settings;
        DesktopLocalizer.SetLanguage(_settings.Current.General.Language);
        _themeManager = new ThemeManager(_settings.Current.General.Theme);
        MainWindow? chatWindow = null;
        MonitorWindow? monitorWindow = null;
        var skillCompletion = new SkillCompletionProvider(skillDiscovery);

        _composerWindow = new PromptComposerWindow(
            coordinator,
            settings,
            piConfiguration,
            skillCompletion,
            draft => chatWindow?.OpenWithDraft(draft),
            () => monitorWindow?.ShowWithoutActivation());

        _monitorWindow = new MonitorWindow(
            coordinator,
            settings,
            skillCompletion,
            () => chatWindow?.OpenCurrentTask(),
            () => chatWindow?.OpenNewTask(),
            Exit);
        monitorWindow = _monitorWindow;
        _monitorWindow.ApplySettings(_settings.Current.Monitor);

        _chatWindow = new MainWindow(
            coordinator,
            _settings,
            piConfiguration,
            _themeManager.CurrentTheme,
            ApplySettings,
            _monitorWindow.ShowWithoutActivation,
            _monitorWindow.ToggleVisibility,
            Exit);
        chatWindow = _chatWindow;
        _themeManager.ThemeChanged += OnThemeChanged;

        _trayIcon = new TrayIconService(
            _chatWindow.ShowAndActivate,
            _monitorWindow.ToggleVisibility,
            Exit);
        if (coordinator.Current is { } current)
        {
            _lastRunStatuses[current.RunId] = current.Status;
        }
        coordinator.TaskChanged += OnProjectionChanged;
    }

    public void Start(ExplorerActivationRequest? initialActivation = null, bool startInBackground = false)
    {
        _trayIcon.Show();
        if (initialActivation is not null)
        {
            if (_settings.Current.Monitor.ShowOnStartup)
            {
                _monitorWindow.ShowWithoutActivation();
            }

            HandleExplorerActivation(initialActivation);
            return;
        }

        if (!startInBackground)
        {
            _chatWindow.Show();
        }

        if (_settings.Current.Monitor.ShowOnStartup)
        {
            _monitorWindow.ShowWithoutActivation();
        }
    }

    public void HandleExplorerActivation(ExplorerActivationRequest request) =>
        _composerWindow.ShowActivation(request);

    public void Dispose()
    {
        _coordinator.TaskChanged -= OnProjectionChanged;
        _themeManager.ThemeChanged -= OnThemeChanged;
        _themeManager.Dispose();
        _trayIcon.Dispose();
    }

    private void OnProjectionChanged(TaskProjection? projection)
    {
        if (projection is null)
        {
            return;
        }

        RunStatus previous;
        lock (_lastRunStatuses)
        {
            previous = _lastRunStatuses.GetValueOrDefault(projection.RunId, projection.Status);
            _lastRunStatuses[projection.RunId] = projection.Status;
        }
        if (previous == projection.Status)
        {
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var isTerminal = projection.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Interrupted;
            if (isTerminal)
            {
                ApplyCompletionBehavior(_settings.Current.Tasks.CompletionBehavior ?? "keep");
            }

            var notification = _settings.Current.Notifications!;
            if (notification.OnlyWhenAppIsInBackground &&
                (_chatWindow.IsActive || _monitorWindow.IsActive || _composerWindow.IsActive))
            {
                return;
            }

            var shouldNotify = projection.Status switch
            {
                RunStatus.Completed => notification.NotifyOnCompletion,
                RunStatus.Failed or RunStatus.Interrupted => notification.NotifyOnFailure,
                RunStatus.WaitingForApproval or RunStatus.WaitingForAnswer => notification.NotifyWhenAttentionRequired,
                _ => false,
            };
            if (!shouldNotify)
            {
                return;
            }

            var title = projection.Status switch
            {
                RunStatus.Completed => DesktopLocalizer.Text("任务已完成", "Task completed"),
                RunStatus.Failed => DesktopLocalizer.Text("任务失败", "Task failed"),
                RunStatus.Interrupted => DesktopLocalizer.Text("任务已停止", "Task stopped"),
                RunStatus.WaitingForApproval => DesktopLocalizer.Text("等待授权", "Approval required"),
                _ => DesktopLocalizer.Text("等待回答", "Answer required"),
            };
            var message = string.IsNullOrWhiteSpace(projection.Summary) ? projection.Title : projection.Summary;
            _trayIcon.ShowNotification(
                title,
                message,
                projection.Status is RunStatus.Failed or RunStatus.Interrupted,
                notification.PlaySound);
        });
    }

    private void ApplyCompletionBehavior(string behavior)
    {
        switch (behavior)
        {
            case "keep-expanded":
                _monitorWindow.ExpandAfterCompletion();
                break;
            case "collapse-monitor":
                _monitorWindow.CollapseAfterCompletion();
                break;
            case "show-chat":
                _chatWindow.ShowAndActivate();
                break;
        }
    }

    private void ApplySettings(PiCompanionSettings settings)
    {
        _themeManager.SetPreference(settings.General.Theme);
        DesktopLocalizer.SetLanguage(settings.General.Language);
        DesktopLocalizer.Apply(_chatWindow);
        DesktopLocalizer.Apply(_composerWindow);
        DesktopLocalizer.Apply(_monitorWindow);
        _monitorWindow.ApplySettings(settings.Monitor);
        _composerWindow.ApplySettings(settings.Agent, settings.Tasks);
        _monitorWindow.RefreshLocalization();
        _composerWindow.RefreshLocalization();
        _trayIcon.RefreshLocalization();
    }

    private void OnThemeChanged(AppTheme theme) => _chatWindow.ApplyTheme(theme);

    private void Exit()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _trayIcon.Dispose();
        _chatWindow.AllowClose = true;
        _chatWindow.Close();
        _composerWindow.AllowClose = true;
        _composerWindow.Close();
        _monitorWindow.AllowClose = true;
        _monitorWindow.Close();
        System.Windows.Application.Current.Shutdown();
    }
}
