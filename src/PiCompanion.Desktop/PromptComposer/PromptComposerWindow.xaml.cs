using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PiCompanion.Application.Demo;
using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Settings;
using PiCompanion.Core.Activation;
using PiCompanion.Core.Runs;
using PiCompanion.Desktop.Branding;
using PiCompanion.Desktop.Shell;
using PiCompanion.Desktop.Localization;
using PiCompanion.Desktop.Skills;

namespace PiCompanion.Desktop.PromptComposer;

public partial class PromptComposerWindow : Window
{
    private const double AttachmentChipWidth = 164;
    private const double AttachmentChipGap = 8;
    private const double AttachmentOverflowWidth = 44;
    private readonly TaskCoordinator _coordinator;
    private readonly AppSettingsService _settings;
    private readonly PiConfigurationService _piConfiguration;
    private readonly Action<ComposerDraft> _openChat;
    private readonly Action _showMonitor;
    private readonly SkillCompletionController _skillCompletion;
    private readonly ObservableCollection<ModelChoice> _modelChoices = [];
    private readonly ICollectionView _modelView;
    private bool _modelSearchActive;
    private bool _allowModelSearchFocusExit;
    private bool _isPiConfigurationLoading;
    private bool _suppressPermissionSelectionChanged;
    private string _previousPermissionMode = "standard";
    private string? _modelSelectionBeforeSearch;
    private CancellationTokenSource? _prewarmCancellation;
    private PiConfigurationSnapshot _piSnapshot = PiConfigurationSnapshot.Unavailable(
        DesktopLocalizer.Text("尚未读取 Pi 配置。", "Pi configuration has not been loaded."));

    public ObservableCollection<ComposerAttachment> Attachments { get; } = [];
    public ObservableCollection<ComposerAttachment> VisibleAttachments { get; } = [];

    internal PromptComposerWindow(
        TaskCoordinator coordinator,
        AppSettingsService settings,
        PiConfigurationService piConfiguration,
        SkillCompletionProvider skillCompletion,
        Action<ComposerDraft> openChat,
        Action showMonitor)
    {
        InitializeComponent();
        Icon = PiAppIcon.WindowIcon;
        ModelComboBox.ItemsSource = _modelChoices;
        _modelView = CollectionViewSource.GetDefaultView(_modelChoices);
        _modelView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelChoice.ProviderName)));
        _coordinator = coordinator;
        _settings = settings;
        _piConfiguration = piConfiguration;
        _openChat = openChat;
        _showMonitor = showMonitor;
        _skillCompletion = new SkillCompletionController(
            PromptTextBox,
            SkillSuggestionPopup,
            SkillSuggestionList,
            SkillSuggestionStatus,
            cancellationToken => skillCompletion.GetEffectiveSkillsAsync(
                WorkingDirectoryText.Text,
                PiCompanion.Core.Tasks.TaskScopeKind.Workspace,
                cancellationToken: cancellationToken));
        WorkingDirectoryText.Text = Environment.CurrentDirectory;
        ThinkingComboBox.SelectionChanged += OnPrewarmInputChanged;
        AttachmentItems.ItemsSource = VisibleAttachments;
        DesktopLocalizer.Apply(this);
    }

    public void RefreshLocalization()
    {
        var thinkingLevel = GetComboBoxValue(ThinkingComboBox);
        DesktopLocalizer.Apply(this);
        UpdateThinkingOptions(thinkingLevel);
        UpdateAttachmentState();
        UpdatePiSelectionState();
    }

    public bool AllowClose { get; set; }

    public async void ShowNearCursor()
    {
        _skillCompletion.Invalidate();
        WorkingDirectoryText.Text = Environment.CurrentDirectory;
        Attachments.Clear();
        UpdateAttachmentState();
        ValidationText.Text = string.Empty;
        var initialSnapshot = _piConfiguration.CachedSnapshot ?? _piSnapshot;
        _isPiConfigurationLoading = initialSnapshot.Models.Count == 0;
        ApplyPiSnapshot(initialSnapshot, preserveSelection: false);
        SelectDefaultPermissionMode(_settings.Current.Tasks.PermissionMode);
        ShowPlaceAndFocus(() => WindowPlacementService.PlaceNearCursor(this));
        SchedulePrewarm();
        await RefreshPiDefaultsAsync();
        SchedulePrewarm();
    }

    public async void ShowActivation(ExplorerActivationRequest request)
    {
        _skillCompletion.Invalidate();
        var activation = ExplorerActivationValidator.Normalize(request);
        WorkingDirectoryText.Text = activation.WorkingDirectory;
        PromptTextBox.Text = string.Empty;
        Attachments.Clear();
        foreach (var selectedPath in activation.SelectedPaths)
        {
            Attachments.Add(CreateAttachment(selectedPath));
        }

        UpdateAttachmentState();
        ValidationText.Text = string.Empty;
        var initialSnapshot = _piConfiguration.CachedSnapshot ?? _piSnapshot;
        _isPiConfigurationLoading = initialSnapshot.Models.Count == 0;
        ApplyPiSnapshot(initialSnapshot, preserveSelection: false);
        SelectDefaultPermissionMode(_settings.Current.Tasks.PermissionMode);
        ShowPlaceAndFocus(() => WindowPlacementService.PlaceNearActivation(
            this,
            activation.CursorPosition,
            activation.ExplorerWindowHandle));
        SchedulePrewarm();
        await RefreshPiDefaultsAsync();
        SchedulePrewarm();
    }

    private void ShowPlaceAndFocus(Action placeWindow)
    {
        if (!IsVisible)
        {
            Show();
        }

        placeWindow();
        FocusPromptInput();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(FocusPromptInput));
    }

    private void FocusPromptInput()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = SetForegroundWindow(handle);
        }

        _ = Activate();
        if (!IsActive)
        {
            return;
        }

        _ = Keyboard.Focus(PromptTextBox);
        PromptTextBox.Focus();
        PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_skillCompletion.CommitSelection())
        {
            return;
        }

        var draft = CreateDraft();
        if (!ValidateDraft(draft, requirePrompt: true))
        {
            return;
        }

        try
        {
            if (_coordinator.Current is { Status: var status } && !status.IsActive())
            {
                _coordinator.BeginNewTask();
            }

            await _coordinator.StartAsync(
                draft.Prompt,
                draft.WorkingDirectory,
                draft.Model,
                draft.ThinkingLevel,
                DemoRunMode.InteractiveSuccess,
                attachments: draft.Attachments.Select(attachment => attachment.Path).ToArray(),
                permissionMode: draft.PermissionMode);
            Hide();
            _showMonitor();
        }
        catch (InvalidOperationException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void OnOpenChatClick(object sender, RoutedEventArgs e)
    {
        if (_skillCompletion.CommitSelection())
        {
            return;
        }

        var draft = CreateDraft();
        if (!ValidateDraft(draft, requirePrompt: false))
        {
            return;
        }

        if (_coordinator.Current is { Status: var status } && !status.IsActive())
        {
            _coordinator.BeginNewTask();
        }

        Hide();
        _openChat(draft);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelPendingPrewarm();
        _skillCompletion.Close();
        Hide();
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path })
        {
            return;
        }

        var attachment = Attachments.FirstOrDefault(
            item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (attachment is not null)
        {
            Attachments.Remove(attachment);
            UpdateAttachmentState();
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnResizeGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
        Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
    }

    private void OnAttachmentHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAttachmentState();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (FullAccessConfirmationOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                DismissFullAccessConfirmation();
            }

            e.Handled = true;
            return;
        }

        if (_skillCompletion.HandlePreviewKeyDown(e))
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnStartClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        CancelPendingPrewarm();
        _skillCompletion.Close();
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _skillCompletion.Dispose();
        }
    }

    private ComposerDraft CreateDraft()
    {
        var model = GetComboBoxValue(ModelComboBox) ?? string.Empty;
        var thinking = (ThinkingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "high";
        var permissionMode = (PermissionComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "standard";
        return new ComposerDraft(
            WorkingDirectoryText.Text,
            PromptTextBox.Text.Trim(),
            model,
            thinking,
            Attachments.ToArray(),
            permissionMode);
    }

    private void OnPermissionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPermissionSelectionChanged)
        {
            return;
        }

        var selected = GetComboBoxValue(PermissionComboBox) ?? "standard";
        if (!string.Equals(selected, "full-access", StringComparison.OrdinalIgnoreCase))
        {
            _previousPermissionMode = selected;
            UpdatePermissionVisualState(selected);
            return;
        }

        _suppressPermissionSelectionChanged = true;
        try
        {
            SelectComboBoxValue(PermissionComboBox, _previousPermissionMode);
            UpdatePermissionVisualState(_previousPermissionMode);
        }
        finally
        {
            _suppressPermissionSelectionChanged = false;
        }

        FullAccessConfirmationOverlay.Visibility = Visibility.Visible;
    }

    private void OnCancelFullAccessClick(object sender, RoutedEventArgs e) =>
        DismissFullAccessConfirmation();

    private void OnConfirmFullAccessClick(object sender, RoutedEventArgs e)
    {
        _suppressPermissionSelectionChanged = true;
        try
        {
            SelectComboBoxValue(PermissionComboBox, "full-access");
            _previousPermissionMode = "full-access";
            UpdatePermissionVisualState("full-access");
        }
        finally
        {
            _suppressPermissionSelectionChanged = false;
        }

        DismissFullAccessConfirmation();
    }

    private void DismissFullAccessConfirmation()
    {
        FullAccessConfirmationOverlay.Visibility = Visibility.Collapsed;
        PermissionComboBox.Focus();
    }

    private void UpdatePermissionVisualState(string permissionMode)
    {
        var fullAccess = string.Equals(permissionMode, "full-access", StringComparison.OrdinalIgnoreCase);
        PermissionComboBox.SetResourceReference(
            ForegroundProperty,
            fullAccess ? "DangerBrush" : "TextPrimaryBrush");
        PermissionComboBox.SetResourceReference(
            BorderBrushProperty,
            fullAccess ? "DangerBrush" : "StrokeBrush");
    }

    private bool ValidateDraft(ComposerDraft draft, bool requirePrompt)
    {
        if (string.IsNullOrWhiteSpace(draft.Model))
        {
            ValidationText.Text = _piSnapshot.Error ?? DesktopLocalizer.Text("Pi 没有可用的默认模型", "Pi has no available default model");
            return false;
        }

        if (!Directory.Exists(draft.WorkingDirectory))
        {
            ValidationText.Text = DesktopLocalizer.Text("工作目录不存在", "Working directory does not exist");
            return false;
        }

        var unavailableAttachment = draft.Attachments.FirstOrDefault(
            attachment => !File.Exists(attachment.Path) && !Directory.Exists(attachment.Path));
        if (unavailableAttachment is not null)
        {
            ValidationText.Text = DesktopLocalizer.Text($"附件不可用：{unavailableAttachment.DisplayName}", $"Attachment unavailable: {unavailableAttachment.DisplayName}");
            return false;
        }

        if (requirePrompt && string.IsNullOrWhiteSpace(draft.Prompt))
        {
            ValidationText.Text = DesktopLocalizer.Text("请输入任务内容", "Enter a task description");
            return false;
        }

        ValidationText.Text = string.Empty;
        return true;
    }

    private void UpdateAttachmentState()
    {
        var hasAttachments = Attachments.Count > 0;
        EmptyAttachmentText.Visibility = hasAttachments ? Visibility.Collapsed : Visibility.Visible;
        AttachmentContentPanel.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;

        VisibleAttachments.Clear();
        if (!hasAttachments)
        {
            AttachmentOverflowBadge.Visibility = Visibility.Collapsed;
            AttachmentOverflowBadge.ToolTip = null;
            return;
        }

        var availableWidth = AttachmentContentHost.ActualWidth;
        if (availableWidth <= 0 || double.IsNaN(availableWidth))
        {
            availableWidth = Math.Max(AttachmentChipWidth, Width - 88);
        }

        var fullRowCapacity = CalculateAttachmentCapacity(availableWidth);
        var hasOverflow = Attachments.Count > fullRowCapacity;
        var visibleCount = hasOverflow
            ? CalculateAttachmentCapacityWithOverflow(availableWidth)
            : Attachments.Count;

        foreach (var attachment in Attachments.Take(Math.Min(Attachments.Count, visibleCount)))
        {
            VisibleAttachments.Add(attachment);
        }

        var hiddenAttachments = Attachments.Skip(visibleCount).ToArray();
        AttachmentOverflowBadge.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        AttachmentOverflowText.Text = $"+{hiddenAttachments.Length}";
        AttachmentOverflowBadge.ToolTip = hasOverflow
            ? new TextBlock
            {
                Text = DesktopLocalizer.Text($"还有 {hiddenAttachments.Length} 个附件", $"{hiddenAttachments.Length} more attachments") +
                    Environment.NewLine + string.Join(Environment.NewLine, hiddenAttachments.Select(attachment => attachment.Path)),
                MaxWidth = 410,
                TextWrapping = TextWrapping.Wrap,
            }
            : null;
    }

    private static int CalculateAttachmentCapacity(double availableWidth)
    {
        if (availableWidth <= 0)
        {
            return 1;
        }

        return Math.Max(
            1,
            (int)Math.Floor((availableWidth + AttachmentChipGap) / (AttachmentChipWidth + AttachmentChipGap)));
    }

    private static int CalculateAttachmentCapacityWithOverflow(double availableWidth)
    {
        var attachmentWidth = availableWidth - AttachmentOverflowWidth;
        if (attachmentWidth <= 0)
        {
            return 0;
        }

        return Math.Max(
            0,
            (int)Math.Floor(attachmentWidth / (AttachmentChipWidth + AttachmentChipGap)));
    }

    private static ComposerAttachment CreateAttachment(string path)
        => ComposerAttachment.FromPath(path);

    public void ApplySettings(AgentSettings settings, TaskSettings taskSettings)
    {
        ApplyPiSnapshot(_piSnapshot, preserveSelection: true);
        SelectComboBoxValue(ModelComboBox, settings.DefaultModel);
        UpdateThinkingOptions(settings.DefaultThinkingLevel);
        SelectDefaultPermissionMode(taskSettings.PermissionMode);
    }

    private async Task RefreshPiDefaultsAsync()
    {
        try
        {
            var snapshot = await _piConfiguration.EnsureFreshSnapshotAsync();
            if (!snapshot.Available && _piSnapshot.Available)
            {
                return;
            }

            ApplyPiSnapshot(snapshot, preserveSelection: true);
        }
        finally
        {
            _isPiConfigurationLoading = false;
            UpdatePiSelectionState();
        }
    }

    private void ApplyPiSnapshot(PiConfigurationSnapshot snapshot, bool preserveSelection)
    {
        var previousModel = preserveSelection ? GetComboBoxValue(ModelComboBox) : null;
        var previousThinkingLevel = preserveSelection ? GetComboBoxValue(ThinkingComboBox) : null;
        _piSnapshot = snapshot;
        if (snapshot.Available && snapshot.Models.Count > 0)
        {
            _settings.TryMigrateLegacyModelVisibility(
                snapshot.Models.Select(model => $"{model.Provider}/{model.Id}").ToArray(),
                snapshot.EnabledModels,
                out _);
        }
        _modelChoices.Clear();
        var hiddenModels = new HashSet<string>(
            _settings.Current.ModelVisibility!.HiddenModelReferences,
            StringComparer.Ordinal);
        foreach (var model in _piSnapshot.Models.Where(model =>
                     !hiddenModels.Contains($"{model.Provider}/{model.Id}")))
        {
            var providerName = _piSnapshot.Providers.FirstOrDefault(provider => provider.Id == model.Provider)?.Name ?? model.Provider;
            _modelChoices.Add(new ModelChoice(
                model.Name,
                providerName,
                model.Id,
                $"{model.Provider}/{model.Id}",
                BuildModelTooltip(model)));
        }

        var fallback = _settings.Current.Agent.DefaultModel;
        var selectedModel = !string.IsNullOrWhiteSpace(previousModel) &&
            _piSnapshot.Models.Any(model => string.Equals(
                $"{model.Provider}/{model.Id}",
                previousModel,
                StringComparison.Ordinal))
            ? previousModel
            : !string.IsNullOrWhiteSpace(_piSnapshot.DefaultModel)
            ? _piSnapshot.DefaultModel
            : fallback;
        if (!SelectComboBoxValue(ModelComboBox, selectedModel) && ModelComboBox.Items.Count > 0)
        {
            ModelComboBox.SelectedIndex = 0;
        }

        UpdateThinkingOptions(
            !string.IsNullOrWhiteSpace(previousThinkingLevel)
                ? previousThinkingLevel
                : _piSnapshot.Available
                ? _piSnapshot.DefaultThinkingLevel
                : _settings.Current.Agent.DefaultThinkingLevel);
        UpdatePiSelectionState();
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetComboBoxValue(ModelComboBox);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            _modelSelectionBeforeSearch = selected;
        }

        UpdateThinkingOptions((ThinkingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        SchedulePrewarm();
    }

    private void OnPrewarmInputChanged(object sender, EventArgs e) => SchedulePrewarm();

    private void SchedulePrewarm()
    {
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        CancelPendingPrewarm();
        var cancellation = new CancellationTokenSource();
        _prewarmCancellation = cancellation;
        _ = PrepareAfterDelayAsync(cancellation);
    }

    private async Task PrepareAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(250, cancellation.Token);
            var draft = CreateDraft();
            if (string.IsNullOrWhiteSpace(draft.Model) || !Directory.Exists(draft.WorkingDirectory))
            {
                return;
            }

            await _coordinator.PrepareAsync(
                draft.WorkingDirectory,
                draft.Model,
                draft.ThinkingLevel,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Prewarming is opportunistic. StartAsync retains the normal cold-start fallback.
        }
        finally
        {
            if (ReferenceEquals(_prewarmCancellation, cancellation))
            {
                _prewarmCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelPendingPrewarm()
    {
        var cancellation = _prewarmCancellation;
        _prewarmCancellation = null;
        cancellation?.Cancel();
    }

    private void OnModelDropDownOpened(object sender, EventArgs e)
    {
        _modelSelectionBeforeSearch = GetComboBoxValue(ModelComboBox);
        _modelSearchActive = true;
        _allowModelSearchFocusExit = false;
        _modelView.Filter = null;
        _modelView.Refresh();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                if (ModelComboBox.Template.FindName("PopupSearchBox", ModelComboBox) is System.Windows.Controls.TextBox searchBox)
                {
                    searchBox.TextChanged -= OnModelSearchTextChanged;
                    searchBox.LostKeyboardFocus -= OnModelSearchLostKeyboardFocus;
                    searchBox.PreviewKeyDown -= OnModelSearchPreviewKeyDown;
                    searchBox.Text = string.Empty;
                    searchBox.TextChanged += OnModelSearchTextChanged;
                    searchBox.LostKeyboardFocus += OnModelSearchLostKeyboardFocus;
                    searchBox.PreviewKeyDown += OnModelSearchPreviewKeyDown;
                    _ = Keyboard.Focus(searchBox);
                    searchBox.CaretIndex = 0;
                }
            }));
    }

    private void OnModelDropDownClosed(object sender, EventArgs e)
    {
        _modelSearchActive = false;
        _allowModelSearchFocusExit = false;
        if (ModelComboBox.Template.FindName("PopupSearchBox", ModelComboBox) is System.Windows.Controls.TextBox searchBox)
        {
            searchBox.TextChanged -= OnModelSearchTextChanged;
            searchBox.LostKeyboardFocus -= OnModelSearchLostKeyboardFocus;
            searchBox.PreviewKeyDown -= OnModelSearchPreviewKeyDown;
        }
        _modelView.Filter = null;
        _modelView.Refresh();
        var selected = GetComboBoxValue(ModelComboBox) ?? _modelSelectionBeforeSearch;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            SelectComboBoxValue(ModelComboBox, selected);
        }
    }

    private void OnModelSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_modelSearchActive || sender is not System.Windows.Controls.TextBox searchBox)
        {
            return;
        }

        var query = searchBox.Text.Trim();
        _modelView.Filter = item => item is ModelChoice choice &&
            (query.Length == 0 ||
             choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             choice.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             choice.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase));
        _modelView.Refresh();
    }

    private void OnModelSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_modelSearchActive ||
            _allowModelSearchFocusExit ||
            sender is not System.Windows.Controls.TextBox searchBox)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                if (_modelSearchActive &&
                    ModelComboBox.IsDropDownOpen &&
                    !_allowModelSearchFocusExit &&
                    Mouse.LeftButton == MouseButtonState.Released &&
                    Mouse.RightButton == MouseButtonState.Released)
                {
                    _ = Keyboard.Focus(searchBox);
                }
            }));
    }

    private void OnModelSearchPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Escape)
        {
            _allowModelSearchFocusExit = true;
        }
    }

    private void UpdateThinkingOptions(string? preferredLevel)
    {
        var modelReference = GetComboBoxValue(ModelComboBox);
        var model = _piSnapshot.Models.FirstOrDefault(candidate =>
            string.Equals($"{candidate.Provider}/{candidate.Id}", modelReference, StringComparison.Ordinal));
        if (model is null)
        {
            ThinkingComboBox.Items.Clear();
            UpdatePiSelectionState();
            return;
        }

        var levels = model?.ThinkingLevels is { Count: > 0 }
            ? model.ThinkingLevels
            : ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
        var current = preferredLevel ?? (ThinkingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "high";
        ThinkingComboBox.Items.Clear();
        foreach (var level in levels)
        {
            ThinkingComboBox.Items.Add(new ComboBoxItem { Content = ThinkingLevelLabel(level), Tag = level });
        }

        if (!SelectComboBoxValue(ThinkingComboBox, current) && ThinkingComboBox.Items.Count > 0)
        {
            var highIndex = Array.FindIndex(levels.ToArray(), level => level == "high");
            ThinkingComboBox.SelectedIndex = highIndex >= 0 ? highIndex : 0;
        }

        UpdatePiSelectionState();
    }

    private void UpdatePiSelectionState()
    {
        var hasModels = _modelChoices.Count > 0;
        var hasModelSelection = GetComboBoxValue(ModelComboBox) is not null;
        var hasThinkingLevels = ThinkingComboBox.Items.Count > 0;
        var hasThinkingSelection = GetComboBoxValue(ThinkingComboBox) is not null;

        ModelComboBox.IsEnabled = hasModels;
        ThinkingComboBox.IsEnabled = hasModelSelection && hasThinkingLevels;
        ModelPlaceholderText.Text = _isPiConfigurationLoading
            ? DesktopLocalizer.Text("正在获取 Pi 模型…", "Loading Pi models…")
            : hasModels ? DesktopLocalizer.Text("选择模型", "Select a model") : DesktopLocalizer.Text("暂无可用模型", "No models available");
        ThinkingPlaceholderText.Text = _isPiConfigurationLoading
            ? DesktopLocalizer.Text("正在获取推理等级…", "Loading reasoning levels…")
            : hasThinkingLevels ? DesktopLocalizer.Text("选择推理等级", "Select a reasoning level") : DesktopLocalizer.Text("暂无可用推理等级", "No reasoning levels available");
        ModelPlaceholderText.Visibility = hasModelSelection ? Visibility.Collapsed : Visibility.Visible;
        ThinkingPlaceholderText.Visibility = hasThinkingSelection ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool SelectComboBoxValue(System.Windows.Controls.ComboBox comboBox, string? value)
    {
        foreach (var item in comboBox.Items)
        {
            var itemValue = item switch
            {
                ComboBoxItem comboBoxItem => comboBoxItem.Tag?.ToString(),
                ModelChoice modelChoice => modelChoice.Reference,
                _ => null,
            };
            if (string.Equals(itemValue, value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return true;
            }
        }

        return false;
    }

    private void SelectDefaultPermissionMode(string? value)
    {
        var normalized = string.Equals(value, "read-only", StringComparison.OrdinalIgnoreCase)
            ? "read-only"
            : "standard";
        _suppressPermissionSelectionChanged = true;
        try
        {
            SelectComboBoxValue(PermissionComboBox, normalized);
            _previousPermissionMode = normalized;
            UpdatePermissionVisualState(normalized);
            FullAccessConfirmationOverlay.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressPermissionSelectionChanged = false;
        }
    }

    private static string? GetComboBoxValue(System.Windows.Controls.ComboBox comboBox) => comboBox.SelectedItem switch
    {
        ComboBoxItem item => item.Tag?.ToString(),
        ModelChoice choice => choice.Reference,
        _ => null,
    };

    private static string BuildModelTooltip(PiModelInfo model) => string.Join(
        Environment.NewLine,
        DesktopLocalizer.Text($"上下文窗口：{model.ContextWindow:N0} tokens", $"Context window: {model.ContextWindow:N0} tokens"),
        DesktopLocalizer.Text($"推理：{(model.Reasoning ? "支持" : "不支持")}", $"Reasoning: {(model.Reasoning ? "Supported" : "Not supported")}"),
        DesktopLocalizer.Text($"图像输入：{(model.Input.Contains("image", StringComparer.OrdinalIgnoreCase) ? "支持" : "不支持")}", $"Image input: {(model.Input.Contains("image", StringComparer.OrdinalIgnoreCase) ? "Supported" : "Not supported")}"));

    private sealed record ModelChoice(
        string Name,
        string ProviderName,
        string ModelId,
        string Reference,
        string Tooltip);

    private static string ThinkingLevelLabel(string level) => level switch
    {
        "off" => DesktopLocalizer.Text("无", "None"),
        "minimal" => DesktopLocalizer.Text("最低", "Minimal"),
        "low" => DesktopLocalizer.Text("低", "Low"),
        "medium" => DesktopLocalizer.Text("中", "Medium"),
        "high" => DesktopLocalizer.Text("高", "High"),
        "xhigh" => DesktopLocalizer.Text("很高", "Xhigh"),
        "max" => DesktopLocalizer.Text("最高", "Max"),
        _ => level,
    };

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr windowHandle);
}
