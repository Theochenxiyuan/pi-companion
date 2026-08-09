namespace PiCompanion.Core.Tasks;

public enum ScheduledTaskFrequency
{
    Once,
    Daily,
    Weekdays,
    Weekly,
}

[Flags]
public enum ScheduledDaysOfWeek
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
}

public enum ScheduledTaskOccurrenceStatus
{
    Dispatching,
    Enqueued,
    Skipped,
    Failed,
}

public sealed record ScheduledTask(
    Guid Id,
    string Name,
    bool IsEnabled,
    Guid? TemplateId,
    string? Prompt,
    TaskTemplateTargetKind? TargetKind,
    Guid? WorkspaceId,
    string? Model,
    string? ThinkingLevel,
    string? PermissionMode,
    ScheduledTaskFrequency Frequency,
    DateTime LocalStartAt,
    ScheduledDaysOfWeek DaysOfWeek,
    string TimeZoneId,
    DateTimeOffset? NextRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ScheduledTaskOccurrence? LastOccurrence = null);

public sealed record ScheduledTaskOccurrence(
    Guid Id,
    Guid ScheduledTaskId,
    DateTimeOffset ScheduledFor,
    ScheduledTaskOccurrenceStatus Status,
    Guid? TaskId,
    Guid? RunId,
    string? Error,
    string? ResolvedDraftJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class ScheduledTaskRules
{
    public const int MaximumNameLength = 80;
    public const int MaximumPromptLength = TaskTemplateRules.MaximumPromptLength;
    private static readonly HashSet<string> ThinkingLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static ScheduledTask Normalize(ScheduledTask scheduledTask)
    {
        ArgumentNullException.ThrowIfNull(scheduledTask);
        var name = string.Join(' ', (scheduledTask.Name ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            throw new ArgumentException("定时任务名称不能为空。", nameof(scheduledTask));
        }

        if (name.Length > MaximumNameLength)
        {
            throw new ArgumentException($"定时任务名称不能超过 {MaximumNameLength} 个字符。", nameof(scheduledTask));
        }

        if (!Enum.IsDefined(scheduledTask.Frequency))
        {
            throw new ArgumentException("定时任务频率无效。", nameof(scheduledTask));
        }

        if (scheduledTask.LocalStartAt == default)
        {
            throw new ArgumentException("定时任务开始时间不能为空。", nameof(scheduledTask));
        }

        try
        {
            _ = ScheduledTaskRecurrence.ResolveTimeZone(scheduledTask.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ArgumentException("定时任务时区无效。", nameof(scheduledTask));
        }
        catch (InvalidTimeZoneException)
        {
            throw new ArgumentException("定时任务时区无效。", nameof(scheduledTask));
        }

        var daysOfWeek = scheduledTask.Frequency == ScheduledTaskFrequency.Weekly
            ? scheduledTask.DaysOfWeek & AllDays
            : ScheduledDaysOfWeek.None;
        if (scheduledTask.Frequency == ScheduledTaskFrequency.Weekly && daysOfWeek == ScheduledDaysOfWeek.None)
        {
            throw new ArgumentException("每周运行的定时任务必须至少选择一天。", nameof(scheduledTask));
        }

        var model = NormalizeOptional(scheduledTask.Model);
        var thinkingLevel = NormalizeOptional(scheduledTask.ThinkingLevel)?.ToLowerInvariant();
        if (thinkingLevel is not null && !ThinkingLevels.Contains(thinkingLevel))
        {
            throw new ArgumentException("定时任务推理等级无效。", nameof(scheduledTask));
        }

        var permissionMode = NormalizeOptional(scheduledTask.PermissionMode);
        if (permissionMode is not null && permissionMode is not ("read-only" or "standard"))
        {
            throw new ArgumentException("定时任务权限只能设为只读或标准访问。", nameof(scheduledTask));
        }

        string? prompt = null;
        TaskTemplateTargetKind? targetKind = null;
        Guid? workspaceId = null;
        if (scheduledTask.TemplateId is null)
        {
            prompt = scheduledTask.Prompt?.Trim() ?? string.Empty;
            if (prompt.Length == 0)
            {
                throw new ArgumentException("定时任务内容不能为空。", nameof(scheduledTask));
            }

            if (prompt.Length > MaximumPromptLength)
            {
                throw new ArgumentException($"定时任务内容不能超过 {MaximumPromptLength} 个字符。", nameof(scheduledTask));
            }

            targetKind = scheduledTask.TargetKind;
            if (targetKind is not (TaskTemplateTargetKind.Workspace or TaskTemplateTargetKind.GeneralChat))
            {
                throw new ArgumentException("定时任务必须指定工作区或直接对话。", nameof(scheduledTask));
            }

            workspaceId = targetKind == TaskTemplateTargetKind.Workspace
                ? scheduledTask.WorkspaceId ?? throw new ArgumentException("工作区定时任务必须绑定具体工作区。", nameof(scheduledTask))
                : null;
            if (targetKind == TaskTemplateTargetKind.GeneralChat)
            {
                permissionMode = null;
            }
        }
        else
        {
            model = null;
            thinkingLevel = null;
            permissionMode = null;
        }

        var now = DateTimeOffset.UtcNow;
        return scheduledTask with
        {
            Id = scheduledTask.Id == Guid.Empty ? Guid.NewGuid() : scheduledTask.Id,
            Name = name,
            Prompt = prompt,
            TargetKind = targetKind,
            WorkspaceId = workspaceId,
            Model = model,
            ThinkingLevel = thinkingLevel,
            PermissionMode = permissionMode,
            LocalStartAt = DateTime.SpecifyKind(scheduledTask.LocalStartAt, DateTimeKind.Unspecified),
            DaysOfWeek = daysOfWeek,
            TimeZoneId = scheduledTask.TimeZoneId.Trim(),
            CreatedAt = scheduledTask.CreatedAt == default ? now : scheduledTask.CreatedAt,
            UpdatedAt = scheduledTask.UpdatedAt == default ? now : scheduledTask.UpdatedAt,
        };
    }

    private const ScheduledDaysOfWeek AllDays =
        ScheduledDaysOfWeek.Monday |
        ScheduledDaysOfWeek.Tuesday |
        ScheduledDaysOfWeek.Wednesday |
        ScheduledDaysOfWeek.Thursday |
        ScheduledDaysOfWeek.Friday |
        ScheduledDaysOfWeek.Saturday |
        ScheduledDaysOfWeek.Sunday;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class ScheduledTaskRecurrence
{
    public static DateTimeOffset? GetNextOccurrence(
        ScheduledTask scheduledTask,
        DateTimeOffset after)
    {
        var timeZone = ResolveTimeZone(scheduledTask.TimeZoneId);
        if (scheduledTask.Frequency == ScheduledTaskFrequency.Once)
        {
            var occurrence = ResolveLocalTime(scheduledTask.LocalStartAt, timeZone);
            return occurrence > after ? occurrence : null;
        }

        var localAfter = TimeZoneInfo.ConvertTime(after, timeZone).DateTime;
        var startDate = scheduledTask.LocalStartAt.Date;
        var timeOfDay = scheduledTask.LocalStartAt.TimeOfDay;
        var firstDate = localAfter.Date > startDate ? localAfter.Date : startDate;
        for (var offset = 0; offset < 3660; offset++)
        {
            var date = firstDate.AddDays(offset);
            if (!MatchesDay(scheduledTask, date.DayOfWeek))
            {
                continue;
            }

            var occurrence = ResolveLocalTime(date + timeOfDay, timeZone);
            if (occurrence > after)
            {
                return occurrence;
            }
        }

        throw new InvalidOperationException("无法计算定时任务的下一次运行时间。");
    }

    private static bool MatchesDay(ScheduledTask scheduledTask, DayOfWeek dayOfWeek) =>
        scheduledTask.Frequency switch
        {
            ScheduledTaskFrequency.Daily => true,
            ScheduledTaskFrequency.Weekdays => dayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            ScheduledTaskFrequency.Weekly => scheduledTask.DaysOfWeek.HasFlag(ToScheduledDay(dayOfWeek)),
            _ => false,
        };

    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (
            TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsTimeZoneId))
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
        }
    }

    private static ScheduledDaysOfWeek ToScheduledDay(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => ScheduledDaysOfWeek.Monday,
        DayOfWeek.Tuesday => ScheduledDaysOfWeek.Tuesday,
        DayOfWeek.Wednesday => ScheduledDaysOfWeek.Wednesday,
        DayOfWeek.Thursday => ScheduledDaysOfWeek.Thursday,
        DayOfWeek.Friday => ScheduledDaysOfWeek.Friday,
        DayOfWeek.Saturday => ScheduledDaysOfWeek.Saturday,
        DayOfWeek.Sunday => ScheduledDaysOfWeek.Sunday,
        _ => ScheduledDaysOfWeek.None,
    };

    private static DateTimeOffset ResolveLocalTime(DateTime localTime, TimeZoneInfo timeZone)
    {
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(localTime))
        {
            localTime = localTime.AddMinutes(1);
        }

        if (timeZone.IsAmbiguousTime(localTime))
        {
            var offset = timeZone.GetAmbiguousTimeOffsets(localTime).Max();
            return new DateTimeOffset(localTime, offset).ToUniversalTime();
        }

        return TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone);
    }
}
