using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PiCompanion.Application.Demo;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.Settings;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;
using PiCompanion.Desktop.Branding;
using PiCompanion.Desktop.Shell;
using PiCompanion.Desktop.Localization;
using PiCompanion.Desktop.Skills;
using MediaBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace PiCompanion.Desktop.Monitor;

public partial class MonitorWindow : Window
{
    private const string WindowPlacementName = "monitor";
    private const double CapsuleWidth = 440;
    private const double CapsuleHeight = 88;
    private const double ExpandedWidth = 440;
    private const double ExpandedMaximumHeight = 620;
    private const string OtherChoice = "其他…";
    private const int ResultInteractionLimit = 3;
    private const int ResultInteractionSummaryLimit = 160;
    private const int ResultFallbackMessageLimit = 240;
    private const int RecentTaskChoiceLimit = 5;
    private const double TaskPickerRowHeight = 54;
    private const double TaskPickerPadding = 6;
    private const int TaskPickerWheelThreshold = 120;
    private const int TaskPickerWheelThrottleMilliseconds = 90;
    private const int TaskPickerAutoCloseDelayMilliseconds = 500;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private readonly TaskCoordinator _coordinator;
    private readonly AppSettingsService _appSettings;
    private readonly Action _openChat;
    private readonly Action _newTask;
    private readonly Action _exit;
    private readonly SkillCompletionController _skillCompletion;
    private readonly ObservableCollection<string> _activities = [];
    private readonly ObservableCollection<ResultInteractionSummary> _resultInteractions = [];
    private readonly DispatcherTimer _taskPickerAutoCloseTimer;
    private bool _isDragging;
    private bool _isExpanded;
    private bool _isContextMenuOpen;
    private bool _isUpdatingTaskPicker;
    private int _taskPickerWheelDelta;
    private long _taskPickerWheelBlockedUntil;
    private bool _hasUserPosition;
    private string? _currentInteractionId;
    private Guid? _skillCompletionTaskId;
    private string? _lastAutoExpandedInteractionId;
    private MonitorSettings _settings = PiCompanionSettings.Default.Monitor;
    private bool _aiSummaryEnabled = PiCompanionSettings.Default.Tasks.AiSummaryEnabled;
    private readonly DispatcherTimer _autoCollapseTimer;

    internal MonitorWindow(
        TaskCoordinator coordinator,
        AppSettingsService appSettings,
        SkillCompletionProvider skillCompletion,
        Action openChat,
        Action newTask,
        Action exit)
    {
        InitializeComponent();
        Icon = PiAppIcon.WindowIcon;
        _coordinator = coordinator;
        _appSettings = appSettings;
        _openChat = openChat;
        _newTask = newTask;
        _exit = exit;
        _skillCompletion = new SkillCompletionController(
            DirectionTextBox,
            SkillSuggestionPopup,
            SkillSuggestionList,
            SkillSuggestionStatus,
            cancellationToken =>
            {
                var current = _coordinator.Current;
                return current is null
                    ? Task.FromResult<IReadOnlyList<SkillCompletionItem>>([])
                    : skillCompletion.GetEffectiveSkillsAsync(
                        current.WorkingDirectory,
                        current.ScopeKind,
                        current.TaskId,
                        cancellationToken);
            });
        _autoCollapseTimer = new DispatcherTimer();
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            Collapse();
        };
        _taskPickerAutoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TaskPickerAutoCloseDelayMilliseconds),
        };
        _taskPickerAutoCloseTimer.Tick += (_, _) =>
        {
            _taskPickerAutoCloseTimer.Stop();
            TaskPickerPopup.IsOpen = false;
        };
        ActivityList.ItemsSource = _activities;
        ResultInteractionList.ItemsSource = _resultInteractions;
        IsVisibleChanged += OnMonitorVisibilityChanged;
        SizeChanged += OnMonitorSizeChanged;
        _coordinator.ProjectionChanged += OnProjectionChanged;
        DesktopLocalizer.Apply(this);
        UpdateExpandedHeaderState();
        Render(_coordinator.Current);
    }

    public void RefreshLocalization()
    {
        DesktopLocalizer.Apply(this);
        UpdateExpandedHeaderState();
        Render(_coordinator.Current);
    }

    public bool AllowClose { get; set; }

    public void ApplySettings(MonitorSettings settings, TaskSettings taskSettings)
    {
        var positionChanged = !string.Equals(_settings.Position, settings.Position, StringComparison.Ordinal);
        var animationsChanged = _settings.AnimationsEnabled != settings.AnimationsEnabled;
        _settings = settings;
        _aiSummaryEnabled = taskSettings.AiSummaryEnabled;
        Topmost = settings.AlwaysOnTop;
        _autoCollapseTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, settings.AutoCollapseSeconds));
        if (positionChanged)
        {
            _hasUserPosition = false;
        }

        if (IsVisible && !_hasUserPosition)
        {
            PlaceAtConfiguredStartupPosition();
        }

        if (animationsChanged)
        {
            ApplyStatusAnimationPreference();
        }

        Render(_coordinator.Current);
    }

    public void ShowWithoutActivation()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_hasUserPosition)
        {
            PlaceAtConfiguredStartupPosition();
        }
    }

    private void PlaceAtConfiguredStartupPosition()
    {
        if (string.Equals(_settings.Position, "last-position", StringComparison.Ordinal) &&
            _appSettings.LoadWindowPlacement(WindowPlacementName) is { } placement &&
            WindowPlacementService.Restore(this, placement, restoreSize: false))
        {
            _hasUserPosition = true;
            return;
        }

        var corner = string.Equals(_settings.Position, "last-position", StringComparison.Ordinal)
            ? "top-right"
            : _settings.Position;
        WindowPlacementService.PlaceAtCorner(this, corner);
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            TaskPickerPopup.IsOpen = false;
            ClearInputFocus();
            Hide();
        }
        else
        {
            ShowWithoutActivation();
        }
    }

    public void CollapseAfterCompletion()
    {
        _autoCollapseTimer.Stop();
        Collapse();
    }

    public void ExpandAfterCompletion()
    {
        _autoCollapseTimer.Stop();
        ShowWithoutActivation();
        Expand();
    }

    private void OnProjectionChanged(TaskProjection? projection)
    {
        _ = Dispatcher.InvokeAsync(() => Render(projection));
    }

    private void Render(TaskProjection? projection)
    {
        if (_skillCompletionTaskId != projection?.TaskId)
        {
            _skillCompletionTaskId = projection?.TaskId;
            _skillCompletion.Invalidate();
        }

        var isAiSummaryLoading =
            projection is not null &&
            projection.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Interrupted &&
            _aiSummaryEnabled &&
            string.IsNullOrWhiteSpace(projection.Summary);
        UpdateAiSummaryLoadingState(isAiSummaryLoading);

        if (projection is null)
        {
            RenderEmptyState();
            return;
        }

        OpenCurrentTaskButton.Content = DesktopLocalizer.Text("在智能体对话中打开 ↗", "Open in Agent Chat ↗");

        var pendingInteractionId = projection.Status is RunStatus.WaitingForApproval or RunStatus.WaitingForAnswer
            ? projection.Transcript.LastOrDefault(block =>
                block.Kind == TranscriptBlockKind.Interaction && block.Status == TranscriptBlockStatus.Pending)?.InteractionId
            : null;
        var shouldAutoExpand = pendingInteractionId is not null &&
            !string.Equals(pendingInteractionId, _lastAutoExpandedInteractionId, StringComparison.Ordinal);

        HeaderTitle.Text = projection.Title;
        UpdateTaskSelectorAvailability();
        HeaderStatus.Text = MonitorRunStatus(projection.Status);
        DirectoryText.Text = projection.ScopeKind == TaskScopeKind.GeneralChat
            ? DesktopLocalizer.Text("直接对话 · 隔离空间", "Direct Chat · Isolated workspace")
            : projection.WorkingDirectory;
        DirectoryText.ToolTip = projection.ScopeKind == TaskScopeKind.GeneralChat
            ? DesktopLocalizer.Text(
                "文件操作仅限 Pi Companion 管理的隔离空间",
                "File operations are limited to an isolated workspace managed by Pi Companion")
            : projection.WorkingDirectory;
        ModelText.Text = DesktopLocalizer.Text($"{projection.Model} · 推理 {projection.ThinkingLevel}", $"{projection.Model} · Reasoning {projection.ThinkingLevel}");

        _activities.Clear();
        foreach (var activity in BuildMonitorActivitySummaries(projection))
        {
            _activities.Add(activity);
        }

        if (_activities.Count > 0)
        {
            ActivityList.ScrollIntoView(_activities[^1]);
        }

        HidePrimaryPanels();
        switch (projection.Status)
        {
            case RunStatus.WaitingForApproval:
            case RunStatus.WaitingForAnswer:
                RenderInteraction(projection);
                break;
            case RunStatus.Completed:
            case RunStatus.Failed:
            case RunStatus.Interrupted:
                RenderResult(projection);
                break;
            case RunStatus.Deleted:
            case RunStatus.Draft:
                IdlePanel.Visibility = Visibility.Visible;
                break;
            default:
                RenderActivity(projection);
                break;
        }

        DirectionPanel.Visibility = projection.Status is RunStatus.Deleted or RunStatus.Draft
            ? Visibility.Collapsed
            : Visibility.Visible;
        var isActive = projection.Status.IsActive();
        var queuedCount = projection.PendingSteering.Count + projection.PendingFollowUps.Count;
        DirectionPlaceholderText.Text = isActive
            ? queuedCount > 0
                ? DesktopLocalizer.Text($"立即调整当前任务 · 队列 {queuedCount} 条", $"Steer current task · {queuedCount} queued")
                : DesktopLocalizer.Text("立即调整当前任务", "Steer current task")
            : DesktopLocalizer.Text("继续这项任务", "Continue this task");
        DirectionButton.Content = isActive
            ? DesktopLocalizer.Text("立即调整", "Steer now")
            : DesktopLocalizer.Text("发送新一轮", "Start new run");
        UpdateDirectionPlaceholderVisibility();
        SetStatusBrush(projection.Status, projection.TaskId, projection.RunId);

        if (shouldAutoExpand)
        {
            _lastAutoExpandedInteractionId = pendingInteractionId;
            Expand();
        }
    }

    private void RenderEmptyState()
    {
        HeaderTitle.Text = "Pi Companion";
        UpdateTaskSelectorAvailability();
        OpenCurrentTaskButton.Content = DesktopLocalizer.Text("打开智能体对话 ↗", "Open Agent Chat ↗");
        HeaderStatus.Text = DesktopLocalizer.Text("准备就绪", "Ready");
        DirectoryText.Text = DesktopLocalizer.Text("尚未选择工作目录", "No working directory selected");
        DirectoryText.ToolTip = null;
        ModelText.Text = DesktopLocalizer.Text("等待任务", "Waiting for a task");
        ActivityStatusText.Text = DesktopLocalizer.Text("等待任务", "Waiting for a task");
        _activities.Clear();
        HidePrimaryPanels();
        IdlePanel.Visibility = Visibility.Visible;
        DirectionPanel.Visibility = Visibility.Collapsed;
        _currentInteractionId = null;
        SetStatusBrush(RunStatus.Draft, null, null);
    }

    private void HidePrimaryPanels()
    {
        ActivityPanel.Visibility = Visibility.Collapsed;
        InteractionPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Collapsed;
    }

    private void RenderActivity(TaskProjection projection)
    {
        ActivityPanel.Visibility = Visibility.Visible;
        ActivityPanelTitle.Text = projection.Status switch
        {
            RunStatus.Queued => DesktopLocalizer.Text("等待执行", "Waiting to run"),
            RunStatus.Starting => DesktopLocalizer.Text("正在启动", "Starting"),
            RunStatus.Cancelling => DesktopLocalizer.Text("正在停止", "Stopping"),
            _ => DesktopLocalizer.Text("正在执行", "Running"),
        };
        ActivityStatusText.Text = projection.Transcript.Any(block =>
            IsWebSearchBlock(block) && block.Status == TranscriptBlockStatus.Running)
                ? DesktopLocalizer.Text("网络搜索进行中", "Searching the web")
                : projection.ActivityStatus ??
                  _activities.LastOrDefault() ??
                  LocalizedStatus(projection.Status);
    }

    private void RenderInteraction(TaskProjection projection)
    {
        InteractionPanel.Visibility = Visibility.Visible;
        var interaction = projection.Transcript.LastOrDefault(block =>
            block.Kind == TranscriptBlockKind.Interaction && block.Status == TranscriptBlockStatus.Pending);
        var waitingForAnswer = interaction?.InteractionKind == "Question" || projection.Status == RunStatus.WaitingForAnswer;
        var isChoiceQuestion = waitingForAnswer &&
            interaction?.InteractionMethod == "select" &&
            interaction.InteractionOptions is { Count: > 0 };
        _currentInteractionId = interaction?.InteractionId;
        InteractionTitle.Text = waitingForAnswer
            ? DesktopLocalizer.Text("Agent 等待回答", "Agent is waiting for an answer")
            : DesktopLocalizer.Text("Agent 请求权限", "Agent requests permission");
        CopyInteractionButton.Content = DesktopLocalizer.Text("复制详情", "Copy details");
        InteractionPanel.SetResourceReference(
            Border.BackgroundProperty,
            waitingForAnswer ? "RunningSurfaceBrush" : "WarningTintBrush");
        InteractionPanel.SetResourceReference(
            Border.BorderBrushProperty,
            waitingForAnswer ? "RunningBrush" : "WarningBrush");
        InteractionPrompt.Text = interaction?.Content ?? projection.Activities.LastOrDefault()?.Text ?? projection.Summary;
        ApprovalActions.Visibility = waitingForAnswer ? Visibility.Collapsed : Visibility.Visible;
        AnswerActions.Visibility = waitingForAnswer && !isChoiceQuestion ? Visibility.Visible : Visibility.Collapsed;
        QuestionSelectActions.Visibility = isChoiceQuestion ? Visibility.Visible : Visibility.Collapsed;
        InteractionOptionsComboBox.ItemsSource = interaction?.InteractionOptions;
        InteractionOptionsComboBox.SelectedIndex = isChoiceQuestion ? 0 : -1;
    }

    private void RenderResult(TaskProjection projection)
    {
        ResultPanel.Visibility = Visibility.Visible;
        var resultSummary = _aiSummaryEnabled
            ? string.IsNullOrWhiteSpace(projection.Summary)
                ? string.Empty
                : projection.Summary
            : BuildLatestAgentMessageSummary(projection);
        ResultSummary.Text = resultSummary;
        ResultSummary.Visibility = !string.IsNullOrWhiteSpace(resultSummary)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var thinkingCount = projection.Transcript.Count(block => block.Kind == TranscriptBlockKind.Thinking);
        var toolCount = projection.Transcript.Count(block =>
            block.Kind == TranscriptBlockKind.Tool && !IsWebSearchBlock(block));
        var webSearchCount = projection.Transcript.Count(IsWebSearchBlock);
        ResultThinkingCount.Text = thinkingCount.ToString("N0", DesktopLocalizer.Culture);
        ResultToolCount.Text = toolCount.ToString("N0", DesktopLocalizer.Culture);
        ResultWebSearchCount.Text = webSearchCount.ToString("N0", DesktopLocalizer.Culture);
        ResultThinkingBadge.Visibility = thinkingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultToolBadge.Visibility = toolCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultWebSearchBadge.Visibility = webSearchCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultActivityCounts.Visibility = thinkingCount > 0 || toolCount > 0 || webSearchCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        _resultInteractions.Clear();
        foreach (var interaction in projection.Transcript
            .Where(block =>
                block.Kind == TranscriptBlockKind.Interaction &&
                block.Status != TranscriptBlockStatus.Pending)
            .OrderByDescending(block => block.LastSequence)
            .Take(ResultInteractionLimit))
        {
            _resultInteractions.Add(new ResultInteractionSummary(
                interaction.InteractionKind == "Question"
                    ? DesktopLocalizer.Text("回答", "Answer")
                    : DesktopLocalizer.Text("授权", "Approval"),
                BuildResultInteractionSummary(interaction)));
        }
        ResultInteractionSection.Visibility = _resultInteractions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var (title, resourceKey) = projection.Status switch
        {
            RunStatus.Completed => (DesktopLocalizer.Text("本轮任务已完成", "Run completed"), "SuccessBrush"),
            RunStatus.Interrupted => (DesktopLocalizer.Text("本轮任务已停止", "Run stopped"), "DangerBrush"),
            _ => (DesktopLocalizer.Text("本轮任务失败", "Run failed"), "DangerBrush"),
        };
        var brush = (MediaBrush)FindResource(resourceKey);
        ResultTitle.Text = title;
        ResultPanel.BorderBrush = brush;
        ResultStatusDot.Fill = brush;
    }

    private static string BuildLatestAgentMessageSummary(TaskProjection projection)
    {
        var message = projection.Transcript.LastOrDefault(block =>
            block.Kind == TranscriptBlockKind.AssistantMessage &&
            !string.IsNullOrWhiteSpace(block.Content))?.Content;
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = NormalizeSingleLine(message);
        return normalized.Length <= ResultFallbackMessageLimit
            ? normalized
            : string.Concat(
                normalized.AsSpan(0, ResultFallbackMessageLimit - 1).TrimEnd(),
                "…");
    }

    private static bool IsWebSearchBlock(TranscriptBlock block) =>
        block.Kind == TranscriptBlockKind.WebSearch ||
        block.Kind == TranscriptBlockKind.Tool &&
        string.Equals(block.Title, "web_search", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildMonitorActivitySummaries(TaskProjection projection)
    {
        var summaries = projection.Activities
            .Where(activity => !IsToolActivity(activity.Kind))
            .Select(activity => (
                activity.Sequence,
                Text: NormalizeActivitySummary(activity.Text)))
            .ToList();

        summaries.AddRange(projection.Transcript
            .Where(block =>
                block.Kind is TranscriptBlockKind.Tool or TranscriptBlockKind.WebSearch)
            .Select(block => (
                Sequence: block.LastSequence,
                Text: BuildToolActivitySummary(block))));

        return summaries
            .OrderBy(summary => summary.Sequence)
            .TakeLast(12)
            .Select(summary => summary.Text)
            .ToArray();
    }

    private static bool IsToolActivity(CompanionRunEventKind kind) =>
        kind is CompanionRunEventKind.ToolStarted
            or CompanionRunEventKind.ToolProgressed
            or CompanionRunEventKind.ToolCompleted
            or CompanionRunEventKind.ToolFailed;

    private static string BuildToolActivitySummary(TranscriptBlock block)
    {
        var toolName = IsWebSearchBlock(block)
            ? DesktopLocalizer.Text("网络搜索", "Web Search")
            : NormalizeActivitySummary(block.Title);
        var target = NormalizeActivitySummary(block.Input ?? string.Empty);
        var description = string.IsNullOrWhiteSpace(target)
            ? toolName
            : DesktopLocalizer.Text($"{toolName}：{target}", $"{toolName}: {target}");
        return block.Status switch
        {
            TranscriptBlockStatus.Completed => $"✓ {description}",
            TranscriptBlockStatus.Failed => $"✕ {description}",
            TranscriptBlockStatus.Cancelled => $"— {description}",
            _ => description,
        };
    }

    private static string NormalizeActivitySummary(string value) =>
        string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string BuildResultInteractionSummary(TranscriptBlock interaction)
    {
        var summary = interaction.InteractionKind == "Question"
            ? BuildAnswerSummary(interaction)
            : BuildApprovalSummary(interaction);
        if (summary.Length <= ResultInteractionSummaryLimit)
        {
            return summary;
        }

        return string.Concat(summary.AsSpan(0, ResultInteractionSummaryLimit - 1), "…");
    }

    private static string BuildApprovalSummary(TranscriptBlock interaction)
    {
        var requestType = FirstNonEmptyLine(interaction.Content);
        var outcome = interaction.Status switch
        {
            TranscriptBlockStatus.Completed => DesktopLocalizer.Text("已允许", "Allowed"),
            TranscriptBlockStatus.Cancelled when WasCancelledWithoutDecision(interaction.Output) =>
                DesktopLocalizer.Text("已取消", "Cancelled"),
            TranscriptBlockStatus.Cancelled => DesktopLocalizer.Text("已拒绝", "Denied"),
            TranscriptBlockStatus.Failed => DesktopLocalizer.Text("失败", "Failed"),
            _ => NormalizeSingleLine(interaction.Output ?? string.Empty),
        };

        return JoinSummaryParts(
            string.IsNullOrWhiteSpace(requestType)
                ? DesktopLocalizer.Text("权限请求", "Permission request")
                : requestType,
            outcome);
    }

    private static string BuildAnswerSummary(TranscriptBlock interaction)
    {
        var answer = NormalizeSingleLine(interaction.Output ?? string.Empty);
        if (interaction.Status == TranscriptBlockStatus.Completed && !string.IsNullOrWhiteSpace(answer))
        {
            return answer;
        }

        return interaction.Status switch
        {
            TranscriptBlockStatus.Completed => DesktopLocalizer.Text("已回答", "Answered"),
            TranscriptBlockStatus.Cancelled => DesktopLocalizer.Text("已取消", "Cancelled"),
            TranscriptBlockStatus.Failed => DesktopLocalizer.Text("失败", "Failed"),
            _ => DesktopLocalizer.Text("无回答", "No answer"),
        };
    }

    private static string FirstNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    private static string JoinSummaryParts(string first, string second) =>
        string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";

    private static bool WasCancelledWithoutDecision(string? outcome) =>
        outcome?.Contains("取消", StringComparison.Ordinal) == true ||
        outcome?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true;

    private static string NormalizeSingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string LocalizedStatus(RunStatus status) => status switch
    {
        RunStatus.Draft => DesktopLocalizer.Text("准备就绪", "Ready"),
        RunStatus.Queued => DesktopLocalizer.Text("排队中", "Queued"),
        RunStatus.Starting => DesktopLocalizer.Text("正在启动", "Starting"),
        RunStatus.Running => DesktopLocalizer.Text("执行中", "Running"),
        RunStatus.WaitingForApproval => DesktopLocalizer.Text("等待授权", "Waiting for approval"),
        RunStatus.WaitingForAnswer => DesktopLocalizer.Text("等待回答", "Waiting for an answer"),
        RunStatus.Cancelling => DesktopLocalizer.Text("正在停止", "Stopping"),
        RunStatus.Completed => DesktopLocalizer.Text("已完成", "Completed"),
        RunStatus.Failed => DesktopLocalizer.Text("失败", "Failed"),
        RunStatus.Interrupted => DesktopLocalizer.Text("已停止", "Stopped"),
        RunStatus.Deleted => DesktopLocalizer.Text("已删除", "Deleted"),
        _ => status.ToString(),
    };

    private static string MonitorRunStatus(RunStatus status)
    {
        var localized = LocalizedStatus(status);
        return status switch
        {
            RunStatus.Queued or
            RunStatus.Starting or
            RunStatus.Running or
            RunStatus.WaitingForApproval or
            RunStatus.WaitingForAnswer or
            RunStatus.Cancelling => DesktopLocalizer.Text(
                $"当前一轮 · {localized}",
                $"Current: {localized}"),
            RunStatus.Completed or
            RunStatus.Failed or
            RunStatus.Interrupted => DesktopLocalizer.Text(
                $"最近一轮：{localized}",
                $"Latest: {localized}"),
            _ => localized,
        };
    }

    private void SetStatusBrush(RunStatus status, Guid? taskId, Guid? runId)
    {
        var key = status switch
        {
            RunStatus.WaitingForApproval => "WarningBrush",
            RunStatus.WaitingForAnswer => "RunningBrush",
            RunStatus.Completed => "SuccessBrush",
            RunStatus.Failed or RunStatus.Interrupted => "DangerBrush",
            RunStatus.Queued or RunStatus.Starting or RunStatus.Running or RunStatus.Cancelling => "RunningBrush",
            _ => "AccentHoverBrush",
        };
        var brush = (MediaBrush)FindResource(key);
        HeaderStatusDot.Fill = brush;
        HeaderStatusHalo.Fill = brush.Clone();
        HeaderStatusHalo.Opacity = 0.15;
        UpdateStatusIndicatorAnimation(status, taskId, runId);
    }

    private void UpdateTaskSelectorAvailability()
    {
        var choices = BuildTaskChoices();
        var hasAlternativeTask = choices.Any(choice => !choice.IsCurrent);
        HeaderTitleButton.IsEnabled = hasAlternativeTask;
        if (!hasAlternativeTask && TaskPickerPopup.IsOpen)
        {
            TaskPickerPopup.IsOpen = false;
        }
        else if (TaskPickerPopup.IsOpen)
        {
            RefreshTaskPicker(choices);
        }
    }

    private void OnTaskSelectorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not WpfButton button)
        {
            return;
        }

        _taskPickerAutoCloseTimer.Stop();
        var moved = DragMonitorWindow();
        if (moved)
        {
            TaskPickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (TaskPickerPopup.IsOpen && ReferenceEquals(TaskPickerPopup.PlacementTarget, button))
        {
            TaskPickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        OpenTaskPicker(button);
        e.Handled = true;
    }

    private void OpenTaskPicker(WpfButton button, bool focusList = true)
    {
        var choices = BuildTaskChoices();
        if (!choices.Any(choice => !choice.IsCurrent))
        {
            return;
        }

        TaskPickerPopup.PlacementTarget = button;
        TaskPickerPopup.CustomPopupPlacementCallback = PlaceTaskPicker;
        RefreshTaskPicker(choices);
        TaskPickerPopup.IsOpen = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!TaskPickerPopup.IsOpen)
                {
                    return;
                }

                if (focusList)
                {
                    TaskPickerList.Focus();
                }
                if (TaskPickerList.SelectedItem is not null)
                {
                    TaskPickerList.ScrollIntoView(TaskPickerList.SelectedItem);
                }
            }));
    }

    private CustomPopupPlacement[] PlaceTaskPicker(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset)
    {
        var selectedIndex = Math.Max(0, TaskPickerList.SelectedIndex);
        var selectedRowTop = TaskPickerPadding + (selectedIndex * TaskPickerRowHeight);
        return
        [
            new(
                new System.Windows.Point(
                    ((targetSize.Width - popupSize.Width) / 2) + offset.X,
                    ((targetSize.Height - TaskPickerRowHeight) / 2) - selectedRowTop + offset.Y),
                PopupPrimaryAxis.Horizontal),
        ];
    }

    private void RefreshTaskPicker(IReadOnlyList<MonitorTaskChoice> choices)
    {
        _isUpdatingTaskPicker = true;
        try
        {
            TaskPickerList.ItemsSource = choices;
            TaskPickerList.SelectedIndex = -1;
            for (var index = 0; index < choices.Count; index++)
            {
                if (choices[index].IsCurrent)
                {
                    TaskPickerList.SelectedIndex = index;
                    break;
                }
            }
        }
        finally
        {
            _isUpdatingTaskPicker = false;
        }

        RefreshTaskPickerPlacement();
    }

    private void RefreshTaskPickerPlacement()
    {
        if (!TaskPickerPopup.IsOpen)
        {
            return;
        }

        AlignTaskPickerWithoutScreenClamping();
    }

    private void AlignTaskPickerWithoutScreenClamping()
    {
        if (!TaskPickerPopup.IsOpen ||
            TaskPickerPopup.PlacementTarget is not FrameworkElement target ||
            TaskPickerPopup.Child is not FrameworkElement popupContent ||
            PresentationSource.FromVisual(popupContent) is not HwndSource popupSource ||
            popupSource.Handle == IntPtr.Zero)
        {
            return;
        }

        var selectedIndex = Math.Max(0, TaskPickerList.SelectedIndex);
        var selectedRowTop = TaskPickerPadding + (selectedIndex * TaskPickerRowHeight);
        var targetPoint = target.PointToScreen(new System.Windows.Point(
            (target.ActualWidth - popupContent.ActualWidth) / 2,
            ((target.ActualHeight - TaskPickerRowHeight) / 2) - selectedRowTop));
        _ = SetWindowPos(
            popupSource.Handle,
            IntPtr.Zero,
            (int)Math.Round(targetPoint.X),
            (int)Math.Round(targetPoint.Y),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private IReadOnlyList<MonitorTaskChoice> BuildTaskChoices()
    {
        var currentTaskId = _coordinator.Current?.TaskId;
        var workspaces = _coordinator.Workspaces;
        return _coordinator.RecentTasks
            .Take(RecentTaskChoiceLimit)
            .Select(task => CreateTaskChoice(task, currentTaskId == task.TaskId, workspaces))
            .ToArray();
    }

    private MonitorTaskChoice CreateTaskChoice(
        TaskHistoryEntry task,
        bool isCurrent,
        IReadOnlyList<WorkspaceHistoryEntry> workspaces)
    {
        var workspaceLabel = WorkspaceLabel(task, workspaces);
        return new MonitorTaskChoice(
            task.TaskId,
            task.Title,
            MonitorRunStatus(task.Status),
            workspaceLabel,
            StatusBrush(task.Status),
            isCurrent);
    }

    private static string WorkspaceLabel(
        TaskHistoryEntry task,
        IReadOnlyList<WorkspaceHistoryEntry> workspaces)
    {
        if (task.ScopeKind == TaskScopeKind.GeneralChat)
        {
            return DesktopLocalizer.Text("直接对话", "Direct Chat");
        }

        var workspace = task.WorkspaceId is { } workspaceId
            ? workspaces.FirstOrDefault(candidate => candidate.Id == workspaceId)
            : workspaces.FirstOrDefault(candidate =>
                string.Equals(
                    Path.TrimEndingDirectorySeparator(candidate.WorkingDirectory),
                    Path.TrimEndingDirectorySeparator(task.WorkingDirectory),
                    StringComparison.OrdinalIgnoreCase));
        if (workspace is not null)
        {
            return workspace.Name;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(task.WorkingDirectory));
        return string.IsNullOrWhiteSpace(name) ? task.WorkingDirectory : name;
    }

    private MediaBrush StatusBrush(RunStatus status)
    {
        var key = status switch
        {
            RunStatus.WaitingForApproval => "WarningBrush",
            RunStatus.WaitingForAnswer => "RunningBrush",
            RunStatus.Completed => "SuccessBrush",
            RunStatus.Failed or RunStatus.Interrupted => "DangerBrush",
            RunStatus.Queued or RunStatus.Starting or RunStatus.Running or RunStatus.Cancelling => "RunningBrush",
            _ => "AccentHoverBrush",
        };
        return (MediaBrush)FindResource(key);
    }

    private void OnTaskPickerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTaskPicker ||
            TaskPickerList.SelectedItem is not MonitorTaskChoice choice ||
            _coordinator.Current?.TaskId == choice.TaskId)
        {
            return;
        }

        SelectTaskFromPicker(choice.TaskId);
    }

    private void OnTaskPickerItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(TaskPickerList, source) is not ListBoxItem item ||
            item.DataContext is not MonitorTaskChoice choice)
        {
            return;
        }

        e.Handled = true;
        if (_coordinator.Current?.TaskId != choice.TaskId)
        {
            SelectTaskFromPicker(choice.TaskId);
        }

        TaskPickerPopup.IsOpen = false;
    }

    private void SelectTaskFromPicker(Guid taskId)
    {
        try
        {
            _coordinator.SelectTask(taskId);
            RefreshTaskPicker(BuildTaskChoices());
        }
        catch (InvalidOperationException exception)
        {
            ShowInlineError(exception.Message);
            RefreshTaskPicker(BuildTaskChoices());
        }
    }

    private void OnTaskPickerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var now = Environment.TickCount64;
        if (now < _taskPickerWheelBlockedUntil)
        {
            return;
        }

        if (_taskPickerWheelDelta != 0 && Math.Sign(_taskPickerWheelDelta) != Math.Sign(e.Delta))
        {
            _taskPickerWheelDelta = 0;
        }

        _taskPickerWheelDelta += e.Delta;
        if (Math.Abs(_taskPickerWheelDelta) < TaskPickerWheelThreshold)
        {
            return;
        }

        var direction = _taskPickerWheelDelta > 0 ? -1 : 1;
        _taskPickerWheelDelta = 0;
        var openedForWheel = !TaskPickerPopup.IsOpen;
        if (openedForWheel && sender is WpfButton button)
        {
            OpenTaskPicker(button, focusList: false);
        }
        if (!TaskPickerPopup.IsOpen)
        {
            return;
        }

        _taskPickerWheelBlockedUntil = now + TaskPickerWheelThrottleMilliseconds;
        MoveTaskPickerSelection(direction);
        if (openedForWheel || _taskPickerAutoCloseTimer.IsEnabled)
        {
            _taskPickerAutoCloseTimer.Stop();
            _taskPickerAutoCloseTimer.Start();
        }
    }

    private void OnTaskPickerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            TaskPickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space))
        {
            return;
        }

        e.Handled = true;
        if (!TaskPickerPopup.IsOpen)
        {
            var button = sender as WpfButton ??
                TaskPickerPopup.PlacementTarget as WpfButton ??
                HeaderTitleButton;
            OpenTaskPicker(button);
        }
        if (!TaskPickerPopup.IsOpen || e.Key is Key.Enter or Key.Space)
        {
            return;
        }

        if (e.Key == Key.Home)
        {
            SetTaskPickerSelection(0);
        }
        else if (e.Key == Key.End)
        {
            SetTaskPickerSelection(TaskPickerList.Items.Count - 1);
        }
        else
        {
            MoveTaskPickerSelection(e.Key == Key.Up ? -1 : 1);
        }
    }

    private void MoveTaskPickerSelection(int direction) =>
        SetTaskPickerSelection(Math.Clamp(
            TaskPickerList.SelectedIndex + direction,
            0,
            Math.Max(0, TaskPickerList.Items.Count - 1)));

    private void SetTaskPickerSelection(int index)
    {
        if (index < 0 ||
            index >= TaskPickerList.Items.Count ||
            index == TaskPickerList.SelectedIndex)
        {
            return;
        }

        TaskPickerList.SelectedIndex = index;
        TaskPickerList.ScrollIntoView(TaskPickerList.SelectedItem);
        RefreshTaskPickerPlacement();
    }

    private void OnTaskPickerOpened(object sender, EventArgs e)
    {
        _autoCollapseTimer.Stop();
        AlignTaskPickerWithoutScreenClamping();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(AlignTaskPickerWithoutScreenClamping));
    }

    private void OnTaskPickerClosed(object sender, EventArgs e)
    {
        _taskPickerAutoCloseTimer.Stop();
        _taskPickerWheelDelta = 0;
        _taskPickerWheelBlockedUntil = 0;
        if (_isExpanded &&
            !RootBorder.IsMouseOver &&
            _settings.AutoCollapseSeconds > 0)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Start();
        }
    }

    private void Expand()
    {
        if (_isExpanded)
        {
            return;
        }

        _isExpanded = true;
        ExpandedBody.Visibility = Visibility.Visible;
        UpdateExpandedHeaderState();
        Width = ExpandedWidth;
        MaxHeight = ExpandedMaximumHeight;
        Height = double.NaN;
        SizeToContent = SizeToContent.Height;
        RootBorder.CornerRadius = new CornerRadius(20);
        if (!_hasUserPosition)
        {
            WindowPlacementService.PlaceAtCorner(this, _settings.Position);
        }
    }

    private void UpdateExpandedHeaderState()
    {
        HeaderExpandedToggleButton.ToolTip = _isExpanded
            ? DesktopLocalizer.Text("收起", "Collapse")
            : DesktopLocalizer.Text("展开", "Expand");
        HeaderExpandIcon.Visibility = _isExpanded
            ? Visibility.Collapsed
            : Visibility.Visible;
        HeaderCollapseIcon.Visibility = _isExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Collapse()
    {
        if (!_isExpanded)
        {
            return;
        }

        TaskPickerPopup.IsOpen = false;
        _isExpanded = false;
        SizeToContent = SizeToContent.Manual;
        ClearValue(MaxHeightProperty);
        ExpandedBody.Visibility = Visibility.Collapsed;
        UpdateExpandedHeaderState();
        Width = CapsuleWidth;
        Height = CapsuleHeight;
        RootBorder.CornerRadius = new CornerRadius(20);
        if (!_hasUserPosition)
        {
            WindowPlacementService.PlaceAtCorner(this, _settings.Position);
        }
    }

    private void OnMonitorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isExpanded || !IsVisible || _hasUserPosition || _isDragging)
        {
            return;
        }

        WindowPlacementService.PlaceAtCorner(this, _settings.Position);
    }

    private void OnMonitorMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();
    }

    private void OnMonitorMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isExpanded ||
            _settings.AutoCollapseSeconds == 0 ||
            _isContextMenuOpen ||
            TaskPickerPopup.IsOpen)
        {
            return;
        }

        _autoCollapseTimer.Stop();
        _autoCollapseTimer.Start();
    }

    private void OnDragSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            e.ClickCount != 1 ||
            HeaderTitleButton.IsMouseOver ||
            IsDragInteractionSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _ = DragMonitorWindow();
        e.Handled = true;
    }

    private bool DragMonitorWindow()
    {
        var leftBeforeDrag = Left;
        var topBeforeDrag = Top;
        _isDragging = true;
        try
        {
            DragMove();
        }
        finally
        {
            _isDragging = false;
        }

        var moved =
            Math.Abs(Left - leftBeforeDrag) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(Top - topBeforeDrag) >= SystemParameters.MinimumVerticalDragDistance;
        if (moved)
        {
            _hasUserPosition = true;
            WindowPlacementService.ConstrainToCursorWorkArea(this);
        }

        return moved;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (TaskPickerPopup.IsOpen && !IsTaskPickerToggleSource(source))
        {
            TaskPickerPopup.IsOpen = false;
        }

        if (IsInputInteractionSource(source))
        {
            return;
        }

        ClearInputFocusAfterMouseRouting();
    }

    private bool IsTaskPickerToggleSource(DependencyObject? source) =>
        source is not null &&
        (ReferenceEquals(source, HeaderTitleButton) ||
         HeaderTitleButton.IsAncestorOf(source));

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (!IsInputInteractionSource(e.OriginalSource as DependencyObject))
        {
            ClearInputFocusAfterMouseRouting();
        }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (!_isContextMenuOpen && !TaskPickerPopup.IsOpen)
        {
            ClearInputFocus();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isContextMenuOpen &&
            !TaskPickerPopup.IsOpen &&
            !InteractionOptionsComboBox.IsDropDownOpen)
        {
            ClearInputFocus();
        }
    }

    private void ClearInputFocusAfterMouseRouting()
    {
        if (_isContextMenuOpen || TaskPickerPopup.IsOpen || HasActiveInputFocus())
        {
            return;
        }

        ClearInputFocus();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                if (!_isContextMenuOpen &&
                    !TaskPickerPopup.IsOpen &&
                    !HasActiveInputFocus())
                {
                    ClearInputFocus();
                }
            }));
    }

    private bool HasActiveInputFocus() =>
        InteractionResponseTextBox.IsKeyboardFocusWithin ||
        InteractionCustomResponseTextBox.IsKeyboardFocusWithin ||
        InteractionOptionsComboBox.IsKeyboardFocusWithin ||
        InteractionOptionsComboBox.IsDropDownOpen ||
        DirectionTextBox.IsKeyboardFocusWithin;

    private void ClearInputFocus()
    {
        FocusManager.SetFocusedElement(this, null);
        Keyboard.ClearFocus();
    }

    private static bool IsInputInteractionSource(DependencyObject? source) =>
        FindVisualAncestor<WpfTextBoxBase>(source) is not null ||
        FindVisualAncestor<System.Windows.Controls.ComboBox>(source) is not null ||
        FindVisualAncestor<System.Windows.Controls.ComboBoxItem>(source) is not null;

    private static bool IsDragInteractionSource(DependencyObject? source) =>
        IsInputInteractionSource(source) ||
        FindVisualAncestor<WpfButtonBase>(source) is not null ||
        FindVisualAncestor<System.Windows.Controls.Primitives.Thumb>(source) is not null ||
        FindVisualAncestor<WpfScrollBar>(source) is not null ||
        FindVisualAncestor<System.Windows.Controls.ListBoxItem>(source) is not null;

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            IsDragInteractionSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _openChat();
        e.Handled = true;
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e) =>
        await ResolveAsync(true, "允许一次");

    private async void OnApproveTaskClick(object sender, RoutedEventArgs e) =>
        await ResolveAsync(true, "本任务内允许同类操作");

    private async void OnRejectClick(object sender, RoutedEventArgs e) => await ResolveAsync(false);

    private void OnCopyInteractionClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InteractionPrompt.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(InteractionPrompt.Text);
            CopyInteractionButton.Content = DesktopLocalizer.Text("已复制", "Copied");
        }
        catch (Exception exception)
        {
            ShowInlineError(exception.Message);
        }
    }

    private async void OnCancelInteractionClick(object sender, RoutedEventArgs e)
    {
        await ResolveAsync(false);
        InteractionResponseTextBox.Clear();
        InteractionCustomResponseTextBox.Clear();
    }

    private async void OnAnswerClick(object sender, RoutedEventArgs e) =>
        await SubmitAnswerAsync();

    private async Task SubmitAnswerAsync()
    {
        var response = InteractionResponseTextBox.Text.Trim();
        if (response.Length == 0)
        {
            return;
        }

        await ResolveAsync(true, response);
        InteractionResponseTextBox.Clear();
    }

    private async void OnSelectAnswerClick(object sender, RoutedEventArgs e) =>
        await SubmitSelectedAnswerAsync();

    private async Task SubmitSelectedAnswerAsync()
    {
        if (InteractionOptionsComboBox.SelectedItem is not string response)
        {
            return;
        }

        if (response == OtherChoice)
        {
            response = InteractionCustomResponseTextBox.Text.Trim();
            if (response.Length == 0)
            {
                InteractionCustomResponseTextBox.Focus();
                return;
            }
        }

        await ResolveAsync(true, response);
        InteractionCustomResponseTextBox.Clear();
    }

    private void OnInteractionOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustomAnswer = InteractionOptionsComboBox.SelectedItem as string == OtherChoice;
        InteractionCustomResponseTextBox.Visibility = isCustomAnswer
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectAnswerButton.Content = isCustomAnswer
            ? DesktopLocalizer.Text("回答", "Answer")
            : DesktopLocalizer.Text("选择", "Select");
        if (!isCustomAnswer)
        {
            InteractionCustomResponseTextBox.Clear();
        }
    }

    private async Task ResolveAsync(bool approved, string? response = null)
    {
        try
        {
            await _coordinator.ResolveInteractionAsync(approved, response, _currentInteractionId);
        }
        catch (InvalidOperationException exception)
        {
            ShowInlineError(exception.Message);
        }
    }

    private async void OnDirectionClick(object sender, RoutedEventArgs e) =>
        await SubmitDirectionAsync();

    private async Task SubmitDirectionAsync()
    {
        if (_skillCompletion.CommitSelection())
        {
            return;
        }

        var message = DirectionTextBox.Text.Trim();
        if (message.Length == 0)
        {
            return;
        }

        try
        {
            if (_coordinator.Current?.Status.IsActive() == true)
            {
                await _coordinator.SteerAsync(message);
            }
            else
            {
                await StartDemoAsync(DemoRunMode.Success, message);
            }

            DirectionTextBox.Clear();
        }
        catch (InvalidOperationException exception)
        {
            ShowInlineError(exception.Message);
        }
    }

    private async Task StartDemoAsync(DemoRunMode mode, string prompt)
    {
        try
        {
            var current = _coordinator.Current;
            await _coordinator.StartAsync(
                prompt,
                current?.WorkingDirectory ?? Environment.CurrentDirectory,
                current?.Model ?? "Pi 默认模型",
                current?.ThinkingLevel ?? "高",
                mode,
                attachments: current?.Attachments);
        }
        catch (InvalidOperationException exception)
        {
            ShowInlineError(exception.Message);
        }
    }

    private async void OnMonitorInputPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (ReferenceEquals(sender, DirectionTextBox) &&
            _skillCompletion.HandlePreviewKeyDown(e))
        {
            return;
        }

        if (e.Key != Key.Enter ||
            e.IsRepeat ||
            (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        if (ReferenceEquals(sender, DirectionTextBox))
        {
            await SubmitDirectionAsync();
        }
        else if (ReferenceEquals(sender, InteractionResponseTextBox))
        {
            await SubmitAnswerAsync();
        }
        else if (ReferenceEquals(sender, InteractionCustomResponseTextBox))
        {
            await SubmitSelectedAnswerAsync();
        }
    }

    private void ShowInlineError(string message)
    {
        if (InteractionPanel.IsVisible)
        {
            InteractionPrompt.Text = message;
        }
        else if (ResultPanel.IsVisible)
        {
            ResultSummary.Visibility = Visibility.Visible;
            ResultSummary.Text = message;
        }
        else
        {
            ActivityStatusText.Text = message;
        }
    }

    private void OnDirectionTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateDirectionPlaceholderVisibility();

    private void UpdateDirectionPlaceholderVisibility()
    {
        DirectionPlaceholderText.Visibility = string.IsNullOrEmpty(DirectionTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnOpenChatClick(object sender, RoutedEventArgs e) => _openChat();

    private void OnExpandedToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isExpanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    private void OnNewTaskClick(object sender, RoutedEventArgs e) => _newTask();

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        TaskPickerPopup.IsOpen = false;
        _skillCompletion.Close();
        ClearInputFocus();
        Hide();
    }

    private void OnMonitorContextMenuOpened(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = true;
        if (sender is System.Windows.Controls.ContextMenu contextMenu)
        {
            DesktopLocalizer.Apply(contextMenu);
        }
    }

    private void OnMonitorContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _isContextMenuOpen = true;
        if (sender is FrameworkElement element && element.ContextMenu is { } contextMenu)
        {
            DesktopLocalizer.Apply(contextMenu);
        }
    }

    private void OnMonitorContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => _exit();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        TaskPickerPopup.IsOpen = false;
        _appSettings.SaveWindowPlacement(WindowPlacementName, WindowPlacementService.Capture(this));
        _coordinator.ProjectionChanged -= OnProjectionChanged;
        if (!AllowClose)
        {
            e.Cancel = true;
            _coordinator.ProjectionChanged += OnProjectionChanged;
            Hide();
        }
        else
        {
            _skillCompletion.Dispose();
        }
    }

    private sealed record ResultInteractionSummary(string Kind, string Summary);

    private sealed record MonitorTaskChoice(
        Guid TaskId,
        string Title,
        string Status,
        string Workspace,
        MediaBrush StatusBrush,
        bool IsCurrent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
