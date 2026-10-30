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

    internal sealed record DiskLocations(string ScratchRoot, string RasterRoot);

    public static DiskLocations Check(ExportPlan plan, string destination, ExportEngineSettings settings,
        string? temporaryDirectory = null)
    {
        var available = settings.AvailableMemoryBytes();
        if (plan.EstimatedRawWorkingBytes > available - available / 4)
            throw new ExportValidationException("Недостаточно свободной памяти для выбранного экспорта с резервом. Уменьшите размер или выберите пакет тайлов.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var local = temporaryDirectory ?? Path.GetTempPath();
        if (!plan.UsesStreamingPng)
        {
            CheckDisk(destination, plan.RequiredFreeDiskBytes, settings);
            return new(directory, directory);
        }
        var disk = ExportDiskEstimate.Create(plan);
        // Select once, using the same injected probes as admission. The writer must use these
        // locations too: reselecting later could put unaccounted scratch on another volume.
        try
        {
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(local)), Path.GetPathRoot(Path.GetFullPath(destination)),
                StringComparison.OrdinalIgnoreCase))
            {
                CheckDisk(destination, disk.CombinedBytes, settings);
                return new(local, local);
            }
            if (settings.AvailableDiskBytes(local) >= disk.TemporaryBytes)
            {
                CheckDisk(destination, disk.DestinationBytes, settings);
                return new(local, local);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        // Local TEMP cannot hold the PNG + pyramid (or the raster band): use the destination.
        CheckDisk(destination, disk.CombinedBytes, settings);
        return new(directory, directory);
    }

    private static void CheckDisk(string path, long required, ExportEngineSettings settings)
    {
        if (settings.AvailableDiskBytes(path) < required)
            throw new ExportValidationException(
                $"Для экспорта на диске {Path.GetPathRoot(Path.GetFullPath(path))} нужно ориентировочно "
                + $"{required / (1024d * 1024 * 1024):N1} ГиБ свободного места с учётом временных данных и резерва. "
                + "Шум и плохо сжимаемые изображения требуют больше места. Выберите другой диск или отдельный регион.");
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
