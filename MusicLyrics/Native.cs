using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace MusicLyrics;

internal static class Native
{
    public const int HotkeyMessage = 0x0312;
    public const int SettingChange = 0x001A;
    public const int Moving = 0x0216;
    public const int Sizing = 0x0214;
    private const int ExStyle = -20;
    private const int Transparent = 0x20;
    private const int ToolWindow = 0x80;
    private const int NoActivate = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point(int x, int y)
    {
        public int X = x, Y = y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(nint window, int index, nint value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint window, int id);

    public static void MakeClickThrough(nint handle) =>
        SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | Transparent | NoActivate | ToolWindow);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags, Tint, Animation; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData { public int Attribute; public nint Data; public nuint Size; }
    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(nint window, ref CompositionData data);

    public static void SetBackdropBlur(nint handle, bool enabled)
    {
        var size = Marshal.SizeOf<AccentPolicy>();
        var data = new CompositionData { Attribute = 19, Size = (nuint)size, Data = Marshal.AllocHGlobal(size) };
        try
        {
            Marshal.StructureToPtr(new AccentPolicy { State = enabled ? 3 : 0 }, data.Data, false);
            SetWindowCompositionAttribute(handle, ref data);
        }
        finally { Marshal.FreeHGlobal(data.Data); }
    }

    public static void Place(Window window, Screen screen, OverlayRect rect)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var bounds = screen.Bounds;
        var scale = Scale(screen);
        var fitted = rect.Fit(bounds.Width / scale, bounds.Height / scale);
        SetWindowPos(handle, new nint(-1), bounds.Left + Round(fitted.X * scale),
            bounds.Top + Round(fitted.Y * scale), Round(fitted.Width * scale),
            Round(fitted.Height * scale), 0x0010 | 0x0200);
    }

    public static double Scale(Screen screen)
    {
        var bounds = screen.Bounds;
        var monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2), 2);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96d : 1;
    }

    public static bool AnimationsEnabled() =>
        SystemParametersInfoW(0x1042, 0, out var enabled, 0) && enabled;

    private static int Round(double value) => (int)Math.Round(value);
}
