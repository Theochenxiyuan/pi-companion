using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PiCompanion.Application.Settings;
using PiCompanion.Core.Activation;

namespace PiCompanion.Desktop.Shell;

internal static partial class WindowPlacementService
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int EffectiveDpi = 0;

    public static void PlaceNearCursor(Window window)
    {
        _ = GetCursorPos(out var cursor);
        Place(window, cursor, nearCursor: true);
    }

    public static void PlaceNearActivation(
        Window window,
        ScreenPoint? cursorPosition,
        long explorerWindowHandle)
    {
        NativePoint anchor;
        if (cursorPosition is not null)
        {
            anchor = new NativePoint { X = cursorPosition.X, Y = cursorPosition.Y };
        }
        else if (TryGetWindowCenter(explorerWindowHandle, out var windowCenter))
        {
            anchor = windowCenter;
        }
        else if (!GetCursorPos(out anchor))
        {
            anchor = new NativePoint();
        }

        Place(window, anchor, nearCursor: true);
    }

    public static void PlaceTopRight(Window window)
    {
        _ = GetCursorPos(out var cursor);
        Place(window, cursor, nearCursor: false);
    }

    public static void PlaceAtCorner(Window window, string corner)
    {
        _ = GetCursorPos(out var cursor);
        Place(window, cursor, nearCursor: false, corner);
    }

    public static void ConstrainToCursorWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero ||
            !GetWindowRect(handle, out var windowRect) ||
            !GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var constrained = WindowPlacementCalculator.ClampToWorkArea(
            new PixelRect(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom),
            new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
        if (constrained.Left == windowRect.Left && constrained.Top == windowRect.Top)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            constrained.Left,
            constrained.Top,
            constrained.Width,
            constrained.Height,
            SwpNoActivate | SwpNoZOrder);
    }

    public static WindowPlacementState Capture(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        return new WindowPlacementState(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            window.WindowState == WindowState.Maximized);
    }

    public static bool Restore(Window window, WindowPlacementState state, bool restoreSize)
    {
        if (!IsUsable(state))
        {
            return false;
        }

        var width = restoreSize ? Math.Max(window.MinWidth, state.Width) : window.Width;
        var height = restoreSize ? Math.Max(window.MinHeight, state.Height) : window.Height;
        var left = state.Left;
        var top = state.Top;
        var virtualBounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var candidate = new Rect(left, top, width, height);
        if (!candidate.IntersectsWith(virtualBounds))
        {
            var workArea = SystemParameters.WorkArea;
            left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
            top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (restoreSize)
        {
            window.Width = width;
            window.Height = height;
        }
        window.Left = left;
        window.Top = top;
        if (restoreSize && state.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
        return true;
    }

    private static bool IsUsable(WindowPlacementState state) =>
        double.IsFinite(state.Left) &&
        double.IsFinite(state.Top) &&
        double.IsFinite(state.Width) &&
        double.IsFinite(state.Height) &&
        state.Width > 0 &&
        state.Height > 0;

    private static void Place(Window window, NativePoint cursor, bool nearCursor, string corner = "top-right")
    {
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var dpi = 96u;
        try
        {
            if (GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0)
            {
                dpi = dpiX;
            }
        }
        catch (DllNotFoundException)
        {
            dpi = 96;
        }

        var scale = dpi / 96d;
        var logicalWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var logicalHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        var width = (int)Math.Round(logicalWidth * scale);
        var height = (int)Math.Round(logicalHeight * scale);
        var margin = (int)Math.Round(18 * scale);

        int x;
        int y;
        if (nearCursor)
        {
            var placement = WindowPlacementCalculator.PlaceNearPoint(
                new ScreenPoint(cursor.X, cursor.Y),
                new PixelSize(width, height),
                new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
                margin);
            x = placement.Left;
            y = placement.Top;
            width = placement.Width;
            height = placement.Height;
        }
        else
        {
            var placeLeft = corner is "top-left" or "bottom-left";
            var placeBottom = corner is "bottom-left" or "bottom-right";
            x = placeLeft ? info.Work.Left + margin : info.Work.Right - width - margin;
            y = placeBottom ? info.Work.Bottom - height - margin : info.Work.Top + margin;
        }

        x = Math.Clamp(x, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        y = Math.Clamp(y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoActivate | SwpNoZOrder);
        }
    }

    private static bool TryGetWindowCenter(long windowHandle, out NativePoint center)
    {
        center = default;
        if (windowHandle == 0)
        {
            return false;
        }

        var handle = new IntPtr(windowHandle);
        if (!IsWindow(handle) || !GetWindowRect(handle, out var rect))
        {
            return false;
        }

        center = new NativePoint
        {
            X = rect.Left + ((rect.Right - rect.Left) / 2),
            Y = rect.Top + ((rect.Bottom - rect.Top) / 2),
        };
        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
