namespace Map.App.Graphics;

/// <summary>Immutable startup facts. Null means unknown, never absence or zero.</summary>
public interface IGraphicsAdapterInfo
{
    ulong? AdapterLuid { get; }
    uint? VendorId { get; }
    uint? DeviceId { get; }
    string? Name { get; }
    ulong? DedicatedMemoryBytes { get; }
    ulong? SharedMemoryBytes { get; }
    bool? IsSoftware { get; }
    bool? IsWarp { get; }
    bool? IsBasicRender { get; }
    string? DriverVersion { get; }
    bool? IsRemoteSession { get; }
    bool? IsVirtualMachineHint { get; }
    bool? AngleAvailable { get; }
    GraphicsBackend Backend { get; }
}

public enum GraphicsBackend { Unknown, Software, AngleD3D11, AngleD3D9, OpenGL, Vulkan }

public sealed record GraphicsAdapterInfo : IGraphicsAdapterInfo
{
    public ulong? AdapterLuid { get; init; }
    public uint? VendorId { get; init; }
    public uint? DeviceId { get; init; }
    public string? Name { get; init; }
    public ulong? DedicatedMemoryBytes { get; init; }
    public ulong? SharedMemoryBytes { get; init; }
    public bool? IsSoftware { get; init; }
    public bool? IsWarp { get; init; }
    public bool? IsBasicRender { get; init; }
    public string? DriverVersion { get; init; }
    public bool? IsRemoteSession { get; init; }
    public bool? IsVirtualMachineHint { get; init; }
    public bool? AngleAvailable { get; init; }
    public GraphicsBackend Backend { get; init; }
    /// <summary>True when the backend's device was not known yet and the primary adapter was read instead.</summary>
    public bool AdapterAssumed { get; init; }
}

/// <summary>
/// Observed runtime selection, supplied by the future Avalonia startup integration.
/// Options, installed DLLs and DXGI enumeration order do not establish a chosen backend.
/// AdapterLuid must identify the device actually used by that backend.
/// </summary>
public sealed record GraphicsBackendObservation(
    GraphicsBackend Backend = GraphicsBackend.Unknown,
    ulong? AdapterLuid = null,
    bool? AngleAvailable = null,
    bool? IsWarp = null);

/// <summary>Independent reads let one failed API leave the other facts intact.</summary>
public interface IGraphicsAdapterSource
{
    GraphicsBackendObservation ReadBackend();
    GraphicsAdapterInfo ReadAdapter(ulong? selectedLuid);
    bool? ReadRemoteSession();
}
