using Microsoft.Data.Sqlite;
using PiCompanion.Application.Persistence;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Core.Tests;

public sealed class TaskTemplateTests
{
    [Fact]
    public void Rules_NormalizeOptionalPreferencesAndRejectFullAccess()
    {
        var normalized = TaskTemplateRules.Normalize(new TaskTemplate(
            Guid.Empty,
            "  Review   changes  ",
            "  Review the current changes.  ",
            TaskTemplateTargetKind.CurrentContext,
            null,
            "  provider/model ",
            " high ",
            " standard ",
            true,
            default,
            default));

        Assert.NotEqual(Guid.Empty, normalized.Id);
        Assert.Equal("Review changes", normalized.Name);
        Assert.Equal("Review the current changes.", normalized.Prompt);
        Assert.Equal("provider/model", normalized.Model);
        Assert.Equal("high", normalized.ThinkingLevel);
        Assert.Equal("standard", normalized.PermissionMode);

        var exception = Assert.Throws<ArgumentException>(() => TaskTemplateRules.Normalize(
            normalized with { PermissionMode = "full-access" }));
        Assert.Contains("只读或标准访问", exception.Message);

        Assert.Throws<ArgumentException>(() => TaskTemplateRules.Normalize(
            normalized with { ThinkingLevel = "unbounded" }));

        var generalChat = TaskTemplateRules.Normalize(normalized with
        {
            TargetKind = TaskTemplateTargetKind.GeneralChat,
            PermissionMode = "read-only",
        });
        Assert.Null(generalChat.PermissionMode);
    }

    [Fact]
    public void Store_PersistsUpdatesOrdersAndDeletesTemplates()
    {
        var root = Directory.CreateTempSubdirectory("pi-companion-template-tests-").FullName;
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var store = new SqliteRunEventStore(databasePath);
            var workspace = store.CreateWorkspace(Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName);
            var first = store.UpsertTaskTemplate(CreateTemplate("Review", pinned: false));
            var second = store.UpsertTaskTemplate(CreateTemplate(
                "Workspace health",
                pinned: true,
                targetKind: TaskTemplateTargetKind.Workspace,
                workspaceId: workspace.Id));

            var reopened = new SqliteRunEventStore(databasePath);
            var restored = reopened.GetTaskTemplates();
            Assert.Equal([second.Id, first.Id], restored.Select(template => template.Id));
            Assert.Equal(workspace.Id, restored[0].WorkspaceId);

            var updated = reopened.UpsertTaskTemplate(first with
            {
                Prompt = "Review only staged changes.",
                IsPinned = true,
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            });
            Assert.Equal(first.CreatedAt, updated.CreatedAt);
            Assert.Equal("Review only staged changes.", updated.Prompt);

            reopened.DeleteTaskTemplate(second.Id);
            Assert.Equal(updated.Id, Assert.Single(reopened.GetTaskTemplates()).Id);

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 16;";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_AllowsDisplayNameReuseAndRejectsUnavailableWorkspaces()
    {
        var root = Directory.CreateTempSubdirectory("pi-companion-template-tests-").FullName;
        try
        {
            var store = new SqliteRunEventStore(Path.Combine(root, "state.db"));
            var first = store.UpsertTaskTemplate(CreateTemplate("Review"));
            var second = store.UpsertTaskTemplate(CreateTemplate("review"));

            Assert.Equal(2, store.GetTaskTemplates().Count);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Throws<InvalidOperationException>(() =>
                store.UpsertTaskTemplate(CreateTemplate(
                    "Missing workspace",
                    targetKind: TaskTemplateTargetKind.Workspace,
                    workspaceId: Guid.NewGuid())));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static TaskTemplate CreateTemplate(
        string name,
        bool pinned = false,
        TaskTemplateTargetKind targetKind = TaskTemplateTargetKind.CurrentContext,
        Guid? workspaceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new TaskTemplate(
            Guid.NewGuid(),
            name,
            $"Prompt for {name}",
            targetKind,
            workspaceId,
            null,
            null,
            null,
            pinned,
            now,
            now);
    }
}
