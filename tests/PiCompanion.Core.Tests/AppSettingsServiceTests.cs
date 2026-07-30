using Microsoft.Data.Sqlite;
using PiCompanion.Application.Persistence;
using PiCompanion.Application.Settings;

namespace PiCompanion.Core.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void Save_PersistsNormalizedSettingsAcrossServiceInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var service = new AppSettingsService(store, Path.Combine(root, "logs"));

            var saved = service.Save(new PiCompanionSettings(
                new GeneralSettings(true, false, "invalid", "SYSTEM", "DEBUG", 999, 99, "invalid"),
                new MonitorSettings("bottom-left", false, false, 999, false),
                new TaskSettings(true, "  openai-codex/gpt-5.6-luna  ", false, "  openai-codex/gpt-5.6-terra  ", 99,
                    PermissionMode: "full-access",
                    FileChangesExpandedByDefault: true, CompletionBehavior: "collapse-monitor",
                    AutoStartLocalQueueEnabled: true, AutoStartLocalQueueDelaySeconds: 99,
                    RecentTaskSubtitle: "invalid"),
                new AgentSettings("  openai-codex/gpt-5.6-sol  ", "max", false, false,
                    512, 999999, 99, 1, 9999999, "all", "invalid"),
                new NotificationSettings(false, true, false, false, false),
                new DataRetentionSettings(7, 30, 123)));

            Assert.True(saved.General.LaunchAtLogin);
            Assert.False(saved.General.KeepRunningInTray);
            Assert.Equal("zh-CN", saved.General.Language);
            Assert.Equal("system", saved.General.Theme);
            Assert.Equal("debug", saved.General.LogLevel);
            Assert.Equal(200, saved.General.UiScalePercent);
            Assert.Equal(0, saved.General.GitAutoRefreshSeconds);
            Assert.Equal("normal", saved.General.ConversationDetailLevel);
            Assert.Equal(300, saved.Monitor.AutoCollapseSeconds);
            Assert.Equal("openai-codex/gpt-5.6-terra", saved.Tasks.AiTitleModel);
            Assert.False(saved.Tasks.AiSummaryEnabled);
            Assert.Equal("openai-codex/gpt-5.6-terra", saved.Tasks.AiSummaryModel);
            Assert.Equal("openai-codex/gpt-5.6-terra", saved.Tasks.AiMetadataModel);
            Assert.Equal(20, saved.Tasks.RecentTaskCount);
            Assert.Equal("workspace", saved.Tasks.RecentTaskSubtitle);
            Assert.Equal("standard", saved.Tasks.PermissionMode);
            Assert.True(saved.Tasks.FileChangesExpandedByDefault);
            Assert.Equal("collapse-monitor", saved.Tasks.CompletionBehavior);
            Assert.True(saved.Tasks.AutoStartLocalQueueEnabled);
            Assert.Equal(15, saved.Tasks.AutoStartLocalQueueDelaySeconds);
            Assert.Equal("openai-codex/gpt-5.6-sol", saved.Agent.DefaultModel);
            Assert.Equal("max", saved.Agent.DefaultThinkingLevel);
            Assert.Equal(1024, saved.Agent.CompactionReserveTokens);
            Assert.Equal(262144, saved.Agent.CompactionKeepRecentTokens);
            Assert.Equal(20, saved.Agent.RetryMaxRetries);
            Assert.Equal(100, saved.Agent.RetryBaseDelayMilliseconds);
            Assert.Equal(3600000, saved.Agent.RetryMaxDelayMilliseconds);
            Assert.Equal("all", saved.Agent.SteeringMode);
            Assert.Equal("one-at-a-time", saved.Agent.FollowUpMode);
            Assert.False(saved.Notifications!.NotifyOnCompletion);
            Assert.Equal(7, saved.DataRetention!.TaskHistoryDays);
            Assert.Equal(30, saved.DataRetention.RecycleBinDays);
            Assert.Equal(0, saved.DataRetention.LogDays);

            var restored = new AppSettingsService(store, Path.Combine(root, "logs")).Current;
            Assert.Equal(saved, restored);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void Save_AcceptsSupportedInterfaceLanguages(string language)
    {
        var settings = PiCompanionSettings.Default with
        {
            General = PiCompanionSettings.Default.General with { Language = language },
        };

        Assert.Equal(language, settings.Normalize().General.Language);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("system")]
    public void Save_AcceptsSupportedThemes(string theme)
    {
        var settings = PiCompanionSettings.Default with
        {
            General = PiCompanionSettings.Default.General with { Theme = theme },
        };

        Assert.Equal(theme, settings.Normalize().General.Theme);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("normal")]
    [InlineData("verbose")]
    public void Save_AcceptsSupportedConversationDetailLevels(string detailLevel)
    {
        var settings = PiCompanionSettings.Default with
        {
            General = PiCompanionSettings.Default.General with { ConversationDetailLevel = detailLevel },
        };

        Assert.Equal(detailLevel, settings.Normalize().General.ConversationDetailLevel);
    }

    [Fact]
    public void WindowPlacement_PersistsIndependentlyFromUserSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var service = new AppSettingsService(store, Path.Combine(root, "logs"));
            var expected = new WindowPlacementState(120, 80, 1280, 840, true);

            service.SaveWindowPlacement("agent-chat", expected);

            var restored = new AppSettingsService(store, Path.Combine(root, "logs"))
                .LoadWindowPlacement("agent-chat");
            Assert.Equal(expected, restored);
            Assert.Equal(PiCompanionSettings.Default, service.Current);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Normalize_AcceptsLastMonitorPosition()
    {
        var settings = PiCompanionSettings.Default with
        {
            Monitor = PiCompanionSettings.Default.Monitor with { Position = "last-position" },
        };

        Assert.Equal("last-position", settings.Normalize().Monitor.Position);
    }

}
