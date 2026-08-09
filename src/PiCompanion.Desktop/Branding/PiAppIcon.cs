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
    private const int BaseIconSize = 32;
    private const int WmDpiChanged = 0x02E0;
    public static BitmapSource WindowIcon { get; } = CreateWindowIcon();

    public static Icon CreateTrayIcon() => CreateIcon(BaseIconSize);

    public static void ApplyTo(Window window)
    {
        window.Icon = WindowIcon;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            ApplyWindowIcon(window, handle);
            HwndSource.FromHwnd(handle)?.AddHook((
                IntPtr sourceHandle,
                int message,
                IntPtr wParam,
                IntPtr lParam,
                ref bool handled) =>
            {
                if (message == WmDpiChanged)
                {
                    _ = window.Dispatcher.BeginInvoke(() => ApplyWindowIcon(window, handle));
                }

                handled = false;
                return IntPtr.Zero;
            });
        };
    }

    private static void ApplyWindowIcon(Window window, IntPtr handle)
    {
        var dpi = handle == IntPtr.Zero ? 96u : GetDpiForWindow(handle);
        var pixelSize = Math.Clamp(
            (int)Math.Ceiling(BaseIconSize * Math.Max(dpi, 96u) / 96d),
            BaseIconSize,
            128);
        window.Icon = CreateWindowIcon(pixelSize);
    }

    private static BitmapSource CreateWindowIcon(int pixelSize = BaseIconSize)
    {
        using var icon = CreateIcon(pixelSize);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Icon CreateIcon(int pixelSize)
    {
        var scale = pixelSize / (float)BaseIconSize;
        using var bitmap = new Bitmap(pixelSize, pixelSize);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(ColorDesignTokens.Transparent);

        using var background = new SolidBrush(ColorDesignTokens.IconSurface);
        graphics.FillEllipse(background, 0.5f * scale, 0.5f * scale, 31 * scale, 31 * scale);

        using var font = new Font("Georgia", 22 * scale, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(ColorDesignTokens.IconForeground);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString("π", font, foreground, new RectangleF(0, -scale, pixelSize, 34 * scale), format);

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

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);
}
