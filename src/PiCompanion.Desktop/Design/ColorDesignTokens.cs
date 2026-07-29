using DrawingColor = System.Drawing.Color;

namespace PiCompanion.Desktop.Design;

/// <summary>
/// Runtime color tokens for drawing surfaces that cannot consume WPF resources.
/// Values come from the generated cross-renderer design token palette.
/// </summary>
internal static class ColorDesignTokens
{
    public static DrawingColor Transparent { get; } = DrawingColor.Transparent;

    public static DrawingColor Canvas(AppTheme theme) =>
        ToDrawingColor(GeneratedDesignTokens.For(theme).Tones[0]);

    public static DrawingColor IconSurface { get; } =
        ToDrawingColor(GeneratedDesignTokens.Dark.Tones[2]);

    public static DrawingColor IconForeground { get; } =
        ToDrawingColor(GeneratedDesignTokens.Dark.Tones[15]);

    private static DrawingColor ToDrawingColor(System.Windows.Media.Color color) =>
        DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
}
