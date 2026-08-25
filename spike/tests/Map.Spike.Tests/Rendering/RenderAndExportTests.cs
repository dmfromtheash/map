using Map.Spike.Core.Commands;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;
using Map.Spike.Export;
using Map.Spike.Rendering;
using Map.Spike.Tests.Support;
using SkiaSharp;

namespace Map.Spike.Tests.Rendering;

public class RenderAndExportTests
{
    private const int ExportSize = 4096;

    private static SpikeDocument BuildScene(int objects = 300)
    {
        var document = SpikeFixtures.CreateObjectScene(objects, canvasSize: 2048);

        // Decor patch sits in tiles (0,0)..(1,0); the brush stroke is kept well away from it so a
        // mask test can compare the two regions independently.
        SpikeFixtures.FillDecorPatch(document, 2, 1, 0xC02040FF);

        var history = new CommandHistory();
        var brush = new BrushStrokeSession(document.Brush, BrushSettings.Default with { Radius = 40 });
        brush.AddPoint(new Vec2(1200, 1200));
        brush.AddPoint(new Vec2(1500, 1400));
        history.Execute(document, brush.Complete()!);

        return document;
    }

    private static SKBitmap RenderToBitmap(SpikeDocument document, RenderSettings settings, int size = 512)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;

        var viewport = new Viewport();
        viewport.Resize(size, size);
        viewport.Set(Vec2.Zero, size / (double)document.CanvasWidth);

        using var tiles = new TileImageCache();
        using var geometry = ObjectGeometryCache.Build(document.Objects);

        var stats = default(RenderStats);
        SceneRenderer.Render(surface.Canvas, document, viewport, settings, tiles, geometry, ref stats);
        surface.Canvas.Flush();

        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    [Fact]
    public void Export_writes_a_real_4096_png_with_an_alpha_channel()
    {
        using var workspace = new TempWorkspace();
        var document = BuildScene();
        var path = workspace.File("export-4k.png");

        var result = OffscreenPngExporter.Export(document, path, ExportSize, ExportSize);

        Assert.True(File.Exists(path));
        Assert.Equal(ExportSize, result.Width);
        Assert.Equal(ExportSize, result.Height);
        Assert.True(result.FileBytes > 0);

        // Read the file back through a decoder, not from the in-memory surface.
        using var decoded = SKBitmap.Decode(path);
        Assert.NotNull(decoded);
        Assert.Equal(ExportSize, decoded!.Width);
        Assert.Equal(ExportSize, decoded.Height);

        var header = File.ReadAllBytes(path)[..8];
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);

        Assert.True(decoded.Info.AlphaType != SKAlphaType.Opaque, "Export must keep an alpha channel.");
    }

    [Fact]
    public void Exported_png_really_is_transparent_where_nothing_was_drawn()
    {
        using var workspace = new TempWorkspace();

        // An empty canvas with a single small object: most of the image must be untouched.
        var document = SpikeFixtures.CreateObjectScene(1, canvasSize: 2048);
        var path = workspace.File("transparent.png");

        OffscreenPngExporter.Export(document, path, 1024, 1024);

        using var decoded = SKBitmap.Decode(path)!;

        var transparent = 0;
        var opaque = 0;
        for (var y = 0; y < decoded.Height; y += 8)
        {
            for (var x = 0; x < decoded.Width; x += 8)
            {
                var alpha = decoded.GetPixel(x, y).Alpha;
                if (alpha == 0)
                {
                    transparent++;
                }
                else if (alpha == 255)
                {
                    opaque++;
                }
            }
        }

        Assert.True(transparent > 0, "Nothing in the export was transparent.");
        Assert.True(opaque > 0, "Nothing in the export was opaque.");
        Assert.True(transparent > opaque, "A near-empty canvas should be mostly transparent.");
    }

    [Fact]
    public void Overlay_chrome_never_reaches_the_export()
    {
        using var workspace = new TempWorkspace();
        var document = BuildScene();

        var maskSession = new MaskStrokeSession(document.Mask, BrushSettings.Default with { Radius = 50 });
        maskSession.AddPoint(new Vec2(1250, 1250));
        new CommandHistory().Execute(document, maskSession.Complete()!);

        var withOverlay = new RenderSettings
        {
            IncludeOverlay = true,
            ShowMaskEditOverlay = true,
            SelectedObjectId = document.Objects.Objects[0].Id,
            BackgroundColor = null,
        };

        var withoutOverlay = new RenderSettings
        {
            IncludeOverlay = false,
            ShowMaskEditOverlay = false,
            BackgroundColor = null,
        };

        using var overlayBitmap = RenderToBitmap(document, withOverlay);
        using var plainBitmap = RenderToBitmap(document, withoutOverlay);

        // The overlay must be visible on screen...
        Assert.True(CountDifferingPixels(overlayBitmap, plainBitmap) > 0, "Overlay drew nothing on screen.");

        // ...and the exporter must produce the overlay-free image, pixel for pixel.
        var path = workspace.File("no-overlay.png");
        OffscreenPngExporter.Export(document, path, 512, 512);

        using var exported = SKBitmap.Decode(path)!;
        Assert.Equal(0, CountDifferingPixels(exported, plainBitmap));
    }

    [Fact]
    public void Mask_changes_the_brush_layer_and_leaves_the_other_raster_layer_alone()
    {
        var document = BuildScene();

        var maskSession = new MaskStrokeSession(document.Mask, BrushSettings.Default with { Radius = 60, Flow = 1.0 });
        maskSession.AddPoint(new Vec2(1250, 1250));
        maskSession.AddPoint(new Vec2(1400, 1350));
        new CommandHistory().Execute(document, maskSession.Complete()!);

        var maskOn = new RenderSettings { IncludeOverlay = false, MaskEnabled = true };
        var maskOff = new RenderSettings { IncludeOverlay = false, MaskEnabled = false };

        using var on = RenderToBitmap(document, maskOn);
        using var off = RenderToBitmap(document, maskOff);

        // Decor occupies world tiles (0,0)-(1,0) => world x 0..512, y 0..256, which at a
        // 2048 -> 512 fit is screen x 0..128, y 0..64.
        var decorDiff = CountDifferingPixels(on, off, new SKRectI(0, 0, 128, 64));
        Assert.Equal(0, decorDiff);

        // The brush stroke around world (1200,1200)-(1500,1400) maps to screen ~ (300,300)-(375,350).
        var brushDiff = CountDifferingPixels(on, off, new SKRectI(290, 290, 390, 360));
        Assert.True(brushDiff > 0, "Enabling the mask changed nothing in the masked region.");
    }

    [Fact]
    public void An_unpainted_mask_does_not_erase_the_layer_it_targets()
    {
        var document = BuildScene();
        Assert.Equal(0, document.Mask.TileCount);

        using var on = RenderToBitmap(document, new RenderSettings { IncludeOverlay = false, MaskEnabled = true });
        using var off = RenderToBitmap(document, new RenderSettings { IncludeOverlay = false, MaskEnabled = false });

        Assert.Equal(0, CountDifferingPixels(on, off));
    }

    [Fact]
    public void Rendering_does_not_mutate_the_document()
    {
        var document = BuildScene();

        var revisionBefore = document.Revision;
        var brushTilesBefore = document.Brush.TileCount;
        var maskTilesBefore = document.Mask.TileCount;
        var objectsBefore = document.Objects.Count;

        using var _ = RenderToBitmap(document, new RenderSettings { IncludeOverlay = true, ShowMaskEditOverlay = true });

        Assert.Equal(revisionBefore, document.Revision);
        Assert.Equal(brushTilesBefore, document.Brush.TileCount);
        Assert.Equal(maskTilesBefore, document.Mask.TileCount);
        Assert.Equal(objectsBefore, document.Objects.Count);
    }

    [Fact]
    public void Export_does_not_mutate_the_document()
    {
        using var workspace = new TempWorkspace();
        var document = BuildScene();
        var revisionBefore = document.Revision;
        var dirtyBefore = document.IsDirty;

        OffscreenPngExporter.Export(document, workspace.File("side-effects.png"), 512, 512);

        Assert.Equal(revisionBefore, document.Revision);
        Assert.Equal(dirtyBefore, document.IsDirty);
    }

    [Fact]
    public void Tile_cache_reuses_uploads_until_the_tile_revision_changes()
    {
        var document = BuildScene();
        using var cache = new TileImageCache();

        var key = document.Brush.TileKeys.First();

        var first = cache.GetRasterTile(document.Brush, key);
        var second = cache.GetRasterTile(document.Brush, key);

        Assert.Same(first, second);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);

        document.Brush.BumpRevision(key);
        var third = cache.GetRasterTile(document.Brush, key);

        Assert.NotSame(second, third);
        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public void Objects_render_in_their_own_colour()
    {
        // Guards the DrawVertices blend mode. Per-vertex colours only reach the pixels under a
        // specific paint/blend combination; getting it wrong renders the whole scene one flat
        // colour, which no other test in this file would notice.
        const uint Argb = 0xFF3E5C76;

        var layer = new ObjectLayer(LayerId.New(), "objects");
        layer.Add(new SpikeObject
        {
            Id = ObjectId.New(),
            Kind = SpikeObjectKind.Square,
            Center = new Vec2(256, 256),
            Size = 200,
            Rotation = 0,
            ColorArgb = Argb,
        });

        layer.RebuildIndex();

        var document = SpikeFixtures.CreateObjectScene(0, canvasSize: 512);
        var info = new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;

        var viewport = new Viewport();
        viewport.Resize(512, 512);
        viewport.Set(Vec2.Zero, 1.0);

        using var tiles = new TileImageCache();
        using var geometry = ObjectGeometryCache.Build(layer);

        var stats = default(RenderStats);
        SceneRenderer.Render(
            surface.Canvas,
            document,
            viewport,
            new RenderSettings { IncludeOverlay = false, BackgroundColor = SKColors.Black },
            tiles,
            geometry,
            ref stats);

        surface.Canvas.Flush();

        using var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);

        var centre = bitmap.GetPixel(256, 256);
        Assert.Equal((byte)((Argb >> 16) & 0xFF), centre.Red);
        Assert.Equal((byte)((Argb >> 8) & 0xFF), centre.Green);
        Assert.Equal((byte)(Argb & 0xFF), centre.Blue);
        Assert.Equal(255, centre.Alpha);

        // And a point outside the square keeps the background.
        Assert.Equal(SKColors.Black, bitmap.GetPixel(20, 20));
    }

    [Fact]
    public void Geometry_cache_culls_chunks_outside_the_viewport()
    {
        var document = SpikeFixtures.CreateObjectScene(10_000);
        using var cache = ObjectGeometryCache.Build(document.Objects);

        var everything = cache.VisibleChunks(new Rect2(0, 0, 4096, 4096)).ToList();
        var corner = cache.VisibleChunks(new Rect2(0, 0, 512, 512)).ToList();

        Assert.NotEmpty(corner);
        Assert.True(
            corner.Count < everything.Count / 4,
            $"Culling to 1/64 of the canvas still kept {corner.Count} of {everything.Count} buckets.");
    }

    private static int CountDifferingPixels(SKBitmap a, SKBitmap b, SKRectI? region = null)
    {
        var rect = region ?? new SKRectI(0, 0, a.Width, a.Height);
        var differing = 0;

        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        return differing;
    }
}
