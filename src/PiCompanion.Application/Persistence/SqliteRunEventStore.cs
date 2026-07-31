using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Evidence;
using PiCompanion.Core.Runs;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Application.Persistence;

public sealed class SqliteRunEventStore : IRunEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _connectionString;

    public SqliteRunEventStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    public static SqliteRunEventStore CreateDefault()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiCompanion");
        return new SqliteRunEventStore(Path.Combine(dataDirectory, "pi-companion.db"));
    }

    public void CreateRun(TaskProjection projection, string prompt)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var workspaceId = projection.ScopeKind == TaskScopeKind.Workspace
                ? EnsureWorkspace(connection, transaction, projection.WorkingDirectory, now)
                : null;

            Execute(
                connection,
                transaction,
                """
                INSERT INTO tasks (
                    id, workspace_id, title, working_directory, model, thinking_level, permission_mode, scope_kind, status, summary, created_at, updated_at)
                VALUES ($id, $workspaceId, $title, $workingDirectory, $model, $thinkingLevel, $permissionMode, $scopeKind, $status, $summary, $now, $now)
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    title = excluded.title,
                    working_directory = excluded.working_directory,
                    model = excluded.model,
                    thinking_level = excluded.thinking_level,
                    permission_mode = excluded.permission_mode,
                    scope_kind = excluded.scope_kind,
                    status = excluded.status,
                    summary = excluded.summary,
                    updated_at = excluded.updated_at;
                """,
                ("$id", projection.TaskId.ToString("D")),
                ("$workspaceId", workspaceId),
                ("$title", projection.Title),
                ("$workingDirectory", projection.WorkingDirectory),
                ("$model", projection.PreferredModel),
                ("$thinkingLevel", projection.PreferredThinkingLevel),
                ("$permissionMode", projection.PermissionMode),
                ("$scopeKind", projection.ScopeKind.ToString()),
                ("$status", projection.Status.ToString()),
                ("$summary", projection.Summary),
                ("$now", now));

            Execute(
                connection,
                transaction,
                "DELETE FROM task_attachments WHERE task_id = $taskId;",
                ("$taskId", projection.TaskId.ToString("D")));
            for (var index = 0; index < projection.Attachments.Count; index++)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO task_attachments (task_id, ordinal, path) VALUES ($taskId, $ordinal, $path);",
                    ("$taskId", projection.TaskId.ToString("D")),
                    ("$ordinal", index),
                    ("$path", projection.Attachments[index]));
            }

            Execute(
                connection,
                transaction,
                """
                INSERT INTO runs (
                    id, task_id, prompt, model, thinking_level, status, created_at,
                    last_event_sequence, attachments_snapshot)
                VALUES ($id, $taskId, $prompt, $model, $thinkingLevel, $status, $now, 0, 1);
                """,
                ("$id", projection.RunId.ToString("D")),
                ("$taskId", projection.TaskId.ToString("D")),
                ("$prompt", prompt),
                ("$model", projection.Model),
                ("$thinkingLevel", projection.ThinkingLevel),
                ("$status", projection.Status.ToString()),
                ("$now", now));

            for (var index = 0; index < projection.Attachments.Count; index++)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO run_attachments (run_id, ordinal, path) VALUES ($runId, $ordinal, $path);",
                    ("$runId", projection.RunId.ToString("D")),
                    ("$ordinal", index),
                    ("$path", projection.Attachments[index]));
            }

            transaction.Commit();
        }
    }

    public void AppendRunEvent(CompanionRunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO run_events (
                    event_id, task_id, run_id, sequence, kind, timestamp, status, payload_json, source_version)
                VALUES (
                    $eventId, $taskId, $runId, $sequence, $kind, $timestamp, $status, $payload, $sourceVersion);
                """;
            AddParameters(
                insert,
                ("$eventId", runEvent.EventId.ToString("D")),
                ("$taskId", runEvent.TaskId.ToString("D")),
                ("$runId", runEvent.RunId.ToString("D")),
                ("$sequence", runEvent.Sequence),
                ("$kind", runEvent.Kind.ToString()),
                ("$timestamp", runEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                ("$status", runEvent.Status.ToString()),
                ("$payload", JsonSerializer.Serialize(runEvent.Payload, JsonOptions)),
                ("$sourceVersion", runEvent.SourceVersion));
            if (insert.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return;
            }

            var isTerminal = runEvent.Status is RunStatus.Completed or
                RunStatus.Failed or RunStatus.Interrupted;
            var sessionId = runEvent.Payload.TryGetValue("piSessionId", out var sessionIdValue) ? sessionIdValue : null;
            var sessionPath = runEvent.Payload.TryGetValue("piSessionPath", out var sessionPathValue) ? sessionPathValue : null;
            var entryCursor = runEvent.Payload.TryGetValue("piEntryCursor", out var entryCursorValue) ? entryCursorValue : null;
            var exitReason = runEvent.Payload.TryGetValue("exitReason", out var exitReasonValue) ? exitReasonValue : null;
            var timestamp = runEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture);
            Execute(
                connection,
                transaction,
                """
                UPDATE runs
                SET status = $status,
                    last_event_sequence = $sequence,
                    started_at = CASE WHEN $kind = 'RunStarted' THEN COALESCE(started_at, $timestamp) ELSE started_at END,
                    settled_at = CASE WHEN $isTerminal = 1 THEN $timestamp ELSE settled_at END,
                    exit_reason = COALESCE($exitReason, exit_reason),
                    pi_session_id = COALESCE($sessionId, pi_session_id),
                    pi_session_path = COALESCE($sessionPath, pi_session_path),
                    pi_entry_cursor = COALESCE($entryCursor, pi_entry_cursor)
                WHERE id = $runId AND last_event_sequence < $sequence;
                """,
                ("$status", runEvent.Status.ToString()),
                ("$sequence", runEvent.Sequence),
                ("$kind", runEvent.Kind.ToString()),
                ("$timestamp", timestamp),
                ("$isTerminal", isTerminal ? 1 : 0),
                ("$exitReason", exitReason),
                ("$sessionId", sessionId),
                ("$sessionPath", sessionPath),
                ("$entryCursor", entryCursor),
                ("$runId", runEvent.RunId.ToString("D")));

            Execute(
                connection,
                transaction,
                """
                UPDATE tasks
                SET status = $status,
                    updated_at = $timestamp
                WHERE id = $taskId;
                """,
                ("$status", runEvent.Status.ToString()),
                ("$timestamp", timestamp),
                ("$taskId", runEvent.TaskId.ToString("D")));
            MaterializeInteraction(connection, transaction, runEvent);
            transaction.Commit();
        }
    }

    public void UpdateTaskExecutionDefaults(Guid taskId, string model, string thinkingLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(thinkingLevel);
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(
                connection,
                null,
                "UPDATE tasks SET model = $model, thinking_level = $thinkingLevel WHERE id = $taskId AND deleted_at IS NULL;",
                ("$model", model.Trim()),
                ("$thinkingLevel", thinkingLevel.Trim()),
                ("$taskId", taskId.ToString("D")));
        }
    }

    public TaskProjection? RestoreLatestProjection()
    {
        lock (_gate)
        {
            InterruptOrphanedActiveRuns();
            return ReadProjection();
        }
    }

    private void InterruptOrphanedActiveRuns()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT task_id, id, last_event_sequence
            FROM runs
            WHERE status IN ('Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling');
            """;
        var activeRuns = new List<(Guid TaskId, Guid RunId, long LastSequence)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                activeRuns.Add((
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt64(2)));
            }
        }

        foreach (var active in activeRuns)
        {
            AppendRunEvent(new CompanionRunEvent(
                Guid.NewGuid(),
                active.TaskId,
                active.RunId,
                active.LastSequence + 1,
                CompanionRunEventKind.RunInterrupted,
                DateTimeOffset.UtcNow,
                RunStatus.Interrupted,
                new Dictionary<string, string>
                {
                    ["activity"] = "应用关闭时任务仍在进行",
                    ["summary"] = "应用关闭时任务仍在进行，你可以继续提问",
                    ["exitReason"] = "application-restart",
                },
                "pi-companion-recovery-v1"));
        }
    }

    public TaskProjection? RestoreProjection(Guid taskId)
    {
        lock (_gate)
        {
            return ReadProjection(taskId);
        }
    }

    public IReadOnlyList<TaskProjection> RestoreTaskRuns(Guid taskId)
    {
        lock (_gate)
        {
            return ReadTaskRuns(taskId);
        }
    }

    public IReadOnlyList<TaskHistoryEntry> GetRecentTasks(int limit = 20)
    {
        return limit <= 0 ? [] : QueryTasks(new TaskHistoryQuery(Limit: limit));
    }

    public IReadOnlyList<TaskHistoryEntry> QueryTasks(TaskHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        if (query.Limit is <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var sql = new StringBuilder(
                """
                SELECT t.id,
                       COALESCE((
                           SELECT r.id
                           FROM runs r
                           WHERE r.task_id = t.id
                           ORDER BY r.created_at DESC
                           LIMIT 1
                       ), ''),
                       t.title, t.working_directory, t.status, t.summary, t.updated_at, t.deleted_at,
                       t.scope_kind, t.workspace_id
                FROM tasks t
                LEFT JOIN workspaces w ON w.id = t.workspace_id
                WHERE
                """);
            sql.Append(query.IncludeDeleted ? " t.deleted_at IS NOT NULL" : " t.deleted_at IS NULL");
            sql.Append(" AND (t.scope_kind <> 'Workspace' OR w.hidden_at IS NULL)");

            var search = query.Search?.Trim();
            if (!string.IsNullOrWhiteSpace(search))
            {
                sql.Append(
                    " AND (t.title LIKE $search ESCAPE '\\' COLLATE NOCASE" +
                    " OR t.working_directory LIKE $search ESCAPE '\\' COLLATE NOCASE" +
                    " OR t.summary LIKE $search ESCAPE '\\' COLLATE NOCASE" +
                    " OR w.display_name LIKE $search ESCAPE '\\' COLLATE NOCASE)");
                command.Parameters.AddWithValue(
                    "$search",
                    $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
            }

            var statuses = query.Statuses?.Distinct().ToArray() ?? [];
            if (statuses.Length > 0)
            {
                var names = new string[statuses.Length];
                for (var index = 0; index < statuses.Length; index++)
                {
                    names[index] = $"$status{index}";
                    command.Parameters.AddWithValue(names[index], statuses[index].ToString());
                }

                sql.Append($" AND t.status IN ({string.Join(", ", names)})");
            }

            sql.Append(query.IncludeDeleted
                ? " ORDER BY t.deleted_at DESC, t.updated_at DESC, t.id ASC"
                : " ORDER BY t.updated_at DESC, t.id ASC");
            if (query.Limit is { } limit)
            {
                sql.Append(" LIMIT $limit");
                command.Parameters.AddWithValue("$limit", limit);
            }
            else if (query.Offset > 0)
            {
                sql.Append(" LIMIT -1");
            }

            if (query.Offset > 0)
            {
                sql.Append(" OFFSET $offset");
                command.Parameters.AddWithValue("$offset", query.Offset);
            }

            sql.Append(';');
            command.CommandText = sql.ToString();
            using var reader = command.ExecuteReader();
            var tasks = new List<TaskHistoryEntry>();
            while (reader.Read())
            {
                var runIdText = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(runIdText))
                {
                    continue;
                }

                tasks.Add(new TaskHistoryEntry(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(runIdText),
                    reader.GetString(2),
                    reader.GetString(3),
                    Enum.Parse<RunStatus>(reader.GetString(4)),
                    reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    Enum.TryParse<TaskScopeKind>(reader.GetString(8), out var scopeKind)
                        ? scopeKind
                        : TaskScopeKind.Workspace,
                    reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                    string.IsNullOrWhiteSpace(reader.GetString(5))
                        ? AiSummaryStatus.NotRequested
                        : AiSummaryStatus.Available));
            }

            return tasks;
        }
    }

    public WorkspaceHistoryEntry CreateWorkspace(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var workspaceId = EnsureWorkspace(connection, transaction, workingDirectory, now);
            Execute(
                connection,
                transaction,
                """
                UPDATE workspaces
                SET hidden_at = NULL,
                    updated_at = $updatedAt
                WHERE id = $workspaceId;
                """,
                ("$updatedAt", now),
                ("$workspaceId", workspaceId));
            transaction.Commit();
            return ReadWorkspace(connection, workspaceId);
        }
    }

    public IReadOnlyList<WorkspaceHistoryEntry> GetWorkspaces()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT w.id, w.working_directory, w.display_name, w.icon_key, w.color_key, w.created_at,
                       CASE
                           WHEN MAX(t.updated_at) IS NOT NULL
                                AND julianday(MAX(t.updated_at)) > julianday(w.updated_at)
                               THEN MAX(t.updated_at)
                           ELSE w.updated_at
                       END AS activity_at,
                       COUNT(t.id) AS task_count,
                       MAX(CASE
                           WHEN t.status IN ('Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling')
                               THEN 1
                           ELSE 0
                       END) AS has_active_task
                FROM workspaces w
                LEFT JOIN tasks t ON t.workspace_id = w.id AND t.deleted_at IS NULL
                WHERE w.hidden_at IS NULL
                GROUP BY w.id, w.working_directory, w.display_name, w.icon_key, w.color_key, w.created_at, w.updated_at
                ORDER BY julianday(activity_at) DESC, activity_at DESC, w.id ASC;
                """;
            using var reader = command.ExecuteReader();
            var workspaces = new List<WorkspaceHistoryEntry>();
            while (reader.Read())
            {
                workspaces.Add(CreateWorkspaceHistoryEntry(reader));
            }

            return workspaces;
        }
    }

    public WorkspaceHistoryEntry UpdateWorkspacePresentation(
        Guid workspaceId,
        string? displayName,
        string iconKey,
        string colorKey)
    {
        var normalizedDisplayName = NormalizeWorkspaceDisplayName(displayName);
        var normalizedIconKey = NormalizeWorkspaceIconKey(iconKey);
        var normalizedColorKey = NormalizeWorkspaceColorKey(colorKey);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE workspaces
                SET display_name = $displayName,
                    icon_key = $iconKey,
                    color_key = $colorKey,
                    updated_at = $updatedAt
                WHERE id = $workspaceId;
                """;
            command.Parameters.AddWithValue(
                "$displayName",
                normalizedDisplayName is null ? DBNull.Value : normalizedDisplayName);
            command.Parameters.AddWithValue("$iconKey", normalizedIconKey);
            command.Parameters.AddWithValue("$colorKey", normalizedColorKey);
            command.Parameters.AddWithValue(
                "$updatedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$workspaceId", workspaceId.ToString("D"));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("工作区不存在或已不可用。");
            }

            return ReadWorkspace(connection, workspaceId.ToString("D"));
        }
    }

    public void HideWorkspace(Guid workspaceId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var workspace = ReadWorkspace(connection, workspaceId.ToString("D"));
            if (workspace.HasActiveTask)
            {
                throw new InvalidOperationException("工作区仍有运行中的任务，请先停止任务再隐藏。");
            }
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE workspaces
                SET hidden_at = $hiddenAt
                WHERE id = $workspaceId
                  AND hidden_at IS NULL;
                """;
            command.Parameters.AddWithValue(
                "$hiddenAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$workspaceId", workspaceId.ToString("D"));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("工作区不存在或已不可用。");
            }
        }
    }

    public void UpsertTaskArtifact(TaskArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(
                connection,
                null,
                """
                INSERT INTO task_artifacts (
                    id, task_id, run_id, display_name, storage_path, content_type, size, sha256, created_at)
                VALUES (
                    $id, $taskId, $runId, $displayName, $storagePath, $contentType, $size, $sha256, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    display_name = excluded.display_name,
                    storage_path = excluded.storage_path,
                    content_type = excluded.content_type,
                    size = excluded.size,
                    sha256 = excluded.sha256;
                """,
                ("$id", artifact.Id.ToString("D")),
                ("$taskId", artifact.TaskId.ToString("D")),
                ("$runId", artifact.RunId.ToString("D")),
                ("$displayName", artifact.DisplayName),
                ("$storagePath", artifact.StoragePath),
                ("$contentType", artifact.ContentType),
                ("$size", artifact.Size),
                ("$sha256", artifact.Sha256),
                ("$createdAt", artifact.CreatedAt.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public IReadOnlyList<TaskArtifact> GetTaskArtifacts(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, task_id, run_id, display_name, storage_path, content_type, size, sha256, created_at
                FROM task_artifacts
                WHERE task_id = $taskId
                ORDER BY created_at, rowid;
                """;
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            using var reader = command.ExecuteReader();
            var artifacts = new List<TaskArtifact>();
            while (reader.Read())
            {
                artifacts.Add(ReadArtifact(reader));
            }

            return artifacts;
        }
    }

    public TaskArtifact? GetTaskArtifact(Guid artifactId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, task_id, run_id, display_name, storage_path, content_type, size, sha256, created_at
                FROM task_artifacts
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", artifactId.ToString("D"));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadArtifact(reader) : null;
        }
    }

    private static TaskArtifact ReadArtifact(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture));

    public void RenameTask(Guid taskId, string title)
    {
        var normalized = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            throw new ArgumentException("任务名称不能为空。", nameof(title));
        }

        if (normalized.Length > 120)
        {
            throw new ArgumentException("任务名称不能超过 120 个字符。", nameof(title));
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE tasks SET title = $title, updated_at = $updatedAt WHERE id = $taskId AND deleted_at IS NULL;";
            command.Parameters.AddWithValue("$title", normalized);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("未找到可重命名的任务。");
            }
        }
    }

    public void UpdateRunSummary(Guid taskId, Guid runId, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var normalized = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var updatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Execute(
                connection,
                transaction,
                """
                UPDATE runs
                SET ai_summary = $summary
                WHERE id = $runId AND task_id = $taskId
                  AND EXISTS (SELECT 1 FROM tasks WHERE id = $taskId AND deleted_at IS NULL);
                """,
                ("$summary", normalized),
                ("$runId", runId.ToString("D")),
                ("$taskId", taskId.ToString("D")));
            Execute(
                connection,
                transaction,
                """
                UPDATE tasks
                SET summary = $summary,
                    updated_at = $updatedAt
                WHERE id = $taskId AND deleted_at IS NULL
                  AND $runId = (
                      SELECT id FROM runs
                      WHERE task_id = $taskId
                      ORDER BY created_at DESC, rowid DESC
                      LIMIT 1
                  );
                """,
                ("$summary", normalized),
                ("$updatedAt", updatedAt),
                ("$taskId", taskId.ToString("D")),
                ("$runId", runId.ToString("D")));
            transaction.Commit();
        }
    }

    public void MoveTaskToRecycleBin(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var status = ReadTaskStatus(connection, transaction, taskId, includeDeleted: false) ??
                throw new InvalidOperationException("未找到要删除的任务。");
            if (status.IsActive())
            {
                throw new InvalidOperationException("任务仍在运行，停止后才能移入回收站。");
            }

            var deletedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Execute(
                connection,
                transaction,
                "UPDATE tasks SET deleted_at = $deletedAt, updated_at = $deletedAt WHERE id = $taskId;",
                ("$deletedAt", deletedAt),
                ("$taskId", taskId.ToString("D")));
            Execute(
                connection,
                transaction,
                """
                INSERT INTO recycle_bin (task_id, deleted_at, data_json)
                SELECT id, $deletedAt, json_object('title', title, 'workingDirectory', working_directory)
                FROM tasks
                WHERE id = $taskId
                ON CONFLICT(task_id) DO UPDATE SET
                    deleted_at = excluded.deleted_at,
                    data_json = excluded.data_json;
                """,
                ("$deletedAt", deletedAt),
                ("$taskId", taskId.ToString("D")));
            transaction.Commit();
        }
    }

    public void RestoreTaskFromRecycleBin(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE tasks SET deleted_at = NULL, updated_at = $updatedAt WHERE id = $taskId AND deleted_at IS NOT NULL;";
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("未找到可恢复的任务。");
            }

            Execute(
                connection,
                transaction,
                "DELETE FROM recycle_bin WHERE task_id = $taskId;",
                ("$taskId", taskId.ToString("D")));
            transaction.Commit();
        }
    }

    public void DeleteTaskPermanently(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                "DELETE FROM recycle_bin WHERE task_id = $taskId;",
                ("$taskId", taskId.ToString("D")));
            Execute(
                connection,
                transaction,
                """
                DELETE FROM command_executions WHERE run_id IN (SELECT id FROM runs WHERE task_id = $taskId);
                DELETE FROM file_changes WHERE run_id IN (SELECT id FROM runs WHERE task_id = $taskId);
                DELETE FROM test_results WHERE run_id IN (SELECT id FROM runs WHERE task_id = $taskId);
                DELETE FROM warnings WHERE run_id IN (SELECT id FROM runs WHERE task_id = $taskId);
                DELETE FROM recovery_actions WHERE run_id IN (SELECT id FROM runs WHERE task_id = $taskId);
                DELETE FROM run_evidence WHERE task_id = $taskId;
                DELETE FROM tool_calls WHERE run_id IN (SELECT id FROM runs WHERE task_id = $taskId);
                DELETE FROM interaction_requests WHERE task_id = $taskId;
                DELETE FROM messages WHERE task_id = $taskId;
                """,
                ("$taskId", taskId.ToString("D")));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tasks WHERE id = $taskId AND deleted_at IS NOT NULL;";
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("未找到可永久删除的任务。");
            }

            transaction.Commit();
        }
    }

    public void EmptyRecycleBin()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM recycle_bin;");
            Execute(
                connection,
                transaction,
                """
                DELETE FROM command_executions WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL));
                DELETE FROM file_changes WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL));
                DELETE FROM test_results WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL));
                DELETE FROM warnings WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL));
                DELETE FROM recovery_actions WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL));
                DELETE FROM run_evidence WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL);
                DELETE FROM tool_calls WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL));
                DELETE FROM interaction_requests WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL);
                DELETE FROM messages WHERE task_id IN (SELECT id FROM tasks WHERE deleted_at IS NOT NULL);
                """);
            Execute(connection, transaction, "DELETE FROM tasks WHERE deleted_at IS NOT NULL;");
            transaction.Commit();
        }
    }

    public void PurgeExpiredTasks(DateTimeOffset? taskHistoryCutoff, DateTimeOffset? recycleBinCutoff)
    {
        if (taskHistoryCutoff is null && recycleBinCutoff is null)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                """
                CREATE TEMP TABLE IF NOT EXISTS pi_companion_purge_tasks (id TEXT PRIMARY KEY);
                DELETE FROM pi_companion_purge_tasks;
                INSERT OR IGNORE INTO pi_companion_purge_tasks (id)
                SELECT id
                FROM tasks
                WHERE (
                    $recycleCutoff IS NOT NULL AND
                    deleted_at IS NOT NULL AND
                    deleted_at < $recycleCutoff
                ) OR (
                    $historyCutoff IS NOT NULL AND
                    deleted_at IS NULL AND
                    updated_at < $historyCutoff AND
                    status IN ('Completed', 'Failed', 'Interrupted') AND
                    id NOT IN (
                        SELECT id FROM tasks
                        WHERE deleted_at IS NULL
                        ORDER BY updated_at DESC
                        LIMIT 1
                    )
                );

                DELETE FROM command_executions WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks));
                DELETE FROM file_changes WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks));
                DELETE FROM test_results WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks));
                DELETE FROM warnings WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks));
                DELETE FROM recovery_actions WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks));
                DELETE FROM run_evidence WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks);
                DELETE FROM tool_calls WHERE run_id IN (
                    SELECT id FROM runs WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks));
                DELETE FROM interaction_requests WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks);
                DELETE FROM messages WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks);
                DELETE FROM recycle_bin WHERE task_id IN (SELECT id FROM pi_companion_purge_tasks);
                DELETE FROM tasks WHERE id IN (SELECT id FROM pi_companion_purge_tasks);
                DELETE FROM pi_companion_purge_tasks;
                """,
                ("$historyCutoff", taskHistoryCutoff?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$recycleCutoff", recycleBinCutoff?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
            transaction.Commit();
        }
    }

    public string? GetLatestSessionPath(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT pi_session_path
                FROM runs
                WHERE task_id = $taskId AND pi_session_path IS NOT NULL
                ORDER BY created_at DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            return command.ExecuteScalar() as string;
        }
    }

    public string? GetLatestPiEntryCursor(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT pi_entry_cursor
                FROM runs
                WHERE task_id = $taskId AND pi_entry_cursor IS NOT NULL
                ORDER BY created_at DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            return command.ExecuteScalar() as string;
        }
    }

    public IReadOnlyList<PersistedInteractionRequest> GetInteractionRequests(Guid runId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, task_id, run_id, kind, method, title, options_json, status,
                       response, created_at, resolved_at
                FROM interaction_requests
                WHERE run_id = $runId
                ORDER BY created_at, id;
                """;
            command.Parameters.AddWithValue("$runId", runId.ToString("D"));
            using var reader = command.ExecuteReader();
            var interactions = new List<PersistedInteractionRequest>();
            while (reader.Read())
            {
                interactions.Add(new PersistedInteractionRequest(
                    reader.GetString(0),
                    Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions) ?? [],
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                    reader.IsDBNull(10)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
            }

            return interactions;
        }
    }

    public void UpsertRunEvidenceMetadata(RunEvidenceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                """
                INSERT INTO run_evidence (run_id, task_id, data_json, updated_at)
                VALUES ($runId, $taskId, $data, $updatedAt)
                ON CONFLICT(run_id) DO UPDATE SET
                    task_id = excluded.task_id,
                    data_json = excluded.data_json,
                    updated_at = excluded.updated_at;
                """,
                ("$runId", metadata.RunId.ToString("D")),
                ("$taskId", metadata.TaskId.ToString("D")),
                ("$data", JsonSerializer.Serialize(metadata, JsonOptions)),
                ("$updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            transaction.Commit();
        }
    }

    public RunEvidenceMetadata? GetRunEvidenceMetadata(Guid runId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data_json FROM run_evidence WHERE run_id = $runId;";
            command.Parameters.AddWithValue("$runId", runId.ToString("D"));
            return DeserializeRecord<RunEvidenceMetadata>(command.ExecuteScalar() as string);
        }
    }

    public void UpsertFileChange(FileChangeEvidence fileChange) =>
        UpsertEvidenceRecord("file_changes", fileChange.Id, fileChange.RunId, fileChange, fileChange.UpdatedAt);

    public FileChangeEvidence? GetFileChange(Guid fileChangeId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data_json FROM file_changes WHERE id = $id;";
            command.Parameters.AddWithValue("$id", fileChangeId.ToString("D"));
            return DeserializeRecord<FileChangeEvidence>(command.ExecuteScalar() as string);
        }
    }

    public void UpsertCommandExecution(CommandExecutionEvidence command) =>
        UpsertEvidenceRecord("command_executions", command.Id, command.RunId, command, command.StartedAt);

    public void UpsertTestResult(TestResultEvidence testResult) =>
        UpsertEvidenceRecord("test_results", testResult.Id, testResult.RunId, testResult, testResult.CompletedAt);

    public void ReplaceEvidenceWarnings(Guid runId, IReadOnlyList<EvidenceWarning> warnings)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM warnings WHERE run_id = $runId;", ("$runId", runId.ToString("D")));
            foreach (var warning in warnings)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO warnings (id, run_id, data_json, created_at) VALUES ($id, $runId, $data, $createdAt);",
                    ("$id", warning.Id.ToString("D")),
                    ("$runId", warning.RunId.ToString("D")),
                    ("$data", JsonSerializer.Serialize(warning, JsonOptions)),
                    ("$createdAt", warning.CreatedAt.ToString("O", CultureInfo.InvariantCulture)));
            }

            transaction.Commit();
        }
    }

    public void AppendRecoveryAction(RecoveryActionEvidence action) =>
        UpsertEvidenceRecord("recovery_actions", action.Id, action.RunId, action, action.CreatedAt);

    public RunEvidenceSnapshot GetRunEvidence(Guid runId)
    {
        lock (_gate)
        {
            var metadata = GetRunEvidenceMetadata(runId);
            var files = ReadEvidenceRecords<FileChangeEvidence>("file_changes", runId);
            var commands = ReadEvidenceRecords<CommandExecutionEvidence>("command_executions", runId);
            var tests = ReadEvidenceRecords<TestResultEvidence>("test_results", runId);
            var warnings = ReadEvidenceRecords<EvidenceWarning>("warnings", runId);
            var testStatus = tests.Count == 0
                ? TestEvidenceStatus.NotRun
                : tests.Any(test => test.Status == TestEvidenceStatus.Failed)
                    ? TestEvidenceStatus.Failed
                    : tests.Any(test => test.Status == TestEvidenceStatus.Unknown)
                        ? TestEvidenceStatus.Unknown
                        : TestEvidenceStatus.Passed;
            return new RunEvidenceSnapshot(
                runId,
                metadata?.Finalized ?? false,
                metadata?.IsGitRepository ?? false,
                metadata?.GitRoot,
                metadata?.HeadBefore,
                metadata?.HeadAfter,
                testStatus,
                files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                commands.OrderBy(command => command.StartedAt).ToArray(),
                tests.OrderBy(test => test.CompletedAt).ToArray(),
                warnings.OrderBy(warning => warning.CreatedAt).ToArray());
        }
    }

    public string? GetSettingJson(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_json FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public SessionStatisticsCacheEntry? GetSessionStatisticsCache(
        Guid taskId,
        Guid runId,
        long lastSequence)
    {
        if (lastSequence < 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT data_json, updated_at
                FROM session_statistics_cache
                WHERE task_id = $taskId
                  AND run_id = $runId
                  AND last_sequence = $lastSequence;
                """;
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            command.Parameters.AddWithValue("$runId", runId.ToString("D"));
            command.Parameters.AddWithValue("$lastSequence", lastSequence);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            try
            {
                var statistics = JsonSerializer.Deserialize<AgentSessionStatistics>(
                    reader.GetString(0),
                    JsonOptions);
                return statistics is null
                    ? null
                    : new SessionStatisticsCacheEntry(
                        taskId,
                        runId,
                        lastSequence,
                        statistics,
                        DateTimeOffset.Parse(
                            reader.GetString(1),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public void UpsertSessionStatisticsCache(SessionStatisticsCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.TaskId == Guid.Empty || entry.RunId == Guid.Empty)
        {
            throw new ArgumentException("Session statistics cache requires task and run identifiers.", nameof(entry));
        }

        if (entry.LastSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Session statistics cache sequence cannot be negative.");
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO session_statistics_cache (
                    task_id, run_id, last_sequence, data_json, updated_at)
                VALUES ($taskId, $runId, $lastSequence, $dataJson, $updatedAt)
                ON CONFLICT(task_id) DO UPDATE SET
                    run_id = excluded.run_id,
                    last_sequence = excluded.last_sequence,
                    data_json = excluded.data_json,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$taskId", entry.TaskId.ToString("D"));
            command.Parameters.AddWithValue("$runId", entry.RunId.ToString("D"));
            command.Parameters.AddWithValue("$lastSequence", entry.LastSequence);
            command.Parameters.AddWithValue(
                "$dataJson",
                JsonSerializer.Serialize(entry.Statistics, JsonOptions));
            command.Parameters.AddWithValue(
                "$updatedAt",
                entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    public void SetSettingJson(string key, string valueJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueJson);
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO settings (key, value_json, updated_at)
                VALUES ($key, $valueJson, $updatedAt)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$valueJson", valueJson);
            command.Parameters.AddWithValue(
                "$updatedAt",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private void UpsertEvidenceRecord<T>(string tableName, Guid id, Guid runId, T record, DateTimeOffset createdAt)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                $"""
                INSERT INTO {tableName} (id, run_id, data_json, created_at)
                VALUES ($id, $runId, $data, $createdAt)
                ON CONFLICT(id) DO UPDATE SET
                    run_id = excluded.run_id,
                    data_json = excluded.data_json,
                    created_at = excluded.created_at;
                """,
                ("$id", id.ToString("D")),
                ("$runId", runId.ToString("D")),
                ("$data", JsonSerializer.Serialize(record, JsonOptions)),
                ("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture)));
            transaction.Commit();
        }
    }

    private IReadOnlyList<T> ReadEvidenceRecords<T>(string tableName, Guid runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT data_json FROM {tableName} WHERE run_id = $runId ORDER BY created_at, id;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        using var reader = command.ExecuteReader();
        var records = new List<T>();
        while (reader.Read())
        {
            if (DeserializeRecord<T>(reader.GetString(0)) is { } record)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static T? DeserializeRecord<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static void MaterializeInteraction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanionRunEvent runEvent)
    {
        if (runEvent.Kind is CompanionRunEventKind.ApprovalRequested or CompanionRunEventKind.QuestionRequested)
        {
            if (!runEvent.Payload.TryGetValue("interactionId", out var interactionId))
            {
                return;
            }

            Execute(
                connection,
                transaction,
                """
                INSERT INTO interaction_requests (
                    id, task_id, run_id, kind, method, title, options_json, status,
                    response, created_at, resolved_at, data_json)
                VALUES (
                    $id, $taskId, $runId, $kind, $method, $title, $options, 'Pending',
                    NULL, $createdAt, NULL, $dataJson)
                ON CONFLICT(id) DO UPDATE SET
                    task_id = excluded.task_id,
                    run_id = excluded.run_id,
                    kind = excluded.kind,
                    method = excluded.method,
                    title = excluded.title,
                    options_json = excluded.options_json,
                    data_json = excluded.data_json;
                """,
                ("$id", interactionId),
                ("$taskId", runEvent.TaskId.ToString("D")),
                ("$runId", runEvent.RunId.ToString("D")),
                ("$kind", runEvent.Kind == CompanionRunEventKind.ApprovalRequested ? "Approval" : "Question"),
                ("$method", GetPayload(runEvent, "interactionMethod") ?? "unknown"),
                ("$title", GetPayload(runEvent, "activity") ?? "Pi Agent 交互请求"),
                ("$options", GetPayload(runEvent, "interactionOptions") ?? "[]"),
                ("$createdAt", runEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                ("$dataJson", JsonSerializer.Serialize(runEvent.Payload, JsonOptions)));
            return;
        }

        if (runEvent.Kind == CompanionRunEventKind.InteractionResolved &&
            runEvent.Payload.TryGetValue("interactionId", out var resolvedId))
        {
            var approved = !string.Equals(GetPayload(runEvent, "approved"), "false", StringComparison.OrdinalIgnoreCase);
            Execute(
                connection,
                transaction,
                """
                UPDATE interaction_requests
                SET status = $status,
                    response = $response,
                    resolved_at = $resolvedAt
                WHERE id = $id AND run_id = $runId;
                """,
                ("$status", approved ? "Approved" : "Rejected"),
                ("$response", GetPayload(runEvent, "response")),
                ("$resolvedAt", runEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                ("$id", resolvedId),
                ("$runId", runEvent.RunId.ToString("D")));
            return;
        }

        if (runEvent.Kind is CompanionRunEventKind.RunFailed or CompanionRunEventKind.RunInterrupted or CompanionRunEventKind.RunSettled)
        {
            Execute(
                connection,
                transaction,
                """
                UPDATE interaction_requests
                SET status = 'Cancelled', resolved_at = $resolvedAt
                WHERE run_id = $runId AND status = 'Pending';
                """,
                ("$resolvedAt", runEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                ("$runId", runEvent.RunId.ToString("D")));
        }
    }

    private static string? GetPayload(CompanionRunEvent runEvent, string name) =>
        runEvent.Payload.TryGetValue(name, out var value) ? value : null;

    private TaskProjection? ReadProjection(Guid? filterTaskId = null)
    {
        using var connection = OpenConnection();
        var taskId = filterTaskId ?? ReadLatestTaskId(connection);
        if (taskId is null)
        {
            return null;
        }

        return ReadTaskRuns(connection, taskId.Value).LastOrDefault();
    }

    private IReadOnlyList<TaskProjection> ReadTaskRuns(Guid taskId)
    {
        using var connection = OpenConnection();
        return ReadTaskRuns(connection, taskId);
    }

    private static Guid? ReadLatestTaskId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.id
            FROM tasks t
            LEFT JOIN workspaces w ON w.id = t.workspace_id
            WHERE t.deleted_at IS NULL
              AND (t.scope_kind <> 'Workspace' OR w.hidden_at IS NULL)
              AND EXISTS (SELECT 1 FROM runs r WHERE r.task_id = t.id)
            ORDER BY t.updated_at DESC
            LIMIT 1;
            """;
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);
    }

    private static IReadOnlyList<TaskProjection> ReadTaskRuns(SqliteConnection connection, Guid taskId)
    {
        var runs = new List<StoredRun>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT r.id, t.title, t.working_directory,
                       COALESCE(r.model, t.model), COALESCE(r.thinking_level, t.thinking_level),
                       t.permission_mode, r.prompt, r.created_at, r.attachments_snapshot, r.ai_summary,
                       t.model, t.thinking_level, t.scope_kind
                FROM tasks t
                JOIN runs r ON r.task_id = t.id
                WHERE t.deleted_at IS NULL AND t.id = $taskId
                ORDER BY r.created_at, r.rowid;
                """;
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(new StoredRun(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    reader.GetInt64(8) != 0,
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    Enum.TryParse<TaskScopeKind>(reader.GetString(12), out var scopeKind)
                        ? scopeKind
                        : TaskScopeKind.Workspace));
            }
        }

        if (runs.Count == 0)
        {
            return [];
        }

        var attachments = new List<string>();
        using (var attachmentCommand = connection.CreateCommand())
        {
            attachmentCommand.CommandText =
                "SELECT path FROM task_attachments WHERE task_id = $taskId ORDER BY ordinal;";
            attachmentCommand.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            using var attachmentReader = attachmentCommand.ExecuteReader();
            while (attachmentReader.Read())
            {
                attachments.Add(attachmentReader.GetString(0));
            }
        }

        var projections = new List<TaskProjection>(runs.Count);
        foreach (var run in runs)
        {
            var runAttachments = run.HasAttachmentSnapshot
                ? ReadRunAttachments(connection, run.RunId)
                : attachments;
            var projection = new TaskProjection(
                taskId,
                run.RunId,
                run.Title,
                run.WorkingDirectory,
                run.Model,
                run.ThinkingLevel,
                runAttachments,
                run.Prompt,
                run.CreatedAt,
                run.PermissionMode,
                run.PreferredModel,
                run.PreferredThinkingLevel,
                run.ScopeKind);
            using var eventCommand = connection.CreateCommand();
            eventCommand.CommandText =
                """
                SELECT event_id, sequence, kind, timestamp, status, payload_json, source_version
                FROM run_events
                WHERE run_id = $runId
                ORDER BY sequence;
                """;
            eventCommand.Parameters.AddWithValue("$runId", run.RunId.ToString("D"));
            using var eventReader = eventCommand.ExecuteReader();
            while (eventReader.Read())
            {
                var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    eventReader.GetString(5),
                    JsonOptions) ?? [];
                projection.Apply(new CompanionRunEvent(
                    Guid.Parse(eventReader.GetString(0)),
                    taskId,
                    run.RunId,
                    eventReader.GetInt64(1),
                    Enum.Parse<CompanionRunEventKind>(eventReader.GetString(2)),
                    DateTimeOffset.Parse(eventReader.GetString(3), CultureInfo.InvariantCulture),
                    Enum.Parse<RunStatus>(eventReader.GetString(4)),
                    payload,
                    eventReader.GetString(6)));
            }

            if (!string.IsNullOrWhiteSpace(run.AiSummary))
            {
                projection.SetSummary(run.AiSummary);
            }

            projections.Add(projection);
        }

        var artifacts = ReadTaskArtifacts(connection, taskId);
        foreach (var projection in projections)
        {
            projection.RestoreArtifacts(artifacts);
        }

        return projections;
    }

    private static IReadOnlyList<TaskArtifact> ReadTaskArtifacts(SqliteConnection connection, Guid taskId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, task_id, run_id, display_name, storage_path, content_type, size, sha256, created_at
            FROM task_artifacts
            WHERE task_id = $taskId
            ORDER BY created_at, rowid;
            """;
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        using var reader = command.ExecuteReader();
        var artifacts = new List<TaskArtifact>();
        while (reader.Read())
        {
            artifacts.Add(ReadArtifact(reader));
        }

        return artifacts;
    }

    private static IReadOnlyList<string> ReadRunAttachments(SqliteConnection connection, Guid runId)
    {
        var attachments = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM run_attachments WHERE run_id = $runId ORDER BY ordinal;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            attachments.Add(reader.GetString(0));
        }

        return attachments;
    }

    private sealed record StoredRun(
        Guid RunId,
        string Title,
        string WorkingDirectory,
        string Model,
        string ThinkingLevel,
        string PermissionMode,
        string Prompt,
        DateTimeOffset CreatedAt,
        bool HasAttachmentSnapshot,
        string? AiSummary,
        string PreferredModel,
        string PreferredThinkingLevel,
        TaskScopeKind ScopeKind);

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;

                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS workspaces (
                    id TEXT PRIMARY KEY,
                    working_directory TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    display_name TEXT NULL,
                    icon_key TEXT NOT NULL DEFAULT 'folder',
                    color_key TEXT NOT NULL DEFAULT 'blue',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    hidden_at TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    workspace_id TEXT NULL REFERENCES workspaces(id),
                    title TEXT NOT NULL,
                    working_directory TEXT NOT NULL,
                    model TEXT NOT NULL,
                    thinking_level TEXT NOT NULL,
                    permission_mode TEXT NOT NULL DEFAULT 'standard',
                    scope_kind TEXT NOT NULL DEFAULT 'Workspace',
                    status TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS task_attachments (
                    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                    ordinal INTEGER NOT NULL,
                    path TEXT NOT NULL,
                    PRIMARY KEY (task_id, ordinal)
                );
                CREATE TABLE IF NOT EXISTS task_artifacts (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                    run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                    display_name TEXT NOT NULL,
                    storage_path TEXT NOT NULL,
                    content_type TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_task_artifacts_task ON task_artifacts(task_id, created_at);
                CREATE INDEX IF NOT EXISTS ix_task_artifacts_run ON task_artifacts(run_id, created_at);
                CREATE TABLE IF NOT EXISTS runs (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                    prompt TEXT NOT NULL,
                    model TEXT NULL,
                    thinking_level TEXT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    started_at TEXT NULL,
                    settled_at TEXT NULL,
                    exit_reason TEXT NULL,
                    last_event_sequence INTEGER NOT NULL DEFAULT 0,
                    pi_session_id TEXT NULL,
                    pi_session_path TEXT NULL,
                    pi_entry_cursor TEXT NULL,
                    attachments_snapshot INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS run_attachments (
                    run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                    ordinal INTEGER NOT NULL,
                    path TEXT NOT NULL,
                    PRIMARY KEY (run_id, ordinal)
                );
                CREATE TABLE IF NOT EXISTS run_events (
                    event_id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                    run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                    sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    status TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    source_version TEXT NOT NULL,
                    UNIQUE (run_id, sequence)
                );
                CREATE INDEX IF NOT EXISTS ix_run_events_run_sequence ON run_events(run_id, sequence);
                CREATE INDEX IF NOT EXISTS ix_runs_task_created ON runs(task_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS messages (id TEXT PRIMARY KEY, task_id TEXT, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE TABLE IF NOT EXISTS tool_calls (id TEXT PRIMARY KEY, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE TABLE IF NOT EXISTS run_evidence (
                    run_id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    data_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS command_executions (id TEXT PRIMARY KEY, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE TABLE IF NOT EXISTS interaction_requests (
                    id TEXT PRIMARY KEY,
                    task_id TEXT,
                    run_id TEXT,
                    kind TEXT,
                    method TEXT,
                    title TEXT,
                    options_json TEXT,
                    status TEXT,
                    response TEXT,
                    created_at TEXT,
                    resolved_at TEXT,
                    data_json TEXT
                );
                CREATE TABLE IF NOT EXISTS file_changes (id TEXT PRIMARY KEY, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE TABLE IF NOT EXISTS test_results (id TEXT PRIMARY KEY, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE TABLE IF NOT EXISTS warnings (id TEXT PRIMARY KEY, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE TABLE IF NOT EXISTS recovery_actions (id TEXT PRIMARY KEY, run_id TEXT, data_json TEXT, created_at TEXT);
                CREATE INDEX IF NOT EXISTS ix_file_changes_run ON file_changes(run_id, created_at);
                CREATE INDEX IF NOT EXISTS ix_command_executions_run ON command_executions(run_id, created_at);
                CREATE INDEX IF NOT EXISTS ix_test_results_run ON test_results(run_id, created_at);
                CREATE INDEX IF NOT EXISTS ix_warnings_run ON warnings(run_id, created_at);
                CREATE INDEX IF NOT EXISTS ix_recovery_actions_run ON recovery_actions(run_id, created_at);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS recycle_bin (task_id TEXT PRIMARY KEY, deleted_at TEXT NOT NULL, data_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS session_statistics_cache (
                    task_id TEXT PRIMARY KEY REFERENCES tasks(id) ON DELETE CASCADE,
                    run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                    last_sequence INTEGER NOT NULL,
                    data_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "interaction_requests", "task_id", "TEXT");
            EnsureColumn(connection, "interaction_requests", "kind", "TEXT");
            EnsureColumn(connection, "interaction_requests", "method", "TEXT");
            EnsureColumn(connection, "interaction_requests", "title", "TEXT");
            EnsureColumn(connection, "interaction_requests", "options_json", "TEXT");
            EnsureColumn(connection, "interaction_requests", "status", "TEXT");
            EnsureColumn(connection, "interaction_requests", "response", "TEXT");
            EnsureColumn(connection, "interaction_requests", "resolved_at", "TEXT");
            EnsureColumn(connection, "runs", "model", "TEXT");
            EnsureColumn(connection, "runs", "thinking_level", "TEXT");
            EnsureColumn(connection, "runs", "attachments_snapshot", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "ai_summary", "TEXT");
            EnsureColumn(connection, "runs", "pi_entry_cursor", "TEXT");
            EnsureColumn(connection, "tasks", "permission_mode", "TEXT NOT NULL DEFAULT 'standard'");
            EnsureColumn(connection, "tasks", "scope_kind", "TEXT NOT NULL DEFAULT 'Workspace'");
            EnsureColumn(connection, "tasks", "workspace_id", "TEXT");
            EnsureColumn(connection, "workspaces", "display_name", "TEXT");
            EnsureColumn(connection, "workspaces", "icon_key", "TEXT NOT NULL DEFAULT 'folder'");
            EnsureColumn(connection, "workspaces", "color_key", "TEXT NOT NULL DEFAULT 'blue'");
            EnsureColumn(connection, "workspaces", "hidden_at", "TEXT");

            using var migration = connection.CreateCommand();
            migration.CommandText =
                """
                UPDATE interaction_requests
                SET task_id = COALESCE(task_id, ''),
                    kind = COALESCE(kind, 'Unknown'),
                    method = COALESCE(method, 'unknown'),
                    title = COALESCE(title, 'Legacy interaction'),
                    options_json = COALESCE(options_json, '[]'),
                    status = COALESCE(status, 'Unknown'),
                    created_at = COALESCE(created_at, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                DELETE FROM run_events
                WHERE status = 'Acknowledged';

                UPDATE run_events
                SET status = CASE status
                    WHEN 'CompletedUnacknowledged' THEN 'Completed'
                    WHEN 'FailedUnacknowledged' THEN 'Failed'
                    ELSE status
                END
                WHERE status IN ('CompletedUnacknowledged', 'FailedUnacknowledged');

                UPDATE runs
                SET status = COALESCE((
                        SELECT e.status
                        FROM run_events e
                        WHERE e.run_id = runs.id
                        ORDER BY e.sequence DESC
                        LIMIT 1
                    ), CASE status
                        WHEN 'CompletedUnacknowledged' THEN 'Completed'
                        WHEN 'FailedUnacknowledged' THEN 'Failed'
                        WHEN 'Acknowledged' THEN 'Completed'
                        ELSE status
                    END),
                    last_event_sequence = COALESCE((
                        SELECT MAX(e.sequence)
                        FROM run_events e
                        WHERE e.run_id = runs.id
                    ), 0),
                    settled_at = COALESCE((
                        SELECT e.timestamp
                        FROM run_events e
                        WHERE e.run_id = runs.id
                          AND e.status IN ('Completed', 'Failed', 'Interrupted')
                        ORDER BY e.sequence DESC
                        LIMIT 1
                    ), settled_at)
                WHERE status IN ('Acknowledged', 'CompletedUnacknowledged', 'FailedUnacknowledged');

                UPDATE tasks
                SET status = COALESCE((
                        SELECT r.status
                        FROM runs r
                        WHERE r.task_id = tasks.id
                        ORDER BY r.created_at DESC
                        LIMIT 1
                    ), CASE status
                        WHEN 'CompletedUnacknowledged' THEN 'Completed'
                        WHEN 'FailedUnacknowledged' THEN 'Failed'
                        WHEN 'Acknowledged' THEN 'Completed'
                        ELSE status
                    END),
                    updated_at = COALESCE((
                        SELECT e.timestamp
                        FROM run_events e
                        JOIN runs r ON r.id = e.run_id
                        WHERE r.task_id = tasks.id
                        ORDER BY r.created_at DESC, e.sequence DESC
                        LIMIT 1
                    ), updated_at)
                WHERE status IN ('Acknowledged', 'CompletedUnacknowledged', 'FailedUnacknowledged');

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                UPDATE runs
                SET model = COALESCE(model, (
                        SELECT t.model FROM tasks t WHERE t.id = runs.task_id
                    )),
                    thinking_level = COALESCE(thinking_level, (
                        SELECT t.thinking_level FROM tasks t WHERE t.id = runs.task_id
                    ));

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                INSERT OR IGNORE INTO run_attachments (run_id, ordinal, path)
                SELECT r.id, a.ordinal, a.path
                FROM runs r
                JOIN task_attachments a ON a.task_id = r.task_id
                WHERE r.attachments_snapshot = 0;

                UPDATE runs
                SET attachments_snapshot = 1
                WHERE attachments_snapshot = 0;

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (6, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                UPDATE tasks
                SET updated_at = COALESCE((
                    SELECT activity.timestamp
                    FROM (
                        SELECT r.created_at AS timestamp
                        FROM runs r
                        WHERE r.task_id = tasks.id
                        UNION ALL
                        SELECT e.timestamp
                        FROM run_events e
                        WHERE e.task_id = tasks.id
                        UNION ALL
                        SELECT tasks.created_at
                    ) activity
                    ORDER BY julianday(activity.timestamp) DESC, activity.timestamp DESC
                    LIMIT 1
                ), created_at)
                WHERE deleted_at IS NULL
                  AND NOT EXISTS (SELECT 1 FROM schema_migrations WHERE version = 7);

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (8, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (9, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (10, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (11, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

                UPDATE tasks
                SET summary = COALESCE((
                    SELECT r.ai_summary
                    FROM runs r
                    WHERE r.task_id = tasks.id
                    ORDER BY r.created_at DESC, r.rowid DESC
                    LIMIT 1
                ), '')
                WHERE NOT EXISTS (SELECT 1 FROM schema_migrations WHERE version = 12);

                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (12, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            migration.ExecuteNonQuery();
            BackfillTaskWorkspaces(connection);
            Execute(
                connection,
                null,
                """
                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (13, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """);
            Execute(
                connection,
                null,
                """
                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (14, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """);
            Execute(
                connection,
                null,
                """
                INSERT OR IGNORE INTO schema_migrations (version, applied_at)
                VALUES (15, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """);
        }
    }

    private static string EnsureWorkspace(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string workingDirectory,
        string timestamp)
    {
        var normalized = NormalizeWorkspaceDirectory(workingDirectory);
        var workspaceId = Guid.NewGuid().ToString("D");
        Execute(
            connection,
            transaction,
            """
            INSERT INTO workspaces (id, working_directory, created_at, updated_at)
            VALUES ($id, $workingDirectory, $timestamp, $timestamp)
            ON CONFLICT(working_directory) DO NOTHING;
            """,
            ("$id", workspaceId),
            ("$workingDirectory", normalized),
            ("$timestamp", timestamp));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM workspaces WHERE working_directory = $workingDirectory COLLATE NOCASE;";
        command.Parameters.AddWithValue("$workingDirectory", normalized);
        return command.ExecuteScalar() as string ??
            throw new InvalidOperationException("无法创建或读取工作区。");
    }

    private static WorkspaceHistoryEntry ReadWorkspace(
        SqliteConnection connection,
        string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT w.id, w.working_directory, w.display_name, w.icon_key, w.color_key, w.created_at,
                   CASE
                       WHEN MAX(t.updated_at) IS NOT NULL
                            AND julianday(MAX(t.updated_at)) > julianday(w.updated_at)
                           THEN MAX(t.updated_at)
                       ELSE w.updated_at
                   END AS activity_at,
                   COUNT(t.id) AS task_count,
                   MAX(CASE
                       WHEN t.status IN ('Queued', 'Starting', 'Running', 'WaitingForApproval', 'WaitingForAnswer', 'Cancelling')
                           THEN 1
                       ELSE 0
                   END) AS has_active_task
            FROM workspaces w
            LEFT JOIN tasks t ON t.workspace_id = w.id AND t.deleted_at IS NULL
            WHERE w.id = $workspaceId
            GROUP BY w.id, w.working_directory, w.display_name, w.icon_key, w.color_key, w.created_at, w.updated_at;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? CreateWorkspaceHistoryEntry(reader)
            : throw new InvalidOperationException("工作区创建后无法读取。");
    }

    private static WorkspaceHistoryEntry CreateWorkspaceHistoryEntry(SqliteDataReader reader)
    {
        var workingDirectory = reader.GetString(1);
        var fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory));
        var displayName = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new WorkspaceHistoryEntry(
            Guid.Parse(reader.GetString(0)),
            string.IsNullOrWhiteSpace(displayName)
                ? string.IsNullOrWhiteSpace(fallbackName) ? workingDirectory : fallbackName
                : displayName,
            workingDirectory,
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.GetInt32(7),
            reader.GetInt32(8) != 0,
            reader.IsDBNull(3) ? "folder" : reader.GetString(3),
            reader.IsDBNull(4) ? "blue" : reader.GetString(4),
            displayName);
    }

    private static string? NormalizeWorkspaceDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var normalized = string.Join(
            ' ',
            displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 60)
        {
            throw new ArgumentException("工作区显示名称不能超过 60 个字符。", nameof(displayName));
        }

        return normalized;
    }

    private static string NormalizeWorkspaceIconKey(string iconKey)
    {
        var normalized = iconKey?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "folder" or "code" or "terminal" or "book" or
            "globe" or "flask" or "database" or "app"
            ? normalized
            : throw new ArgumentException("不支持的工作区图标。", nameof(iconKey));
    }

    private static string NormalizeWorkspaceColorKey(string colorKey)
    {
        var normalized = colorKey?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "blue" or "indigo" or "violet" or "pink" or
            "red" or "orange" or "green" or "teal"
            ? normalized
            : throw new ArgumentException("不支持的工作区图标颜色。", nameof(colorKey));
    }

    private static string NormalizeWorkspaceDirectory(string workingDirectory)
    {
        var fullPath = Path.GetFullPath(workingDirectory);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void BackfillTaskWorkspaces(SqliteConnection connection)
    {
        var tasks = new List<(string TaskId, string WorkingDirectory, string Timestamp)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, working_directory, created_at
                FROM tasks
                WHERE scope_kind = 'Workspace'
                  AND length(trim(working_directory)) > 0
                  AND workspace_id IS NULL;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (tasks.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var task in tasks)
        {
            string workspaceId;
            try
            {
                workspaceId = EnsureWorkspace(
                    connection,
                    transaction,
                    task.WorkingDirectory,
                    task.Timestamp);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            Execute(
                connection,
                transaction,
                "UPDATE tasks SET workspace_id = $workspaceId WHERE id = $taskId;",
                ("$workspaceId", workspaceId),
                ("$taskId", task.TaskId));
        }

        transaction.Commit();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string declaration)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {declaration};";
        alter.ExecuteNonQuery();
    }

    private static RunStatus? ReadTaskStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        bool includeDeleted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = includeDeleted
            ? "SELECT status FROM tasks WHERE id = $taskId;"
            : "SELECT status FROM tasks WHERE id = $taskId AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<RunStatus>(value);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(
        SqliteCommand command,
        params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }
}
