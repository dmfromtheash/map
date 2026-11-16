using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>Native ladder's 100% edit view: a warm pan image, ten separately rendered edits.
/// The budget is structural (at most 16 stamp evaluations), independent of Mac/PC CPU speed.
/// A 64x48 stamp moving 3x2 pixels touches at most nine neighbours in this fixture.</summary>
public sealed class EditFrameBudgetTests
{
    [Theory]
    [InlineData(10_000, false)]
    [InlineData(100_000, false)]
    [InlineData(10_000, true)]
    [InlineData(100_000, true)]
    [InlineData(10_000, false, true)]
    [InlineData(10_000, true, true)]
    public void Paced_place_select_drag_repair_only_the_changed_area_and_match_fresh_pixels(int count, bool still, bool fractional = false)
    {
        using var images = new OwnerImage();
        var objects = Enumerable.Range(0, count).Select(i => Stamp(i < 240 ? 15632 + i % 20 * 72 : i % 200 * 64,
            i < 240 ? 15952 + i / 20 * 64 : i / 200 * 48)).ToArray();
        objects[^1] = Stamp(16384, 16384);
        var layer = new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects);
        var document = new MapDocument(DocumentId.New(), "edit budget", DateTimeOffset.UnixEpoch,
            new(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"), layer]);
        DocumentDerivedIndexes.Prepare(document);
        ObjectSpatialIndex.For(layer);
        var session = new EditingSession(document);
        using var cache = new LayerImageCache();
        using var surface = SKSurface.Create(new SKImageInfo(1536, 864))!;
        var failures = new List<string>();
        var cameraShift = fractional && !still ? 8f : 0f;
        Frame(document, cache, null);
        if (still) cache.ClearPanned();
        Frame(document, cache, null);
        foreach (var route in new[] { "place-10", "select", "preview-drag", "preview-far", "cancel-preview", "drag", "far-drag" })
        for (var step = 0; step < 10; step++)
        {
            var started = Stopwatch.GetTimestamp();
            var before = session.Document;
            IReadOnlyList<SceneObject>? live = route == "preview-drag"
                ? [SceneObjectClone.At(objects[^1], objects[^1].X + 3 * (step + 1), objects[^1].Y + 2 * (step + 1))]
                : route == "preview-far" ? [SceneObjectClone.At(objects[^1], 30000, 30000)] : null;
            if (route == "place-10") session.Execute(new AddObjectCommand(layer.Id, Stamp(16224 + step * 32, 16504)));
            if (route is "drag" or "far-drag")
            {
                var selected = DocumentRevisionIndex.For(before).TryFind(objects[^1].Id, out var location) ? location.Object : throw new InvalidOperationException();
                var moved = route == "drag" ? SceneObjectClone.At(selected, selected.X + 3, selected.Y + 2)
                    : SceneObjectClone.At(selected, step % 2 == 0 ? 30000 : 16384, step % 2 == 0 ? 30000 : 16384);
                session.Execute(new ReplaceObjectsCommand([selected], [moved], "Drag"));
            }
            var commandMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            var stats = Frame(session.Document, cache, route == "select" ? objects[^1].Id : null, live);
            var frameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine(FormattableString.Invariant($"EDIT-FRAME count={count} still={still} fractional={fractional} route={route} step={step} commandMs={commandMs:F4} frameMs={frameMs:F4} objectMs={stats.ObjectsMilliseconds:F4} terrainMs={stats.TerrainMilliseconds:F4} bytes={bytes} drawn={stats.ObjectsDrawn} candidates={stats.ObjectCandidates}"));
            if (stats.ObjectsDrawn > 16) failures.Add($"{route}/{step}: {stats.ObjectsDrawn} stamp evaluations > 16");
            // The reference uses the same production image format and transform, with cold
            // layer images. Compare every byte, including transparent/antialiased edges.
            var actual = Pixels();
            using (var fresh = new LayerImageCache())
            {
                Frame(session.Document, fresh, route == "select" ? objects[^1].Id : null, live);
                if (still) { fresh.ClearPanned(); Frame(session.Document, fresh, route == "select" ? objects[^1].Id : null, live); }
                Assert.Equal(Pixels(), actual);
            }
            if (route is "select" or "preview-drag" or "preview-far" or "cancel-preview") Assert.Same(before, session.Document);
            Thread.Sleep(16);
        }
        var edited = session.Document;
        for (var i = 0; i < 30; i++) { session.Undo(); CheckHistory(); }
        Assert.Equal(objects, ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects);
        Assert.Equal(0, session.UndoDepth);
        for (var i = 0; i < 30; i++) { session.Redo(); CheckHistory(); }
        Assert.Equal(((ObjectLayer)edited.FindLayer(layer.Id)!).Objects, ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects);
        Assert.Equal(30, session.UndoDepth);
        Console.WriteLine($"EDIT-PIXELS count={count} still={still} fractional={fractional} sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Pixels()))}");
        Assert.Empty(failures);

        void CheckHistory()
        {
            Frame(session.Document, cache, null);
            var actual = Pixels();
            using var fresh = new LayerImageCache();
            Frame(session.Document, fresh, null);
            if (still) { fresh.ClearPanned(); Frame(session.Document, fresh, null); }
            Assert.Equal(Pixels(), actual);
        }

        RenderStats Frame(MapDocument scene, LayerImageCache layers, ObjectId? selected, IReadOnlyList<SceneObject>? live = null)
        {
            surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White);
            var scale = fractional ? .83f : 1f;
            var dy = fractional ? .37f : 0f;
            surface.Canvas.Translate(-15616 * scale + cameraShift, -15952 * scale + dy);
            surface.Canvas.Scale(scale);
            var stats = new RenderStats();
            SceneRenderer.Render(surface.Canvas, scene, new RenderOptions
            {
                Stamps = images, LayerImages = layers, Stats = stats, ViewScale = scale,
                LodTier = live is null ? RenderLodTier.Exact : RenderLodTier.Interactive, FastPreview = live is not null,
                ObjectOverrides = live, VisibleWorld = new(15616 - cameraShift / scale, 15952 - dy / scale,
                    15616 + (1536 - cameraShift) / scale, 15952 + (864 - dy) / scale), SelectedObjectId = selected,
            });
            surface.Canvas.Flush();
            return stats;
        }
        byte[] Pixels()
        {
            using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image);
            return bitmap.Bytes;
        }
    }

    internal static SceneObject Stamp(double x, double y) => new(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
        x, y, 64, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new(new string('a', 64)) };

    internal sealed class OwnerImage : IStampImageSource, IDisposable
    {
        private readonly SKImage _image;
        public OwnerImage()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            _image = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"))!;
        }
        public SKImage? TryGetImage(string sha256) => _image;
        public void Dispose() => _image.Dispose();
    }
}
