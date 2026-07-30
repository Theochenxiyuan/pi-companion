using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PiCompanion.Application.Demo;
using PiCompanion.Application.Files;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Settings;
using PiCompanion.Application.Skills;
using PiCompanion.Application.Tasks;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using PiCompanion.Desktop.Branding;
using PiCompanion.Desktop.ChatHost;
using PiCompanion.Desktop.Design;
using PiCompanion.Desktop.Settings;
using PiCompanion.Desktop.Shell;
using PiCompanion.Desktop.Localization;

namespace PiCompanion.Desktop;

public partial class MainWindow : Window
{
    private const string WindowPlacementName = "agent-chat";
    private const int MaximumClipboardImageBytes = 10 * 1024 * 1024;
    private const int WorkspaceGitHistoryPageSize = 25;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TaskCoordinator _coordinator;
    private readonly WorkspaceFileBrowser _workspaceFileBrowser = new();
    private readonly WorkspaceGitBrowser _workspaceGitBrowser = new();
    private readonly SkillDiscoveryService _skillDiscovery = new();
    private readonly SkillRemovalService _skillRemoval = new();
    private readonly SkillImportService _skillImport = new();
    private readonly PiProjectTrustService _piProjectTrust = new();
    private readonly Dictionary<string, PendingSkillSource> _pendingSkillSources =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingSkillImport> _pendingSkillImports =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workspaceMutationGate = new(1, 1);
    private readonly Dictionary<Guid, string> _backgroundTaskVersions = [];
    private readonly HashSet<string> _clipboardDraftAttachments = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettingsService _settings;
    private readonly PiConfigurationService _piConfiguration;
    private readonly Action<PiCompanionSettings> _applySettings;
    private readonly Action _showMonitor;
    private readonly Action _toggleMonitor;
    private readonly Action _exit;
    private AppTheme _theme;
    private ComposerDraft? _draft;
    private bool _bridgeReady;
    private bool _isInitializing;
    private bool _isInitialized;
    private bool _openCurrentTaskWhenReady;
    private Guid? _incrementalTaskId;
    private Guid? _incrementalRunId;
    private long _incrementalSequence;
    private bool _suppressTaskUpdate;
    private CancellationTokenSource? _piOAuthLoginCancellation;
    private string? _piOAuthLoginProviderId;
    private PiConfigurationSnapshot _piConfigurationSnapshot =
        PiConfigurationSnapshot.Unavailable("正在读取 Pi Provider 与模型目录…");

    internal MainWindow(
        TaskCoordinator coordinator,
        AppSettingsService settings,
        PiConfigurationService piConfiguration,
        AppTheme theme,
        Action<PiCompanionSettings> applySettings,
        Action showMonitor,
        Action toggleMonitor,
        Action exit)
    {
        InitializeComponent();
        Icon = PiAppIcon.WindowIcon;
        _coordinator = coordinator;
        _settings = settings;
        _piConfiguration = piConfiguration;
        _theme = theme;
        _applySettings = applySettings;
        _showMonitor = showMonitor;
        _toggleMonitor = toggleMonitor;
        _exit = exit;
        _piConfiguration.SnapshotChanged += OnPiConfigurationSnapshotChanged;
        _coordinator.ProjectionChanged += OnProjectionChanged;
        _coordinator.TaskChanged += OnTaskChanged;
        _coordinator.RunEventReceived += OnRunEventReceived;
        _coordinator.EvidenceChanged += OnEvidenceChanged;
        DesktopLocalizer.Apply(this);
    }

    public bool AllowClose { get; set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_settings.LoadWindowPlacement(WindowPlacementName) is { } placement)
        {
            WindowPlacementService.Restore(this, placement, restoreSize: true);
        }
        ApplyWindowChromeTheme();
    }

    internal void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        ApplyWindowChromeTheme();
        if (ChatWebView.CoreWebView2 is not null)
        {
            ChatWebView.DefaultBackgroundColor = ColorDesignTokens.Canvas(theme);
        }
    }

    private void ApplyWindowChromeTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = _theme == AppTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        var borderColor = _theme == AppTheme.Dark ? 0x00292929 : 0x00DCDCDC;
        var captionColor = _theme == AppTheme.Dark ? 0x00101010 : 0x00F7F7F7;
        var textColor = _theme == AppTheme.Dark ? 0x00EDEDED : 0x00202020;
        _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref borderColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmTextColor, ref textColor, sizeof(int));
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void OpenCurrentTask()
    {
        ShowAndActivate();
        _openCurrentTaskWhenReady = true;
        PostOpenCurrentTask();
    }

    public void OpenWithDraft(ComposerDraft draft)
    {
        if (_coordinator.Current is { Status: var status })
        {
            if (status.IsActive())
            {
                ShowAndActivate();
                if (_bridgeReady)
                {
                    PostMessage("BridgeError", new { message = "当前任务仍在运行，不能切换工作目录。" });
                }

                return;
            }

            _coordinator.BeginNewTask();
        }

        DiscardDraft();
        _draft = draft;
        if (!string.IsNullOrWhiteSpace(draft.WorkingDirectory) &&
            Directory.Exists(draft.WorkingDirectory))
        {
            _coordinator.CreateWorkspace(draft.WorkingDirectory);
        }
        ShowAndActivate();
        if (_bridgeReady)
        {
            PostTaskCollections();
            PostMessage("DraftLoaded", draft);
        }
    }

    public void OpenNewTask()
    {
        ShowAndActivate();
        try
        {
            BeginNewTask();
            _openCurrentTaskWhenReady = true;
            PostOpenCurrentTask();
        }
        catch (InvalidOperationException exception)
        {
            if (_bridgeReady)
            {
                PostMessage("BridgeError", new { message = exception.Message });
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized && !_isInitializing)
        {
            await InitializeWebViewAsync();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        _isInitializing = true;
        ChatWebView.Visibility = Visibility.Hidden;
        LoadingPanel.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        LoadingTitle.Text = DesktopLocalizer.Text("正在准备智能体对话", "Preparing Agent Chat");
        LoadingDetail.Text = DesktopLocalizer.Text("启动 WebView2 并加载本地 Vue 应用", "Starting WebView2 and loading the local Vue app");

        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PiCompanion",
                "webview2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await ChatWebView.EnsureCoreWebView2Async(environment);

            var assetsPath = Path.Combine(AppContext.BaseDirectory, "ChatAssets");
            if (!File.Exists(Path.Combine(assetsPath, "index.html")))
            {
                throw new FileNotFoundException(DesktopLocalizer.Text("未找到智能体对话构建产物。", "Agent Chat build output was not found."), Path.Combine(assetsPath, "index.html"));
            }

            ChatWebView.DefaultBackgroundColor = ColorDesignTokens.Canvas(_theme);
            ChatWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ChatWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            ChatWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ChatWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.pi-companion.local",
                assetsPath,
                CoreWebView2HostResourceAccessKind.DenyCors);
            ChatWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            ChatWebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            ChatWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            ChatWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ChatWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            ChatWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            ChatWebView.ZoomFactorChanged -= OnChatWebViewZoomFactorChanged;
            ChatWebView.ZoomFactorChanged += OnChatWebViewZoomFactorChanged;
            ApplyChatZoom(_settings.Current.General.UiScalePercent);
            var startupTheme = Uri.EscapeDataString(_settings.Current.General.Theme);
            ChatWebView.CoreWebView2.Navigate($"https://app.pi-companion.local/index.html?theme={startupTheme}");
            _isInitialized = true;
        }
        catch (Exception exception)
        {
            ChatWebView.Visibility = Visibility.Hidden;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingTitle.Text = DesktopLocalizer.Text("智能体对话启动失败", "Agent Chat failed to start");
            LoadingDetail.Text = exception.Message;
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("https://app.pi-companion.local/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ChatWebView.Visibility = Visibility.Visible;
        }
        else
        {
            ChatWebView.Visibility = Visibility.Hidden;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingTitle.Text = DesktopLocalizer.Text("本地 Vue 应用加载失败", "The local Vue app failed to load");
            LoadingDetail.Text = $"WebView2 error: {e.WebErrorStatus}";
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void ApplyChatZoom(int percent)
    {
        var zoomFactor = Math.Clamp(percent, 50, 200) / 100d;
        if (Math.Abs(ChatWebView.ZoomFactor - zoomFactor) > 0.001)
        {
            ChatWebView.ZoomFactor = zoomFactor;
        }
    }

    private void OnChatWebViewZoomFactorChanged(object? sender, object e)
    {
        var requestedPercent = (int)Math.Round(ChatWebView.ZoomFactor * 100, MidpointRounding.AwayFromZero);
        var current = _settings.Current;
        if (current.General.UiScalePercent == requestedPercent)
        {
            return;
        }

        var saved = _settings.Save(current with
        {
            General = current.General with { UiScalePercent = requestedPercent },
        });
        ApplyChatZoom(saved.General.UiScalePercent);
        if (_bridgeReady)
        {
            PostSettingsSnapshot();
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? messageType = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            messageType = root.GetProperty("type").GetString();
            var payload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : default;

            switch (messageType)
            {
                case "BridgeReady":
                    CancelPendingSkillImports();
                    _bridgeReady = true;
                    if (_piConfiguration.CachedSnapshot is { } cachedPiConfiguration)
                    {
                        _piConfigurationSnapshot = cachedPiConfiguration;
                        PostCurrentSnapshot();
                        _ = _piConfiguration.RefreshSnapshotAsync();
                    }
                    else
                    {
                        _piConfigurationSnapshot = await _piConfiguration.RefreshSnapshotAsync();
                        PostCurrentSnapshot();
                    }
                    PostOpenCurrentTask();
                    break;
                case "SendPrompt":
                case "StartDemo":
                    await StartFromBridgeAsync(payload);
                    break;
                case "Steer":
                    await _coordinator.SteerAsync(ReadString(payload, "message"));
                    break;
                case "FollowUp":
                    await _coordinator.FollowUpAsync(ReadString(payload, "message"));
                    break;
                case "QueueLocalMessage":
                    _coordinator.QueueLocalMessage(ReadString(payload, "message"));
                    break;
                case "UpdateLocalMessage":
                    _coordinator.UpdateLocalMessage(
                        Guid.Parse(ReadString(payload, "messageId")),
                        ReadString(payload, "message"),
                        ReadStringArray(payload, "attachments"));
                    break;
                case "RemoveLocalMessage":
                    _coordinator.RemoveLocalMessage(Guid.Parse(ReadString(payload, "messageId")));
                    break;
                case "MoveLocalMessage":
                    _coordinator.MoveLocalMessage(
                        Guid.Parse(ReadString(payload, "messageId")),
                        payload.GetProperty("newIndex").GetInt32());
                    break;
                case "DispatchLocalMessage":
                    await _coordinator.DispatchLocalMessageAsync(
                        Guid.Parse(ReadString(payload, "messageId")),
                        ReadString(payload, "delivery"));
                    break;
                case "CancelLocalQueueAutoStart":
                    _coordinator.CancelLocalQueueAutoStart();
                    break;
                case "SelectLocalMessageAttachments":
                    SelectLocalMessageAttachments(payload);
                    break;
                case "AbortRun":
                    await _coordinator.AbortAsync();
                    break;
                case "AbortRetry":
                    await _coordinator.AbortRetryAsync();
                    break;
                case "CompactSession":
                    await _coordinator.CompactSessionAsync(
                        Guid.Parse(ReadString(payload, "taskId")),
                        ReadOptionalString(payload, "customInstructions"));
                    PostSettingsAction("当前任务上下文已压缩。", true);
                    break;
                case "ResolveInteraction":
                    await _coordinator.ResolveInteractionAsync(
                        payload.TryGetProperty("approved", out var approved) && approved.GetBoolean(),
                        ReadOptionalString(payload, "response"),
                        ReadOptionalString(payload, "interactionId"));
                    break;
                case "NewTask":
                    BeginNewTask();
                    break;
                case "CreateWorkspace":
                    CreateWorkspace();
                    break;
                case "UpdateWorkspacePresentation":
                    _coordinator.UpdateWorkspacePresentation(
                        Guid.Parse(ReadString(payload, "workspaceId")),
                        ReadOptionalString(payload, "displayName"),
                        ReadString(payload, "iconKey"),
                        ReadString(payload, "colorKey"));
                    PostTaskCollections();
                    break;
                case "HideWorkspace":
                    _coordinator.HideWorkspace(Guid.Parse(ReadString(payload, "workspaceId")));
                    PostTaskCollections();
                    break;
                case "NewTaskInWorkspace":
                    BeginNewTaskInWorkspace(Guid.Parse(ReadString(payload, "workspaceId")));
                    break;
                case "SelectTask":
                    SelectTask(Guid.Parse(ReadString(payload, "taskId")));
                    break;
                case "LoadMoreTaskHistory":
                    await PostTaskHistoryPageAsync(payload, loadAll: false);
                    break;
                case "LoadAllTaskHistory":
                    await PostTaskHistoryPageAsync(payload, loadAll: true);
                    break;
                case "LoadSkills":
                    await PostSkillsAsync(payload);
                    break;
                case "TrustSkillWorkspace":
                    await TrustSkillWorkspaceAsync(payload);
                    break;
                case "SetWorkspaceTrustDecision":
                    await SetWorkspaceTrustDecisionAsync(payload);
                    break;
                case "RemoveSkillInstallation":
                    await RemoveSkillInstallationAsync(payload);
                    break;
                case "BeginSkillImport":
                    await BeginSkillImportAsync(payload);
                    break;
                case "PrepareSkillImport":
                    await PrepareSkillImportAsync(payload);
                    break;
                case "ConfirmSkillImport":
                    await ConfirmSkillImportAsync(payload);
                    break;
                case "CancelSkillImport":
                    await CancelSkillImportAsync(payload);
                    break;
                case "RenameTask":
                    _coordinator.RenameTask(
                        Guid.Parse(ReadString(payload, "taskId")),
                        ReadString(payload, "title"));
                    PostCurrentSnapshot();
                    break;
                case "UpdateTaskExecutionDefaults":
                {
                    var taskId = Guid.Parse(ReadString(payload, "taskId"));
                    if (_coordinator.Current?.TaskId == taskId)
                    {
                        _coordinator.UpdateTaskExecutionDefaults(
                            taskId,
                            ReadString(payload, "model"),
                            ReadString(payload, "thinkingLevel"));
                    }
                    break;
                }
                case "MoveTaskToRecycleBin":
                    _coordinator.MoveTaskToRecycleBin(Guid.Parse(ReadString(payload, "taskId")));
                    PostCurrentSnapshot();
                    break;
                case "RestoreTaskFromRecycleBin":
                    _coordinator.RestoreTaskFromRecycleBin(Guid.Parse(ReadString(payload, "taskId")));
                    PostTaskCollections();
                    break;
                case "DeleteTaskPermanently":
                    _coordinator.DeleteTaskPermanently(Guid.Parse(ReadString(payload, "taskId")));
                    PostTaskCollections();
                    break;
                case "EmptyRecycleBin":
                    _coordinator.EmptyRecycleBin();
                    PostTaskCollections();
                    PostSettingsAction("回收站已清空。", true);
                    break;
                case "OpenExternalLink":
                    OpenExternalLink(ReadString(payload, "url"));
                    break;
                case "OpenArtifact":
                    OpenArtifact(Guid.Parse(ReadString(payload, "artifactId")));
                    break;
                case "SaveArtifact":
                    SaveArtifact(Guid.Parse(ReadString(payload, "artifactId")));
                    break;
                case "RefreshWorkspaceFiles":
                case "LoadWorkspaceDirectory":
                    await PostWorkspaceDirectoryAsync(payload);
                    break;
                case "SearchWorkspaceFiles":
                    await PostWorkspaceFileSearchAsync(payload);
                    break;
                case "RevealWorkspaceEntry":
                    RevealWorkspaceEntry(payload);
                    break;
                case "OpenWorkspaceLocation":
                    OpenWorkspaceLocation(payload);
                    break;
                case "RefreshWorkspaceGit":
                    await PostWorkspaceGitStatusAsync(payload);
                    break;
                case "RefreshWorkspaceGitHistory":
                    await PostWorkspaceGitHistoryAsync(payload);
                    break;
                case "GetWorkspaceGitDiff":
                    await PostWorkspaceGitDiffAsync(payload);
                    break;
                case "GetWorkspaceGitCommitDiff":
                    await PostWorkspaceGitCommitDiffAsync(payload);
                    break;
                case "GenerateWorkspaceGitCommitMessage":
                    await PostWorkspaceGitCommitMessageAsync(payload);
                    break;
                case "RunWorkspaceGitAction":
                    await PostWorkspaceGitActionAsync(payload);
                    break;
                case "RefreshSessionStatistics":
                    await PostSessionStatisticsAsync(payload);
                    break;
                case "SelectWorkingDirectory":
                    SelectWorkingDirectory(payload);
                    break;
                case "SelectAttachments":
                    SelectAttachments(payload);
                    break;
                case "AddDroppedAttachments":
                    AddDroppedAttachments(payload, e.AdditionalObjects);
                    break;
                case "AddClipboardImageAttachment":
                    AddClipboardImageAttachment(payload);
                    break;
                case "RemoveAttachment":
                    RemoveAttachment(payload);
                    break;
                case "GetFileDiff":
                    PostFileDiff(Guid.Parse(ReadString(payload, "changeId")));
                    break;
                case "RestoreFile":
                    var recovery = _coordinator.RestoreFile(Guid.Parse(ReadString(payload, "changeId")));
                    PostMessage("RecoveryCompleted", new
                    {
                        changeId = recovery.FileChange.Id,
                        succeeded = recovery.Succeeded,
                        status = recovery.Status.ToString(),
                        message = recovery.Message,
                    });
                    break;
                case "SaveSettings":
                    await SaveSettingsAsync(payload);
                    break;
                case "SaveCompanionSettings":
                    SaveCompanionSettings(payload);
                    break;
                case "SavePiAgentSettings":
                    await SavePiAgentSettingsAsync(payload);
                    break;
                case "RefreshPiConfiguration":
                case "ReloadPiConfiguration":
                    if (messageType == "ReloadPiConfiguration")
                    {
                        _coordinator.InvalidateRuntimeResources(null);
                    }
                    _piConfigurationSnapshot = await _piConfiguration.RefreshSnapshotAsync();
                    PostSettingsSnapshot("已重新读取本地 Pi Runtime、Provider 和缓存模型。", true);
                    break;
                case "RefreshPiModelCatalog":
                    _piConfigurationSnapshot = await _piConfiguration.RefreshModelCatalogAsync();
                    PostSettingsSnapshot("已联网刷新 Pi 模型目录。", true);
                    break;
                case "SavePiApiKey":
                    _piConfigurationSnapshot = await _piConfiguration.SaveApiKeyAsync(
                        ReadString(payload, "providerId"),
                        ReadString(payload, "apiKey"));
                    PostSettingsSnapshot("Provider API Key 已保存到 Pi auth.json，新任务会直接使用。", true);
                    break;
                case "LogoutPiProvider":
                    _piConfigurationSnapshot = await _piConfiguration.LogoutAsync(ReadString(payload, "providerId"));
                    PostSettingsSnapshot("已从 Pi auth.json 移除 Provider 凭据。", true);
                    break;
                case "AddPiCustomProvider":
                    if (!payload.TryGetProperty("provider", out var customProviderElement))
                    {
                        throw new InvalidOperationException("缺少自定义 Provider 配置。");
                    }
                    var customProvider = customProviderElement.Deserialize<PiCustomProviderInfo>(JsonOptions) ??
                        throw new InvalidOperationException("自定义 Provider 配置无效。");
                    _piConfigurationSnapshot = await _piConfiguration.AddCustomProviderAsync(
                        customProvider,
                        ReadOptionalString(payload, "apiKey"),
                        ReadOptionalString(payload, "modelsConfigRevision"));
                    PostSettingsSnapshot(DesktopLocalizer.Text($"自定义 Provider {customProvider.Name} 已添加到 Pi。", $"Custom provider {customProvider.Name} was added to Pi."), true);
                    break;
                case "UpdatePiCustomProvider":
                    if (!payload.TryGetProperty("provider", out var updatedCustomProviderElement))
                    {
                        throw new InvalidOperationException("缺少自定义 Provider 配置。");
                    }
                    var updatedCustomProvider = updatedCustomProviderElement.Deserialize<PiCustomProviderInfo>(JsonOptions) ??
                        throw new InvalidOperationException("自定义 Provider 配置无效。");
                    _piConfigurationSnapshot = await _piConfiguration.UpdateCustomProviderAsync(
                        updatedCustomProvider,
                        ReadOptionalString(payload, "apiKey"),
                        ReadOptionalString(payload, "modelsConfigRevision"));
                    PostSettingsSnapshot(DesktopLocalizer.Text($"自定义 Provider {updatedCustomProvider.Name} 已更新。", $"Custom provider {updatedCustomProvider.Name} was updated."), true);
                    break;
                case "DeletePiCustomProvider":
                    var deletedCustomProviderId = ReadString(payload, "providerId");
                    var deletedCustomProviderName = _piConfigurationSnapshot?.CustomProviders
                        .FirstOrDefault(provider => provider.Id == deletedCustomProviderId)?.Name ?? deletedCustomProviderId;
                    _piConfigurationSnapshot = await _piConfiguration.DeleteCustomProviderAsync(
                        deletedCustomProviderId,
                        ReadOptionalString(payload, "modelsConfigRevision"));
                    PostSettingsSnapshot(DesktopLocalizer.Text($"自定义 Provider {deletedCustomProviderName} 已删除。", $"Custom provider {deletedCustomProviderName} was deleted."), true);
                    break;
                case "SavePiEnabledModels":
                    _piConfigurationSnapshot = await _piConfiguration.SaveEnabledModelsAsync(
                        payload.TryGetProperty("enabledModels", out var enabledModelsElement) && enabledModelsElement.ValueKind != JsonValueKind.Null
                            ? enabledModelsElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                            : null);
                    PostSettingsSnapshot("Pi 模型启用范围已更新。", true);
                    break;
                case "OpenPiLogin":
                    var loginProviderId = ReadString(payload, "providerId");
                    if (_piOAuthLoginCancellation is not null)
                    {
                        PostSettingsAction("已有 OAuth 登录正在等待完成。", false);
                        break;
                    }
                    var loginCancellation = new CancellationTokenSource();
                    _piOAuthLoginCancellation = loginCancellation;
                    _piOAuthLoginProviderId = loginProviderId;
                    var browserLoginStarted = 0;
                    PostPiOAuthLoginProgress(loginProviderId, "opening");
                    try
                    {
                        _piConfigurationSnapshot = await _piConfiguration.LoginOAuthAsync(
                            loginProviderId,
                            loginEvent =>
                            {
                                if (loginEvent.Type is "auth_url" or "device_code")
                                {
                                    Interlocked.Exchange(ref browserLoginStarted, 1);
                                    Dispatcher.Invoke(() => PostPiOAuthLoginProgress(loginProviderId, "waiting"));
                                }
                                Dispatcher.Invoke(() => HandlePiOAuthLoginEvent(loginEvent));
                            },
                            loginCancellation.Token);
                        PostSettingsSnapshot("OAuth 登录已完成，Provider 状态已刷新。", true);
                    }
                    catch (OperationCanceledException)
                    {
                        PostSettingsAction("OAuth 登录已取消。", true);
                    }
                    catch (InvalidOperationException exception) when (
                        exception.Message.Contains("interactive input", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Volatile.Read(ref browserLoginStarted) != 0)
                        {
                            PostSettingsAction("浏览器授权已启动，但该 Provider 还要求额外输入；不会再重复打开 Pi 终端。", false);
                        }
                        else
                        {
                            PostSettingsAction(
                                $"该 Provider 的 OAuth 流程仍需要终端输入。{_piConfiguration.LaunchInteractiveLogin(loginProviderId)}",
                                false);
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(_piOAuthLoginCancellation, loginCancellation))
                        {
                            _piOAuthLoginCancellation = null;
                            _piOAuthLoginProviderId = null;
                        }
                        loginCancellation.Dispose();
                        PostPiOAuthLoginProgress(loginProviderId, "idle");
                    }
                    break;
                case "CancelPiOAuthLogin":
                    var cancelProviderId = ReadString(payload, "providerId");
                    if (string.Equals(_piOAuthLoginProviderId, cancelProviderId, StringComparison.Ordinal))
                    {
                        _piOAuthLoginCancellation?.Cancel();
                    }
                    break;
                case "OpenDataDirectory":
                    OpenDirectory(GetDataDirectory());
                    break;
                case "OpenLogDirectory":
                    OpenDirectory(Path.Combine(GetDataDirectory(), "logs"));
                    break;
                case "ExportDiagnostics":
                    ExportDiagnostics();
                    break;
                case "ClearCache":
                    _coordinator.ClearAttachmentCache();
                    if (ChatWebView.CoreWebView2 is not null)
                    {
                        await ChatWebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                            CoreWebView2BrowsingDataKinds.DiskCache);
                    }
                    PostSettingsAction("WebView 与附件缓存已清理。", true);
                    break;
            }
        }
        catch (Exception exception)
        {
            if (_draft is not null && _bridgeReady)
            {
                PostMessage("DraftLoaded", _draft);
            }

            if (IsSettingsRequest(messageType))
            {
                PostSettingsAction(exception.Message, false, SettingsOperation(messageType));
            }
            else
            {
                PostMessage("BridgeError", new { message = DesktopLocalizer.Text(exception.Message) });
            }
        }
    }

    private void BeginNewTask()
    {
        _coordinator.BeginNewTask();
        DiscardDraft();
        if (_bridgeReady)
        {
            PostMessage("InitializeSnapshot", CreateSnapshot(null, null));
        }
    }

    private void SelectTask(Guid taskId)
    {
        TaskProjection projection;
        _suppressTaskUpdate = true;
        try
        {
            projection = _coordinator.SelectTask(taskId);
        }
        finally
        {
            _suppressTaskUpdate = false;
        }

        DiscardDraft();
        if (_bridgeReady)
        {
            PostMessage("InitializeSnapshot", CreateSnapshot(projection, null));
        }
    }

    private void PostCurrentSnapshot()
    {
        if (!_bridgeReady)
        {
            return;
        }

        PostMessage("InitializeSnapshot", CreateSnapshot(_coordinator.Current, _draft));
    }

    private void PostOpenCurrentTask()
    {
        if (!_bridgeReady || !_openCurrentTaskWhenReady)
        {
            return;
        }

        _openCurrentTaskWhenReady = false;
        PostMessage("OpenCurrentTask", new { taskId = _coordinator.Current?.TaskId });
    }

    private void OnPiConfigurationSnapshotChanged(PiConfigurationSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            _piConfigurationSnapshot = snapshot;
            if (_bridgeReady)
            {
                PostSettingsSnapshot();
            }
        });
    }

    private const int TaskHistoryPageSize = 10;

    private InitializeSnapshotDto CreateSnapshot(TaskProjection? projection, ComposerDraft? draft)
    {
        var historyPage = GetTaskHistoryPage(0);
        return BridgeContracts.CreateSnapshot(
            projection,
            _coordinator.CurrentConversation,
            _coordinator.Workspaces,
            _coordinator.RecentTasks,
            historyPage.Items,
            historyPage.HasMore,
            _coordinator.RecycleBinTasks,
            draft,
            BridgeContracts.CreateSettingsSnapshot(_settings.Current, _piConfigurationSnapshot),
            _coordinator.GetRunEvidence,
            _piProjectTrust.GetStatus);
    }

    private async Task PostSkillsAsync(JsonElement payload)
    {
        var request = payload.Deserialize<LoadSkillsRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("技能扫描请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new InvalidOperationException("技能扫描请求缺少 requestId。");
        }

        var workspaces = GetSkillWorkspaces();
        var snapshot = await Task.Run(() => _skillDiscovery.Discover(workspaces));
        PostMessage(
            "SkillsLoaded",
            BridgeContracts.CreateSkillsLoaded(request.RequestId, snapshot));
    }

    private async Task TrustSkillWorkspaceAsync(JsonElement payload)
    {
        var request = payload.Deserialize<TrustSkillWorkspaceRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("工作区信任请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            request.WorkspaceId == Guid.Empty)
        {
            throw new InvalidOperationException("工作区信任请求缺少必要字段。");
        }

        var workspace = _coordinator.Workspaces.FirstOrDefault(candidate =>
            candidate.Id == request.WorkspaceId);
        if (workspace is null)
        {
            var availableWorkspaces = GetSkillWorkspaces();
            var missingSnapshot = await Task.Run(() =>
                _skillDiscovery.Discover(availableWorkspaces));
            PostMessage(
                "SkillWorkspaceTrustCompleted",
                BridgeContracts.CreateSkillWorkspaceTrustCompleted(
                    request.RequestId,
                    succeeded: false,
                    "目标工作区不存在或已不可用。",
                    request.WorkspaceId,
                    missingSnapshot));
            return;
        }

        try
        {
            await Task.Run(() => _piProjectTrust.Trust(workspace.WorkingDirectory));
            _coordinator.InvalidateRuntimeResources(workspace.WorkingDirectory);
            PostTaskCollections();
            var refreshedWorkspaces = GetSkillWorkspaces();
            var snapshot = await Task.Run(() =>
                _skillDiscovery.Discover(refreshedWorkspaces));
            PostMessage(
                "SkillWorkspaceTrustCompleted",
                BridgeContracts.CreateSkillWorkspaceTrustCompleted(
                    request.RequestId,
                    succeeded: true,
                    $"已信任工作区“{workspace.Name}”。",
                    request.WorkspaceId,
                    snapshot));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            var refreshedWorkspaces = GetSkillWorkspaces();
            var snapshot = await Task.Run(() =>
                _skillDiscovery.Discover(refreshedWorkspaces));
            PostMessage(
                "SkillWorkspaceTrustCompleted",
                BridgeContracts.CreateSkillWorkspaceTrustCompleted(
                    request.RequestId,
                    succeeded: false,
                    exception.Message,
                    request.WorkspaceId,
                    snapshot));
        }
    }

    private async Task SetWorkspaceTrustDecisionAsync(JsonElement payload)
    {
        var request = payload.Deserialize<SetWorkspaceTrustDecisionRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("工作区信任决定请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            request.WorkspaceId == Guid.Empty)
        {
            throw new InvalidOperationException("工作区信任决定请求缺少必要字段。");
        }

        var workspace = _coordinator.Workspaces.FirstOrDefault(candidate =>
            candidate.Id == request.WorkspaceId);
        if (workspace is null)
        {
            PostMessage(
                "WorkspaceTrustDecisionCompleted",
                new WorkspaceTrustDecisionCompletedDto(
                    request.RequestId,
                    false,
                    "目标工作区不存在或已不可用。",
                    request.WorkspaceId,
                    "undecided"));
            return;
        }

        try
        {
            var trust = await Task.Run(() =>
                _piProjectTrust.SetDecision(workspace.WorkingDirectory, request.Trusted));
            _coordinator.InvalidateRuntimeResources(workspace.WorkingDirectory);
            PostTaskCollections();
            PostMessage(
                "WorkspaceTrustDecisionCompleted",
                new WorkspaceTrustDecisionCompletedDto(
                    request.RequestId,
                    true,
                    request.Trusted
                        ? $"已信任工作区“{workspace.Name}”。"
                        : $"已将工作区“{workspace.Name}”设为不信任。",
                    request.WorkspaceId,
                    trust.Status));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            PostMessage(
                "WorkspaceTrustDecisionCompleted",
                new WorkspaceTrustDecisionCompletedDto(
                    request.RequestId,
                    false,
                    exception.Message,
                    request.WorkspaceId,
                    "undecided"));
        }
    }

    private SkillDiscoveryWorkspace[] GetSkillWorkspaces() =>
        _coordinator.Workspaces
            .Select(workspace =>
            {
                var trust = _piProjectTrust.GetStatus(workspace.WorkingDirectory);
                return new SkillDiscoveryWorkspace(
                    workspace.Id,
                    workspace.Name,
                    workspace.WorkingDirectory,
                    trust.Status,
                    trust.DecisionPath,
                    trust.Inherited);
            })
            .ToArray();

    private async Task RemoveSkillInstallationAsync(JsonElement payload)
    {
        var request = payload.Deserialize<RemoveSkillInstallationRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("技能卸载请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.InstallationId) ||
            string.IsNullOrWhiteSpace(request.ExpectedContentHash))
        {
            throw new InvalidOperationException("技能卸载请求缺少必要字段。");
        }

        var workspaces = GetSkillWorkspaces();
        var snapshot = await Task.Run(() => _skillDiscovery.Discover(workspaces));
        var skill = snapshot.Skills.FirstOrDefault(candidate =>
            string.Equals(
                SkillRemovalService.CreateInstallationId(candidate),
                request.InstallationId,
                StringComparison.Ordinal));
        if (skill is null)
        {
            PostMessage(
                "SkillRemovalCompleted",
                BridgeContracts.CreateSkillRemovalCompleted(
                    request.RequestId,
                    false,
                    "未找到这个技能安装位置；它可能已经被移动或卸载。",
                    null,
                    null,
                    snapshot));
            return;
        }

        try
        {
            if (request.WorkspaceId is Guid workspaceId &&
                !skill.Origins.Any(origin =>
                    origin.Scope == "workspace" &&
                    origin.Source == "pi" &&
                    origin.WorkspaceId == workspaceId))
            {
                throw new InvalidOperationException("这里只能卸载当前项目中的 Pi 专属技能。");
            }

            if (!string.Equals(
                    skill.ContentHash,
                    request.ExpectedContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("技能内容已变化，请刷新详情后重试。");
            }

            var piOrigins = skill.Origins
                .Where(static origin =>
                    string.Equals(origin.Source, "pi", StringComparison.Ordinal))
                .ToArray();
            var affectedWorkspacePaths = piOrigins
                .Where(static origin => origin.Scope == "workspace")
                .Select(static origin => origin.WorkspacePath)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var affectsGlobal = piOrigins.Any(static origin => origin.Scope == "global");
            _coordinator.InvalidateRuntimeResources(
                !affectsGlobal && affectedWorkspacePaths.Length == 1
                    ? affectedWorkspacePaths[0]
                    : null);

            var result = await Task.Run(() =>
                _skillRemoval.Remove(skill, request.ExpectedContentHash));
            var refreshed = await Task.Run(() => _skillDiscovery.Discover(workspaces));
            PostMessage(
                "SkillRemovalCompleted",
                BridgeContracts.CreateSkillRemovalCompleted(
                    request.RequestId,
                    true,
                    result.Message,
                    result.InstallationId,
                    result.RecoveryPath,
                    refreshed));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                IOException or
                UnauthorizedAccessException)
        {
            var refreshed = await Task.Run(() => _skillDiscovery.Discover(workspaces));
            PostMessage(
                "SkillRemovalCompleted",
                BridgeContracts.CreateSkillRemovalCompleted(
                    request.RequestId,
                    false,
                    exception.Message,
                    null,
                    null,
                    refreshed));
        }
    }

    private async Task BeginSkillImportAsync(JsonElement payload)
    {
        var request = payload.Deserialize<BeginSkillImportRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("技能导入请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            request.SourceKind is not ("folder" or "zip"))
        {
            throw new InvalidOperationException("技能导入请求缺少必要字段。");
        }

        var selectedPath = SelectSkillImportSource(request.SourceKind, initialDirectory: null);
        if (selectedPath is null)
        {
            PostMessage(
                "SkillImportSourceInspected",
                BridgeContracts.CreateSkillImportSourceInspected(
                    request.RequestId,
                    succeeded: false,
                    cancelled: true,
                    "已取消选择。",
                    null));
            return;
        }

        try
        {
            var source = await Task.Run(() => request.SourceKind == "folder"
                ? _skillImport.InspectDirectory(selectedPath)
                : _skillImport.InspectArchive(selectedPath));
            CancelSkillImportRequest(request.RequestId);
            _pendingSkillSources.Add(
                source.Token,
                new PendingSkillSource(request.RequestId, source));
            PostMessage(
                "SkillImportSourceInspected",
                BridgeContracts.CreateSkillImportSourceInspected(
                    request.RequestId,
                    succeeded: true,
                    cancelled: false,
                    "技能来源已就绪。",
                    source));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            PostMessage(
                "SkillImportSourceInspected",
                BridgeContracts.CreateSkillImportSourceInspected(
                    request.RequestId,
                    succeeded: false,
                    cancelled: false,
                    exception.Message,
                    null));
        }
    }

    private async Task PrepareSkillImportAsync(JsonElement payload)
    {
        var request = payload.Deserialize<PrepareSkillImportRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("技能导入目标请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.SourceToken) ||
            request.TargetScope is not ("global" or "workspace") ||
            !_pendingSkillSources.TryGetValue(request.SourceToken, out var pendingSource) ||
            !string.Equals(
                pendingSource.RequestId,
                request.RequestId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("技能来源预览已失效，请重新选择。");
        }

        CancelSkillImportPreparations(request.RequestId);
        var workspaces = GetSkillWorkspaces();
        SkillImportPreparation? preparation = null;
        try
        {
            var workspace = ResolveSkillImportWorkspace(
                request.TargetScope,
                request.WorkspaceId,
                workspaces);
            var trust = workspace is null
                ? null
                : await Task.Run(() => _piProjectTrust.GetStatus(workspace.WorkingDirectory));
            preparation = await Task.Run(() => _skillImport.PrepareSource(
                request.SourceToken,
                request.TargetScope,
                workspace,
                trust));
            _pendingSkillImports.Add(
                preparation.Token,
                new PendingSkillImport(request.RequestId, preparation));
            PostMessage(
                "SkillImportReady",
                BridgeContracts.CreateSkillImportReady(
                    request.RequestId,
                    succeeded: true,
                    "导入位置已就绪。",
                    preparation));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            if (preparation is not null)
            {
                _skillImport.Cancel(preparation.Token);
            }
            PostMessage(
                "SkillImportReady",
                BridgeContracts.CreateSkillImportReady(
                    request.RequestId,
                    succeeded: false,
                    exception.Message,
                    null));
        }
    }

    private async Task ConfirmSkillImportAsync(JsonElement payload)
    {
        var request = payload.Deserialize<ConfirmSkillImportRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("技能导入确认无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.Token) ||
            !_pendingSkillImports.TryGetValue(request.Token, out var pending) ||
            !string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("技能导入确认已失效，请重新选择。");
        }
        _pendingSkillImports.Remove(request.Token);

        await CompleteSkillImportAsync(
            request.RequestId,
            pending.Preparation);
    }

    private async Task CancelSkillImportAsync(JsonElement payload)
    {
        var request = payload.Deserialize<CancelSkillImportRequestDto>(JsonOptions) ??
            throw new InvalidOperationException("取消技能导入请求无效。");
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new InvalidOperationException("取消技能导入请求缺少必要字段。");
        }

        if (!string.IsNullOrWhiteSpace(request.PreparationToken) &&
            _pendingSkillImports.TryGetValue(request.PreparationToken, out var pending) &&
            string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            _pendingSkillImports.Remove(request.PreparationToken);
            await Task.Run(() => _skillImport.Cancel(request.PreparationToken));
        }
        if (!string.IsNullOrWhiteSpace(request.SourceToken) &&
            _pendingSkillSources.TryGetValue(request.SourceToken, out var source) &&
            string.Equals(source.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            _pendingSkillSources.Remove(request.SourceToken);
            await Task.Run(() => _skillImport.CancelSource(request.SourceToken));
        }
        if (string.IsNullOrWhiteSpace(request.SourceToken) &&
            string.IsNullOrWhiteSpace(request.PreparationToken))
        {
            CancelSkillImportRequest(request.RequestId);
        }

        await PostSkillImportCompletedAsync(
            request.RequestId,
                succeeded: false,
                cancelled: true,
                "已取消技能导入。",
                null,
                null);
    }

    private async Task CompleteSkillImportAsync(
        string requestId,
        SkillImportPreparation preparation)
    {
        try
        {
            _coordinator.InvalidateRuntimeResources(
                preparation.Scope == "global" ? null : preparation.WorkspacePath);
            var result = await Task.Run(() => _skillImport.Commit(
                preparation.Token,
                preparation.RequiresProjectTrust
                    ? () => _piProjectTrust.Trust(
                        preparation.WorkspacePath ??
                        throw new SkillImportException("工作区导入缺少信任路径。"))
                    : null));
            _pendingSkillSources.Remove(preparation.SourceToken);
            await PostSkillImportCompletedAsync(
                requestId,
                succeeded: true,
                cancelled: false,
                $"已导入技能“{result.Name}”。",
                result.Name,
                result.TargetPath);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or InvalidDataException)
        {
            await Task.Run(() => _skillImport.Cancel(preparation.Token));
            await PostSkillImportCompletedAsync(
                requestId,
                succeeded: false,
                cancelled: false,
                exception.Message,
                preparation.Name,
                preparation.TargetPath);
        }
    }

    private async Task PostSkillImportCompletedAsync(
        string requestId,
        bool succeeded,
        bool cancelled,
        string message,
        string? skillName,
        string? targetPath)
    {
        var workspaces = GetSkillWorkspaces();
        var snapshot = await Task.Run(() => _skillDiscovery.Discover(workspaces));
        PostMessage(
            "SkillImportCompleted",
            BridgeContracts.CreateSkillImportCompleted(
                requestId,
                succeeded,
                cancelled,
                message,
                skillName,
                targetPath,
                snapshot));
    }

    private static SkillDiscoveryWorkspace? ResolveSkillImportWorkspace(
        string targetScope,
        Guid? requestedWorkspaceId,
        IReadOnlyList<SkillDiscoveryWorkspace> workspaces)
    {
        if (targetScope == "global")
        {
            if (requestedWorkspaceId is not null)
            {
                throw new InvalidOperationException("全局导入不能指定工作区。");
            }
            return null;
        }

        if (requestedWorkspaceId is not Guid workspaceId)
        {
            throw new InvalidOperationException("请选择技能要导入到的工作区。");
        }
        return workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId) ??
            throw new InvalidOperationException("目标工作区不存在或已不可用。");
    }

    private string? SelectSkillImportSource(string sourceKind, string? initialDirectory)
    {
        if (sourceKind == "folder")
        {
            var dialog = new OpenFolderDialog
            {
                Title = DesktopLocalizer.Text("选择技能文件夹", "Select skill folder"),
                Multiselect = false,
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory) &&
                Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }

        var archiveDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = DesktopLocalizer.Text("选择技能 ZIP", "Select skill ZIP"),
            Multiselect = false,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = DesktopLocalizer.Text(
                "ZIP 压缩包 (*.zip)|*.zip",
                "ZIP archives (*.zip)|*.zip"),
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) &&
            Directory.Exists(initialDirectory))
        {
            archiveDialog.InitialDirectory = initialDirectory;
        }
        return archiveDialog.ShowDialog(this) == true ? archiveDialog.FileName : null;
    }

    private void CancelPendingSkillImports()
    {
        foreach (var requestId in _pendingSkillSources.Values
                     .Select(static source => source.RequestId)
                     .Concat(_pendingSkillImports.Values.Select(static item => item.RequestId))
                     .Distinct(StringComparer.Ordinal)
                     .ToArray())
        {
            CancelSkillImportRequest(requestId);
        }
    }

    private void CancelSkillImportRequest(string requestId)
    {
        CancelSkillImportPreparations(requestId);
        foreach (var source in _pendingSkillSources.Values
                     .Where(source => string.Equals(
                         source.RequestId,
                         requestId,
                         StringComparison.Ordinal))
                     .ToArray())
        {
            _pendingSkillSources.Remove(source.Source.Token);
            _skillImport.CancelSource(source.Source.Token);
        }
    }

    private void CancelSkillImportPreparations(string requestId)
    {
        foreach (var pending in _pendingSkillImports.Values
                     .Where(item => string.Equals(
                         item.RequestId,
                         requestId,
                         StringComparison.Ordinal))
                     .ToArray())
        {
            _pendingSkillImports.Remove(pending.Preparation.Token);
            _skillImport.Cancel(pending.Preparation.Token);
        }
    }

    private async Task SaveSettingsAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("settings", out var settingsElement))
        {
            throw new InvalidOperationException("缺少设置内容。");
        }

        var requested = settingsElement.Deserialize<PiCompanionSettings>(JsonOptions) ??
            throw new InvalidOperationException("设置内容无效。");
        var previous = _settings.Current;
        var piDefaultsChanged =
            !string.Equals(requested.Agent.DefaultModel, previous.Agent.DefaultModel, StringComparison.Ordinal) ||
            !string.Equals(requested.Agent.DefaultThinkingLevel, previous.Agent.DefaultThinkingLevel, StringComparison.Ordinal) ||
            requested.Agent.AutoCompact != previous.Agent.AutoCompact ||
            requested.Agent.AutoRetry != previous.Agent.AutoRetry ||
            requested.Agent.CompactionReserveTokens != previous.Agent.CompactionReserveTokens ||
            requested.Agent.CompactionKeepRecentTokens != previous.Agent.CompactionKeepRecentTokens ||
            requested.Agent.RetryMaxRetries != previous.Agent.RetryMaxRetries ||
            requested.Agent.RetryBaseDelayMilliseconds != previous.Agent.RetryBaseDelayMilliseconds ||
            requested.Agent.RetryMaxDelayMilliseconds != previous.Agent.RetryMaxDelayMilliseconds ||
            requested.Agent.SteeringMode != previous.Agent.SteeringMode ||
            requested.Agent.FollowUpMode != previous.Agent.FollowUpMode;
        if (_piConfigurationSnapshot.Available || piDefaultsChanged)
        {
            _piConfigurationSnapshot = await _piConfiguration.SaveAgentDefaultsAsync(
                requested.Agent.DefaultModel,
                requested.Agent.DefaultThinkingLevel,
                requested.Agent.AutoCompact,
                requested.Agent.AutoRetry,
                requested.Agent.CompactionReserveTokens,
                requested.Agent.CompactionKeepRecentTokens,
                requested.Agent.RetryMaxRetries,
                requested.Agent.RetryBaseDelayMilliseconds,
                requested.Agent.RetryMaxDelayMilliseconds,
                requested.Agent.SteeringMode ?? "one-at-a-time",
                requested.Agent.FollowUpMode ?? "one-at-a-time");
        }

        if (requested.General.LaunchAtLogin != previous.General.LaunchAtLogin)
        {
            DevelopmentStartupRegistration.Apply(requested.General.LaunchAtLogin);
        }

        var saved = _settings.Save(requested);
        _applySettings(saved);
        _coordinator.RefreshLocalQueueAutomation();
        ApplyChatZoom(saved.General.UiScalePercent);
        PostSettingsSnapshot("设置已保存。", true);
    }

    private void SaveCompanionSettings(JsonElement payload)
    {
        if (!payload.TryGetProperty("settings", out var settingsElement))
        {
            throw new InvalidOperationException("缺少 Companion 设置内容。");
        }

        var requested = settingsElement.Deserialize<PiCompanionSettings>(JsonOptions) ??
            throw new InvalidOperationException("Companion 设置内容无效。");
        var previous = _settings.Current;
        if (requested.General.LaunchAtLogin != previous.General.LaunchAtLogin)
        {
            DevelopmentStartupRegistration.Apply(requested.General.LaunchAtLogin);
        }

        var saved = _settings.Save(previous with
        {
            General = requested.General,
            Monitor = requested.Monitor,
            Tasks = requested.Tasks,
            Notifications = requested.Notifications,
            DataRetention = requested.DataRetention,
        });
        _applySettings(saved);
        _coordinator.RefreshLocalQueueAutomation();
        ApplyChatZoom(saved.General.UiScalePercent);
        PostSettingsSnapshot();
        if (previous.DataRetention != saved.DataRetention)
        {
            PostTaskCollections();
        }
        PostSettingsAction("已自动保存。", true, "companion-auto-save", silent: true);
    }

    private async Task SavePiAgentSettingsAsync(JsonElement payload)
    {
        if (!payload.TryGetProperty("agent", out var agentElement))
        {
            throw new InvalidOperationException("缺少 Pi Agent 设置内容。");
        }

        var requested = agentElement.Deserialize<AgentSettings>(JsonOptions) ??
            throw new InvalidOperationException("Pi Agent 设置内容无效。");
        _piConfigurationSnapshot = await _piConfiguration.SaveAgentDefaultsAsync(
            requested.DefaultModel,
            requested.DefaultThinkingLevel,
            requested.AutoCompact,
            requested.AutoRetry,
            requested.CompactionReserveTokens,
            requested.CompactionKeepRecentTokens,
            requested.RetryMaxRetries,
            requested.RetryBaseDelayMilliseconds,
            requested.RetryMaxDelayMilliseconds,
            requested.SteeringMode ?? "one-at-a-time",
            requested.FollowUpMode ?? "one-at-a-time");

        var saved = _settings.Save(_settings.Current with { Agent = requested });
        _applySettings(saved);
        PostSettingsSnapshot();
        PostSettingsAction("Pi Agent 设置已保存。", true, "pi-agent-save");
    }

    private void PostSettingsSnapshot(string? message = null, bool succeeded = true)
    {
        PostMessage("SettingsUpdated", BridgeContracts.CreateSettingsSnapshot(
            _settings.Current,
            _piConfigurationSnapshot));
        if (!string.IsNullOrWhiteSpace(message))
        {
            PostSettingsAction(message, succeeded);
        }
    }

    private void PostSettingsAction(
        string message,
        bool succeeded,
        string? operation = null,
        bool silent = false) =>
        PostMessage("SettingsActionCompleted", new
        {
            message = DesktopLocalizer.Text(message),
            succeeded,
            operation,
            silent,
        });

    private static string? SettingsOperation(string? messageType) => messageType switch
    {
        "SaveCompanionSettings" => "companion-auto-save",
        "SavePiAgentSettings" => "pi-agent-save",
        _ => null,
    };

    private void PostPiOAuthLoginProgress(string providerId, string phase) =>
        PostMessage("PiOAuthLoginProgress", new { providerId, phase });

    private static bool IsSettingsRequest(string? messageType) => messageType is
        "SaveSettings" or
        "SaveCompanionSettings" or
        "SavePiAgentSettings" or
        "RefreshPiConfiguration" or
        "ReloadPiConfiguration" or
        "RefreshPiModelCatalog" or
        "SavePiApiKey" or
        "LogoutPiProvider" or
        "AddPiCustomProvider" or
        "UpdatePiCustomProvider" or
        "DeletePiCustomProvider" or
        "SavePiEnabledModels" or
        "OpenPiLogin" or
        "CancelPiOAuthLogin" or
        "OpenDataDirectory" or
        "OpenLogDirectory" or
        "ExportDiagnostics" or
        "ClearCache" or
        "EmptyRecycleBin";

    private void PostTaskCollections()
    {
        if (!_bridgeReady)
        {
            return;
        }

        var historyPage = GetTaskHistoryPage(0);
        PostMessage("TaskCollectionsUpdated", BridgeContracts.CreateTaskCollections(
            _coordinator.Workspaces,
            _coordinator.RecentTasks,
            historyPage.Items,
            historyPage.HasMore,
            _coordinator.RecycleBinTasks,
            _piProjectTrust.GetStatus));
    }

    private void CreateWorkspace()
    {
        var dialog = new OpenFolderDialog
        {
            Title = DesktopLocalizer.Text("添加工作区", "Add workspace"),
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _coordinator.CreateWorkspace(dialog.FolderName);
        PostTaskCollections();
    }

    private void BeginNewTaskInWorkspace(Guid workspaceId)
    {
        var workspace = _coordinator.Workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId) ??
            throw new InvalidOperationException("工作区不存在或已不可用。");
        if (!Directory.Exists(workspace.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作区目录不存在：{workspace.WorkingDirectory}");
        }

        _coordinator.BeginNewTask();
        DiscardDraft();
        _draft = new ComposerDraft(
            workspace.WorkingDirectory,
            string.Empty,
            ResolveDefaultModel(),
            ResolveDefaultThinkingLevel(),
            [],
            ResolveDefaultPermissionMode());
        if (_bridgeReady)
        {
            PostMessage("InitializeSnapshot", CreateSnapshot(null, _draft));
        }
    }

    private async Task PostTaskHistoryPageAsync(JsonElement payload, bool loadAll)
    {
        if (!_bridgeReady)
        {
            return;
        }

        var requestId = ReadString(payload, "requestId");
        var offset = loadAll ? 0 : Math.Max(0, payload.TryGetProperty("offset", out var value) ? value.GetInt32() : 0);
        var page = await Task.Run(() => loadAll
            ? (_coordinator.HistoryTasks, false)
            : GetTaskHistoryPage(offset));
        PostMessage("TaskHistoryPageLoaded", new
        {
            requestId,
            offset,
            items = page.Item1.Select(BridgeContracts.CreateHistoryTask).ToArray(),
            hasMore = page.Item2,
            replaces = loadAll,
        });
    }

    private (IReadOnlyList<TaskHistoryEntry> Items, bool HasMore) GetTaskHistoryPage(int offset)
    {
        var results = _coordinator.GetHistoryTasksPage(offset, TaskHistoryPageSize + 1);
        return (results.Take(TaskHistoryPageSize).ToArray(), results.Count > TaskHistoryPageSize);
    }

    private void SelectWorkingDirectory(JsonElement payload)
    {
        if (_coordinator.Current is not null)
        {
            throw new InvalidOperationException("已创建的任务不能更改工作目录，请新建任务。");
        }

        var initialDirectory = ReadOptionalString(payload, "initialDirectory");
        var dialog = new OpenFolderDialog
        {
            Title = DesktopLocalizer.Text("选择任务工作目录", "Select task working directory"),
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var current = _coordinator.Current;
        _coordinator.CreateWorkspace(dialog.FolderName);
        _draft = new ComposerDraft(
            dialog.FolderName,
            ReadOptionalString(payload, "prompt") ?? string.Empty,
            ReadOptionalString(payload, "model") ?? current?.Model ?? ResolveDefaultModel(),
            ReadOptionalString(payload, "thinkingLevel") ?? current?.ThinkingLevel ?? ResolveDefaultThinkingLevel(),
            _draft?.Attachments ?? [],
            ReadOptionalString(payload, "permissionMode") ?? _draft?.PermissionMode ?? current?.PermissionMode ?? ResolveDefaultPermissionMode());
        PostTaskCollections();
        PostMessage("DraftLoaded", _draft);
    }

    private void SelectAttachments(JsonElement payload)
    {
        if (_coordinator.Current?.Status.IsActive() == true)
        {
            throw new InvalidOperationException("任务运行时暂不能添加附件。");
        }

        var initialDirectory = ReadOptionalString(payload, "initialDirectory");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = DesktopLocalizer.Text("添加附件", "Add attachments"),
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = DesktopLocalizer.Text("所有文件 (*.*)|*.*", "All files (*.*)|*.*"),
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AddAttachments(payload, dialog.FileNames);
    }

    private void SelectLocalMessageAttachments(JsonElement payload)
    {
        var requestId = ReadString(payload, "requestId");
        var existingPaths = ReadStringArray(payload, "attachments");
        var initialDirectory = ReadOptionalString(payload, "initialDirectory") ??
            (_coordinator.Current is { ScopeKind: TaskScopeKind.Workspace } current
                ? current.WorkingDirectory
                : null);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = DesktopLocalizer.Text("添加待发送任务附件", "Add pending task attachments"),
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = DesktopLocalizer.Text("所有文件 (*.*)|*.*", "All files (*.*)|*.*"),
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var normalized = TaskAttachmentRules.NormalizeAndValidate(existingPaths.Concat(dialog.FileNames));
        PostMessage("LocalMessageAttachmentsSelected", new
        {
            requestId,
            attachments = normalized.Select(ComposerAttachment.FromPath).ToArray(),
        });
    }

    private void AddDroppedAttachments(JsonElement payload, IReadOnlyList<object> additionalObjects)
    {
        var paths = additionalObjects
            .Select(static item => item switch
            {
                CoreWebView2File file => file.Path,
                CoreWebView2FileSystemHandle handle => handle.Path,
                _ => null,
            })
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0)
        {
            throw new InvalidOperationException("未能读取拖放的文件。");
        }

        AddAttachments(payload, paths);
    }

    private void AddClipboardImageAttachment(JsonElement payload)
    {
        var mimeType = ReadString(payload, "mimeType").ToLowerInvariant();
        var extension = mimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException("剪贴板中的图片格式不受支持。"),
        };
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(ReadString(payload, "data"));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("剪贴板图片数据无效。", exception);
        }

        if (bytes.Length == 0 || bytes.Length > MaximumClipboardImageBytes)
        {
            throw new InvalidOperationException("粘贴的图片不能超过 10 MB。");
        }

        var directory = Path.Combine(GetDataDirectory(), "clipboard-attachments");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"clipboard-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        try
        {
            AddAttachments(payload, [path]);
            _clipboardDraftAttachments.Add(path);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private void AddAttachments(JsonElement payload, IEnumerable<string> paths)
    {
        if (_coordinator.Current?.Status.IsActive() == true)
        {
            throw new InvalidOperationException("任务运行时暂不能添加附件。");
        }

        var normalized = TaskAttachmentRules.NormalizeAndValidate(
            CurrentDraftAttachments().Select(attachment => attachment.Path).Concat(paths));
        var attachments = normalized.Select(ComposerAttachment.FromPath).ToArray();
        UpdateDraft(payload, attachments);
    }

    private void RemoveAttachment(JsonElement payload)
    {
        if (_coordinator.Current?.Status.IsActive() == true)
        {
            throw new InvalidOperationException("任务运行时暂不能移除附件。");
        }

        var path = Path.GetFullPath(ReadString(payload, "path"));
        var attachments = CurrentDraftAttachments()
            .Where(attachment => !string.Equals(attachment.Path, path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        UpdateDraft(payload, attachments);
        if (_clipboardDraftAttachments.Remove(path))
        {
            DeleteClipboardDraftAttachment(path);
        }
    }

    private IEnumerable<ComposerAttachment> CurrentDraftAttachments()
    {
        if (_draft is not null)
        {
            return _draft.Attachments;
        }

        return _coordinator.Current?.Attachments.Select(ComposerAttachment.FromPath) ?? [];
    }

    private void UpdateDraft(JsonElement payload, IReadOnlyList<ComposerAttachment> attachments)
    {
        var current = _coordinator.Current;
        _draft = new ComposerDraft(
            current?.ScopeKind == TaskScopeKind.GeneralChat
                ? string.Empty
                : ReadOptionalString(payload, "workingDirectory") ?? _draft?.WorkingDirectory ?? current?.WorkingDirectory ?? string.Empty,
            ReadOptionalString(payload, "prompt") ?? _draft?.Prompt ?? string.Empty,
            ReadOptionalString(payload, "model") ?? _draft?.Model ?? current?.Model ?? ResolveDefaultModel(),
            ReadOptionalString(payload, "thinkingLevel") ?? _draft?.ThinkingLevel ?? current?.ThinkingLevel ?? ResolveDefaultThinkingLevel(),
            attachments,
            ReadOptionalString(payload, "permissionMode") ?? _draft?.PermissionMode ?? current?.PermissionMode ?? ResolveDefaultPermissionMode());
        PostMessage("DraftLoaded", _draft);
    }

    private void DiscardDraft()
    {
        foreach (var path in _clipboardDraftAttachments)
        {
            DeleteClipboardDraftAttachment(path);
        }

        _clipboardDraftAttachments.Clear();
        _draft = null;
    }

    private static void DeleteClipboardDraftAttachment(string path)
    {
        var root = Path.GetFullPath(Path.Combine(GetDataDirectory(), "clipboard-attachments"));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative) ||
            !Path.GetFileName(fullPath).StartsWith("clipboard-", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            return;
        }

        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
    }

    private async Task StartFromBridgeAsync(JsonElement payload)
    {
        if (!await _workspaceMutationGate.WaitAsync(0))
        {
            throw new InvalidOperationException("本地 Git 写入正在进行，请稍后再开始任务。");
        }

        try
        {
            var prompt = ReadOptionalString(payload, "prompt") ?? string.Empty;
            var current = _coordinator.Current;
            var workingDirectory = ReadOptionalString(payload, "workingDirectory") ??
                _draft?.WorkingDirectory ?? current?.WorkingDirectory;
            var scopeKind = current?.ScopeKind ??
                (string.IsNullOrWhiteSpace(workingDirectory)
                    ? TaskScopeKind.GeneralChat
                    : TaskScopeKind.Workspace);
            if (scopeKind == TaskScopeKind.Workspace &&
                (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)))
            {
                throw new InvalidOperationException("请先选择有效的工作目录。");
            }

            if (current is { ScopeKind: TaskScopeKind.Workspace } &&
                !string.Equals(
                    Path.GetFullPath(workingDirectory!),
                    Path.GetFullPath(current.WorkingDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("已创建的任务不能更改工作目录，请新建任务。");
            }
            var model = ReadOptionalString(payload, "model") ?? _draft?.Model ?? current?.Model ?? ResolveDefaultModel();
            var thinkingLevel = ReadOptionalString(payload, "thinkingLevel") ?? _draft?.ThinkingLevel ?? current?.ThinkingLevel ?? ResolveDefaultThinkingLevel();
            var permissionMode = current?.PermissionMode ?? ReadOptionalString(payload, "permissionMode") ?? _draft?.PermissionMode ?? ResolveDefaultPermissionMode();
            var modeName = ReadOptionalString(payload, "mode") ?? DemoRunMode.InteractiveSuccess.ToString();
            _ = Enum.TryParse<DemoRunMode>(modeName, true, out var mode);

            var composerAttachments = _draft?.Attachments ??
                current?.Attachments.Select(ComposerAttachment.FromPath).ToArray() ?? [];
            if (string.IsNullOrWhiteSpace(prompt) && composerAttachments.Count == 0)
            {
                throw new InvalidOperationException("请输入任务内容或添加附件。");
            }
            _draft = new ComposerDraft(
                workingDirectory ?? string.Empty,
                prompt,
                model,
                thinkingLevel,
                composerAttachments,
                permissionMode);
            var attachments = composerAttachments.Select(attachment => attachment.Path).ToArray();
            await _coordinator.StartAsync(
                prompt,
                workingDirectory,
                model,
                thinkingLevel,
                mode,
                attachments: attachments,
                permissionMode: permissionMode,
                scopeKind: scopeKind);
            DiscardDraft();
            _showMonitor();
        }
        finally
        {
            _workspaceMutationGate.Release();
        }
    }

    private string ResolveDefaultModel() =>
        _piConfigurationSnapshot.DefaultModel ?? _settings.Current.Agent.DefaultModel;

    private string ResolveDefaultThinkingLevel() =>
        _piConfigurationSnapshot.Available
            ? _piConfigurationSnapshot.DefaultThinkingLevel
            : _settings.Current.Agent.DefaultThinkingLevel;

    private string ResolveDefaultPermissionMode() => _settings.Current.Tasks.PermissionMode ?? "standard";

    private void OnProjectionChanged(TaskProjection? projection)
    {
        if (!_bridgeReady || projection is null || _suppressTaskUpdate)
        {
            return;
        }

        if (_incrementalTaskId == projection.TaskId &&
            _incrementalRunId == projection.RunId &&
            _incrementalSequence == projection.LastSequence)
        {
            _incrementalTaskId = null;
            _incrementalRunId = null;
            _incrementalSequence = 0;
            return;
        }

        var update = BridgeContracts.CreateTask(projection, _coordinator.CurrentConversation, _coordinator.GetRunEvidence);
        _ = Dispatcher.InvokeAsync(() => PostMessage("TaskUpdated", update));
    }

    private void OnTaskChanged(TaskProjection projection)
    {
        if (!_bridgeReady || _coordinator.Current?.TaskId == projection.TaskId)
        {
            return;
        }

        var version = string.Join(
            '\n',
            projection.RunId,
            projection.Status,
            projection.Title,
            projection.Summary);
        lock (_backgroundTaskVersions)
        {
            if (_backgroundTaskVersions.GetValueOrDefault(projection.TaskId) == version)
            {
                return;
            }

            _backgroundTaskVersions[projection.TaskId] = version;
        }

        _ = Dispatcher.InvokeAsync(PostTaskCollections);
    }

    private void OnEvidenceChanged(Guid runId)
    {
        if (!_bridgeReady)
        {
            return;
        }

        var evidence = BridgeContracts.CreateEvidence(_coordinator.GetRunEvidence(runId));
        _ = Dispatcher.InvokeAsync(() => PostMessage("EvidenceUpdated", evidence));
    }

    private void PostFileDiff(Guid changeId)
    {
        var diff = _coordinator.GetFileDiff(changeId) ??
            throw new InvalidOperationException("未找到文件 Diff。");
        PostMessage("FileDiffLoaded", BridgeContracts.CreateFileDiff(diff));
    }

    private void OnRunEventReceived(CompanionRunEvent runEvent)
    {
        if (!_bridgeReady)
        {
            return;
        }

        var projection = _coordinator.Current;
        if (projection is null ||
            projection.TaskId != runEvent.TaskId ||
            projection.RunId != runEvent.RunId ||
            projection.LastSequence != runEvent.Sequence)
        {
            return;
        }

        _incrementalTaskId = runEvent.TaskId;
        _incrementalRunId = runEvent.RunId;
        _incrementalSequence = runEvent.Sequence;
        var update = BridgeContracts.CreateAppendEvents(runEvent, projection);
        _ = Dispatcher.InvokeAsync(() => PostMessage("AppendEvents", update));
    }

    private static string GetDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PiCompanion");

    private static void OpenDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void OpenWorkspaceLocation(JsonElement payload)
    {
        var current = _coordinator.Current;
        if (current?.HasUserWorkspace != true)
        {
            throw new InvalidOperationException("当前任务没有可打开的用户工作区。");
        }

        var workingDirectory = RequireCurrentWorkspace(payload);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("当前工作区目录已不存在。");
        }

        switch (ReadString(payload, "action"))
        {
            case "terminal":
                OpenWorkspaceTerminal(workingDirectory);
                break;
            case "explorer":
                Process.Start(new ProcessStartInfo(workingDirectory) { UseShellExecute = true });
                break;
            case "copy":
                System.Windows.Clipboard.SetText(workingDirectory);
                break;
            default:
                throw new InvalidOperationException("未知的工作区打开方式。");
        }
    }

    private static void OpenWorkspaceTerminal(string workingDirectory)
    {
        var terminal = new ProcessStartInfo("wt.exe")
        {
            UseShellExecute = false,
        };
        terminal.ArgumentList.Add("-d");
        terminal.ArgumentList.Add(workingDirectory);
        try
        {
            Process.Start(terminal);
        }
        catch (Win32Exception)
        {
            var fallback = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
            };
            fallback.ArgumentList.Add("/K");
            fallback.ArgumentList.Add("cd");
            fallback.ArgumentList.Add("/d");
            fallback.ArgumentList.Add(workingDirectory);
            Process.Start(fallback);
        }
    }

    private void OpenArtifact(Guid artifactId)
    {
        var artifact = _coordinator.GetArtifact(artifactId) ??
            throw new InvalidOperationException("未找到这个生成文件。");
        if (!File.Exists(artifact.StoragePath))
        {
            throw new FileNotFoundException("生成文件已不可用。", artifact.DisplayName);
        }

        EnsureArtifactIntegrity(artifact);
        Process.Start(new ProcessStartInfo(artifact.StoragePath) { UseShellExecute = true });
    }

    private void SaveArtifact(Guid artifactId)
    {
        var artifact = _coordinator.GetArtifact(artifactId) ??
            throw new InvalidOperationException("未找到这个生成文件。");
        if (!File.Exists(artifact.StoragePath))
        {
            throw new FileNotFoundException("生成文件已不可用。", artifact.DisplayName);
        }

        EnsureArtifactIntegrity(artifact);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = DesktopLocalizer.Text("保存生成文件", "Save generated file"),
            FileName = artifact.DisplayName,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = DesktopLocalizer.Text("所有文件 (*.*)|*.*", "All files (*.*)|*.*"),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.Copy(artifact.StoragePath, dialog.FileName, overwrite: true);
        File.SetAttributes(dialog.FileName, FileAttributes.Normal);
    }

    private static void EnsureArtifactIntegrity(TaskArtifact artifact)
    {
        using var stream = File.OpenRead(artifact.StoragePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("生成文件的内容校验失败，已停止打开或保存。");
        }
    }

    private async Task PostWorkspaceDirectoryAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        try
        {
            var workingDirectory = RequireCurrentWorkspace(payload);
            var relativePath = ReadOptionalString(payload, "relativePath");
            var listing = await Task.Run(() =>
                _workspaceFileBrowser.ReadDirectory(workingDirectory, relativePath));
            PostMessage("WorkspaceDirectoryLoaded", new
            {
                requestId,
                listing.WorkingDirectory,
                listing.RelativePath,
                listing.Entries,
                listing.InaccessibleEntries,
                error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PostMessage("WorkspaceDirectoryLoaded", new
            {
                requestId,
                workingDirectory = ReadOptionalString(payload, "workingDirectory") ?? string.Empty,
                relativePath = ReadOptionalString(payload, "relativePath") ?? string.Empty,
                entries = Array.Empty<WorkspaceFileEntry>(),
                inaccessibleEntries = 0,
                error = exception.Message,
            });
        }
    }

    private async Task PostWorkspaceFileSearchAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        var query = ReadOptionalString(payload, "query") ?? string.Empty;
        var includeIgnored = payload.TryGetProperty("includeIgnored", out var includeIgnoredElement) &&
                             includeIgnoredElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                             includeIgnoredElement.GetBoolean();
        try
        {
            var workingDirectory = RequireCurrentWorkspace(payload);
            var result = await Task.Run(() =>
                _workspaceFileBrowser.Search(workingDirectory, query, includeIgnored: includeIgnored));
            PostMessage("WorkspaceFileSearchResults", new
            {
                requestId,
                result.WorkingDirectory,
                result.Query,
                result.Entries,
                result.Truncated,
                result.VisitedEntries,
                result.InaccessibleEntries,
                error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PostMessage("WorkspaceFileSearchResults", new
            {
                requestId,
                workingDirectory = ReadOptionalString(payload, "workingDirectory") ?? string.Empty,
                query,
                entries = Array.Empty<WorkspaceFileEntry>(),
                truncated = false,
                visitedEntries = 0,
                inaccessibleEntries = 0,
                error = exception.Message,
            });
        }
    }

    private void RevealWorkspaceEntry(JsonElement payload)
    {
        var workingDirectory = RequireCurrentWorkspace(payload);
        var target = _workspaceFileBrowser.ResolveExistingPath(
            workingDirectory,
            ReadString(payload, "relativePath"));
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (File.Exists(target))
        {
            startInfo.ArgumentList.Add("/select,");
        }

        startInfo.ArgumentList.Add(target);
        Process.Start(startInfo);
    }

    private async Task PostWorkspaceGitStatusAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        try
        {
            var workingDirectory = RequireCurrentWorkspace(payload);
            var snapshot = await Task.Run(() => _workspaceGitBrowser.Read(workingDirectory));
            PostMessage("WorkspaceGitStatusLoaded", new
            {
                requestId,
                snapshot.WorkingDirectory,
                snapshot.IsRepository,
                snapshot.RepositoryRoot,
                snapshot.Branch,
                snapshot.IsDetached,
                snapshot.Branches,
                snapshot.OperationState,
                snapshot.CanManageBranches,
                snapshot.Entries,
                snapshot.StagedFingerprint,
                error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PostMessage("WorkspaceGitStatusLoaded", new
            {
                requestId,
                workingDirectory = ReadOptionalString(payload, "workingDirectory") ?? string.Empty,
                isRepository = false,
                repositoryRoot = (string?)null,
                branch = (string?)null,
                isDetached = false,
                branches = Array.Empty<WorkspaceGitBranch>(),
                operationState = "None",
                canManageBranches = false,
                entries = Array.Empty<WorkspaceGitEntry>(),
                stagedFingerprint = (string?)null,
                error = exception.Message,
            });
        }
    }

    private async Task PostWorkspaceGitHistoryAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        var offset = Math.Max(
            0,
            payload.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var parsedOffset)
                ? parsedOffset
                : 0);
        try
        {
            var workingDirectory = RequireCurrentWorkspace(payload);
            var snapshot = await Task.Run(() => _workspaceGitBrowser.ReadHistory(
                workingDirectory,
                offset,
                WorkspaceGitHistoryPageSize));
            PostMessage("WorkspaceGitHistoryLoaded", new
            {
                requestId,
                snapshot.WorkingDirectory,
                snapshot.Entries,
                offset,
                snapshot.HasMore,
                error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PostMessage("WorkspaceGitHistoryLoaded", new
            {
                requestId,
                workingDirectory = ReadOptionalString(payload, "workingDirectory") ?? string.Empty,
                entries = Array.Empty<WorkspaceGitCommit>(),
                offset,
                hasMore = false,
                error = exception.Message,
            });
        }
    }

    private async Task PostWorkspaceGitDiffAsync(JsonElement payload)
    {
        var workingDirectory = RequireCurrentWorkspace(payload);
        var diff = await Task.Run(() => _workspaceGitBrowser.ReadDiff(
            workingDirectory,
            ReadString(payload, "relativePath")));
        PostMessage("WorkspaceGitDiffLoaded", new
        {
            changeId = $"workspace-git:{diff.RelativePath}",
            runId = string.Empty,
            path = Path.Combine(diff.WorkingDirectory, diff.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            diff.DiffText,
            diff.IsBinary,
            diff.Truncated,
            source = "WorkspaceGit",
        });
    }

    private async Task PostWorkspaceGitCommitDiffAsync(JsonElement payload)
    {
        var workingDirectory = RequireCurrentWorkspace(payload);
        var diff = await Task.Run(() => _workspaceGitBrowser.ReadCommitDiff(
            workingDirectory,
            ReadString(payload, "commitHash")));
        PostMessage("WorkspaceGitCommitDiffLoaded", new
        {
            diff.WorkingDirectory,
            diff.Hash,
            diff.ShortHash,
            diff.Subject,
            diff.Files,
            diff.Truncated,
        });
    }

    private async Task PostWorkspaceGitCommitMessageAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        var requestedWorkingDirectory = ReadOptionalString(payload, "workingDirectory") ?? string.Empty;
        try
        {
            var workingDirectory = RequireCurrentWorkspace(payload);
            var context = await Task.Run(() =>
                _workspaceGitBrowser.ReadCommitMessageContext(workingDirectory));
            var message = await _coordinator.GenerateCommitMessageAsync(context);
            PostMessage("WorkspaceGitCommitMessageGenerated", new
            {
                requestId,
                workingDirectory,
                succeeded = true,
                message,
                stagedFingerprint = context.StagedFingerprint,
                truncatedInput = context.Truncated,
                error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            TimeoutException or
            OperationCanceledException)
        {
            PostMessage("WorkspaceGitCommitMessageGenerated", new
            {
                requestId,
                workingDirectory = requestedWorkingDirectory,
                succeeded = false,
                message = (string?)null,
                stagedFingerprint = (string?)null,
                truncatedInput = false,
                error = exception.Message,
            });
        }
    }

    private async Task PostWorkspaceGitActionAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        var action = ReadString(payload, "action");
        var requestedWorkingDirectory = ReadOptionalString(payload, "workingDirectory") ?? string.Empty;
        var gateAcquired = false;
        try
        {
            gateAcquired = await _workspaceMutationGate.WaitAsync(0);
            if (!gateAcquired)
            {
                throw new InvalidOperationException("任务正在运行或其他 Git 写入尚未结束。");
            }

            var workingDirectory = RequireCurrentWorkspace(payload);
            if (_coordinator.IsWorkspaceActive(workingDirectory))
            {
                throw new InvalidOperationException("任务运行中，Git 写入暂不可用。");
            }

            var detail = await Task.Run(() =>
            {
                switch (action)
                {
                    case "stage":
                        _workspaceGitBrowser.Stage(workingDirectory, ReadStringArray(payload, "relativePaths"));
                        return null;
                    case "unstage":
                        _workspaceGitBrowser.Unstage(workingDirectory, ReadStringArray(payload, "relativePaths"));
                        return null;
                    case "commit":
                        return _workspaceGitBrowser.Commit(workingDirectory, ReadString(payload, "message"));
                    case "switch-branch":
                        _workspaceGitBrowser.SwitchBranch(workingDirectory, ReadString(payload, "branch"));
                        return null;
                    case "create-branch":
                        _workspaceGitBrowser.CreateBranch(workingDirectory, ReadString(payload, "branch"));
                        return null;
                    case "update-branch":
                        _workspaceGitBrowser.UpdateBranch(
                            workingDirectory,
                            ReadString(payload, "strategy"),
                            ReadString(payload, "sourceBranch"));
                        return null;
                    case "abort-operation":
                        _workspaceGitBrowser.AbortOperation(workingDirectory);
                        return null;
                    default:
                        throw new InvalidOperationException("不支持的 Git 写入操作。");
                }
            });
            PostMessage("WorkspaceGitActionCompleted", new
            {
                requestId,
                workingDirectory,
                action,
                succeeded = true,
                message = action switch
                {
                    "stage" => "已暂存所选文件。",
                    "unstage" => "已取消暂存所选文件。",
                    "commit" => "本地提交已创建。",
                    "switch-branch" => "已切换本地分支。",
                    "create-branch" => "本地分支已创建并切换。",
                    "update-branch" => "当前分支已更新。",
                    "abort-operation" => "Git 操作已中止。",
                    _ => "Git 操作已完成。",
                },
                detail,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PostMessage("WorkspaceGitActionCompleted", new
            {
                requestId,
                workingDirectory = requestedWorkingDirectory,
                action,
                succeeded = false,
                message = exception.Message,
                detail = (string?)null,
            });
        }
        finally
        {
            if (gateAcquired)
            {
                _workspaceMutationGate.Release();
            }
        }
    }

    private async Task PostSessionStatisticsAsync(JsonElement payload)
    {
        var requestId = ReadOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        var current = _coordinator.Current;
        var requestedTaskId = ReadOptionalString(payload, "taskId");
        if (current is null ||
            (!string.IsNullOrWhiteSpace(requestedTaskId) &&
             !string.Equals(current.TaskId.ToString("D"), requestedTaskId, StringComparison.OrdinalIgnoreCase)))
        {
            PostMessage("SessionStatisticsLoaded", new
            {
                requestId,
                taskId = requestedTaskId,
                available = false,
                statistics = (AgentSessionStatistics?)null,
                error = (string?)null,
            });
            return;
        }

        try
        {
            var statistics = await _coordinator.GetSessionStatisticsAsync(
                payload.TryGetProperty("loadHistoricalSession", out var historicalElement) &&
                historicalElement.ValueKind == JsonValueKind.True);
            PostMessage("SessionStatisticsLoaded", new
            {
                requestId,
                taskId = current.TaskId.ToString("D"),
                available = statistics is not null,
                statistics,
                error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            PostMessage("SessionStatisticsLoaded", new
            {
                requestId,
                taskId = current.TaskId.ToString("D"),
                available = false,
                statistics = (AgentSessionStatistics?)null,
                error = exception.Message,
            });
        }
    }

    private string RequireCurrentWorkspace(JsonElement payload)
    {
        var current = _coordinator.Current?.WorkingDirectory ?? _draft?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(current))
        {
            throw new InvalidOperationException("请先选择工作目录。");
        }

        var authorized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        var requested = ReadOptionalString(payload, "workingDirectory");
        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested)),
                authorized,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("文件请求与当前工作目录不一致。");
        }

        return authorized;
    }

    private void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = DesktopLocalizer.Text("导出 Pi Companion 诊断包", "Export Pi Companion diagnostic package"),
            AddExtension = true,
            DefaultExt = ".zip",
            Filter = DesktopLocalizer.Text("ZIP 诊断包 (*.zip)|*.zip", "ZIP diagnostic package (*.zip)|*.zip"),
            FileName = $"pi-companion-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var logDirectory = Path.Combine(GetDataDirectory(), "logs");
        var logs = Directory.Exists(logDirectory)
            ? Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly).ToArray()
            : [];
        if (File.Exists(dialog.FileName))
        {
            File.Delete(dialog.FileName);
        }

        using (var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
            using (var stream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(stream, new
                {
                    exportedAt = DateTimeOffset.UtcNow,
                    applicationVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                    operatingSystem = Environment.OSVersion.VersionString,
                    pi = new
                    {
                        _piConfigurationSnapshot.Available,
                        _piConfigurationSnapshot.Version,
                        _piConfigurationSnapshot.RuntimePath,
                        _piConfigurationSnapshot.Error,
                        configuredProviders = _piConfigurationSnapshot.Providers
                            .Where(provider => provider.Configured)
                            .Select(provider => new { provider.Id, provider.AuthType, provider.AuthSource })
                            .ToArray(),
                    },
                }, JsonOptions);
            }

            foreach (var log in logs)
            {
                archive.CreateEntryFromFile(log, Path.Combine("logs", Path.GetFileName(log)), CompressionLevel.Optimal);
            }
        }

        PostSettingsAction(DesktopLocalizer.Text($"诊断包已导出：{dialog.FileName}", $"Diagnostic package exported: {dialog.FileName}"), true);
    }

    private static void OpenExternalLink(string value)
    {
        if (value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            throw new InvalidOperationException("该链接不受支持。");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    private void HandlePiOAuthLoginEvent(PiOAuthLoginEvent loginEvent)
    {
        if (loginEvent.Type == "auth_url" && !string.IsNullOrWhiteSpace(loginEvent.Url))
        {
            OpenExternalLink(loginEvent.Url);
            return;
        }

        if (loginEvent.Type == "device_code" &&
            !string.IsNullOrWhiteSpace(loginEvent.VerificationUri) &&
            !string.IsNullOrWhiteSpace(loginEvent.UserCode))
        {
            System.Windows.Clipboard.SetText(loginEvent.UserCode);
            OpenExternalLink(loginEvent.VerificationUri);
            System.Windows.MessageBox.Show(
                this,
                DesktopLocalizer.Text(
                    $"设备验证码 {loginEvent.UserCode} 已复制到剪贴板。\n\n请在刚打开的浏览器页面中粘贴并继续登录。",
                    $"Device code {loginEvent.UserCode} was copied to the clipboard.\n\nPaste it into the browser page that just opened to continue signing in."),
                DesktopLocalizer.Text("Pi OAuth 登录", "Pi OAuth sign-in"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void PostMessage<T>(string type, T payload)
    {
        if (ChatWebView.CoreWebView2 is null)
        {
            return;
        }

        var envelope = new BridgeEnvelope<T>(BridgeContracts.ProtocolVersion, type, payload);
        ChatWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static string ReadString(JsonElement payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"缺少 {propertyName}。") : value;
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : [];

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        DesktopLocalizer.Apply(button.ContextMenu);
        RefreshConversationDetailMenu(button.ContextMenu);
        button.ContextMenu.Placement = PlacementMode.Custom;
        button.ContextMenu.CustomPopupPlacementCallback = PlaceMoreMenu;
        button.ContextMenu.IsOpen = true;
    }

    private static CustomPopupPlacement[] PlaceMoreMenu(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset) =>
    [
        new(
            new System.Windows.Point(targetSize.Width - popupSize.Width, targetSize.Height + 4),
            PopupPrimaryAxis.Horizontal),
    ];

    private void OnToggleMonitorClick(object sender, RoutedEventArgs e) => _toggleMonitor();

    private void OnConversationDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string detailLevel } ||
            detailLevel is not ("summary" or "normal" or "verbose"))
        {
            return;
        }

        var current = _settings.Current;
        if (string.Equals(current.General.ConversationDetailLevel, detailLevel, StringComparison.Ordinal))
        {
            return;
        }

        var saved = _settings.Save(current with
        {
            General = current.General with { ConversationDetailLevel = detailLevel },
        });
        _applySettings(saved);
        if (_bridgeReady)
        {
            PostSettingsSnapshot();
        }
    }

    private void RefreshConversationDetailMenu(System.Windows.Controls.ContextMenu menu)
    {
        var selected = _settings.Current.General.ConversationDetailLevel ?? "normal";
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (item.Tag is not string detailLevel ||
                detailLevel is not ("summary" or "normal" or "verbose"))
            {
                continue;
            }

            var label = detailLevel switch
            {
                "summary" => DesktopLocalizer.Text("摘要", "Summary"),
                "verbose" => DesktopLocalizer.Text("详细", "Detailed"),
                _ => DesktopLocalizer.Text("标准", "Standard"),
            };
            var check = string.Equals(selected, detailLevel, StringComparison.Ordinal) ? "✓" : "　";
            item.Header = $"{check} {DesktopLocalizer.Text("对话显示", "Conversation display")}: {label}";
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => _exit();

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        ChatWebView.Visibility = Visibility.Hidden;
        LoadingPanel.Visibility = Visibility.Visible;
        _isInitialized = false;
        await InitializeWebViewAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.SaveWindowPlacement(WindowPlacementName, WindowPlacementService.Capture(this));
        if (!AllowClose)
        {
            if (!_settings.Current.General.KeepRunningInTray)
            {
                e.Cancel = true;
                _ = Dispatcher.BeginInvoke(_exit);
                return;
            }

            e.Cancel = true;
            Hide();
            return;
        }

        _coordinator.ProjectionChanged -= OnProjectionChanged;
        _coordinator.TaskChanged -= OnTaskChanged;
        _coordinator.RunEventReceived -= OnRunEventReceived;
        _coordinator.EvidenceChanged -= OnEvidenceChanged;
        CancelPendingSkillImports();
        DiscardDraft();
        ChatWebView.Dispose();
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private sealed record PendingSkillImport(
        string RequestId,
        SkillImportPreparation Preparation);

    private sealed record PendingSkillSource(
        string RequestId,
        SkillImportSourceInspection Source);
}
