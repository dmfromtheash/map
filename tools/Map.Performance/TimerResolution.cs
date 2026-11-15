using System.Runtime.InteropServices;

namespace Map.Performance;

/// <summary>1 ms system timer for the measuring process (released by Windows when the process exits).</summary>
internal static class TimerResolution
{
#pragma warning disable SYSLIB1054 // a blittable one-argument call; LibraryImport would need unsafe blocks in this tool
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
#pragma warning restore SYSLIB1054

    public static void Begin()
    {
        if (TimeBeginPeriod(1) != 0) Console.Error.WriteLine("timeBeginPeriod(1) failed: route input may run below 60 Hz.");
    }
}
