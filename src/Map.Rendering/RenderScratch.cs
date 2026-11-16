using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Thread-owned native scratch. Creating/discarding a Skia handle during background gen2 can wait for
/// hundreds of milliseconds even with a tiny GC pause. A hot frame borrows existing handles instead.
/// Reset releases every shader/filter reference; no document, image or GPU context remains in the pool.
/// </summary>
internal static class RenderScratch
{
    private const int Capacity = 16;
    [ThreadStatic] private static Stack<SKPaint>? _paints;
    [ThreadStatic] private static Stack<SKPath>? _paths;

    internal static PaintLease Paint() => new(_paints is { Count: > 0 } ? _paints.Pop() : new SKPaint());
    internal static PathLease Path() => new(_paths is { Count: > 0 } ? _paths.Pop() : new SKPath());

    internal readonly struct PaintLease(SKPaint value) : IDisposable
    {
        public SKPaint Value { get; } = value;
        public void Dispose()
        {
            if (Value is null) return; // an optional lease has no handle
            Value.Reset();
            var pool = _paints ??= new(Capacity);
            if (pool.Count < Capacity) pool.Push(Value); else Value.Dispose();
        }
    }

    internal readonly struct PathLease(SKPath value) : IDisposable
    {
        public SKPath Value { get; } = value;
        public void Dispose()
        {
            // Cold tile clips have a few rectangles. An exceptional large path keeps no backing storage.
            if (Value.PointCount > 4096) { Value.Dispose(); return; }
            Value.Reset();
            var pool = _paths ??= new(Capacity);
            if (pool.Count < Capacity) pool.Push(Value); else Value.Dispose();
        }
    }
}
