using System.Security.Principal;

namespace PiCompanion.Desktop.Activation;

internal static class ActivationPipeName
{
    public static string ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("无法确定当前 Windows 用户 SID。");
        return $"PiCompanion.Activation.v1.{sid}";
    }
}
