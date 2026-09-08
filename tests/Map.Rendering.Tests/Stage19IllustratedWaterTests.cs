using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage19IllustratedWaterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_lake_outflow_has_no_earth_plug_for_either_lake_winding(bool reverse)
    {
        VectorPoint[] outline = [new(70, 70), new(180, 70), new(180, 180), new(70, 180)];
        if (reverse) Array.Reverse(outline);
        var lake = RiverEditor.NewLake(outline);
        var river = new River { Style = RiverStyle.Illustrated() with { EdgeRoughness = 0, HighlightOpacity = 0, ShallowWidth = 0 }, Channels = [lake] };
        var geometry = RiverGeometry.Evaluate(river).Channels[lake.Id];
        // Both contours' second segment lies on the right shore (in opposite directions).
        var junction = new RiverJunction(lake.Id, lake.Nodes[1].Id, .5);
        Assert.True(RiverGeometry.TryJunction(geometry, junction, out var meeting));
        var outflow = RiverEditor.NewChannel([new(meeting.X + 40, meeting.Y), new(260, meeting.Y + 30)], 24)
            with { Source = junction };
        river = river with { Channels = [lake, outflow] };
        river.Validate();
        var document = new MapDocument(DocumentId.New(), "lake", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(300, 260, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "page", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "water", true, false, 1, []) { Rivers = [river] }]);
        using var image = Render(document, new WorldBounds(0, 0, 300, 260));
        foreach (var dx in new[] { -5, -2, 0, 2, 5 })
        {
            var pixel = image.GetPixel((int)(meeting.X + dx), (int)meeting.Y);
            Assert.Equal(new SKColor(116, 167, 161), pixel);
        }
    }

    [Fact]
    public void Inner_land_shadow_shades_the_shore_and_preserves_the_plain_interior()
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 30; y < 226; y++) for (var x = 30; x < 226; x++) pixels[y * TileMap.TileSize + x] = 255;
        var land = new LandLayer(LayerId.New(), "land", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), pixels), new LandEdgeStyle
            { FillColor = "#FFFFFFFF", OutlineWidth = 0, RippleCount = 0, OuterShadowOpacity = 0, InnerShadowOpacity = .6, ShadowBlur = 8 });
        var document = new MapDocument(DocumentId.New(), "shore", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FF337788"),
            [new BaseColorLayer(LayerId.New(), "sea", true, false, 1, "#FF337788"), land]);
        using var frame = Render(document, new WorldBounds(0, 0, 256, 256));
        Assert.Equal(SKColors.White, frame.GetPixel(128, 128));
        Assert.InRange(frame.GetPixel(33, 128).Red, 160, 230);
        Assert.Equal(new SKColor(51, 119, 136), frame.GetPixel(12, 128));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Drawn_banks_and_water_lines_stay_fixed_when_a_region_is_exported(bool pen)
    {
        var document = Stage19WaterJoinRenderingTests.Scene(style: _ => RiverStyle.Illustrated(pen));
        var land = document.Layers.OfType<LandLayer>().Single();
        // Isolate the water's new linework. Raster land-effect edges have their own antialias
        // tolerance in the production export tests; they are not the subject of this check.
        document = document.WithLayer(land.Id, _ => land with { EdgeStyle = land.EdgeStyle with
            { OutlineWidth = 0, RippleCount = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0 } });
        using var full = Render(document, new WorldBounds(0, 0, 512, 512));
        using var region = Render(document, new WorldBounds(128, 128, 384, 384));
        // This area includes the junction, both banks and the lake shore. Leave room for
        // offscreen land shadows at the cropped surface boundary.
        var edgeDifferences = 0;
        for (var y = 48; y < 208; y++) for (var x = 48; x < 208; x++)
        {
            var a = full.GetPixel(x + 128, y + 128); var b = region.GetPixel(x, y);
            if (a == b) continue;
            // Same established Skia edge tolerance as Stage19RiverRenderingTests: device-offset
            // rounding may affect at most 0.5% edge pixels, never the interior or the geometry.
            var interior = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }
                .All(d => full.GetPixel(x + 128 + d.Item1, y + 128 + d.Item2) == a);
            var delta = Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));
            Assert.True(!interior && delta <= 64,
                $"A crop moved the drawn shoreline at {x + 128},{y + 128}: {a} / {b}");
            edgeDifferences++;
        }
        Assert.True(edgeDifferences <= 160 * 160 / 200, $"Too many changed shoreline pixels: {edgeDifferences}");
    }

    private static SKBitmap Render(MapDocument document, WorldBounds region)
    {
        var info = new SKImageInfo((int)(region.Right - region.Left), (int)(region.Bottom - region.Top));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Translate((float)-region.Left, (float)-region.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, VisibleWorld = region });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
