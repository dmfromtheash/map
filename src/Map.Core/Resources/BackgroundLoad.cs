namespace Map.Core.Resources;

/// <summary>Diagnostic names only; these do not grant a scheduler slot.</summary>
public enum BackgroundProducer
{
    None,
    BackgroundWork,
    DerivedIndexes,
    LabelIndex,
    Overview,
    OverviewPrefetch,
    DetailRaster,
    AssetHeader,
    AssetBuild,
    AssetSmallReader,
    AssetLargeLane,
    AssetJanitor,
    Terrain,
    ExportRender,
    ExportEncode,
    ExportCompression,
    PngPyramid,
    MbTiles,
    ViewerFolder,
    Watcher,
    AssetRetry,
    AssetMetadata,
    PlacementPreview,
    MemoryTrim,
    Journal,
    StylePaint,
    LightMaps,
    Cliff,
    Generation,
    Validation,
    Planet,
    AssetQuery,
    AssetDecode,
    HexChunk,
}

/// <summary>
/// Opt-in headless diagnostics of executing producer threads, not queued tasks or reserved slots.
/// Scopes must be synchronous, nested and disposed on their entering thread (never across await).
/// Nested work replaces its caller's category, so one thread contributes at most one to the sum.
/// Explicit scheduler/gesture waits are paused; synchronous IO/native calls remain in the count.
/// This measures execution regions, not OS CPU utilization, and never controls scheduling.
/// </summary>
public static class BackgroundLoad
{
    private static readonly object Gate = new();
    private static readonly int[] Busy = new int[Enum.GetValues<BackgroundProducer>().Length];
    private static readonly long[] Entries = new long[Busy.Length];
    private static int _observers;
    [ThreadStatic] private static BackgroundProducer _current;

    public static Scope Enter(BackgroundProducer producer)
    {
        if (Volatile.Read(ref _observers) == 0 && _current == BackgroundProducer.None) return default;
        var previous = _current;
        lock (Gate)
        {
            Change(previous, producer);
            if (producer != BackgroundProducer.None) Entries[(int)producer]++;
        }
        _current = producer;
        return new Scope(previous, true);
    }

    public static Scope Pause() => Enter(BackgroundProducer.None);

    private static void Change(BackgroundProducer from, BackgroundProducer to)
    {
        if (from != BackgroundProducer.None) Busy[(int)from]--;
        if (to != BackgroundProducer.None) Busy[(int)to]++;
    }

    public ref struct Scope
    {
        private readonly BackgroundProducer _previous;
        private bool _active;
        internal Scope(BackgroundProducer previous, bool active) { _previous = previous; _active = active; }
        public void Dispose()
        {
            if (!_active) return;
            lock (Gate) Change(_current, _previous);
            _current = _previous;
            _active = false;
        }
    }

    // Only tests opt in. No reset: scopes already running must still be able to leave safely.
    internal static IDisposable Observe()
    {
        Interlocked.Increment(ref _observers);
        return new Observation();
    }

    /// <summary>The category this thread currently counts under (None outside scopes or when unobserved).</summary>
    internal static BackgroundProducer CurrentProducer => _current;

    internal static Snapshot Capture()
    {
        lock (Gate) return new((int[])Busy.Clone(), (long[])Entries.Clone());
    }

    internal sealed record Snapshot(int[] Busy, long[] Entries)
    {
        public int Total => Busy.Sum();
        public override string ToString() => string.Join(", ", Enum.GetValues<BackgroundProducer>()
            .Where(p => Busy[(int)p] > 0).Select(p => $"{p}={Busy[(int)p]}"));
    }

    private sealed class Observation : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _observers);
        }
    }
}
