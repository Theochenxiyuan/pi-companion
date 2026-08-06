namespace PiCompanion.Core.Tasks;

public enum TaskTemplateTargetKind
{
    CurrentContext,
    Workspace,
    GeneralChat,
}

public enum TaskTemplateOrigin
{
    User,
    Agent,
}

public sealed record TaskTemplate(
    Guid Id,
    string Name,
    string Prompt,
    TaskTemplateTargetKind TargetKind,
    Guid? WorkspaceId,
    string? Model,
    string? ThinkingLevel,
    string? PermissionMode,
    bool IsPinned,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TaskTemplateOrigin Origin = TaskTemplateOrigin.User,
    Guid? SourceTaskId = null,
    Guid? SourceRunId = null);

public static class TaskTemplateRules
{
    public const int MaximumNameLength = 80;
    public const int MaximumPromptLength = 100_000;
    private static readonly HashSet<string> ThinkingLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static TaskTemplate Normalize(TaskTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var name = string.Join(' ', (template.Name ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var prompt = template.Prompt?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new ArgumentException("任务模板名称不能为空。", nameof(template));
        }

        if (name.Length > MaximumNameLength)
        {
            throw new ArgumentException($"任务模板名称不能超过 {MaximumNameLength} 个字符。", nameof(template));
        }

        if (prompt.Length == 0)
        {
            throw new ArgumentException("任务模板内容不能为空。", nameof(template));
        }

        if (prompt.Length > MaximumPromptLength)
        {
            throw new ArgumentException($"任务模板内容不能超过 {MaximumPromptLength} 个字符。", nameof(template));
        }

        if (template.TargetKind != TaskTemplateTargetKind.Workspace && template.WorkspaceId is not null)
        {
            throw new ArgumentException("只有工作区模板可以绑定指定工作区。", nameof(template));
        }

        if (!Enum.IsDefined(template.Origin))
        {
            throw new ArgumentException("任务模板来源无效。", nameof(template));
        }

        if (template.Origin == TaskTemplateOrigin.Agent &&
            (template.SourceTaskId is null || template.SourceRunId is null))
        {
            throw new ArgumentException("AI 创建的任务模板必须记录来源任务和运行。", nameof(template));
        }

        if (template.Origin == TaskTemplateOrigin.User &&
            (template.SourceTaskId is not null || template.SourceRunId is not null))
        {
            throw new ArgumentException("用户创建的任务模板不能带有 AI 来源信息。", nameof(template));
        }

        var permissionMode = NormalizeOptional(template.PermissionMode);
        if (permissionMode is not null && permissionMode is not ("read-only" or "standard"))
        {
            throw new ArgumentException("任务模板权限只能设为只读或标准访问。", nameof(template));
        }

        var thinkingLevel = NormalizeOptional(template.ThinkingLevel)?.ToLowerInvariant();
        if (thinkingLevel is not null && !ThinkingLevels.Contains(thinkingLevel))
        {
            throw new ArgumentException("任务模板推理等级无效。", nameof(template));
        }

        var now = DateTimeOffset.UtcNow;
        return template with
        {
            Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id,
            Name = name,
            Prompt = prompt,
            Model = NormalizeOptional(template.Model),
            ThinkingLevel = thinkingLevel,
            PermissionMode = template.TargetKind == TaskTemplateTargetKind.GeneralChat ? null : permissionMode,
            CreatedAt = template.CreatedAt == default ? now : template.CreatedAt,
            UpdatedAt = template.UpdatedAt == default ? now : template.UpdatedAt,
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
