using Microsoft.Data.Sqlite;
using PiCompanion.Application.Persistence;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Core.Tests;

public sealed class ScheduledTaskTests
{
    [Fact]
    public void Rules_TreatTemplateCopyAsCustomAndTemplateLinkAsTheOnlyRelationship()
    {
        var workspaceId = Guid.NewGuid();
        var custom = ScheduledTaskRules.Normalize(CreateScheduledTask() with
        {
            Name = "  Daily   review ",
            Prompt = "  Review changes. ",
            TargetKind = TaskTemplateTargetKind.Workspace,
            WorkspaceId = workspaceId,
            Model = " provider/model ",
            ThinkingLevel = " HIGH ",
            PermissionMode = "read-only",
        });

        Assert.Equal("Daily review", custom.Name);
        Assert.Equal("Review changes.", custom.Prompt);
        Assert.Equal(workspaceId, custom.WorkspaceId);
        Assert.Equal("provider/model", custom.Model);
        Assert.Equal("high", custom.ThinkingLevel);

        var linked = ScheduledTaskRules.Normalize(custom with
        {
            TemplateId = Guid.NewGuid(),
        });
        Assert.Null(linked.Prompt);
        Assert.Null(linked.TargetKind);
        Assert.Null(linked.WorkspaceId);
        Assert.Null(linked.Model);
        Assert.Null(linked.ThinkingLevel);
        Assert.Null(linked.PermissionMode);
    }

    [Fact]
    public void Recurrence_CalculatesDailyWeekdayAndWeeklyOccurrences()
    {
        var daily = CreateScheduledTask() with
        {
            Frequency = ScheduledTaskFrequency.Daily,
            LocalStartAt = new DateTime(2026, 1, 1, 9, 0, 0),
        };
        Assert.Equal(
            new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero),
            ScheduledTaskRecurrence.GetNextOccurrence(
                daily,
                new DateTimeOffset(2026, 1, 3, 10, 0, 0, TimeSpan.Zero)));

        var weekdays = daily with { Frequency = ScheduledTaskFrequency.Weekdays };
        Assert.Equal(
            new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
            ScheduledTaskRecurrence.GetNextOccurrence(
                weekdays,
                new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero)));

        var weekly = daily with
        {
            Frequency = ScheduledTaskFrequency.Weekly,
            DaysOfWeek = ScheduledDaysOfWeek.Tuesday | ScheduledDaysOfWeek.Thursday,
        };
        Assert.Equal(
            new DateTimeOffset(2026, 1, 6, 9, 0, 0, TimeSpan.Zero),
            ScheduledTaskRecurrence.GetNextOccurrence(
                weekly,
                new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero)));

        var ianaOnWindows = daily with { TimeZoneId = "Asia/Shanghai" };
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 1, 0, 0, TimeSpan.Zero),
            ScheduledTaskRecurrence.GetNextOccurrence(
                ianaOnWindows,
                new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Store_PersistsSchedulesAndKeepsOccurrencesIdempotent()
    {
        var root = Directory.CreateTempSubdirectory("pi-companion-scheduled-task-tests-").FullName;
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var store = new SqliteRunEventStore(databasePath);
            var workspace = store.CreateWorkspace(Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName);
            var template = store.UpsertTaskTemplate(new TaskTemplate(
                Guid.NewGuid(),
                "Review",
                "Review changes.",
                TaskTemplateTargetKind.Workspace,
                workspace.Id,
                null,
                null,
                "read-only",
                false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
            var nextRunAt = DateTimeOffset.UtcNow.AddHours(1);
            var saved = store.UpsertScheduledTask(CreateScheduledTask() with
            {
                TemplateId = template.Id,
                Prompt = null,
                TargetKind = null,
                WorkspaceId = null,
                NextRunAt = nextRunAt,
            });

            var reopened = new SqliteRunEventStore(databasePath);
            var restored = Assert.Single(reopened.GetScheduledTasks());
            Assert.Equal(template.Id, restored.TemplateId);
            Assert.Equal(nextRunAt, restored.NextRunAt);

            var occurrence = new ScheduledTaskOccurrence(
                Guid.NewGuid(),
                saved.Id,
                nextRunAt,
                ScheduledTaskOccurrenceStatus.Dispatching,
                null,
                null,
                null,
                "{}",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            Assert.NotNull(reopened.BeginScheduledTaskOccurrence(
                occurrence,
                nextRunAt.AddDays(1),
                disableSchedule: false,
                advanceSchedule: true));
            Assert.Null(reopened.BeginScheduledTaskOccurrence(
                occurrence with { Id = Guid.NewGuid() },
                nextRunAt.AddDays(2),
                disableSchedule: false,
                advanceSchedule: true));
            reopened.CompleteScheduledTaskOccurrence(
                occurrence.Id,
                ScheduledTaskOccurrenceStatus.Skipped,
                null,
                null,
                "previous run active");

            restored = Assert.Single(reopened.GetScheduledTasks());
            Assert.Equal(nextRunAt.AddDays(1), restored.NextRunAt);
            Assert.Equal(ScheduledTaskOccurrenceStatus.Skipped, restored.LastOccurrence?.Status);
            Assert.Equal("previous run active", restored.LastOccurrence?.Error);
            Assert.Throws<InvalidOperationException>(() => reopened.DeleteTaskTemplate(template.Id));

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 18;";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ScheduledTask CreateScheduledTask()
    {
        var now = DateTimeOffset.UtcNow;
        return new ScheduledTask(
            Guid.NewGuid(),
            "Daily review",
            true,
            null,
            "Review changes.",
            TaskTemplateTargetKind.GeneralChat,
            null,
            null,
            null,
            null,
            ScheduledTaskFrequency.Once,
            new DateTime(2026, 12, 1, 9, 0, 0),
            ScheduledDaysOfWeek.None,
            "UTC",
            null,
            now,
            now);
    }
}
