using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Rendering.Tests;

[Collection("OverviewExactLayers")]
public sealed class WholeMapOpenLifetimeTests
{
    [Fact]
    public void Twenty_opens_of_the_same_document_release_caches_and_managed_memory_plateaus()
    {
        const long tolerance = 4L * 1024 * 1024;
        var document = ColdLibraryWholeMapTests.WholeMapView.Document(50_000);
        var closedViews = new List<WeakReference>();
        var bytes = new List<long>();
        var entries = new List<int>();
        for (var open = 1; open <= 20; open++)
        {
            var closed = OpenRenderClose(document);
            closedViews.AddRange(closed.Owners);
            entries.Add(closed.LiveEntries);
            var retained = Retained();
            bytes.Add(retained);
            Console.WriteLine($"OVGUARD open={open}: managed={retained} closedEntries={closed.ClosedEntries} liveEntries={closed.LiveEntries} reachableClosedOwners={closedViews.Count(w => w.IsAlive)}");
            Assert.Equal(0, closed.ClosedEntries);
        }

        Console.WriteLine($"OVGUARD plateau open3 -> open20: {bytes[2]} -> {bytes[19]} bytes; delta={bytes[19] - bytes[2]}; tolerance={tolerance}");
        Assert.InRange(bytes[19] - bytes[2], -tolerance, tolerance);
        Assert.InRange(bytes.Skip(3).Max() - bytes[2], -tolerance, tolerance);
        Assert.InRange(entries[19], 1, entries[2] + 2);
        Assert.All(closedViews, reference => Assert.False(reference.IsAlive, "a closed renderer/cache owner is still rooted"));
        GC.KeepAlive(document);
    }

    // A distinct stack frame prevents JIT local lifetimes from keeping the last opening alive
    // across the caller's full GC. The canonical document stays alive; each open below
    // materializes fresh instances of its objects, preserving the persisted content and IDs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] Owners, int LiveEntries, int ClosedEntries) OpenRenderClose(MapDocument document)
    {
        // Reopening the same file creates fresh model instances with the same persisted IDs,
        // geometry and assets. Reusing one layer instance would conceal per-document retention.
        var reopened = new MapDocument(document.Id, document.Title, document.CreatedUtc, document.Canvas,
            document.Layers.Select(layer => layer is ObjectLayer objects
                ? (MapLayer)new ObjectLayer(objects.Id, objects.Name, true, false, 1,
                    objects.Objects.Select(item => item with { }).ToArray())
                : layer).ToArray(), document.Assets);
        var view = new ColdLibraryWholeMapTests.WholeMapView(50_000, ready: true, reopened);
        try
        {
            view.Settle();
            // A repeated canvas frame retains the completed layer, in addition to overview tiles.
            var frame = view.Frame("open-settled");
            Assert.Equal(1, frame.Stats.LayerImagesKept);
            view.AssertVisible();
            var live = view.CacheEntries;
            Assert.True(live.Tiles > 0 && live.Layers > 0, "opening did not populate the real render caches");
            var references = view.LifetimeReferences;
            view.Dispose();
            var closed = view.CacheEntries;
            return (references, live.Tiles + live.Atlas + live.Layers + live.Ready + live.Queued,
                closed.Tiles + closed.Atlas + closed.Layers + closed.Ready + closed.Queued);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    private static long Retained()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}
