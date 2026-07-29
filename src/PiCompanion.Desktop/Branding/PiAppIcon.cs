using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PiCompanion.Desktop.Design;

namespace PiCompanion.Desktop.Branding;

internal static partial class PiAppIcon
{
    public static BitmapSource WindowIcon { get; } = CreateWindowIcon();

    public static Icon CreateTrayIcon() => CreateIcon();

    private static BitmapSource CreateWindowIcon()
    {
        using var icon = CreateIcon();
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(ColorDesignTokens.Transparent);

        using var background = new SolidBrush(ColorDesignTokens.IconSurface);
        graphics.FillEllipse(background, 0.5f, 0.5f, 31, 31);

        using var font = new Font("Georgia", 22, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(ColorDesignTokens.IconForeground);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString("π", font, foreground, new RectangleF(0, -1, 32, 34), format);

        var handle = bitmap.GetHicon();
        try
        {
            using var source = Icon.FromHandle(handle);
            return (Icon)source.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);
}
