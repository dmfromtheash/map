using Map.Rendering;

namespace Map.Rendering.Tests;

public sealed class MachineProfileTests
{
    [Theory]
    [InlineData(4, 8, 6, 1024, 8192, 2, 2, 2, 512, 256, 4)] // USER
    [InlineData(16, 16, 12, 2048, 16384, 14, 8, 14, 2048, 512, 14)] // PC
    [InlineData(8, 16, 12, 2048, 16384, 6, 6, 6, 1024, 512, 6)] // Mac
    public void Machine_table_uses_cpu_and_memory(int cores, int total, int available, int cacheMiB,
        int entries, int readers, int lanes, int strips, int draws, int assetMiB, int exportWorkers)
    {
        var limits = new MachineProfile(cores, total * MachineProfile.GiB, available * MachineProfile.GiB).Limits();
        Assert.Equal(cacheMiB * MachineProfile.MiB, limits.CatalogBytes);
        Assert.Equal(entries, limits.ImageCapacity);
        Assert.Equal(limits.CatalogBytes * 3 / 4, limits.SourceBytes);
        Assert.Equal(limits.CatalogBytes / 4, limits.SampleBytes);
        Assert.Equal(limits.CatalogBytes / 8, limits.FramePinBytes);
        Assert.Equal(limits.CatalogBytes / 4, limits.PreparationBytes);
        Assert.Equal(limits.CatalogBytes / 64, limits.AdoptBytes);
        Assert.Equal(limits.CatalogBytes / 256, limits.GestureAdoptBytes);
        Assert.Equal(total * MachineProfile.GiB / 32, limits.LargeWaitingBytes);
        Assert.Equal(readers, limits.SmallReaders);
        Assert.Equal(lanes, limits.LargeLanes);
        Assert.Equal(strips, limits.StripWorkers);
        Assert.Equal(strips, limits.DetailWorkers);
        Assert.Equal(exportWorkers, limits.ExportWorkers);
        Assert.Equal(draws, limits.DirectDraws);
        Assert.Equal(assetMiB * MachineProfile.MiB, limits.AssetCacheBytes);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(4, 8, 1)]
    [InlineData(256, 1024, 1024)]
    public void All_limits_are_bounded_and_pressure_returns_historical_floors(int cores, int total, int available)
    {
        var profile = new MachineProfile(cores, total * MachineProfile.GiB, available * MachineProfile.GiB);
        var limits = profile.Limits(_ => long.MaxValue.ToString());
        Assert.InRange(limits.CatalogBytes, 512 * MachineProfile.MiB, 4 * MachineProfile.GiB);
        Assert.True(limits.SourceBytes + limits.SampleBytes <= limits.CatalogBytes);
        Assert.InRange(limits.LargeLanes, 1, 32);
        Assert.True(limits.CatalogBytes <= Math.Max(512 * MachineProfile.MiB, available * MachineProfile.GiB / 2));
        Assert.True(limits.CatalogBytes <= Math.Max(512 * MachineProfile.MiB, total * MachineProfile.GiB / 4));
        var floor = (profile with { LowMemory = true }).Limits(_ => long.MaxValue.ToString());
        Assert.Equal(512 * MachineProfile.MiB, floor.CatalogBytes);
        Assert.Equal(4096, floor.ImageCapacity);
        Assert.Equal(384 * MachineProfile.MiB, floor.SourceBytes);
        Assert.Equal(128 * MachineProfile.MiB, floor.SampleBytes);
        Assert.Equal(64 * MachineProfile.MiB, floor.FramePinBytes);
        Assert.Equal(128 * MachineProfile.MiB, floor.PreparationBytes);
        Assert.Equal(8 * MachineProfile.MiB, floor.AdoptBytes);
        Assert.Equal(2 * MachineProfile.MiB, floor.GestureAdoptBytes);
        Assert.Equal(128 * MachineProfile.MiB, floor.LargeWaitingBytes);
        Assert.Equal(512, floor.DirectDraws);
        Assert.Equal(256 * MachineProfile.MiB, floor.AssetCacheBytes);
    }

    [Fact]
    public void Falling_available_ram_shrinks_immediately_and_recovery_has_hysteresis()
    {
        var sample = new MachineProfile(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB);
        using var monitor = new MachineProfileMonitor(() => sample);
        var signals = 0;
        monitor.PressureDetected += () => signals++;
        sample = sample with { AvailableMemoryBytes = MachineProfile.GiB };
        monitor.Refresh();
        Assert.True(monitor.Profile.LowMemory);
        Assert.Equal(512 * MachineProfile.MiB, monitor.Limits.CatalogBytes);
        sample = sample with { AvailableMemoryBytes = 3 * MachineProfile.GiB };
        monitor.Refresh();
        Assert.True(monitor.Profile.LowMemory);
        Assert.Equal(1, signals);
        sample = sample with { AvailableMemoryBytes = 12 * MachineProfile.GiB };
        monitor.Refresh();
        Assert.False(monitor.Profile.LowMemory);
        Assert.Equal(2 * MachineProfile.GiB, monitor.Limits.CatalogBytes);
        monitor.Refresh(forceLowMemory: true);
        Assert.True(monitor.Profile.LowMemory);
        Assert.Equal(2, signals);
    }

    [Fact]
    public void Memory_load_alone_triggers_pressure_and_override_cannot_spend_reserved_shares()
    {
        var sample = new MachineProfile(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB);
        using var monitor = new MachineProfileMonitor(() => sample, _ => long.MaxValue.ToString());
        Assert.True(monitor.Limits.SourceBytes + monitor.Limits.SampleBytes <= monitor.Limits.CatalogBytes);
        Assert.True(monitor.Limits.LargeLanes * 256 * MachineProfile.MiB <= monitor.Limits.CatalogBytes);
        sample = sample with { MemoryLoadBytes = 15 * MachineProfile.GiB };
        monitor.Refresh();
        Assert.Equal(512 * MachineProfile.MiB, monitor.Limits.CatalogBytes);
    }
}
