using System.Text.Json;
using PiCompanion.Application.Demo;
using PiCompanion.Application.Persistence;
using PiCompanion.Core.Tasks;

namespace PiCompanion.Application.Tasks;

public sealed class ScheduledTaskService : IDisposable
{
    private static readonly TimeSpan MaximumMissedWindow = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TaskCoordinator _coordinator;
    private readonly IRunEventStore _store;
    private readonly Func<string> _defaultModel;
    private readonly Func<string> _defaultThinkingLevel;
    private readonly Func<string> _defaultPermissionMode;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private Task? _worker;

    public ScheduledTaskService(
        TaskCoordinator coordinator,
        IRunEventStore store,
        Func<string> defaultModel,
        Func<string> defaultThinkingLevel,
        Func<string> defaultPermissionMode,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _store = store;
        _defaultModel = defaultModel;
        _defaultThinkingLevel = defaultThinkingLevel;
        _defaultPermissionMode = defaultPermissionMode;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store.RecoverDispatchingScheduledTaskOccurrences();
        _coordinator.ScheduledTasksChanged += Wake;
    }

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = Task.Run(() => RunAsync(_lifetime.Token), _lifetime.Token);
    }

    public Task RunNowAsync(Guid scheduledTaskId, CancellationToken cancellationToken = default)
    {
        var scheduledTask = _coordinator.ScheduledTasks.FirstOrDefault(task => task.Id == scheduledTaskId)
            ?? throw new InvalidOperationException("定时任务不存在或已被删除。");
        return Task.Run(
            () => ExecuteOccurrenceAsync(
                scheduledTask,
                _timeProvider.GetUtcNow(),
                advanceSchedule: false,
                enforceMissedWindow: false,
                cancellationToken),
            cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = _timeProvider.GetUtcNow();
                var due = _coordinator.ScheduledTasks
                    .Where(task => task.IsEnabled && task.NextRunAt is not null && task.NextRunAt <= now)
                    .OrderBy(task => task.NextRunAt)
                    .ToArray();
                foreach (var scheduledTask in due)
                {
                    if (scheduledTask.NextRunAt is { } scheduledFor)
                    {
                        await ExecuteOccurrenceAsync(
                            scheduledTask,
                            scheduledFor,
                            advanceSchedule: true,
                            enforceMissedWindow: true,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                var nextRunAt = _coordinator.ScheduledTasks
                    .Where(task => task.IsEnabled && task.NextRunAt is not null)
                    .Select(task => task.NextRunAt!.Value)
                    .DefaultIfEmpty(now.AddHours(12))
                    .Min();
                var delay = nextRunAt - _timeProvider.GetUtcNow();
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
                if (delay > TimeSpan.FromHours(12))
                {
                    delay = TimeSpan.FromHours(12);
                }

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delayTask = Task.Delay(delay, _timeProvider, waitCancellation.Token);
                var wakeTask = _wakeSignal.WaitAsync(waitCancellation.Token);
                _ = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
                waitCancellation.Cancel();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ExecuteOccurrenceAsync(
        ScheduledTask scheduledTask,
        DateTimeOffset scheduledFor,
        bool advanceSchedule,
        bool enforceMissedWindow,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var nextRunAt = advanceSchedule && scheduledTask.Frequency != ScheduledTaskFrequency.Once
            ? ScheduledTaskRecurrence.GetNextOccurrence(scheduledTask, now)
            : scheduledTask.NextRunAt;
        var disableAfterOccurrence = advanceSchedule && scheduledTask.Frequency == ScheduledTaskFrequency.Once;

        if (enforceMissedWindow && now - scheduledFor > MaximumMissedWindow)
        {
            RecordTerminalOccurrence(
                scheduledTask,
                scheduledFor,
                ScheduledTaskOccurrenceStatus.Skipped,
                "错过运行时间超过 24 小时，已跳过。",
                nextRunAt,
                disableAfterOccurrence,
                advanceSchedule);
            return;
        }

        if (_store.HasActiveScheduledTaskOccurrence(scheduledTask.Id))
        {
            RecordTerminalOccurrence(
                scheduledTask,
                scheduledFor,
                ScheduledTaskOccurrenceStatus.Skipped,
                "上一次运行仍处于活动状态，已跳过本次运行。",
                nextRunAt,
                disableAfterOccurrence,
                advanceSchedule);
            return;
        }

        ResolvedScheduledTaskDraft resolved;
        try
        {
            resolved = ResolveDraft(scheduledTask);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            RecordTerminalOccurrence(
                scheduledTask,
                scheduledFor,
                ScheduledTaskOccurrenceStatus.Failed,
                exception.Message,
                nextRunAt: null,
                disableSchedule: true,
                advanceSchedule: true);
            return;
        }

        var occurrence = new ScheduledTaskOccurrence(
            Guid.NewGuid(),
            scheduledTask.Id,
            scheduledFor,
            ScheduledTaskOccurrenceStatus.Dispatching,
            null,
            null,
            null,
            JsonSerializer.Serialize(resolved, JsonOptions),
            now,
            now);
        if (_store.BeginScheduledTaskOccurrence(
                occurrence,
                nextRunAt,
                disableAfterOccurrence,
                advanceSchedule) is null)
        {
            return;
        }

        try
        {
            var projection = await _coordinator.StartBackgroundTaskAsync(
                resolved.Prompt,
                resolved.WorkingDirectory,
                resolved.Model,
                resolved.ThinkingLevel,
                DemoRunMode.InteractiveSuccess,
                cancellationToken,
                resolved.PermissionMode,
                resolved.ScopeKind).ConfigureAwait(false);
            _store.CompleteScheduledTaskOccurrence(
                occurrence.Id,
                ScheduledTaskOccurrenceStatus.Enqueued,
                projection.TaskId,
                projection.RunId,
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _store.CompleteScheduledTaskOccurrence(
                occurrence.Id,
                ScheduledTaskOccurrenceStatus.Failed,
                null,
                null,
                exception.Message);
        }
        finally
        {
            _coordinator.NotifyScheduledTasksChanged();
        }
    }

    private void RecordTerminalOccurrence(
        ScheduledTask scheduledTask,
        DateTimeOffset scheduledFor,
        ScheduledTaskOccurrenceStatus status,
        string error,
        DateTimeOffset? nextRunAt,
        bool disableSchedule,
        bool advanceSchedule)
    {
        var now = _timeProvider.GetUtcNow();
        _store.BeginScheduledTaskOccurrence(
            new ScheduledTaskOccurrence(
                Guid.NewGuid(),
                scheduledTask.Id,
                scheduledFor,
                status,
                null,
                null,
                error,
                null,
                now,
                now),
            nextRunAt,
            disableSchedule,
            advanceSchedule);
        _coordinator.NotifyScheduledTasksChanged();
    }

    private ResolvedScheduledTaskDraft ResolveDraft(ScheduledTask scheduledTask)
    {
        var prompt = scheduledTask.Prompt;
        var targetKind = scheduledTask.TargetKind;
        var workspaceId = scheduledTask.WorkspaceId;
        var model = scheduledTask.Model;
        var thinkingLevel = scheduledTask.ThinkingLevel;
        var permissionMode = scheduledTask.PermissionMode;
        DateTimeOffset? templateUpdatedAt = null;
        if (scheduledTask.TemplateId is { } templateId)
        {
            var template = _coordinator.TaskTemplates.FirstOrDefault(candidate => candidate.Id == templateId)
                ?? throw new InvalidOperationException("关联的任务模板不存在或已被删除。");
            prompt = template.Prompt;
            targetKind = template.TargetKind;
            workspaceId = template.WorkspaceId;
            model = template.Model;
            thinkingLevel = template.ThinkingLevel;
            permissionMode = template.PermissionMode;
            templateUpdatedAt = template.UpdatedAt;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("定时任务内容为空。");
        }

        string? workingDirectory = null;
        TaskScopeKind scopeKind;
        if (targetKind == TaskTemplateTargetKind.GeneralChat)
        {
            scopeKind = TaskScopeKind.GeneralChat;
            permissionMode = "standard";
        }
        else if (targetKind == TaskTemplateTargetKind.Workspace && workspaceId is { } resolvedWorkspaceId)
        {
            var workspace = _coordinator.Workspaces.FirstOrDefault(candidate => candidate.Id == resolvedWorkspaceId)
                ?? throw new InvalidOperationException("定时任务绑定的工作区不存在或已不可用。");
            workingDirectory = workspace.WorkingDirectory;
            if (!Directory.Exists(workingDirectory))
            {
                throw new InvalidOperationException("定时任务绑定的工作目录不存在。");
            }
            scopeKind = TaskScopeKind.Workspace;
            permissionMode ??= _defaultPermissionMode();
        }
        else
        {
            throw new InvalidOperationException("关联模板没有确定的运行位置。");
        }

        model = string.IsNullOrWhiteSpace(model) ? _defaultModel() : model;
        thinkingLevel = string.IsNullOrWhiteSpace(thinkingLevel) ? _defaultThinkingLevel() : thinkingLevel;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("当前没有可用于定时任务的默认模型。");
        }

        return new ResolvedScheduledTaskDraft(
            prompt.Trim(),
            workingDirectory,
            model.Trim(),
            string.IsNullOrWhiteSpace(thinkingLevel) ? "high" : thinkingLevel.Trim(),
            permissionMode ?? "standard",
            scopeKind,
            scheduledTask.TemplateId,
            templateUpdatedAt,
            scheduledTask.UpdatedAt);
    }

    private void Wake()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    public void Dispose()
    {
        _coordinator.ScheduledTasksChanged -= Wake;
        _lifetime.Cancel();
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        if (_worker?.IsCompleted != false)
        {
            _wakeSignal.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed record ResolvedScheduledTaskDraft(
        string Prompt,
        string? WorkingDirectory,
        string Model,
        string ThinkingLevel,
        string PermissionMode,
        TaskScopeKind ScopeKind,
        Guid? TemplateId,
        DateTimeOffset? TemplateUpdatedAt,
        DateTimeOffset ScheduledTaskUpdatedAt);
}
