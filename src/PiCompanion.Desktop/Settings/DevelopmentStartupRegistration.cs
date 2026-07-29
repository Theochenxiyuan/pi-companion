using Microsoft.Win32;

namespace PiCompanion.Desktop.Settings;

internal static class DevelopmentStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PiCompanion";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true) ??
            throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("无法确定 Pi Companion 可执行文件路径。");
        }

        key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }
}
