using Map.Core.Resources;

namespace Map.Rendering;

/// <summary>
/// UI-independent resource policy. No allocation is made by granting a budget. MiB = 2^20 bytes.
/// A healthy machine grants min(total RAM share, currently available RAM share), then floor/ceiling.
/// Low memory grants the historical floors; the shared WorkingMemoryBudget may demand less still.
/// All MAP_BUDGET_* overrides are read once, in bytes/counts, and clamped to the same safe bounds.
///
/// Limit                 Total / Available       Floor                 Ceiling       Environment suffix
/// Catalog bytes         1/8 / 1/4               512 MiB               4 GiB         CATALOG_BYTES
/// Image entries         catalog / 128 KiB       4096                  65536         IMAGE_CAPACITY
/// Source/sample bytes   catalog * 3/4, 1/4      384 / 128 MiB         3 / 1 GiB     SOURCE_BYTES / SAMPLE_BYTES
/// Frame pins (per set)  catalog / 8             64 MiB                512 MiB       FRAME_PIN_BYTES
/// Preparation bytes     catalog / 4             128 MiB               1 GiB         PREPARATION_BYTES
/// Settled adoption      catalog / 64            8 MiB                 64 MiB        ADOPT_BYTES
/// Moving adoption       catalog / 256           2 MiB                 16 MiB        GESTURE_ADOPT_BYTES
/// Waiting large mips    1/32 / 1/16             128 MiB               1 GiB         LARGE_WAITING_BYTES
/// Small readers         cores - 2               legacy clamp(2..4)    32            SMALL_READERS
/// Large lanes           cores - 2, catalog/256M legacy clamp(1..4)    32            LARGE_LANES
/// Strip concurrency     cores - 2, catalog/128M legacy clamp(1..4)    32            STRIP_WORKERS
/// Detail workers        cores - 2, catalog/128M legacy clamp(1..8)    32            DETAIL_WORKERS
/// CPU export workers    cores - 2, catalog/128M legacy min(cores,4)   32            EXPORT_WORKERS
/// Moving direct draws   min(cores*128,cat/MiB)   512                   4096          DIRECT_DRAWS
/// Asset cache default   1/32 / 1/8              256 MiB               1 GiB         ASSET_CACHE_BYTES
///
/// Source + samples never exceed the catalog budget, even with overrides. Pins retain existing
/// entries (not additional pixel allocations). Preparation is an admission bound, not another cache.
/// Experimental overrides may at most double the default RAM shares: catalog at most total/4 and
/// available/2, waiting at most total/16 and available/8, asset defaults at most total/16 and available/4.
/// Historical floors win on very small/pressed machines; the shared admission budget can trim lower.
/// The large-lane share allows two 128 MiB source/scratch buffers per lane. Small readers use
/// 256px mips; full decodes on that path retain their existing single-source semaphore.
/// Per-frame values are ceilings for FrameBudgetController; its floors are the old 8/2 MiB/512.
/// Safety limits (image dimensions/encoded/decode maxima) are format validation, not cache defaults.
/// </summary>
public sealed record MachineProfile(int LogicalCores, long TotalMemoryBytes, long AvailableMemoryBytes,
    long MemoryLoadBytes = 0, bool LowMemory = false)
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024 * MiB;

    public MachineLimits Limits(Func<string, string?>? environment = null)
    {
        environment ??= _ => null;
        var cores = Math.Max(1, LogicalCores);
        var total = Math.Max(0, TotalMemoryBytes);
        var available = Math.Clamp(AvailableMemoryBytes, 0, total);
        long Pick(string key, long value, long floor, long ceiling)
        {
            var configured = environment("MAP_BUDGET_" + key);
            if (LowMemory) return floor;
            if (long.TryParse(configured, out var requested) && requested > 0) value = requested;
            return Math.Clamp(value, floor, ceiling);
        }
        var cacheCeiling = Math.Max(512 * MiB, Math.Min(4 * GiB, Math.Min(total / 4, available / 2)));
        var cache = Pick("CATALOG_BYTES", Math.Min(total / 8, available / 4), 512 * MiB, cacheCeiling);
        var sources = Pick("SOURCE_BYTES", cache * 3 / 4, 384 * MiB, cache - 128 * MiB);
        var samples = Pick("SAMPLE_BYTES", cache / 4, 128 * MiB, cache - sources);
        var smallFloor = Math.Clamp(cores - 2, 2, 4);
        var largeFloor = Math.Clamp(cores - 2, 1, 4);
        var stripFloor = Math.Clamp(cores / 2, 1, 4);
        var detailFloor = Math.Clamp(cores - 2, 1, 8);
        var exportFloor = Math.Min(cores, 4);
        return new(
            cache, (int)Pick("IMAGE_CAPACITY", cache / (128 * 1024), 4096, 65536), sources, samples,
            Pick("FRAME_PIN_BYTES", cache / 8, 64 * MiB, cache / 8),
            Pick("PREPARATION_BYTES", cache / 4, 128 * MiB, cache / 4),
            Pick("ADOPT_BYTES", cache / 64, 8 * MiB, 64 * MiB),
            Pick("GESTURE_ADOPT_BYTES", cache / 256, 2 * MiB, 16 * MiB),
            Pick("LARGE_WAITING_BYTES", Math.Min(total / 32, available / 16), 128 * MiB,
                Math.Max(128 * MiB, Math.Min(GiB, Math.Min(total / 16, available / 8)))),
            (int)Pick("SMALL_READERS", cores - 2, smallFloor, 32),
            (int)Pick("LARGE_LANES", cores - 2, largeFloor, Math.Max(largeFloor, Math.Min(32, cache / (256 * MiB)))),
            (int)Pick("STRIP_WORKERS", cores - 2, stripFloor, Math.Max(stripFloor, Math.Min(32, cache / (128 * MiB)))),
            (int)Pick("DETAIL_WORKERS", cores - 2, detailFloor, Math.Max(detailFloor, Math.Min(32, cache / (128 * MiB)))),
            (int)Pick("EXPORT_WORKERS", cores - 2, exportFloor, Math.Max(exportFloor, Math.Min(32, cache / (128 * MiB)))),
            (int)Pick("DIRECT_DRAWS", Math.Min((long)cores * 128, cache / MiB), 512, 4096),
            Pick("ASSET_CACHE_BYTES", Math.Min(total / 32, available / 8), 256 * MiB,
                Math.Max(256 * MiB, Math.Min(GiB, Math.Min(total / 16, available / 4)))));
    }

    public static MachineProfile ReadSystem()
    {
        var gc = GC.GetGCMemoryInfo();
        var memory = WorkingMemoryBudget.Shared.Snapshot;
        var total = gc.TotalAvailableMemoryBytes > 0
            ? Math.Min(memory.PhysicalBytes, gc.TotalAvailableMemoryBytes) : memory.PhysicalBytes;
        var available = Math.Min(memory.AvailableBytes, Math.Max(0, total - gc.MemoryLoadBytes));
        return new(Environment.ProcessorCount, total, available, gc.MemoryLoadBytes);
    }
}

public sealed record MachineLimits(long CatalogBytes, int ImageCapacity, long SourceBytes, long SampleBytes,
    long FramePinBytes, long PreparationBytes, long AdoptBytes, long GestureAdoptBytes, long LargeWaitingBytes,
    int SmallReaders, int LargeLanes, int StripWorkers, int DetailWorkers, int ExportWorkers, int DirectDraws, long AssetCacheBytes);

/// <summary>
/// Reads memory off the canvas thread. One sample on construction, a background sample per second,
/// and an immediate sample on the existing low-memory notification. Recovery requires 25% available
/// and load below 75% (entry: 12.5% available or 85% load). Owners apply the immutable snapshot on
/// their own thread, so notifications never dispose an image that is being drawn.
/// </summary>
public sealed class MachineProfileMonitor : IDisposable
{
    public static MachineProfileMonitor Shared { get; } = new(MachineProfile.ReadSystem, Environment.GetEnvironmentVariable, true);
    private readonly Func<MachineProfile> _read;
    private readonly Func<string, string?> _environment;
    private readonly ITimer? _timer;
    private MachineProfile _profile;
    private MachineLimits _limits;
    private readonly object _gate = new();
    public event Action? PressureDetected;
    public MachineProfile Profile => Volatile.Read(ref _profile);
    public MachineLimits Limits => Volatile.Read(ref _limits);

    public MachineProfileMonitor(Func<MachineProfile> read, Func<string, string?>? environment = null,
        bool monitorSystem = false, TimeProvider? clock = null)
    {
        _read = read;
        // Freeze experimental overrides: no environment calls or parsing on a frame.
        var values = new Dictionary<string, string?>();
        _environment = key => values.TryGetValue(key, out var value) ? value : values[key] = environment?.Invoke(key);
        _profile = read();
        var total = Math.Max(1, _profile.TotalMemoryBytes);
        _profile = _profile with { LowMemory = _profile.LowMemory || _profile.AvailableMemoryBytes < total / 8
            || _profile.MemoryLoadBytes > total / 100 * 85 };
        _limits = _profile.Limits(_environment);
        if (monitorSystem)
        {
            WorkingMemoryBudget.ReleaseDerivedCaches += OnLowMemory;
            _timer = (clock ?? TimeProvider.System).CreateTimer(_ => TryRefresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private void OnLowMemory() => TryRefresh(forceLowMemory: true);

    private void TryRefresh(bool forceLowMemory = false)
    {
        try { Refresh(forceLowMemory); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Keep the last measured budget if an OS query temporarily fails. */ }
    }

    public void Refresh(bool forceLowMemory = false)
    {
        bool entered;
        lock (_gate)
        {
            var sample = _read();
            var total = Math.Max(1, sample.TotalMemoryBytes);
            var pressed = forceLowMemory || sample.LowMemory || sample.AvailableMemoryBytes < total / 8
                || sample.MemoryLoadBytes > total / 100 * 85;
            if (_profile.LowMemory && (sample.AvailableMemoryBytes < total / 4 || sample.MemoryLoadBytes > total / 4 * 3)) pressed = true;
            entered = pressed && !_profile.LowMemory;
            var profile = sample with { LowMemory = pressed };
            Volatile.Write(ref _profile, profile);
            Volatile.Write(ref _limits, profile.Limits(_environment));
        }
        if (entered) PressureDetected?.Invoke();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        WorkingMemoryBudget.ReleaseDerivedCaches -= OnLowMemory;
    }
}
