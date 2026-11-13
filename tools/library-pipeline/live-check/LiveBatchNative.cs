using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Only window-addressed messages. Never modifies the physical cursor or foreground window.
public static class LiveBatchNative
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    public static void UsePhysicalCoordinates()
    {
        if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot select per-monitor physical coordinates.");
    }
    public static void ValidateWindow(IntPtr window, int expectedPid)
    {
        uint pid;
        GetWindowThreadProcessId(window, out pid);
        if (!IsWindow(window) || pid != (uint)expectedPid || !IsWindowVisible(window) || IsIconic(window))
            throw new InvalidOperationException("The target window is missing, hidden, minimized or belongs to another PID.");
    }
    public static double Dpi(IntPtr window)
    {
        uint dpi = GetDpiForWindow(window);
        if (dpi == 0) throw new InvalidOperationException("Window DPI is unavailable.");
        return dpi;
    }
    public static IntPtr Pack(int x, int y)
    {
        if (x < short.MinValue || x > short.MaxValue || y < short.MinValue || y > short.MaxValue)
            throw new ArgumentOutOfRangeException("Coordinates exceed the signed WM_MOUSE range.");
        return new IntPtr(unchecked((y << 16) | (x & 0xffff)));
    }
    public static IntPtr WheelParam(int delta)
    {
        if (delta < short.MinValue || delta > short.MaxValue) throw new ArgumentOutOfRangeException("delta");
        return new IntPtr(unchecked(delta << 16));
    }
    private static POINT ClientPoint(IntPtr window, int screenX, int screenY)
    {
        var point = new POINT { X = screenX, Y = screenY };
        RECT rect;
        if (!ScreenToClient(window, ref point) || !GetClientRect(window, out rect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (point.X < 0 || point.Y < 0 || point.X >= rect.Right || point.Y >= rect.Bottom)
            throw new InvalidOperationException("Input point is outside the target client area.");
        return point;
    }
    private static void Send(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        IntPtr result;
        if (SendMessageTimeout(window, message, wParam, lParam, 2, 2000, out result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Window message timed out or failed.");
    }
    public static void Click(IntPtr window, int screenX, int screenY)
    {
        var point = ClientPoint(window, screenX, screenY);
        var packed = Pack(point.X, point.Y);
        Send(window, 0x0200, IntPtr.Zero, packed);
        try { Send(window, 0x0201, new IntPtr(1), packed); }
        finally { Send(window, 0x0202, IntPtr.Zero, packed); }
    }
    public static void Wheel(IntPtr window, int screenX, int screenY, int delta)
    {
        var point = ClientPoint(window, screenX, screenY);
        Send(window, 0x0200, IntPtr.Zero, Pack(point.X, point.Y));
        // Wheel lParam is in screen pixels; mouse-move lParam is in client pixels.
        Send(window, 0x020a, WheelParam(delta), Pack(screenX, screenY));
    }
}
