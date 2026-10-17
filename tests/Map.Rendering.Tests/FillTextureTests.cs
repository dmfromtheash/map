using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Missing materials fall back safely; clipping and colour paint composition remain available.
/// </summary>
public sealed class FillTextureTests
{
    [Fact]
    public void Removed_materials_keep_legacy_ids_but_have_no_pixels_shader_or_brush_sampler()
    {
        Assert.NotEmpty(MapFillTextures.All);
        foreach (var texture in MapFillTextures.All.Where(t => t.Id != MapFillTextures.Foothill))
        {
            Assert.True(MapFillTextures.IsValidId(texture.Id));
            Assert.Null(MapTextureLibrary.Image(texture.Id));
            Assert.Null(MapTextureLibrary.Shader(texture.Id, 128));
            Assert.Null(MapTextureLibrary.BrushTexture(texture.Id));
        }
        Assert.Equal(1, typeof(MapTextureLibrary).Assembly.GetManifestResourceNames()
            .Count(name => name.StartsWith("Map.Rendering.Textures.", StringComparison.Ordinal)));

        Assert.Null(MapTextureLibrary.Image("no-such-material"));
        Assert.Null(MapTextureLibrary.Shader("no-such-material", 128));
        Assert.True(MapFillTextures.IsValidId(null));
        Assert.False(MapFillTextures.IsValidId("no-such-material"));
    }

    [Theory]
    [InlineData(MapFillTextures.Foothill)]
    public void Retained_material_fill_and_brush_match_across_tile_edges_and_negative_coordinates(string id)
    {
        var image = Assert.IsType<SKImage>(MapTextureLibrary.Image(id));
        var texture = Assert.IsType<BrushTexture>(MapTextureLibrary.BrushTexture(id));
        Assert.Equal(MapFillTextures.Find(id)!.MirrorRepeat, texture.MirrorRepeat);
        Assert.Equal(image.Width, image.Height);
        var coverage = Enumerable.Repeat((byte)255, 256 * 256).ToArray();
        foreach (var key in new[] { new TileKey(-1, -1), new TileKey(image.Width / 256, image.Height / 256) })
        {
            var preview = RasterStrokeSession.RenderPreviewTile(new BrushSettings
            { Texture = texture, Color = "#FFFFFFFF", TextureScale = 1, Opacity = 1 }, key, coverage);
            using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Translate(-key.X * 256, -key.Y * 256);
            Assert.True(MapTextureLibrary.DrawFill(surface.Canvas, id, image.Width, 1,
                new SKRect(key.X * 256, key.Y * 256, key.X * 256 + 256, key.Y * 256 + 256)));
            using var bitmap = new SKBitmap(new SKImageInfo(256, 256));
            surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
            foreach (var y in new[] { 0, 127, 229, 230, 231, 255 })
                foreach (var x in new[] { 0, 127, 229, 230, 231, 255 })
                {
                    var offset = (y * 256 + x) * 4;
                    Assert.Equal(new SKColor(preview[offset], preview[offset + 1], preview[offset + 2], preview[offset + 3]), bitmap.GetPixel(x, y));
                }
        }
    }

    private static MapDocument Scene(LandEdgeStyle landStyle, RiverStyle riverStyle)
    {
        // Land over the left 200 columns only, so there is somewhere on the canvas that is not land.
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < 200; x++) coverage[y * TileMap.TileSize + x] = 255;
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), landStyle);
        var river = new River
        {
            Name = "Река",
            Style = riverStyle,
            Channels = [RiverEditor.NewChannel([new VectorPoint(10, 128), new VectorPoint(246, 128)], 60)],
        };
        river.Validate();
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "Материалы", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land, objects]);
    }

    private static SKBitmap Render(MapDocument document, RenderOptions? options = null)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, options ?? new RenderOptions());
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    [Fact]
    public void Removed_ground_material_falls_back_to_the_same_land_and_sea()
    {
        var plainStyle = new LandEdgeStyle { FillColor = "#FF8CA36B", RippleCount = 0, OutlineWidth = 0 };
        var flatRiver = new RiverStyle { TextureId = null, ShallowWidth = 0, HighlightOpacity = 0, CutsLand = false, ClipToLand = false };
        using var plain = Render(Scene(plainStyle, flatRiver));
        using var textured = Render(Scene(
            plainStyle with { FillTextureId = "parchment", FillTextureOpacity = 0.6, FillTextureScale = 96 }, flatRiver));

        Assert.Equal(plain.GetPixel(100, 45), textured.GetPixel(100, 45));
        Assert.Equal(plain.GetPixel(240, 45), textured.GetPixel(240, 45));
    }

    [Fact]
    public void Removed_water_material_falls_back_while_geometric_bank_shading_remains()
    {
        var landStyle = new LandEdgeStyle { FillColor = "#FF8CA36B", RippleCount = 0, OutlineWidth = 0 };
        var flat = new RiverStyle
        {
            TextureId = null, ShallowWidth = 0, HighlightOpacity = 0,
            CutsLand = false, ClipToLand = false, BankShadowOpacity = 0,
        };
        using var plain = Render(Scene(landStyle, flat));
        var middle = plain.GetPixel(128, 128);
        Assert.Equal(middle, plain.GetPixel(128, 118));

        using var shallows = Render(Scene(landStyle, flat with { ShallowWidth = 6 }));
        var nearBank = shallows.GetPixel(128, 128 - 25);
        var centre = shallows.GetPixel(128, 128);
        Assert.True(nearBank.Red > centre.Red && nearBank.Green > centre.Green && nearBank.Blue > centre.Blue,
            $"water has to lighten towards the bank, centre {centre} near bank {nearBank}");

        using var material = Render(Scene(landStyle, flat with { TextureId = "water-open", TextureOpacity = 0.7, TextureScale = 64 }));
        Assert.Equal(plain.GetPixel(128, 128), material.GetPixel(128, 128));
        Assert.Equal(plain.GetPixel(100, 45), material.GetPixel(100, 45));
    }

    [Fact]
    public void Painting_is_clipped_by_coast_water_mask_opacity_and_higher_land()
    {
        var doc = Scene(new LandEdgeStyle { OutlineWidth = 0, RippleCount = 0, ShadowBlur = 0 },
            new RiverStyle { CutsLand = true, ClipToLand = false, HighlightOpacity = 0 });
        var land = doc.Layers.OfType<LandLayer>().Single();
        var pixels = new byte[256 * 256 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 230; pixels[i + 1] = 80; pixels[i + 2] = 50; pixels[i + 3] = 255; }
        var painted = land with { PaintTiles = TileMap.Empty(4).With(new(0, 0), pixels) };
        var scene = doc.WithLayer(land.Id, _ => painted);
        using var baseline = Render(doc); using var image = Render(scene);
        Assert.Equal(new SKColor(230, 80, 50), image.GetPixel(100, 40));
        Assert.Equal(baseline.GetPixel(240, 40), image.GetPixel(240, 40));
        Assert.Equal(baseline.GetPixel(100, 128), image.GetPixel(100, 128));
        var mask = Enumerable.Repeat((byte)255, 65536).ToArray();
        for (var y = 10; y < 80; y++) for (var x = 10; x < 80; x++) mask[y * 256 + x] = 0;
        using var masked = Render(doc.WithLayer(land.Id, _ => painted with { Mask = new LayerMask(TileMap.Empty(1).With(new(0, 0), mask), true), Opacity = .5 }));
        Assert.Equal(SKColors.White, masked.GetPixel(40, 40));
        Assert.InRange(masked.GetPixel(100, 40).Red, (byte)241, (byte)245);
        var higher = land with { Id = LayerId.New(), Level = 1, EdgeStyle = land.EdgeStyle with { FillColor = "#FF335577" } };
        using var covered = Render(scene.WithLayers([.. scene.Layers, higher]));
        Assert.Equal(new SKColor(51, 85, 119), covered.GetPixel(100, 40));
    }

    [Fact]
    public void A_live_material_eraser_reveals_the_base_without_erasing_the_land()
    {
        var doc = Scene(new LandEdgeStyle { OutlineWidth = 0, RippleCount = 0, ShadowBlur = 0 },
            new RiverStyle { CutsLand = false, ClipToLand = false });
        var land = doc.Layers.OfType<LandLayer>().Single();
        var painting = new RasterStrokeSession(land.PaintTiles, new BrushSettings { Radius = 20, Softness = 0, Color = "#FFFF0000" });
        painting.AddPoint(80, 40);
        var painted = land with { PaintTiles = land.PaintTiles.WithMany(painting.Commit().After) };
        var eraser = new RasterStrokeSession(painted.PaintTiles, new BrushSettings { Radius = 10, Softness = 0, Eraser = true }); eraser.AddPoint(80, 40);
        using var preview = Render(doc.WithLayer(land.Id, _ => painted), new RenderOptions
        { Preview = new StrokePreview { LayerId = land.Id, IsLandPaint = true, Coverage = eraser.Coverage, Eraser = true, Color = "#FFFFFFFF", Opacity = 1 } });
        using var baseline = Render(doc);
        Assert.Equal(baseline.GetPixel(80, 40), preview.GetPixel(80, 40));
        Assert.Equal(new SKColor(255, 0, 0), preview.GetPixel(96, 40));
    }
}
