using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Map.App.Graphics;

public enum GraphicsProfileKind { Unknown, Strong, Weak, SoftwareOnly }

[Flags]
public enum GraphicsProfileReasons
{
    None = 0,
    UnknownAdapter = 1 << 0,
    UnknownBackend = 1 << 1,
    UnknownMeasurement = 1 << 2,
    SoftwareBackend = 1 << 3,
    SoftwareAdapter = 1 << 4,
    Warp = 1 << 5,
    BasicRender = 1 << 6,
    SharedOnlyMemory = 1 << 7,
    SlowBenchmark = 1 << 8,
    FastBenchmark = 1 << 9,
    RemoteSession = 1 << 10,
    VirtualMachineHint = 1 << 11,
    Inconclusive = 1 << 12
}

/// <summary>Provisional startup thresholds, to calibrate with the real fixed workload.</summary>
public sealed record GraphicsProfileTuning
{
    public double StrongMaxFrameP95Ms { get; init; } = 8;
    public double SharedOnlyWeakMinFrameP95Ms { get; init; } = 12;
    public double WeakMinFrameP95Ms { get; init; } = 1000.0 / 60;
    public ulong StrongMinMemoryBytes { get; init; } = 512UL * 1024 * 1024;

    internal void Validate()
    {
        if (!double.IsFinite(StrongMaxFrameP95Ms) || StrongMaxFrameP95Ms <= 0
            || !double.IsFinite(SharedOnlyWeakMinFrameP95Ms)
            || !double.IsFinite(WeakMinFrameP95Ms)
            || SharedOnlyWeakMinFrameP95Ms <= StrongMaxFrameP95Ms
            || WeakMinFrameP95Ms < SharedOnlyWeakMinFrameP95Ms || StrongMinMemoryBytes == 0)
            throw new ArgumentException("Invalid graphics profile thresholds.");
    }
}

/// <summary>Pure startup hint. Does not change rendering or drive ReserveController yet.</summary>
public sealed record GraphicsProfile(GraphicsProfileKind Kind, GraphicsProfileReasons Reasons)
{
    public static GraphicsProfile Classify(IGraphicsAdapterInfo info, GraphicsStartupMeasurement measured,
        GraphicsProfileTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        tuning ??= new();
        tuning.Validate();
        var reasons = GraphicsProfileReasons.None;
        if (info.IsRemoteSession == true) reasons |= GraphicsProfileReasons.RemoteSession;
        if (info.IsVirtualMachineHint == true) reasons |= GraphicsProfileReasons.VirtualMachineHint;
        if (info.Backend == GraphicsBackend.Software) reasons |= GraphicsProfileReasons.SoftwareBackend;
        if (info.IsSoftware == true) reasons |= GraphicsProfileReasons.SoftwareAdapter;
        if (info.IsWarp == true) reasons |= GraphicsProfileReasons.Warp;
        if (info.IsBasicRender == true) reasons |= GraphicsProfileReasons.BasicRender;
        if ((reasons & (GraphicsProfileReasons.SoftwareBackend | GraphicsProfileReasons.SoftwareAdapter
            | GraphicsProfileReasons.Warp | GraphicsProfileReasons.BasicRender)) != 0)
            return new(GraphicsProfileKind.SoftwareOnly, reasons);

        // Availability of ANGLE says nothing about the backend that Avalonia selected.
        bool gpuBackend = info.Backend is GraphicsBackend.AngleD3D11 or GraphicsBackend.AngleD3D9
            or GraphicsBackend.OpenGL or GraphicsBackend.Vulkan;
        if (!gpuBackend) reasons |= GraphicsProfileReasons.UnknownBackend;
        bool knownAdapter = info.IsSoftware == false && info.IsWarp == false && info.IsBasicRender == false
            && info.DedicatedMemoryBytes.HasValue && info.SharedMemoryBytes.HasValue;
        if (!knownAdapter) reasons |= GraphicsProfileReasons.UnknownAdapter;
        bool validMeasurement = measured.FrameP95Ms is double ms && double.IsFinite(ms) && ms > 0;
        if (!validMeasurement) reasons |= GraphicsProfileReasons.UnknownMeasurement;
        if (!gpuBackend || !knownAdapter || !validMeasurement)
            return new(GraphicsProfileKind.Unknown, reasons);

        bool sharedOnly = info.DedicatedMemoryBytes == 0 && info.SharedMemoryBytes > 0;
        if (sharedOnly) reasons |= GraphicsProfileReasons.SharedOnlyMemory;
        double cost = measured.FrameP95Ms!.Value;
        if (cost >= tuning.WeakMinFrameP95Ms || (sharedOnly && cost >= tuning.SharedOnlyWeakMinFrameP95Ms))
            return new(GraphicsProfileKind.Weak, reasons | GraphicsProfileReasons.SlowBenchmark);
        if (cost <= tuning.StrongMaxFrameP95Ms
            && (info.DedicatedMemoryBytes >= tuning.StrongMinMemoryBytes
                || info.SharedMemoryBytes >= tuning.StrongMinMemoryBytes))
            return new(GraphicsProfileKind.Strong, reasons | GraphicsProfileReasons.FastBenchmark);
        return new(GraphicsProfileKind.Unknown, reasons | GraphicsProfileReasons.Inconclusive);
    }

    /// <summary>
    /// Safe filename component for a future local profile file. Missing identity disables reuse.
    /// v1 versions both the identity encoding and the initial benchmark/profile contract.
    /// No profile file is read or written here.
    /// </summary>
    public static string? CreateCacheKey(IGraphicsAdapterInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.AdapterLuid is not ulong luid || info.VendorId is not uint vendor
            || info.DeviceId is not uint device || string.IsNullOrWhiteSpace(info.DriverVersion)
            || !Enum.IsDefined(info.Backend) || info.Backend == GraphicsBackend.Unknown)
            return null;
        string identity = string.Create(CultureInfo.InvariantCulture,
            $"v1|{luid:X16}|{vendor:X8}|{device:X8}|{(int)info.Backend}|{info.DriverVersion}");
        return "graphics-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
