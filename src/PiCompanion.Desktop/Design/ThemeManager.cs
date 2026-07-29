using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace PiCompanion.Desktop.Design;

internal enum AppTheme
{
    Dark,
    Light,
}

internal sealed class ThemeManager : IDisposable
{
    private static readonly string[] NeutralResourceKeys =
    [
        "ColorNeutral1000",
        "ColorNeutral950",
        "ColorNeutral900",
        "ColorNeutral850",
        "ColorNeutral800",
        "ColorNeutral750",
        "ColorNeutral700",
        "ColorNeutral650",
        "ColorNeutral600",
        "ColorNeutral500",
        "ColorNeutral400",
        "ColorNeutral350",
        "ColorNeutral300",
        "ColorNeutral200",
        "ColorNeutral100",
        "ColorNeutral50",
    ];

    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private string _preference;
    private bool _disposed;

    public ThemeManager(string preference)
    {
        _preference = preference;
        CurrentTheme = ResolveTheme(preference, SystemUsesLightTheme());
        ApplyThemeResources(CurrentTheme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppTheme CurrentTheme { get; private set; }

    public event Action<AppTheme>? ThemeChanged;

    public void SetPreference(string preference)
    {
        _preference = preference;
        ApplyResolvedTheme();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    internal static AppTheme ResolveTheme(string preference, bool systemUsesLightTheme) =>
        preference.ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "system" when systemUsesLightTheme => AppTheme.Light,
            _ => AppTheme.Dark,
        };

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!string.Equals(_preference, "system", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(ApplyResolvedTheme);
    }

    private void ApplyResolvedTheme()
    {
        var resolved = ResolveTheme(_preference, SystemUsesLightTheme());
        if (resolved == CurrentTheme)
        {
            return;
        }

        CurrentTheme = resolved;
        ApplyThemeResources(resolved);
        ThemeChanged?.Invoke(resolved);
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            return key?.GetValue(AppsUseLightThemeValue, 1) is int value && value != 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static void ApplyThemeResources(AppTheme theme)
    {
        var palette = GeneratedDesignTokens.For(theme);
        for (var index = 0; index < NeutralResourceKeys.Length; index++)
        {
            SetColor(NeutralResourceKeys[index], palette.Tones[index]);
        }

        SetColor("ColorRunning", palette.Running);
        SetColor("ColorRunningSurface", palette.RunningSurface);
        SetColor("ColorSuccess", palette.Success);
        SetColor("ColorSuccessSurface", palette.SuccessSurface);
        SetColor("ColorWarning", palette.Warning);
        SetColor("ColorWarningSurface", palette.WarningSurface);
        SetColor("ColorDanger", palette.Danger);
        SetColor("ColorDangerSurface", palette.DangerSurface);
        SetColor("ColorShadow", palette.Shadow);
        SetColor("ColorGlassWindow", palette.GlassWindow);
        SetColor("ColorGlassPanel", palette.GlassPanel);
        SetColor("ColorAccentHalo", palette.AccentHalo);
        SetColor("ColorWarningTint", palette.WarningTint);

        SetBrush("WindowBrush", palette.Tones[0]);
        SetBrush("SurfaceBrush", palette.Tones[1]);
        SetBrush("RaisedBrush", palette.Tones[2]);
        SetBrush("ElevatedBrush", palette.Tones[3]);
        SetBrush("HoverBrush", palette.Tones[4]);
        SetBrush("SelectionBrush", palette.Tones[5]);
        SetBrush("SelectionStrongBrush", palette.Tones[6]);
        SetBrush("StrokeBrush", palette.Tones[5]);
        SetBrush("StrokeStrongBrush", palette.Tones[7]);
        SetBrush("FocusBrush", palette.Tones[8]);
        SetBrush("TextPrimaryBrush", palette.Tones[14]);
        SetBrush("TextSecondaryBrush", palette.Tones[11]);
        SetBrush("TextMutedBrush", palette.Tones[10]);
        SetBrush("TextInverseBrush", palette.Tones[0]);
        SetBrush("AccentBrush", palette.Tones[15]);
        SetBrush("AccentHoverBrush", palette.Tones[13]);
        SetBrush("RunningBrush", palette.Running);
        SetBrush("RunningSurfaceBrush", palette.RunningSurface);
        SetBrush("SuccessBrush", palette.Success);
        SetBrush("SuccessSurfaceBrush", palette.SuccessSurface);
        SetBrush("WarningBrush", palette.Warning);
        SetBrush("WarningSurfaceBrush", palette.WarningSurface);
        SetBrush("DangerBrush", palette.Danger);
        SetBrush("DangerSurfaceBrush", palette.DangerSurface);
        SetBrush("GlassWindowBrush", palette.GlassWindow);
        SetBrush("GlassPanelBrush", palette.GlassPanel);
        SetBrush("AccentHaloBrush", palette.AccentHalo);
        SetBrush("WarningTintBrush", palette.WarningTint);
    }

    private static void SetColor(string resourceKey, Color color) =>
        System.Windows.Application.Current.Resources[resourceKey] = color;

    private static void SetBrush(string resourceKey, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        System.Windows.Application.Current.Resources[resourceKey] = brush;
    }
}
