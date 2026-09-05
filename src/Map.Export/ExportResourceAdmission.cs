using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Map.Export;

/// <summary>Read-only resource admission before rendering or creating output. Live available
/// RAM already excludes the editor's retained document, undo history and native caches.</summary>
internal static class ExportResourceAdmission
{
    public static long AvailableMemory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref state))
            return (long)Math.Min(state.AvailablePhysical, (ulong)long.MaxValue);
        using var process = Process.GetCurrentProcess();
        return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes - process.PrivateMemorySize64);
    }

    public static void Check(ExportPlan plan, string destination, ExportEngineSettings settings)
    {
        var available = settings.AvailableMemoryBytes();
        if (plan.EstimatedRawWorkingBytes > available - available / 4)
            throw new ExportValidationException("Недостаточно свободной памяти для выбранного экспорта с резервом. Уменьшите размер или выберите пакет тайлов.");
        // Destination bytes remain intact while this complete sibling result is built.
        var diskRequired = checked((long)plan.OutputWidth * plan.OutputHeight * 8 + 16L * 1024 * 1024);
        if (settings.AvailableDiskBytes(destination) < diskRequired)
            throw new ExportValidationException("Недостаточно свободного места для временного результата экспорта. Выберите другую папку или уменьшите размер.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
