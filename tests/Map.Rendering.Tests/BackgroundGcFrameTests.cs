using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection(RealTimeSettleCollection.Name)]
public sealed class BackgroundGcFrameTests
{
    [Fact]
    public void Every_warm_paper_frame_avoids_allocation_waits_during_background_gen2()
    {
        var previous = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        try
        {
            var sha = new string('a', 64);
            var objects = Enumerable.Range(0, 1_000_000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
                "owner geometry", i % 1000 * 30, i / 1000 * 30, 64, 64, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) }).ToArray();
            // Hidden objects retain the real document graph without adding stamp cost to the native-handle guard.
            var document = new MapDocument(DocumentId.New(), "background GC frame", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.New(), "mountains", false, false, 1, objects)]);
            using var surface = SKSurface.Create(new SKImageInfo(128, 128))!;
            var canvas = surface.Canvas;
            var options = new RenderOptions { VisibleWorld = new(0, 0, 128, 128) };
            void Frame() { SceneRenderer.Render(canvas, document, options); canvas.Flush(); }
            for (var i = 0; i < 10; i++) Frame();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) Frame();
            var frameAllocations = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"PAPER-ALLOC {frameAllocations / 100d:F1} bytes/frame");
            GC.Collect(); GC.WaitForPendingFinalizers();
            var worst = 0d;
            var frames = 0;
            for (var round = 0; round < 3; round++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 600)
                {
                    var start = Stopwatch.GetTimestamp(); Frame();
                    worst = Math.Max(worst, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    frames++;
                }
            }
            Console.WriteLine($"BACKGROUND-GC-FRAME frames={frames} worst={worst:F3}ms");
            Assert.True(frames > 100);
            Assert.True(worst < 60, $"one paper frame waited {worst:F1} ms during background gen2");
            Assert.Equal(0, frameAllocations);
            using var snapshot = surface.Snapshot(); using var pixels = SKBitmap.FromImage(snapshot);
            Assert.Equal(SKColors.White, pixels.GetPixel(64, 64));
            GC.KeepAlive(document);
        }
        finally { System.Runtime.GCSettings.LatencyMode = previous; }
    }

    [Fact]
    public void Native_scratch_is_exclusive_and_returns_with_default_paint_and_empty_path()
    {
        IntPtr paintHandle;
        using (var lease = RenderScratch.Paint())
        {
            paintHandle = lease.Value.Handle;
            lease.Value.BlendMode = SKBlendMode.DstOut; lease.Value.IsAntialias = true; lease.Value.StrokeWidth = 19;
            using var nested = RenderScratch.Paint();
            Assert.NotEqual(paintHandle, nested.Value.Handle);
        }
        using (var lease = RenderScratch.Paint())
        {
            Assert.Equal(paintHandle, lease.Value.Handle);
            Assert.Equal(SKBlendMode.SrcOver, lease.Value.BlendMode);
            Assert.False(lease.Value.IsAntialias);
            Assert.Equal(0, lease.Value.StrokeWidth);
        }
        using (var path = RenderScratch.Path()) path.Value.AddRect(new SKRect(0, 0, 100, 100));
        using var empty = RenderScratch.Path();
        Assert.True(empty.Value.IsEmpty);
    }
}
