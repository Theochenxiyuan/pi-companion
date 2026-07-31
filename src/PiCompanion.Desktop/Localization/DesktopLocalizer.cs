using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PiCompanion.Desktop.Localization;

internal static class DesktopLocalizer
{
    private sealed class OriginalText
    {
        public string? Text { get; init; }
        public string? Content { get; init; }
        public string? Header { get; init; }
        public string? ToolTip { get; init; }
        public string? Title { get; init; }
    }

    private static readonly ConditionalWeakTable<DependencyObject, OriginalText> Originals = new();
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["更多"] = "More",
        ["Pi Companion — 智能体对话"] = "Pi Companion — Agent Chat",
        ["智能体对话"] = "Agent Chat",
        ["任务监视器"] = "Monitor",
        ["快捷任务入口"] = "Quick Task Launcher",
        ["Pi Companion 任务监视器"] = "Pi Companion Monitor",
        ["显示 / 隐藏任务监视器"] = "Show / hide Monitor",
        ["对话显示"] = "Conversation display",
        ["摘要"] = "Summary",
        ["标准"] = "Standard",
        ["详细"] = "Detailed",
        ["退出 Pi Companion"] = "Exit Pi Companion",
        ["正在准备智能体对话"] = "Preparing Agent Chat",
        ["启动 WebView2 并加载本地 Vue 应用"] = "Loading the app",
        ["重试"] = "Retry",
        ["来自资源管理器的目录任务"] = "Task from File Explorer",
        ["取消 (Esc)"] = "Cancel (Esc)",
        ["工作目录"] = "Working directory",
        ["附件"] = "Attachments",
        ["未选择附件，将使用当前目录作为上下文"] = "No attachments selected; the current directory will be used as context",
        ["附件不可用"] = "Attachment unavailable",
        ["移除附件"] = "Remove attachment",
        ["任务描述"] = "Task description",
        ["技能"] = "Skill",
        ["没有提供技能描述"] = "No skill description provided",
        ["正在读取可用技能…"] = "Reading available skills…",
        ["没有匹配的可用技能"] = "No matching available skills",
        ["选择后将插入标准 /skill: 技能调用"] = "Selection inserts a standard /skill: invocation",
        ["Ctrl + Enter 开始任务"] = "Ctrl + Enter to start",
        ["模型"] = "Model",
        ["推理等级"] = "Reasoning level",
        ["权限"] = "Permissions",
        ["只读"] = "Read only",
        ["信任工作区"] = "Trust workspace",
        ["标准访问"] = "Standard access",
        ["允许工作区内普通文件修改；Shell、敏感操作和工作区外访问会请求授权"] = "Allow normal workspace file changes; shell commands, sensitive operations, and access outside the workspace require approval",
        ["完全访问"] = "Full access",
        ["允许在当前 Windows 用户权限范围内访问任意本地路径并执行命令，不再请求授权"] = "Allow access to any local path and run commands as the current Windows user without further approval",
        ["启用完全访问？"] = "Enable full access?",
        ["此任务将能在当前 Windows 用户权限范围内访问任意本地路径并执行命令，不再逐次请求授权。"] = "This task can access any local path and execute commands as the current Windows user without asking each time.",
        ["这不会获得管理员权限，并且只对即将发送的这个任务生效。请仅在你信任任务内容时启用。"] = "This does not grant administrator privileges and applies only to the task you are about to send. Enable it only if you trust the task.",
        ["启用完全访问"] = "Enable full access",
        ["转到智能体对话"] = "Go to Agent Chat",
        ["开始任务  ↵"] = "Start task  ↵",
        ["新建任务"] = "New task",
        ["隐藏任务监视器"] = "Hide Monitor",
        ["准备就绪"] = "Ready",
        ["展开"] = "Expand",
        ["收起"] = "Collapse",
        ["尚未选择工作目录"] = "No working directory selected",
        ["等待任务"] = "Waiting for a task",
        ["正在执行"] = "Running",
        ["Agent 请求权限"] = "Approval required",
        ["Pi Agent 正在等待你的响应。"] = "Response required.",
        ["拒绝"] = "Deny",
        ["本任务内允许同类操作"] = "Allow similar actions for this task",
        ["允许一次"] = "Allow once",
        ["取消"] = "Cancel",
        ["回答"] = "Answer",
        ["选择"] = "Select",
        ["任务已完成"] = "Task completed",
        ["本轮任务已完成"] = "Run completed",
        ["本轮任务已停止"] = "Run stopped",
        ["本轮任务失败"] = "Run failed",
        ["结果摘要"] = "Result summary",
        ["总结："] = "Summary: ",
        ["思考"] = "Thinking",
        ["工具调用"] = "Tool calls",
        ["网络搜索"] = "Web searches",
        ["最近授权与回答"] = "Recent approvals and answers",
        ["任务完成"] = "Task complete",
        ["正在生成 AI 总结"] = "Generating AI summary",
        ["暂无进行中的任务"] = "No active task",
        ["从智能体对话新建任务，任务监视器会在这里显示最高优先级状态。"] = "Start a task in Agent Chat to track it here.",
        ["继续任务"] = "Continue task",
        ["继续这项任务"] = "Continue this task",
        ["立即调整"] = "Steer now",
        ["发送新一轮"] = "Start new run",
        ["在智能体对话中打开 ↗"] = "Open in Agent Chat ↗",
        ["打开智能体对话 ↗"] = "Open Agent Chat ↗",
        ["打开当前任务 ↗"] = "Open current task ↗",
        ["回收站已清空。"] = "Recycle Bin emptied.",
        ["已重新读取本地 Pi Runtime、Provider 和缓存模型。"] = "Reloaded the local Pi runtime, providers, and cached models.",
        ["已联网刷新 Pi 模型目录。"] = "Refreshed the Pi model catalog online.",
        ["Provider API Key 已保存到 Pi auth.json，新任务会直接使用。"] = "API key saved to Pi auth.json. New tasks will use it.",
        ["已从 Pi auth.json 移除 Provider 凭据。"] = "Removed the provider credentials from Pi auth.json.",
        ["只能修改当前任务的模型设置。"] = "Only the current task's model settings can be changed.",
        ["已有 OAuth 登录正在等待完成。"] = "An OAuth sign-in is already awaiting completion.",
        ["OAuth 登录已完成，Provider 状态已刷新。"] = "Signed in with OAuth. Provider status refreshed.",
        ["OAuth 登录已取消。"] = "OAuth sign-in was canceled.",
        ["浏览器授权已启动，但该 Provider 还要求额外输入；不会再重复打开 Pi 终端。"] = "Browser authorization started, but this provider still needs input in Pi Terminal.",
        ["WebView 与附件缓存已清理。"] = "WebView and attachment caches were cleared.",
        ["设置已保存。"] = "Settings saved.",
        ["已自动保存。"] = "Saved automatically.",
        ["Pi Agent 设置已保存。"] = "Pi Agent settings saved.",
    };

    public static bool IsEnglish { get; private set; }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "zh-CN");

    public static void SetLanguage(string? language)
    {
        IsEnglish = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase);
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    public static string Text(string chinese, string english) => IsEnglish ? english : chinese;

    public static string Text(string source) => IsEnglish && English.TryGetValue(source, out var value) ? value : source;

    public static void Apply(DependencyObject root)
    {
        ApplyCurrent(root);

        // A ContentPresenter renders string content with template-generated text elements.
        // The owning ContentControl is localized above; walking into the generated text
        // would cache the translated value as its "original" and make it survive a
        // later language switch.
        if (root is ContentPresenter { Content: string })
        {
            return;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            Apply(child);
        }

        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (LogicalTreeHelper.GetParent(child) is null)
            {
                Apply(child);
            }
        }
    }

    private static void ApplyCurrent(DependencyObject target)
    {
        if (!Originals.TryGetValue(target, out var original))
        {
            original = new OriginalText
            {
                Text = (target as TextBlock)?.Text,
                Content = (target as ContentControl)?.Content as string,
                Header = (target as HeaderedItemsControl)?.Header as string,
                ToolTip = (target as FrameworkElement)?.ToolTip as string,
                Title = (target as Window)?.Title,
            };
            Originals.Add(target, original);
        }

        if (target is TextBlock textBlock && original.Text is not null) textBlock.Text = Text(original.Text);
        if (target is ContentControl contentControl && original.Content is not null) contentControl.Content = Text(original.Content);
        if (target is HeaderedItemsControl headered && original.Header is not null) headered.Header = Text(original.Header);
        if (target is FrameworkElement element && original.ToolTip is not null) element.ToolTip = Text(original.ToolTip);
        if (target is Window window && original.Title is not null) window.Title = Text(original.Title);
    }
}
