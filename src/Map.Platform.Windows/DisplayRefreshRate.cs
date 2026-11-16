using System.Runtime.InteropServices;

namespace Map.Platform.Windows;

/// <summary>Current mode of the canvas's display, not an inferred frame interval or primary-screen
/// assumption. Called on a background worker at most once per second; unknown modes return null.</summary>
public static class DisplayRefreshRate
{
    public static double? Read(string? deviceName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceName)) return null;
        var mode = new DisplayMode { Size = 220 };
        return EnumDisplaySettings(deviceName, -1, ref mode) && mode.Frequency is >= 24 and <= 1000
            ? mode.Frequency : null;
    }

    // Win32 DEVMODEW is 220 bytes on both x86 and x64. Only dmSize (byte 68) and
    // dmDisplayFrequency (byte 184) are used; the native API fills the entire zeroed buffer.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint Frequency;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int mode, ref DisplayMode settings);
}
