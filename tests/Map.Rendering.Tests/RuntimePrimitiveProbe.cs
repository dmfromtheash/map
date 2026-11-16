using System.Diagnostics;
using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>Opt-in control: measure each primitive under its own background GC, with a live million-object graph.</summary>
public sealed class RuntimePrimitiveProbe
{
    private static object? _allocation;
    [OptInFact("locate background GC waits in frame primitives", "MAP_RUNTIME_PRIMITIVES")]
    public void Measure()
    {
        var items = Enumerable.Range(0, 1_000_000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "owner geometry", i, i, 64, 64, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('a', 64)) }).ToArray();
        var key = new object(); var value = new object();
        var table = new ConditionalWeakTable<object, object>(); table.Add(key, value);
        var weak = new WeakReference<object>(value);
        using var surface = SKSurface.Create(new SKImageInfo(128, 128))!;
        var cache = new Dictionary<int, object> { [0] = value };
        using var reused = new SKPaint();
        var document = new MapDocument(DocumentId.New(), "primitive frame", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", false, false, 1, items)]);
        var options = new RenderOptions { VisibleWorld = new(0, 0, 128, 128) };
        Action[] cases = [() => table.TryGetValue(key, out _), () => weak.TryGetTarget(out _),
            () => { using var paint = new SKPaint(); }, () => { _ = surface.Canvas.TotalMatrix; },
            () => { _ = cache.TryGetValue(0, out _); }, () => { Volatile.Write(ref _allocation, new byte[1000]); },
            reused.Reset, () => { using var lease = RenderScratch.Paint(); lease.Value.Color = SKColors.White; },
            () => { reused.Color = SKColors.White; surface.Canvas.DrawRect(0, 0, 128, 128, reused); },
            () => surface.Canvas.Flush(), () => { surface.Canvas.Save(); surface.Canvas.ClipRect(new SKRect(0, 0, 128, 128)); surface.Canvas.Restore(); },
            () => SceneRenderer.Render(surface.Canvas, document, options)];
        var names = new[] { "CWT", "Weak", "SKPaint", "Canvas", "Dictionary", "Allocate", "Reset", "Pool", "Draw", "Flush", "Clip", "Render" };
        foreach (var action in cases) action();
        GC.Collect(); GC.WaitForPendingFinalizers();
        for (var round = 0; round < 2; round++)
        {
            var max = new double[cases.Length];
            for (var k = 0; k < cases.Length; k++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
                var watch = Stopwatch.StartNew();
                var calls = 0;
                while (watch.ElapsedMilliseconds < 600)
                {
                    var start = Stopwatch.GetTimestamp(); cases[k]();
                    max[k] = Math.Max(max[k], Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    calls++;
                }
                Assert.True(calls > 100, $"{names[k]} was not exercised");
            }
            Console.WriteLine($"PRIMITIVES round={round} " + string.Join(' ', names.Select((n, k) => $"{n}={max[k]:F3}")));
        }
        GC.KeepAlive(items); GC.KeepAlive(value); GC.KeepAlive(key);
        Volatile.Write(ref _allocation, null);
    }
}
