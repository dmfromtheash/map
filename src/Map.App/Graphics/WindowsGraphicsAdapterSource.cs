using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Map.App.Graphics;

/// <summary>
/// DXGI facts for the adapter LUID observed on Avalonia's actual device. No device is created.
/// Without runtime selection, adapter identity stays unknown; adapter zero may be a different GPU.
/// The callback must report observations, not configured preferences. Missing integration is unknown.
/// Native calls are reachable only behind an OS guard. No additional interop package is needed.
/// </summary>
public sealed class WindowsGraphicsAdapterSource(Func<GraphicsBackendObservation>? observeBackend = null)
    : IGraphicsAdapterSource
{
    public GraphicsBackendObservation ReadBackend() => observeBackend?.Invoke() ?? new();

    public GraphicsAdapterInfo ReadAdapter(ulong? selectedLuid)
    {
        if (!OperatingSystem.IsWindows()) return new();
        // Before the render backend reports its device, the primary adapter (DXGI index 0, the one
        // Windows gives new D3D devices by default) is read and marked as assumed, not observed.
        try { return ReadWindowsAdapter(selectedLuid); }
        catch (Exception) { return new(); }
    }

    public bool? ReadRemoteSession()
    {
        if (!OperatingSystem.IsWindows()) return null;
        // SM_REMOTESESSION is documented as nonzero for a remote session and zero otherwise.
        try { return Native.GetSystemMetrics(0x1000) != 0; }
        catch (Exception) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static GraphicsAdapterInfo ReadWindowsAdapter(ulong? selectedLuid)
    {
        IDXGIFactory1? factory = null;
        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            Marshal.ThrowExceptionForHR(Native.CreateDXGIFactory1(ref iid, out factory));
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1? adapter = null;
                try
                {
                    int hr = factory.EnumAdapters1(index, out adapter);
                    if (hr == unchecked((int)0x887A0002)) return new(); // DXGI_ERROR_NOT_FOUND: still unknown
                    Marshal.ThrowExceptionForHR(hr);
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var desc));
                    ulong luid = ((ulong)(uint)desc.AdapterLuid.HighPart << 32) | desc.AdapterLuid.LowPart;
                    if (selectedLuid is ulong wanted ? luid != wanted : index != 0) continue;

                    string? driver = null;
                    // IDXGIDevice identifies the user-mode driver version; an unsupported interface
                    // or failed query leaves only this field unknown, preserving the descriptor.
                    Guid deviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                    try
                    {
                        if (adapter.CheckInterfaceSupport(ref deviceIid, out long packed) >= 0)
                        {
                            ulong version = unchecked((ulong)packed);
                            driver = FormattableString.Invariant(
                                $"{version >> 48}.{(version >> 32) & 0xffff}.{(version >> 16) & 0xffff}.{version & 0xffff}");
                        }
                    }
                    catch (Exception) { /* The descriptor remains usable without a driver version. */ }
                    return new()
                    {
                        AdapterLuid = luid,
                        VendorId = desc.VendorId,
                        DeviceId = desc.DeviceId,
                        Name = desc.Description,
                        DedicatedMemoryBytes = desc.DedicatedVideoMemory.ToUInt64(),
                        SharedMemoryBytes = desc.SharedSystemMemory.ToUInt64(),
                        IsSoftware = (desc.Flags & 2) != 0, // DXGI_ADAPTER_FLAG_SOFTWARE
                        IsBasicRender = desc.VendorId == 0x1414 && desc.DeviceId == 0x008c,
                        // DXGI software flag alone does not establish that Avalonia chose WARP.
                        DriverVersion = driver,
                        IsVirtualMachineHint = desc.VendorId is 0x15ad or 0x80ee or 0x1af4 ? true : null,
                        AdapterAssumed = selectedLuid is null,
                    };
                }
                finally { if (adapter is not null) Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { if (factory is not null) Marshal.ReleaseComObject(factory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // DXGI_ADAPTER_DESC1: WCHAR[128], four UINTs, three SIZE_Ts, LUID, UINT flags.
    // The default native packing also preserves x64 alignment after Flags.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    // All inherited slots are declared in native vtable order, including unused methods.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.Bool)] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid iid, out long driverVersion);
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [DllImport("dxgi.dll", ExactSpelling = true)]
        internal static extern int CreateDXGIFactory1(ref Guid iid, out IDXGIFactory1 factory);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int GetSystemMetrics(int index);
    }
}
