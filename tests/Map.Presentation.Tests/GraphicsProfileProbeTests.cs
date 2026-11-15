using System.Globalization;
using Map.App.Graphics;

namespace Map.Presentation.Tests;

public sealed class GraphicsProfileProbeTests
{
    private static readonly GraphicsAdapterInfo Hardware = new()
    {
        AdapterLuid = 0xfedcba9876543210,
        VendorId = 0x10de,
        DeviceId = 0x1234,
        Name = "Fake hardware",
        DedicatedMemoryBytes = 4UL * 1024 * 1024 * 1024,
        SharedMemoryBytes = 8UL * 1024 * 1024 * 1024,
        IsSoftware = false,
        IsWarp = false,
        IsBasicRender = false,
        DriverVersion = "32.0.1.2",
        Backend = GraphicsBackend.AngleD3D11,
        AngleAvailable = true
    };

    [Fact]
    public void Unknown_facts_and_an_unmeasured_benchmark_stay_unknown()
    {
        var profile = GraphicsProfile.Classify(new GraphicsAdapterInfo(), default);
        Assert.Equal(GraphicsProfileKind.Unknown, profile.Kind);
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.UnknownAdapter));
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.UnknownBackend));
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.UnknownMeasurement));
        Assert.Null(GraphicsProfile.CreateCacheKey(new GraphicsAdapterInfo()));
    }

    [Theory]
    [InlineData(1, GraphicsProfileKind.Strong)]
    [InlineData(8, GraphicsProfileKind.Strong)]
    [InlineData(8.001, GraphicsProfileKind.Unknown)]
    [InlineData(12, GraphicsProfileKind.Unknown)]
    [InlineData(1000.0 / 60, GraphicsProfileKind.Weak)]
    [InlineData(40, GraphicsProfileKind.Weak)]
    public void Hardware_uses_measured_cost_and_explicit_boundaries(double ms, GraphicsProfileKind expected)
    {
        var profile = GraphicsProfile.Classify(Hardware, new(ms));
        Assert.Equal(expected, profile.Kind);
        if (expected == GraphicsProfileKind.Strong)
            Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.FastBenchmark));
        if (expected == GraphicsProfileKind.Weak)
            Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.SlowBenchmark));
    }

    [Theory]
    [InlineData(8, GraphicsProfileKind.Strong)]
    [InlineData(11.999, GraphicsProfileKind.Unknown)]
    [InlineData(12, GraphicsProfileKind.Weak)]
    public void Shared_only_memory_needs_measurement_not_a_vendor_blacklist(double ms, GraphicsProfileKind expected)
    {
        var integrated = Hardware with { VendorId = 0x8086, DedicatedMemoryBytes = 0 };
        var profile = GraphicsProfile.Classify(integrated, new(ms));
        Assert.Equal(expected, profile.Kind);
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.SharedOnlyMemory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_measurements_do_not_fabricate_health_or_weakness(double ms)
    {
        var profile = GraphicsProfile.Classify(Hardware, new(ms));
        Assert.Equal(GraphicsProfileKind.Unknown, profile.Kind);
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.UnknownMeasurement));
    }

    [Fact]
    public void Every_software_signal_is_positive_evidence_even_without_measurement()
    {
        foreach (var (info, reason) in new[]
        {
            (new GraphicsAdapterInfo { Backend = GraphicsBackend.Software }, GraphicsProfileReasons.SoftwareBackend),
            (new GraphicsAdapterInfo { IsSoftware = true }, GraphicsProfileReasons.SoftwareAdapter),
            (new GraphicsAdapterInfo { IsWarp = true }, GraphicsProfileReasons.Warp),
            (new GraphicsAdapterInfo { IsBasicRender = true }, GraphicsProfileReasons.BasicRender)
        })
        {
            var profile = GraphicsProfile.Classify(info, default);
            Assert.Equal(GraphicsProfileKind.SoftwareOnly, profile.Kind);
            Assert.True(profile.Reasons.HasFlag(reason));
        }
    }

    [Fact]
    public void Unknown_software_flags_memory_or_backend_are_not_false_or_zero()
    {
        foreach (var incomplete in new[]
        {
            Hardware with { IsSoftware = null }, Hardware with { IsWarp = null },
            Hardware with { IsBasicRender = null }, Hardware with { DedicatedMemoryBytes = null },
            Hardware with { SharedMemoryBytes = null }, Hardware with { Backend = GraphicsBackend.Unknown },
            Hardware with { Backend = (GraphicsBackend)999 }
        })
            Assert.Equal(GraphicsProfileKind.Unknown, GraphicsProfile.Classify(incomplete, new(1)).Kind);
    }

    [Fact]
    public void Remote_and_vm_hints_are_reasons_not_automatic_weakness()
    {
        var profile = GraphicsProfile.Classify(Hardware with { IsRemoteSession = true, IsVirtualMachineHint = true }, new(1));
        Assert.Equal(GraphicsProfileKind.Strong, profile.Kind);
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.RemoteSession));
        Assert.True(profile.Reasons.HasFlag(GraphicsProfileReasons.VirtualMachineHint));
    }

    [Fact]
    public void Angle_availability_does_not_establish_a_chosen_backend()
    {
        Assert.Equal(GraphicsProfileKind.Unknown,
            GraphicsProfile.Classify(Hardware with { Backend = GraphicsBackend.Unknown, AngleAvailable = true }, new(1)).Kind);
        Assert.Equal(GraphicsProfileKind.Strong,
            GraphicsProfile.Classify(Hardware with { Backend = GraphicsBackend.OpenGL, AngleAvailable = false }, new(1)).Kind);
    }

    [Fact]
    public void All_policy_thresholds_are_tunable_and_invalid_tuning_is_rejected()
    {
        Assert.Equal(GraphicsProfileKind.Weak, GraphicsProfile.Classify(Hardware, new(11), new()
        {
            StrongMaxFrameP95Ms = 5, SharedOnlyWeakMinFrameP95Ms = 9, WeakMinFrameP95Ms = 10
        }).Kind);
        Assert.Equal(GraphicsProfileKind.Unknown, GraphicsProfile.Classify(Hardware, new(1), new()
        {
            StrongMinMemoryBytes = 16UL * 1024 * 1024 * 1024
        }).Kind);
        foreach (var invalid in new[]
        {
            new GraphicsProfileTuning { StrongMaxFrameP95Ms = 0 },
            new GraphicsProfileTuning { StrongMaxFrameP95Ms = double.NaN },
            new GraphicsProfileTuning { SharedOnlyWeakMinFrameP95Ms = 8 },
            new GraphicsProfileTuning { WeakMinFrameP95Ms = 10 },
            new GraphicsProfileTuning { WeakMinFrameP95Ms = double.PositiveInfinity },
            new GraphicsProfileTuning { SharedOnlyWeakMinFrameP95Ms = double.NaN },
            new GraphicsProfileTuning { StrongMinMemoryBytes = 0 }
        })
            Assert.Throws<ArgumentException>(() => GraphicsProfile.Classify(Hardware, new(1), invalid));
    }

    [Fact]
    public void Cache_identity_changes_with_driver_adapter_and_backend()
    {
        string key = Assert.IsType<string>(GraphicsProfile.CreateCacheKey(Hardware));
        Assert.StartsWith("graphics-v1-", key);
        Assert.Equal(key, GraphicsProfile.CreateCacheKey(Hardware with { Name = "Renamed", IsRemoteSession = true }));
        foreach (var changed in new[]
        {
            Hardware with { DriverVersion = "32.0.1.3" }, Hardware with { AdapterLuid = 1 },
            Hardware with { VendorId = 1 }, Hardware with { DeviceId = 1 },
            Hardware with { Backend = GraphicsBackend.OpenGL }
        })
            Assert.NotEqual(key, GraphicsProfile.CreateCacheKey(changed));
        foreach (var incomplete in new[]
        {
            Hardware with { DriverVersion = null }, Hardware with { DriverVersion = " " },
            Hardware with { AdapterLuid = null }, Hardware with { VendorId = null },
            Hardware with { DeviceId = null }, Hardware with { Backend = GraphicsBackend.Unknown },
            Hardware with { Backend = (GraphicsBackend)999 }
        })
            Assert.Null(GraphicsProfile.CreateCacheKey(incomplete));
    }

    [Fact]
    public void Cache_identity_does_not_depend_on_current_culture()
    {
        string? key = GraphicsProfile.CreateCacheKey(Hardware);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(key, GraphicsProfile.CreateCacheKey(Hardware));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("adapter")]
    [InlineData("remote")]
    [InlineData("benchmark")]
    public async Task Api_failures_remain_unknown_and_preserve_independent_facts(string failure)
    {
        var source = new FakeSource(failure);
        IGraphicsStartupBenchmark benchmark = failure == "benchmark" ? new FailedBenchmark() : new FakeGraphicsStartupBenchmark(new(1));
        var result = await new GraphicsProfileProbe(source, benchmark).ProbeAsync();
        if (failure == "backend")
        {
            Assert.Equal(GraphicsBackend.Unknown, result.Info.Backend);
            Assert.Null(result.Info.AngleAvailable);
            Assert.Null(result.Info.IsWarp);
            Assert.Null(result.Info.AdapterLuid);
            Assert.Equal(GraphicsProfileKind.Unknown, result.Profile.Kind);
        }
        if (failure == "adapter")
        {
            Assert.Null(result.Info.IsSoftware);
            Assert.Null(result.Info.IsBasicRender);
            Assert.Null(result.Info.DedicatedMemoryBytes);
            Assert.Null(result.Info.SharedMemoryBytes);
            Assert.Null(result.Info.DriverVersion);
            Assert.Equal(GraphicsProfileKind.Unknown, result.Profile.Kind);
        }
        if (failure == "remote")
        {
            Assert.Null(result.Info.IsRemoteSession);
            Assert.Equal(GraphicsProfileKind.Strong, result.Profile.Kind);
        }
        else Assert.True(result.Info.IsRemoteSession);
        if (failure == "benchmark")
        {
            Assert.Null(result.Measured.FrameP95Ms);
            Assert.Equal(GraphicsProfileKind.Unknown, result.Profile.Kind);
        }
    }

    [Fact]
    public async Task Successful_probe_uses_the_observed_device_and_backend()
    {
        var source = new FakeSource();
        var result = await new GraphicsProfileProbe(source, new FakeGraphicsStartupBenchmark(new(1))).ProbeAsync();
        Assert.Equal(Hardware.AdapterLuid, source.RequestedLuid);
        Assert.Equal(GraphicsProfileKind.Strong, result.Profile.Kind);
        Assert.Equal(GraphicsBackend.AngleD3D11, result.Info.Backend);
        Assert.Equal(GraphicsProfile.CreateCacheKey(Hardware), result.CacheKey);
    }

    [Fact]
    public async Task Missing_driver_disables_cache_without_discarding_known_hardware()
    {
        var result = await new GraphicsProfileProbe(new FakeSource { Adapter = Hardware with { DriverVersion = null } },
            new FakeGraphicsStartupBenchmark(new(1))).ProbeAsync();
        Assert.Null(result.CacheKey);
        Assert.Null(result.Info.DriverVersion);
        Assert.Equal(GraphicsProfileKind.Strong, result.Profile.Kind);
    }

    [Fact]
    public async Task Mismatched_adapter_identity_is_unknown_and_not_cacheable()
    {
        var result = await new GraphicsProfileProbe(new FakeSource { Adapter = Hardware with { AdapterLuid = 1 } },
            new FakeGraphicsStartupBenchmark(new(1))).ProbeAsync();
        Assert.Equal(GraphicsProfileKind.Unknown, result.Profile.Kind);
        Assert.Null(result.Info.IsSoftware);
        Assert.Null(result.Info.AdapterLuid);
        Assert.Null(result.CacheKey);
    }

    [Fact]
    public async Task Default_fake_is_unmeasured_and_cancellation_is_not_swallowed()
    {
        var result = await new GraphicsProfileProbe(new FakeSource(), new FakeGraphicsStartupBenchmark()).ProbeAsync();
        Assert.Equal(GraphicsProfileKind.Unknown, result.Profile.Kind);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new GraphicsProfileProbe(new FakeSource(), new FakeGraphicsStartupBenchmark()).ProbeAsync(cancellation.Token));
        using var during = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new GraphicsProfileProbe(new FakeSource(), new CancellingBenchmark(during)).ProbeAsync(during.Token));
    }

    private sealed class FakeSource(string? failure = null) : IGraphicsAdapterSource
    {
        public GraphicsAdapterInfo Adapter { get; init; } = Hardware;
        public ulong? RequestedLuid { get; private set; }
        public GraphicsBackendObservation ReadBackend() => failure == "backend" ? throw new InvalidOperationException()
            : new(GraphicsBackend.AngleD3D11, Hardware.AdapterLuid, true, false);
        public GraphicsAdapterInfo ReadAdapter(ulong? selectedLuid)
        {
            RequestedLuid = selectedLuid;
            if (failure == "adapter") throw new InvalidOperationException();
            return selectedLuid == Hardware.AdapterLuid ? Adapter : new();
        }
        public bool? ReadRemoteSession() => failure == "remote" ? throw new InvalidOperationException() : true;
    }

    private sealed class FailedBenchmark : IGraphicsStartupBenchmark
    {
        public ValueTask<GraphicsStartupMeasurement> MeasureAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class CancellingBenchmark(CancellationTokenSource cancellation) : IGraphicsStartupBenchmark
    {
        public ValueTask<GraphicsStartupMeasurement> MeasureAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(default(GraphicsStartupMeasurement));
        }
    }
}

/// <summary>Opt-in (MAP_REAL_GPU_PROBE=1): reads this machine's real DXGI adapter; prints, never fails on Unknown.</summary>
public sealed class RealGraphicsAdapterProbe(Xunit.Abstractions.ITestOutputHelper output)
{
    [OptInProbe("MAP_REAL_GPU_PROBE", "GraphicsProfileProbeTests.Successful_probe_uses_the_observed_device_and_backend", requiredValue: "1")]
    public void Reads_the_real_adapter_without_throwing()
    {
        Assert.True(OperatingSystem.IsWindows(), "The real DXGI probe requires Windows.");
        var source = new Map.App.Graphics.WindowsGraphicsAdapterSource();
        var adapter = source.ReadAdapter(null);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(adapter));
        output.WriteLine("remote=" + source.ReadRemoteSession());
    }
}
