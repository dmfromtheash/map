using System.Diagnostics;

namespace Map.Presentation.Services;

public sealed partial class StampDerivedAssetStore
{
    public readonly record struct IoSnapshot(long PortableHeaders, long PortablePixels, long LocalMips,
        long SourcePreparations, long SourceVerifications, long ReadsOnCanvasThread,
        double PortableDecompressMs, double SourceDecodeMs);

    private long _portableHeaderReads, _portablePixelReads, _localMipReads, _sourcePreparations,
        _sourceVerifications, _readsOnCanvasThread, _portableDecompressTicks, _sourceDecodeTicks;
    private int _canvasThreadId;

    internal void ObserveCanvasThread() => Volatile.Write(ref _canvasThreadId, Environment.CurrentManagedThreadId);

    // The source acquires this opportunistically before running a canvas operation. Store calls can then
    // re-enter their ordinary gate without parking the frame behind metadata/pixel workers.
    internal bool TryEnterFrameGate() => Monitor.TryEnter(_requestsGate);
    internal void ExitFrameGate() => Monitor.Exit(_requestsGate);

    private void CountIo(ref long counter)
    {
        Interlocked.Increment(ref counter);
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _canvasThreadId))
            Interlocked.Increment(ref _readsOnCanvasThread);
    }

    /// <summary>Cumulative worker counters; sampling performs no file I/O or locking.</summary>
    public IoSnapshot IoDiagnostics => new(Interlocked.Read(ref _portableHeaderReads), Interlocked.Read(ref _portablePixelReads),
        Interlocked.Read(ref _localMipReads), Interlocked.Read(ref _sourcePreparations), Interlocked.Read(ref _sourceVerifications),
        Interlocked.Read(ref _readsOnCanvasThread), Interlocked.Read(ref _portableDecompressTicks) * 1000d / Stopwatch.Frequency,
        Interlocked.Read(ref _sourceDecodeTicks) * 1000d / Stopwatch.Frequency);
}
