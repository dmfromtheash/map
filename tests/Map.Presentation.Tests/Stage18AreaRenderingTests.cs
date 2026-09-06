using System.Security.Cryptography;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage18AreaRenderingTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-path-render-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public async Task Transparent_area_matches_viewport_single_png_and_tiles_across_tile_boundaries()
    {
        using var temp = new TempDirectory();
        var session = Fixture(out _, out _); var document = session.Document;
        using var stamps = new ExportStampImageSource(document, null, null);
        var engine = new ImageExportEngine(new ExportEngineSettings { TileEdge = 128 });
        ExportRequest Request(ExportOutputMode mode) => new(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, 512, 256, mode, true, false);
        var png = Path.Combine(temp.Path, "row.png"); var tiles = Path.Combine(temp.Path, "tiles");
        await engine.ExportAsync(document, engine.Plan(document, Request(ExportOutputMode.SingleImage)), png, stamps);
        await engine.ExportAsync(document, engine.Plan(document, Request(ExportOutputMode.TilePackage)), tiles, stamps);
        using var actual = SKBitmap.Decode(png);
        using var viewportCache = new ObjectOverviewCache();
        using var viewport = Render(document, stamps, 1, viewportCache, export: false);
        using var canonical = Render(document, stamps, 1, viewportCache, export: true);
        Assert.True(viewport.Pixels.SequenceEqual(canonical.Pixels), "Direct viewport and canonical CPU evaluator differ.");
        Assert.Equal(viewport.Pixels, actual.Pixels);
        Assert.Contains(actual.Pixels, p => p.Alpha is > 0 and < 255);
        Assert.Contains(Enumerable.Range(0, 256), y => actual.GetPixel(127, y).Alpha > 0 && actual.GetPixel(128, y).Alpha > 0);
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(File.ReadAllText(Path.Combine(tiles, "manifest.json")))!;
        Assert.Equal(8, manifest.Tiles.Count);
        foreach (var tile in manifest.Tiles)
        {
            using var bitmap = SKBitmap.Decode(Path.Combine(tiles, tile.File));
            for (var y = 0; y < tile.Height; y++)
            for (var x = 0; x < tile.Width; x++)
                Assert.Equal(actual.GetPixel(tile.X + x, tile.Y + y), bitmap.GetPixel(x, y));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.02)]
    public void Cached_detail_and_overview_match_fresh_scene_after_removal_append_and_neighbour_edit(double scale)
    {
        var session = Fixture(out var layer, out var areaId);
        using var stamps = new ExportStampImageSource(session.Document, null, null);
        using var cache = new ObjectOverviewCache();
        using (var warm = Render(session.Document, stamps, scale, cache)) { }
        // A later ordinary object must keep its ordering and lookup when earlier row members disappear.
        var neighbour = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Neighbour",
            120, 90, 36, 36, 0, "#AADD3300", "#00000000", 0, true, false);
        session.Execute(new AddObjectCommand(layer, neighbour));
        foreach (var spacing in new[] { 2, 8 })
        {
            var area = ((ObjectLayer)session.Document.FindLayer(layer)!).PlacementAreas.Single();
            session.Execute(new PlacementAreaCommand(layer, areaId, area with { Density = spacing }, "spacing"));
            using var actual = Render(session.Document, stamps, scale, cache);
            var fresh = Fresh(session.Document);
            using var freshCache = new ObjectOverviewCache();
            using var expected = Render(fresh, stamps, scale, freshCache);
            Assert.Equal(expected.Pixels, actual.Pixels);
            Assert.Same(neighbour, DocumentEditor.FindObject(session.Document, neighbour.Id));
            Assert.Equal(((ObjectLayer)fresh.FindLayer(layer)!).Objects.Count, DocumentRevisionIndex.For(session.Document).ObjectCount);
        }
        var moved = SceneObjectClone.At(neighbour, 220, 90);
        session.Execute(new ReplaceObjectsCommand([neighbour], [moved], "move neighbour"));
        using var final = Render(session.Document, stamps, scale, cache);
        using var finalCache = new ObjectOverviewCache();
        using var reference = Render(Fresh(session.Document), stamps, scale, finalCache);
        Assert.Equal(reference.Pixels, final.Pixels);
        if (scale < .03) Assert.True(cache.Count > 0);
    }

    private static SKBitmap Render(MapDocument doc, IStampImageSource stamps, double scale, ObjectOverviewCache cache, bool export = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(512, 256, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, doc, new RenderOptions
        {
            Stamps = stamps, ViewScale = scale, VisibleWorld = new(0, 0, 512, 256),
            LodTier = scale < .03 ? RenderLodTier.Overview : RenderLodTier.Exact,
            ObjectCache = cache, IsExport = export, IncludeBaseColor = false, IncludeExportGrids = false,
        });
        using var image = surface.Snapshot();
        // Compare both routes after the same PNG alpha conversion.
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return SKBitmap.Decode(data);
    }
    private static MapDocument Fresh(MapDocument doc) => new(doc.Id, doc.Title, doc.CreatedUtc, doc.Canvas,
        doc.Layers.Select(l => l is ObjectLayer o
            ? o with { Objects = o.Objects.ToArray() } : l).ToArray(), doc.Assets);

    private static EditingSession Fixture(out LayerId layer, out Guid areaId)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32))!;
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(30, 150, 60, 170), IsAntialias = false };
        surface.Canvas.DrawRect(3, 3, 26, 26, paint);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray(); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Own alpha fixture", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "test", "test", bytes);
        layer = LayerId.New();
        var doc = new MapDocument(DocumentId.New(), "Area render", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 256, "px", "#00000000"),
            [new BaseColorLayer(LayerId.New(), "Transparent", true, false, 0, "#00000000"),
                new ObjectLayer(layer, "Trees", true, false, 1, [])], [asset]);
        var area = new PlacementArea { LayoutVersion = 1, Nodes = [new(Guid.NewGuid(), 40, 20), new(Guid.NewGuid(), 480, 20),
            new(Guid.NewGuid(), 480, 220), new(Guid.NewGuid(), 40, 220)], Variants = [new(sha, 32, 32)], Density = 5, Seed = 18,
            Request = new() { BaseWidth = 40 }, Style = new(sha) };
        areaId = area.Id; var session = new EditingSession(doc);
        session.Execute(new PlacementAreaCommand(layer, area.Id, area, "create"));
        return session;
    }
}
