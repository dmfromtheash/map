namespace Map.Core.Resources;

/// <summary>Order in which caches give memory back while Windows is short of it: cheapest to rebuild first.</summary>
public enum CacheRebuildCost { Cheap = 0, Moderate = 1, Expensive = 2 }

public readonly record struct CacheMemoryStatistics(string Name, CacheRebuildCost Cost, long Bytes, long PeakBytes,
    long LimitBytes, long TargetBytes, long Trims, long TrimmedBytes, long TrimmedItems);

/// <summary>
/// The byte account of one derived cache. It holds no images and calls nothing back: the cache owner reports its
/// bytes and, on its own thread, trims to <see cref="TargetBytes"/> when that is lower than what it holds.
/// </summary>
public sealed class CacheMemoryAccount : IDisposable
{
    private readonly WorkingMemoryBudget _owner;
    private long _bytes, _peak, _limit, _trims, _trimmedBytes, _trimmedItems;
    private int _disposed;

    internal CacheMemoryAccount(WorkingMemoryBudget owner, string name, CacheRebuildCost cost, long requestedBytes)
    {
        _owner = owner; Name = name; Cost = cost; RequestedBytes = requestedBytes; _limit = requestedBytes;
    }

    public string Name { get; }
    public CacheRebuildCost Cost { get; }
    public long RequestedBytes { get; }
    /// <summary>This cache's share of the one budget derived from physical RAM (normally all it asked for).</summary>
    public long LimitBytes => Interlocked.Read(ref _limit);
    /// <summary>
    /// What the cache should hold at most right now. <see cref="long.MaxValue"/> unless Windows is short of memory
    /// and the pressure has reached this cost class; then the watermark of <see cref="LimitBytes"/>.
    /// </summary>
    public long TargetBytes => _owner.CachePressureStage >= (int)Cost
        ? LimitBytes * WorkingMemoryBudget.CachePressureWatermarkPercent / 100 : long.MaxValue;
    public long Bytes => Interlocked.Read(ref _bytes);
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Report(long bytes)
    {
        bytes = Math.Max(0, bytes);
        Interlocked.Exchange(ref _bytes, bytes);
        var peak = Interlocked.Read(ref _peak);
        if (bytes > peak) Interlocked.CompareExchange(ref _peak, bytes, peak);
    }

    public void RecordTrim(long bytes, int items)
    {
        if (bytes <= 0 && items <= 0) return;
        Interlocked.Increment(ref _trims);
        Interlocked.Add(ref _trimmedBytes, Math.Max(0, bytes));
        Interlocked.Add(ref _trimmedItems, Math.Max(0, items));
    }

    internal void SetLimit(long bytes) => Interlocked.Exchange(ref _limit, Math.Max(1, bytes));

    public CacheMemoryStatistics Statistics => new(Name, Cost, Bytes, Interlocked.Read(ref _peak), LimitBytes,
        TargetBytes, Interlocked.Read(ref _trims), Interlocked.Read(ref _trimmedBytes), Interlocked.Read(ref _trimmedItems));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _bytes, 0);
        _owner.Unregister();
    }
}

/// <summary>
/// One working-memory budget for derived caches (overview tiles, decoded stamps). It is derived from physical RAM and
/// split between the registered caches. Trimming below a cache's own limit happens only while Windows is short of
/// memory: the physical load reaches the GC's high-memory threshold, or admission has seen too little headroom.
/// The decision runs on a background timer and never touches a cache: each owner trims on its own thread, keeps what
/// it used recently, and frees nothing a frame is drawing.
/// </summary>
public sealed partial class WorkingMemoryBudget
{
    /// <summary>Under pressure a cache is asked to fall to this share of its limit.</summary>
    public const int CachePressureWatermarkPercent = 50;
    /// <summary>Pressure ends only when the load falls this share of physical RAM below the threshold.</summary>
    public const int CachePressureExitMarginPercent = 5;
    public static readonly TimeSpan CachePressurePollInterval = TimeSpan.FromSeconds(1);

    private readonly object _cacheGate = new();
    private readonly List<WeakReference<CacheMemoryAccount>> _cacheAccounts = [];
    private Timer? _cacheTimer;
    private long _cacheBudget;
    private int _cachePressureStage = -1;
    private long _cachePressureEvents;
    private int _cachePolling;

    /// <summary>
    /// -1 without pressure. Otherwise the highest <see cref="CacheRebuildCost"/> asked to trim: the first poll under
    /// pressure asks the cheap caches, each further poll one class more, so expensive tiles go last.
    /// </summary>
    public int CachePressureStage => Volatile.Read(ref _cachePressureStage);
    public bool CachesUnderPressure => CachePressureStage >= 0;
    public long CachePressureEvents => Interlocked.Read(ref _cachePressureEvents);
    /// <summary>Probe and test hook: replaces the measured pressure (null measures).</summary>
    public bool? SimulatedCachePressure { get; set; }

    /// <summary>The current budget: between <see cref="CalculateCacheBudget"/> and <see cref="CalculateMaximumCacheBudget"/>.</summary>
    public long CacheBudgetBytes { get { lock (_cacheGate) return EnsureCacheBudget(); } }
    /// <summary>The budget's floor, an eighth of RAM; what it returns to as soon as memory gets busy.</summary>
    public long BaseCacheBudgetBytes { get { lock (_cacheGate) { EnsureCacheBudget(); return _cacheBudgetBase; } } }
    public long CacheBudgetChanges => Interlocked.Read(ref _cacheBudgetChanges);

    /// <summary>An eighth of physical RAM, between 256 MiB and 2 GiB; documents, history and Windows keep the rest.</summary>
    public static long CalculateCacheBudget(long physicalBytes) =>
        Math.Clamp(physicalBytes / 8, 256 * MiB, 2048 * MiB);

    /// <summary>DEC-111: while memory is free the caches may hold up to a quarter of RAM (at most 4 GiB).</summary>
    public static long CalculateMaximumCacheBudget(long physicalBytes) =>
        Math.Clamp(physicalBytes / 4, 256 * MiB, 4096 * MiB);

    // Dynamic budget (DEC-111 p.3). Grow only while the physical load is at least GrowMargin below the GC's
    // high-load threshold, counting what the caches may still fill of the budget already granted, so a full cache
    // stays below that line; fall back to the eighth at once within ShrinkMargin of the threshold or under pressure.
    // The 10-point gap, the grow interval and the regrow delay after a fall keep the budget from oscillating.
    /// <summary>Growth stops this share of RAM below the GC high-load threshold.</summary>
    public const int CacheBudgetGrowMarginPercent = 15;
    /// <summary>Within this share of RAM below the threshold the budget returns to its eighth.</summary>
    public const int CacheBudgetShrinkMarginPercent = 5;
    /// <summary>One growth step is at most this fraction of RAM (512 MiB of 16 GiB).</summary>
    public const int CacheBudgetGrowStepDivisor = 32;
    public static readonly TimeSpan CacheBudgetGrowInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CacheBudgetRegrowDelay = TimeSpan.FromSeconds(60);
    private const long CacheBudgetMinimumStep = 64 * MiB;

    private long _cacheBudgetBase, _cacheBudgetChanges;
    private long _lastCacheGrowMs = long.MinValue / 2, _lastCacheShrinkMs = long.MinValue / 2;
    // Test processes pin Shared to a fixed machine; its budget stays at the eighth there.
    private volatile bool _cacheBudgetFixed;

    private long EnsureCacheBudget()
    {
        if (_cacheBudget != 0) return _cacheBudget;
        long physical;
        try { physical = _read().PhysicalBytes; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { physical = 0; }
        if (physical <= 0) physical = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _cacheBudgetBase = CalculateCacheBudget(physical);
        return _cacheBudget = _cacheBudgetBase;
    }

    private static double HighLoadRatio()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 && info.HighMemoryLoadThresholdBytes > 0
            ? Math.Clamp((double)info.HighMemoryLoadThresholdBytes / info.TotalAvailableMemoryBytes, .5, .99) : .9;
    }

    /// <summary>Background only (the pressure poll). Changes the budget by the rules above and rebalances the shares.</summary>
    private void AdjustCacheBudget(WorkingMemorySnapshot memory, bool pressed)
    {
        if (_cacheBudgetFixed || memory.PhysicalBytes <= 0) return;
        lock (_cacheGate)
        {
            EnsureCacheBudget();
            var physical = memory.PhysicalBytes;
            var floor = _cacheBudgetBase;
            var ceiling = Math.Max(floor, CalculateMaximumCacheBudget(physical));
            var threshold = (long)(physical * HighLoadRatio());
            var load = physical - Math.Max(0, memory.AvailableBytes);
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var next = _cacheBudget;
            if (pressed || load >= threshold - physical * CacheBudgetShrinkMarginPercent / 100)
            {
                if (_cacheBudget > floor) { next = floor; _lastCacheShrinkMs = now; }
            }
            else if (_cacheBudget < ceiling
                && now - _lastCacheShrinkMs >= (long)CacheBudgetRegrowDelay.TotalMilliseconds
                && now - _lastCacheGrowMs >= (long)CacheBudgetGrowInterval.TotalMilliseconds)
            {
                // Bytes the caches may still fill of what is granted already count as load.
                var live = Rebalance();
                var requested = live.GroupBy(account => account.Name, StringComparer.Ordinal)
                    .Sum(group => group.Max(account => account.RequestedBytes));
                var held = live.Sum(account => account.Bytes);
                var unfilled = Math.Max(0, Math.Min(_cacheBudget, requested) - held);
                var room = threshold - physical * CacheBudgetGrowMarginPercent / 100 - load - unfilled;
                var step = Math.Min(Math.Min(physical / CacheBudgetGrowStepDivisor, room), ceiling - _cacheBudget);
                if (step >= Math.Min(CacheBudgetMinimumStep, ceiling - _cacheBudget) && step > 0)
                { next = _cacheBudget + step; _lastCacheGrowMs = now; }
            }
            if (next == _cacheBudget) return;
            _cacheBudget = next;
            Interlocked.Increment(ref _cacheBudgetChanges);
            Rebalance();
        }
    }

    public CacheMemoryAccount RegisterCache(string name, CacheRebuildCost cost, long requestedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedBytes);
        var account = new CacheMemoryAccount(this, name, cost, requestedBytes);
        lock (_cacheGate)
        {
            EnsureCacheBudget();
            _cacheAccounts.Add(new(account));
            Rebalance();
            // Only the process-wide budget polls by itself; other budgets are polled by their owner (tests, probes).
            if (ReferenceEquals(this, Shared))
                _cacheTimer ??= new Timer(_ => PollCachePressure(), null, CachePressurePollInterval, CachePressurePollInterval);
        }
        return account;
    }

    internal void Unregister() { lock (_cacheGate) Rebalance(); }

    /// <summary>
    /// Shares the budget by cache name: two render threads holding the same kind of cache do not halve each other
    /// (each of them is bounded by its own limit), while different kinds together stay within the budget.
    /// </summary>
    private List<CacheMemoryAccount> Rebalance()
    {
        var live = new List<CacheMemoryAccount>(_cacheAccounts.Count);
        for (var i = _cacheAccounts.Count - 1; i >= 0; i--)
        {
            if (_cacheAccounts[i].TryGetTarget(out var account) && !account.IsDisposed) live.Add(account);
            else _cacheAccounts.RemoveAt(i);
        }
        var requested = live.GroupBy(account => account.Name, StringComparer.Ordinal)
            .Sum(group => (double)group.Max(account => account.RequestedBytes));
        var factor = requested <= 0 ? 1 : Math.Min(1, _cacheBudget / requested);
        foreach (var account in live) account.SetLimit((long)(account.RequestedBytes * factor));
        return live;
    }

    /// <summary>Background only: reads memory, sets the stage. No cache locks, Skia calls or GC.Collect.</summary>
    public void PollCachePressure()
    {
        if (Interlocked.CompareExchange(ref _cachePolling, 1, 0) != 0) return;
        try
        {
            var memory = _read();
            var pressed = SimulatedCachePressure ?? MeasureCachePressure(memory);
            AdjustCacheBudget(memory, pressed);
            var stage = CachePressureStage;
            if (!pressed) { Volatile.Write(ref _cachePressureStage, -1); return; }
            if (stage < 0) Interlocked.Increment(ref _cachePressureEvents);
            Volatile.Write(ref _cachePressureStage, Math.Min(stage + 1, (int)CacheRebuildCost.Expensive));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Keep the last stage if an OS query fails. */ }
        finally { Volatile.Write(ref _cachePolling, 0); }
    }

    private bool MeasureCachePressure(WorkingMemorySnapshot memory)
    {
        if (memory.PhysicalBytes <= 0) return IsUnderPressure;
        // The GC's own high-memory threshold (normally 90 % of RAM), applied to the current physical load. The
        // MemoryLoadBytes of GCMemoryInfo is as old as the last GC; native pixels grow without one.
        var threshold = (long)(memory.PhysicalBytes * HighLoadRatio());
        if (CachesUnderPressure) threshold -= memory.PhysicalBytes * CachePressureExitMarginPercent / 100;
        var load = memory.PhysicalBytes - Math.Max(0, memory.AvailableBytes);
        return load >= threshold || IsUnderPressure;
    }

    /// <summary>For the background performance writer, never for the frame producer.</summary>
    public CacheMemoryStatistics[] ReadCacheStatistics()
    {
        lock (_cacheGate)
            return Rebalance().Select(account => account.Statistics)
                .GroupBy(statistics => (statistics.Name, statistics.Cost))
                .Select(group => new CacheMemoryStatistics(group.Key.Name, group.Key.Cost, group.Sum(s => s.Bytes),
                    group.Sum(s => s.PeakBytes), group.Sum(s => s.LimitBytes), group.Min(s => s.TargetBytes),
                    group.Sum(s => s.Trims), group.Sum(s => s.TrimmedBytes), group.Sum(s => s.TrimmedItems)))
                .OrderBy(statistics => statistics.Name, StringComparer.Ordinal).ToArray();
    }
}
