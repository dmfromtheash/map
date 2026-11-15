using System.Diagnostics;
using System.Reflection;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;

namespace Map.Presentation.Tests;

/// <summary>
/// Trace of the wheel at 25 000 objects (PC, 2026-11-15): the frame spent 5 of 10 s waiting for the image source's
/// gate while a background raster pinned its plan. The frame now draws what is resident instead of waiting.
/// </summary>
public sealed class FrameGateWaitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-gate-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_frame_does_not_wait_for_a_gate_held_by_a_background_raster()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var document = new MapDocument(DocumentId.New(), "Gate", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")], []);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null, derivedRoot: Path.Combine(_root, "derived"));
        var gate = typeof(CatalogStampImageSource).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
        holder.Start();
        held.Wait();
        try
        {
            var asked = 0;
            var watch = Stopwatch.StartNew();
            var ready = source.PrepareForPixelSizes([(new string('c', 64), 32, 32)], () => Interlocked.Increment(ref asked));
            watch.Stop();
            Assert.False(ready);
            Assert.Equal(1, asked); // the frame asks to be drawn again
            Assert.True(watch.ElapsedMilliseconds < 100, $"the frame waited {watch.ElapsedMilliseconds} ms for a busy gate");
            Assert.Equal(1, source.FrameGateMisses);
        }
        finally { release.Set(); holder.Join(); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
