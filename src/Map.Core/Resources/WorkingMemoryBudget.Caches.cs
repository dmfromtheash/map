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

    public long CacheBudgetBytes { get { lock (_cacheGate) return EnsureCacheBudget(); } }

    /// <summary>An eighth of physical RAM, between 256 MiB and 2 GiB; documents, history and Windows keep the rest.</summary>
    public static long CalculateCacheBudget(long physicalBytes) =>
        Math.Clamp(physicalBytes / 8, 256 * MiB, 2048 * MiB);

    private long EnsureCacheBudget()
    {
        if (_cacheBudget != 0) return _cacheBudget;
        long physical;
        try { physical = _read().PhysicalBytes; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { physical = 0; }
        if (physical <= 0) physical = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return _cacheBudget = CalculateCacheBudget(physical);
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
            var pressed = SimulatedCachePressure ?? MeasureCachePressure();
            var stage = CachePressureStage;
            if (!pressed) { Volatile.Write(ref _cachePressureStage, -1); return; }
            if (stage < 0) Interlocked.Increment(ref _cachePressureEvents);
            Volatile.Write(ref _cachePressureStage, Math.Min(stage + 1, (int)CacheRebuildCost.Expensive));
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Keep the last stage if an OS query fails. */ }
        finally { Volatile.Write(ref _cachePolling, 0); }
    }

    private bool MeasureCachePressure()
    {
        var memory = _read();
        if (memory.PhysicalBytes <= 0) return IsUnderPressure;
        // The GC's own high-memory threshold (normally 90 % of RAM), applied to the current physical load. The
        // MemoryLoadBytes of GCMemoryInfo is as old as the last GC; native pixels grow without one.
        var info = GC.GetGCMemoryInfo();
        var ratio = info.TotalAvailableMemoryBytes > 0 && info.HighMemoryLoadThresholdBytes > 0
            ? Math.Clamp((double)info.HighMemoryLoadThresholdBytes / info.TotalAvailableMemoryBytes, .5, .99) : .9;
        var threshold = (long)(memory.PhysicalBytes * ratio);
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
