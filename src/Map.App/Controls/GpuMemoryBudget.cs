using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Map.App.Controls;

/// <summary>
/// The size of Skia's GPU texture cache, taken from the memory of the adapter the canvas draws on.
/// <para>
/// PC experiment 2026-11-16 (100 000 objects, same warm cache, one variable): the fixed 256 MiB held only three full-size
/// originals (4096 px, ~85 MiB each with mips), so stamps at 400-1600 % were evicted and uploaded again every frame -
/// pan/1600 5.5 -> 138.7 FPS (1 % low 3.5 -> 59.3), wheel 1600 -> 400 7.7 -> 129 FPS, and the blur after the wheel
/// fit -> 100 lasted 5.4 -> 0.17 s with 1.5 GiB. The owner's machines differ (RTX 3070 8 GiB; Intel HD 630 on shared
/// memory), so the size follows the adapter's own budget instead of one number.
/// </para>
/// The adapter is the ANGLE D3D11 device behind the GL context (EGL_EXT_device_query); its
/// <c>IDXGIAdapter3.QueryVideoMemoryInfo(LOCAL).Budget</c> is what the OS lets this process use (on an integrated GPU
/// that is the shared system memory budget). A quarter of it, at least 256 MiB and at most 3 GiB. Anything missing
/// (another backend, an old driver) keeps 256 MiB. <c>MAP_GPU_CACHE_MB</c> overrides it for experiments.
/// </summary>
public static class GpuMemoryBudget
{
    public const long Floor = 256L * 1024 * 1024;
    public const long Ceiling = 3L * 1024 * 1024 * 1024;

    /// <summary>The adapter budget read at the last successful query (bytes); null when unknown.</summary>
    public static long? LastAdapterBudget { get; private set; }

    /// <summary>The cache size this rule picks for an adapter budget.</summary>
    public static long ForBudget(long? adapterBudget) =>
        adapterBudget is > 0 and var budget ? Math.Clamp(budget / 4, Floor, Ceiling) : Floor;

    /// <summary>The cache size for the GL context of this frame's lease. Call on the render thread.</summary>
    public static long CacheBytes(IGlContext? gl)
    {
        if (long.TryParse(Environment.GetEnvironmentVariable("MAP_GPU_CACHE_MB"), out var mb) && mb is >= 64 and <= 16384)
            return mb * 1024 * 1024;
        LastAdapterBudget = gl is null ? null : TryAdapterBudget(gl);
        return ForBudget(LastAdapterBudget);
    }

    private const int EglDeviceExt = 0x322C;
    private const int EglD3D11DeviceAngle = 0x33A1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetCurrentDisplay();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool QueryAttrib(IntPtr target, int attribute, out IntPtr value);

    private static long? TryAdapterBudget(IGlContext gl)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var gl1 = gl.GlInterface;
            IntPtr current = gl1.GetProcAddress("eglGetCurrentDisplay"),
                displayQuery = gl1.GetProcAddress("eglQueryDisplayAttribEXT"),
                deviceQuery = gl1.GetProcAddress("eglQueryDeviceAttribEXT");
            if (current == IntPtr.Zero || displayQuery == IntPtr.Zero || deviceQuery == IntPtr.Zero) return null;
            var display = Marshal.GetDelegateForFunctionPointer<GetCurrentDisplay>(current)();
            if (display == IntPtr.Zero
                || !Marshal.GetDelegateForFunctionPointer<QueryAttrib>(displayQuery)(display, EglDeviceExt, out var device) || device == IntPtr.Zero
                || !Marshal.GetDelegateForFunctionPointer<QueryAttrib>(deviceQuery)(device, EglD3D11DeviceAngle, out var d3d) || d3d == IntPtr.Zero)
                return null;
            return Budget(d3d);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException
            or COMException or ArgumentException)
        {
            return null;
        }
    }

    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid DxgiAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo { public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterSlot(IntPtr self, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemorySlot(IntPtr self, uint node, int segmentGroup, out VideoMemoryInfo info);

    private static T Slot<T>(IntPtr comObject, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), index * IntPtr.Size));

    /// <summary>ID3D11Device -> IDXGIDevice::GetAdapter (slot 7) -> IDXGIAdapter3::QueryVideoMemoryInfo (slot 14).</summary>
    private static long? Budget(IntPtr d3dDevice)
    {
        IntPtr dxgiDevice = IntPtr.Zero, adapter = IntPtr.Zero, adapter3 = IntPtr.Zero;
        try
        {
            if (Marshal.QueryInterface(d3dDevice, in DxgiDevice, out dxgiDevice) != 0) return null;
            if (Slot<GetAdapterSlot>(dxgiDevice, 7)(dxgiDevice, out adapter) != 0 || adapter == IntPtr.Zero) return null;
            if (Marshal.QueryInterface(adapter, in DxgiAdapter3, out adapter3) != 0) return null;
            return Slot<QueryVideoMemorySlot>(adapter3, 14)(adapter3, 0, 0, out var info) == 0 && info.Budget > 0
                ? (long)Math.Min(info.Budget, long.MaxValue) : null;
        }
        finally
        {
            if (adapter3 != IntPtr.Zero) Marshal.Release(adapter3);
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
            if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
        }
    }
}
