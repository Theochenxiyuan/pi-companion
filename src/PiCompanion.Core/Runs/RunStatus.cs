namespace PiCompanion.Core.Runs;

public enum RunStatus
{
    Draft,
    Queued,
    Starting,
    Running,
    WaitingForApproval,
    WaitingForAnswer,
    Cancelling,
    Completed,
    Failed,
    Interrupted,
    Deleted,
}

public static class RunStatusExtensions
{
    public static bool IsActive(this RunStatus status) => status is
        RunStatus.Queued or
        RunStatus.Starting or
        RunStatus.Running or
        RunStatus.WaitingForApproval or
        RunStatus.WaitingForAnswer or
        RunStatus.Cancelling;

    public static string ToDisplayText(this RunStatus status) => status switch
    {
        RunStatus.Draft => "准备就绪",
        RunStatus.Queued => "排队中",
        RunStatus.Starting => "正在启动",
        RunStatus.Running => "执行中",
        RunStatus.WaitingForApproval => "等待授权",
        RunStatus.WaitingForAnswer => "等待回答",
        RunStatus.Cancelling => "正在停止",
        RunStatus.Completed => "已完成",
        RunStatus.Failed => "失败",
        RunStatus.Interrupted => "已停止",
        RunStatus.Deleted => "已删除",
        _ => status.ToString(),
    };
}
