using System.Reflection;
using Map.Assets;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.Tests;

[CollectionDefinition("BudgetExperiments", DisableParallelization = true)]
public sealed class BudgetExperimentsCollection;

[Collection("BudgetExperiments")]
public sealed class BudgetMergeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-budget-merge", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("64", "32", 64, 32)]
    [InlineData("0", "0", 0, 0)]
    [InlineData("65536", "65536", 65536, 65536)]
    [InlineData(null, null, -1, -1)]
    [InlineData("-1", "invalid", -1, -1)]
    [InlineData("65537", "65537", -1, -1)]
    public async Task Legacy_mip_experiments_override_adaptation_and_pressure_but_setters_still_win(
        string? adopt, string? gesture, int expectedAdopt, int expectedGesture)
    {
        const string adoptKey = "MAP_MIP_ADOPT_MB", gestureKey = "MAP_MIP_GESTURE_ADOPT_MB";
        var previousAdopt = Environment.GetEnvironmentVariable(adoptKey);
        var previousGesture = Environment.GetEnvironmentVariable(gestureKey);
        try
        {
            Environment.SetEnvironmentVariable(adoptKey, adopt);
            Environment.SetEnvironmentVariable(gestureKey, gesture);
            var profile = new MachineProfile(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB);
            using var machine = new MachineProfileMonitor(() => profile);
            await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
            var clock = new FrameClock();
            await using var source = new CatalogStampImageSource(catalog, () => null!, () => null,
                machine: machine, frameClock: clock);
            Assert.Equal((expectedAdopt < 0 ? 8 : expectedAdopt) * MachineProfile.MiB, source.AdoptedMipBytesPerFrame);
            Assert.Equal((expectedGesture < 0 ? 2 : expectedGesture) * MachineProfile.MiB, source.GestureAdoptedMipBytesPerFrame);
            Environment.SetEnvironmentVariable(adoptKey, "1");
            Environment.SetEnvironmentVariable(gestureKey, "1");
            for (var i = 0; i < 700; i++)
            {
                clock.Advance(5);
                using (source.BeginFrame()) clock.Advance(1);
            }
            Assert.Equal((expectedAdopt < 0 ? 32 : expectedAdopt) * MachineProfile.MiB, source.AdoptedMipBytesPerFrame);
            Assert.Equal((expectedGesture < 0 ? 8 : expectedGesture) * MachineProfile.MiB, source.GestureAdoptedMipBytesPerFrame);
            profile = profile with { AvailableMemoryBytes = MachineProfile.GiB };
            machine.Refresh();
            Assert.Equal((expectedAdopt < 0 ? 8 : expectedAdopt) * MachineProfile.MiB, source.AdoptedMipBytesPerFrame);
            Assert.Equal((expectedGesture < 0 ? 2 : expectedGesture) * MachineProfile.MiB, source.GestureAdoptedMipBytesPerFrame);
            source.AdoptedMipBytesPerFrame = 123;
            source.GestureAdoptedMipBytesPerFrame = 456;
            Assert.Equal(123, source.AdoptedMipBytesPerFrame);
            Assert.Equal(456, source.GestureAdoptedMipBytesPerFrame);
        }
        finally
        {
            Environment.SetEnvironmentVariable(adoptKey, previousAdopt);
            Environment.SetEnvironmentVariable(gestureKey, previousGesture);
        }
    }

    [Theory]
    [InlineData(4, 8, 6, 256)] // USER
    [InlineData(16, 16, 12, 512)] // PC
    [InlineData(8, 16, 12, 512)] // Mac
    public async Task Large_reservations_use_the_injected_profile_and_keep_pixelstall_ownership(
        int cores, int total, int available, int waitingMiB)
    {
        var profile = new MachineProfile(cores, total * MachineProfile.GiB, available * MachineProfile.GiB);
        using var machine = new MachineProfileMonitor(() => profile);
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"),
            machine: machine, startWatcher: _ => { }, maintenance: new() { Automatic = false });
        // Exercise the real reservation boundary without allocating hundreds of MiB of pixels.
        // Real decode/adoption/retirement races remain covered by LargeAccountingTests.
        var requestType = typeof(StampDerivedAssetStore).GetNestedType("MipRequest", BindingFlags.NonPublic)!;
        var reserve = typeof(StampDerivedAssetStore).GetMethod("ReserveLarge", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var release = typeof(StampDerivedAssetStore).GetMethod("ReleaseLarge", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [requestType], null)!;
        using var heldStop = new CancellationTokenSource();
        using var fittingStop = new CancellationTokenSource();
        using var waitingStop = new CancellationTokenSource();
        var held = Activator.CreateInstance(requestType, heldStop)!;
        var fitting = Activator.CreateInstance(requestType, fittingStop)!;
        var waiting = Activator.CreateInstance(requestType, waitingStop)!;
        Task Reserve(long bytes, object request) => (Task)reserve.Invoke(store, [bytes, request])!;
        void Release(object request) => release.Invoke(store, [request]);
        var budget = waitingMiB * MachineProfile.MiB;
        Assert.Equal(budget, machine.Limits.LargeWaitingBytes);
        try
        {
            await Reserve(budget - MachineProfile.MiB, held).WaitAsync(TimeSpan.FromSeconds(5));
            // Two reservations distinguish the actual limit from the single-oversized allowance.
            await Reserve(MachineProfile.MiB, fitting).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(budget, store.LargeStatistics.WaitingBytes);
            var blocked = Reserve(MachineProfile.MiB, waiting);
            Assert.False(blocked.IsCompleted);
            waitingStop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            Release(waiting);
            Assert.Equal(budget, store.LargeStatistics.WaitingBytes);
            Release(fitting);
            Release(held);
            Release(held);
            Assert.Equal(0, store.LargeStatistics.WaitingBytes);
            await Reserve(budget + 1, held).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(budget + 1, store.LargeStatistics.WaitingBytes);
            profile = profile with { AvailableMemoryBytes = total * MachineProfile.GiB / 16 };
            machine.Refresh();
            Assert.Equal(128 * MachineProfile.MiB, machine.Limits.LargeWaitingBytes);
            Assert.Equal(budget + 1, store.LargeStatistics.WaitingBytes);
            Release(held);
            await Reserve(128 * MachineProfile.MiB, held).WaitAsync(TimeSpan.FromSeconds(5));
            using var pressedStop = new CancellationTokenSource();
            var pressed = Activator.CreateInstance(requestType, pressedStop)!;
            var pressedWait = Reserve(1, pressed);
            Assert.False(pressedWait.IsCompleted);
            Release(held);
            await pressedWait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, store.LargeStatistics.WaitingBytes);
            Release(pressed);
            Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        }
        finally
        {
            heldStop.Cancel();
            fittingStop.Cancel();
            waitingStop.Cancel();
            Release(fitting);
            Release(waiting);
            Release(held);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FrameClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += milliseconds * TimeSpan.TicksPerMillisecond;
    }
}
