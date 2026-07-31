using System.Text.Json;
using PiCompanion.Application.Persistence;

namespace PiCompanion.Application.Settings;

public sealed record PiCompanionSettings(
    GeneralSettings General,
    MonitorSettings Monitor,
    TaskSettings Tasks,
    AgentSettings Agent,
    NotificationSettings? Notifications = null,
    DataRetentionSettings? DataRetention = null,
    ModelVisibilitySettings? ModelVisibility = null)
{
    public static PiCompanionSettings Default { get; } = new(
        new GeneralSettings(
            LaunchAtLogin: false,
            KeepRunningInTray: true,
            Language: "zh-CN",
            Theme: "dark",
            LogLevel: "information",
            UiScalePercent: 100,
            GitAutoRefreshSeconds: 0,
            ConversationDetailLevel: "normal"),
        new MonitorSettings(
            Position: "top-right",
            ShowOnStartup: true,
            AlwaysOnTop: true,
            AutoCollapseSeconds: 8,
            AnimationsEnabled: true),
        new TaskSettings(
            AiTitleEnabled: true,
            AiTitleModel: string.Empty,
            AiSummaryEnabled: true,
            AiSummaryModel: string.Empty,
            RecentTaskCount: 5,
            RecentTaskSubtitle: "workspace",
            PermissionMode: "standard",
            AiMetadataModel: string.Empty),
        new AgentSettings(
            DefaultModel: string.Empty,
            DefaultThinkingLevel: "high",
            AutoCompact: true,
            AutoRetry: true),
        new NotificationSettings(
            NotifyOnCompletion: true,
            NotifyOnFailure: true,
            NotifyWhenAttentionRequired: true,
            PlaySound: true,
            OnlyWhenAppIsInBackground: true),
        new DataRetentionSettings(
            TaskHistoryDays: 0,
            RecycleBinDays: 30,
            LogDays: 30),
        new ModelVisibilitySettings([], LegacyPiScopeMigrationCompleted: true));

    public PiCompanionSettings Normalize()
    {
        var general = General ?? Default.General;
        var monitor = Monitor ?? Default.Monitor;
        var tasks = Tasks ?? Default.Tasks;
        var agent = Agent ?? Default.Agent;
        var notifications = Notifications ?? Default.Notifications!;
        var dataRetention = DataRetention ?? Default.DataRetention!;
        var modelVisibility = ModelVisibility ?? new ModelVisibilitySettings([], LegacyPiScopeMigrationCompleted: false);
        var metadataModel = FirstNonEmpty(
            NormalizeModel(tasks.AiMetadataModel),
            NormalizeModel(tasks.AiSummaryModel),
            NormalizeModel(tasks.AiTitleModel));
        return new(
        new GeneralSettings(
            general.LaunchAtLogin,
            general.KeepRunningInTray,
            NormalizeChoice(general.Language, ["zh-CN", "en-US"], "zh-CN"),
            NormalizeChoice(general.Theme, ["dark", "light", "system"], "dark"),
            NormalizeChoice(general.LogLevel, ["error", "warning", "information", "debug"], "information"),
            general.UiScalePercent <= 0
                ? Default.General.UiScalePercent
                : Math.Clamp(general.UiScalePercent, 50, 200),
            NormalizeGitAutoRefreshSeconds(general.GitAutoRefreshSeconds),
            NormalizeChoice(general.ConversationDetailLevel, ["summary", "normal", "verbose"], "normal")),
        new MonitorSettings(
            NormalizeChoice(monitor.Position, ["top-left", "top-right", "bottom-left", "bottom-right", "last-position"], "top-right"),
            monitor.ShowOnStartup,
            monitor.AlwaysOnTop,
            Math.Clamp(monitor.AutoCollapseSeconds, 0, 300),
            monitor.AnimationsEnabled),
        new TaskSettings(
            tasks.AiTitleEnabled,
            metadataModel,
            tasks.AiSummaryEnabled,
            metadataModel,
            tasks.RecentTaskCount <= 0
                ? Default.Tasks.RecentTaskCount
                : Math.Clamp(tasks.RecentTaskCount, 1, 20),
            NormalizePermissionMode(tasks.PermissionMode),
            tasks.FileChangesExpandedByDefault,
            NormalizeCompletionBehavior(tasks.CompletionBehavior),
            tasks.AutoStartLocalQueueEnabled,
            NormalizeLocalQueueDelay(tasks.AutoStartLocalQueueDelaySeconds),
            metadataModel,
            NormalizeChoice(tasks.RecentTaskSubtitle, ["workspace", "latest-run"], "workspace")),
        new AgentSettings(
            NormalizeModel(agent.DefaultModel),
            NormalizeChoice(agent.DefaultThinkingLevel, ["off", "minimal", "low", "medium", "high", "xhigh", "max"], "high"),
            agent.AutoCompact,
            agent.AutoRetry,
            Math.Clamp(agent.CompactionReserveTokens, 1024, 262144),
            Math.Clamp(agent.CompactionKeepRecentTokens, 1024, 262144),
            Math.Clamp(agent.RetryMaxRetries, 0, 20),
            Math.Clamp(agent.RetryBaseDelayMilliseconds, 100, 300000),
            Math.Clamp(agent.RetryMaxDelayMilliseconds, 0, 3600000),
            NormalizeChoice(agent.SteeringMode, ["one-at-a-time", "all"], "one-at-a-time"),
            NormalizeChoice(agent.FollowUpMode, ["one-at-a-time", "all"], "one-at-a-time")),
        new NotificationSettings(
            notifications.NotifyOnCompletion,
            notifications.NotifyOnFailure,
            notifications.NotifyWhenAttentionRequired,
            notifications.PlaySound,
            notifications.OnlyWhenAppIsInBackground),
        new DataRetentionSettings(
            NormalizeRetentionDays(dataRetention.TaskHistoryDays),
            NormalizeRetentionDays(dataRetention.RecycleBinDays),
            NormalizeRetentionDays(dataRetention.LogDays)),
        new ModelVisibilitySettings((modelVisibility.HiddenModelReferences ?? [])
            .Select(NormalizeModel)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray(), modelVisibility.LegacyPiScopeMigrationCompleted));
    }

    private static string NormalizeChoice(string? value, IReadOnlyList<string> choices, string fallback) =>
        choices.FirstOrDefault(choice => string.Equals(choice, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static string NormalizePermissionMode(string? value, string fallback = "standard") =>
        value?.Trim().ToLowerInvariant() switch
        {
            "read-only" => "read-only",
            "standard" => "standard",
            _ => fallback,
        };

    private static string NormalizeModel(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Equals("Pi 默认模型", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static int NormalizeRetentionDays(int days) =>
        days is 0 or 7 or 30 or 90 ? days : 0;

    private static string NormalizeCompletionBehavior(string? value) =>
        string.Equals(value, "keep", StringComparison.OrdinalIgnoreCase)
            ? "keep-expanded"
            : NormalizeChoice(value, ["keep-expanded", "collapse-monitor", "show-chat"], "keep-expanded");

    private static int NormalizeLocalQueueDelay(int seconds) =>
        seconds is 0 or 15 or 30 or 60 ? seconds : 15;

    private static int NormalizeGitAutoRefreshSeconds(int seconds) =>
        seconds is 0 or 5 or 10 or 30 or 60 ? seconds : 0;
}

public sealed record GeneralSettings(
    bool LaunchAtLogin,
    bool KeepRunningInTray,
    string Language,
    string Theme,
    string LogLevel,
    int UiScalePercent = 100,
    int GitAutoRefreshSeconds = 0,
    string? ConversationDetailLevel = "normal");

public sealed record MonitorSettings(
    string Position,
    bool ShowOnStartup,
    bool AlwaysOnTop,
    int AutoCollapseSeconds,
    bool AnimationsEnabled);

public sealed record TaskSettings(
    bool AiTitleEnabled,
    string AiTitleModel,
    bool AiSummaryEnabled,
    string AiSummaryModel,
    int RecentTaskCount = 5,
    string? PermissionMode = null,
    bool FileChangesExpandedByDefault = false,
    string? CompletionBehavior = "keep-expanded",
    bool AutoStartLocalQueueEnabled = false,
    int AutoStartLocalQueueDelaySeconds = 15,
    string? AiMetadataModel = null,
    string? RecentTaskSubtitle = "workspace");

public sealed record AgentSettings(
    string DefaultModel,
    string DefaultThinkingLevel,
    bool AutoCompact,
    bool AutoRetry,
    int CompactionReserveTokens = 16384,
    int CompactionKeepRecentTokens = 20000,
    int RetryMaxRetries = 3,
    int RetryBaseDelayMilliseconds = 2000,
    int RetryMaxDelayMilliseconds = 60000,
    string? SteeringMode = "one-at-a-time",
    string? FollowUpMode = "one-at-a-time");

public sealed record NotificationSettings(
    bool NotifyOnCompletion,
    bool NotifyOnFailure,
    bool NotifyWhenAttentionRequired,
    bool PlaySound,
    bool OnlyWhenAppIsInBackground);

public sealed record DataRetentionSettings(
    int TaskHistoryDays,
    int RecycleBinDays,
    int LogDays);

public sealed record ModelVisibilitySettings(
    IReadOnlyList<string> HiddenModelReferences,
    bool LegacyPiScopeMigrationCompleted = true);

public sealed record WindowPlacementState(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized = false);

public sealed class AppSettingsService
{
    private const string SettingsKey = "app.settings.v1";
    private const string WindowPlacementKeyPrefix = "window-placement.v1.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly IRunEventStore _store;
    private readonly string _logDirectory;
    private PiCompanionSettings _current;

    public AppSettingsService(IRunEventStore store, string? logDirectory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logDirectory = Path.GetFullPath(logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion",
            "logs"));
        _current = Load();
        ApplyRetention(_current.DataRetention!);
    }

    public PiCompanionSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public PiCompanionSettings Save(PiCompanionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        DataRetentionSettings? previousRetention;
        lock (_gate)
        {
            previousRetention = _current.DataRetention;
            _store.SetSettingJson(SettingsKey, JsonSerializer.Serialize(normalized, JsonOptions));
            _current = normalized;
        }

        if (previousRetention != normalized.DataRetention)
        {
            ApplyRetention(normalized.DataRetention!);
        }

        return normalized;
    }

    public bool TryMigrateLegacyModelVisibility(
        IReadOnlyList<string> availableModelReferences,
        IReadOnlyList<string>? legacyEnabledModelReferences,
        out PiCompanionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(availableModelReferences);
        lock (_gate)
        {
            if (_current.ModelVisibility!.LegacyPiScopeMigrationCompleted)
            {
                settings = _current;
                return false;
            }

            var enabled = legacyEnabledModelReferences is null
                ? null
                : new HashSet<string>(legacyEnabledModelReferences, StringComparer.Ordinal);
            var hidden = enabled is null
                ? []
                : availableModelReferences
                    .Where(reference => !enabled.Contains(reference))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            var migrated = (_current with
            {
                ModelVisibility = new ModelVisibilitySettings(hidden, LegacyPiScopeMigrationCompleted: true),
            }).Normalize();
            _store.SetSettingJson(SettingsKey, JsonSerializer.Serialize(migrated, JsonOptions));
            _current = migrated;
            settings = migrated;
            return true;
        }
    }

    public WindowPlacementState? LoadWindowPlacement(string windowName)
    {
        var json = _store.GetSettingJson(WindowPlacementKey(windowName));
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<WindowPlacementState>(json, JsonOptions);
            return state is not null &&
                   double.IsFinite(state.Left) &&
                   double.IsFinite(state.Top) &&
                   double.IsFinite(state.Width) &&
                   double.IsFinite(state.Height) &&
                   state.Width > 0 &&
                   state.Height > 0
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveWindowPlacement(string windowName, WindowPlacementState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!double.IsFinite(state.Left) ||
            !double.IsFinite(state.Top) ||
            !double.IsFinite(state.Width) ||
            !double.IsFinite(state.Height) ||
            state.Width <= 0 ||
            state.Height <= 0)
        {
            return;
        }

        _store.SetSettingJson(
            WindowPlacementKey(windowName),
            JsonSerializer.Serialize(state, JsonOptions));
    }

    private static string WindowPlacementKey(string windowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        return WindowPlacementKeyPrefix + windowName.Trim().ToLowerInvariant();
    }

    private void ApplyRetention(DataRetentionSettings retention)
    {
        var now = DateTimeOffset.UtcNow;
        _store.PurgeExpiredTasks(
            retention.TaskHistoryDays == 0 ? null : now.AddDays(-retention.TaskHistoryDays),
            retention.RecycleBinDays == 0 ? null : now.AddDays(-retention.RecycleBinDays));

        if (retention.LogDays == 0)
        {
            return;
        }

        if (!Directory.Exists(_logDirectory))
        {
            return;
        }

        var cutoff = now.AddDays(-retention.LogDays).UtcDateTime;
        foreach (var path in Directory.EnumerateFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention is best-effort; a locked diagnostic log can be retried next time.
            }
        }
    }

    private PiCompanionSettings Load()
    {
        var json = _store.GetSettingJson(SettingsKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return PiCompanionSettings.Default;
        }

        try
        {
            return (JsonSerializer.Deserialize<PiCompanionSettings>(json, JsonOptions) ?? PiCompanionSettings.Default)
                .Normalize();
        }
        catch (JsonException)
        {
            return PiCompanionSettings.Default;
        }
    }
}
