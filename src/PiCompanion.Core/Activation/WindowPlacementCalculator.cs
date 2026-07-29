namespace PiCompanion.Core.Activation;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

public readonly record struct PixelSize(int Width, int Height);

public static class WindowPlacementCalculator
{
    public static PixelRect ClampToWorkArea(PixelRect windowBounds, PixelRect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBounds));
        }

        var left = Math.Clamp(
            windowBounds.Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - windowBounds.Width));
        var top = Math.Clamp(
            windowBounds.Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - windowBounds.Height));
        return new PixelRect(
            left,
            top,
            left + windowBounds.Width,
            top + windowBounds.Height);
    }

    public static PixelRect PlaceNearPoint(
        ScreenPoint anchor,
        PixelSize requestedSize,
        PixelRect workArea,
        int margin)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        var width = Math.Clamp(requestedSize.Width, 1, workArea.Width);
        var height = Math.Clamp(requestedSize.Height, 1, workArea.Height);
        var safeMargin = Math.Max(0, margin);

        var x = anchor.X + safeMargin;
        var y = anchor.Y + safeMargin;
        if (x + width > workArea.Right)
        {
            x = anchor.X - width - safeMargin;
        }

        if (y + height > workArea.Bottom)
        {
            y = anchor.Y - height - safeMargin;
        }

        x = Math.Clamp(x, workArea.Left, workArea.Right - width);
        y = Math.Clamp(y, workArea.Top, workArea.Bottom - height);
        return new PixelRect(x, y, x + width, y + height);
    }
}
