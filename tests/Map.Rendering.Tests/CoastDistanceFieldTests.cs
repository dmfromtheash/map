using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>The coast as a distance field: exact distances, and every edge effect where the filter passes put it.</summary>
public sealed class CoastDistanceFieldTests
{
    [Fact]
    public void Signed_distance_is_exact_around_a_square()
    {
        const int size = 64;
        var coverage = new byte[size * size];
        for (var y = 16; y < 48; y++)
            for (var x = 16; x < 48; x++)
                coverage[(y * size) + x] = 255;

        var field = CoastDistanceField.SignedDistance(coverage, size, size);

        Assert.Equal(-0.5f, field[(16 * size) + 32], 3);      // first land row: half a pixel inland
        Assert.Equal(0.5f, field[(15 * size) + 32], 3);       // first sea row: half a pixel at sea
        Assert.Equal(9.5f, field[(6 * size) + 32], 3);        // ten rows out
        Assert.Equal(-15.5f, field[(31 * size) + 31], 3);     // the centre, sixteen rows from the nearest shore
        Assert.Equal(MathF.Sqrt(9 * 9 + 9 * 9) - 0.5f, field[(7 * size) + 7], 2); // a corner is a true diagonal
    }

    [Fact]
    public void Effects_sit_at_their_distances_and_fast_terrain_is_unchanged()
    {
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 60; y < 200; y++)
            for (var x = 60; x < 200; x++)
                coverage[(y * TileMap.TileSize) + x] = 255;
        var style = new LandEdgeStyle
        {
            FillColor = "#FF3C8C4C", OutlineColor = "#FF102030", OutlineWidth = 3,
            RippleColor = "#FF0000FF", RippleWidth = 2, RippleSpacing = 10, RippleCount = 2,
            OuterShadowOpacity = .5, InnerShadowOpacity = .5, ShadowBlur = 6, FillTextureId = null,
        };
        var land = new LandLayer(LayerId.New(), "land", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), style);
        var document = new MapDocument(DocumentId.New(), "land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"), land]);

        using var exact = Render(document, new RenderOptions { IsExport = true });
        using var fast = Render(document, new RenderOptions { FastTerrain = true });

        // The first surf line lies 10..12 units off the coast at x < 60: blue there, white between.
        var ripple = exact.GetPixel(49, 130);
        Assert.True(ripple.Blue > ripple.Red + 40, $"surf line expected at 11 units: {ripple}");
        var gap = exact.GetPixel(55, 130);
        Assert.True(gap.Red > ripple.Red + 40 && gap.Red > 150, $"no surf line at 5 units: {gap}");
        // The outline hugs the coast on the sea side.
        var outline = exact.GetPixel(58, 130);
        Assert.True(outline.Red < 80 && outline.Green < 80, $"outline expected at the coast: {outline}");
        // The inner shadow darkens the land near its edge compared with the centre.
        var centre = exact.GetPixel(130, 130);
        var edge = exact.GetPixel(62, 130);
        Assert.True(edge.Green < centre.Green - 20, $"inner shadow expected: edge {edge}, centre {centre}");
        // The outer shadow greys the sea just off the coast, well inside the surf.
        var shadowed = exact.GetPixel(57, 130);
        Assert.True(shadowed.Red < 230, $"outer shadow expected: {shadowed}");
        // Nothing of the surf lands on the land itself.
        Assert.True(exact.GetPixel(70, 130).Blue < 120);
        // Fast terrain draws the plain fill without any of it.
        Assert.Equal(new SKColor(255, 255, 255), fast.GetPixel(49, 130));
        Assert.Equal(centre.Green, fast.GetPixel(130, 130).Green);
    }

    [Fact]
    public void Overlapping_land_keeps_one_shared_coast_with_the_field_as_with_the_filters()
    {
        byte[] Square(int from, int to)
        {
            var tile = new byte[TileMap.TileSize * TileMap.TileSize];
            for (var y = from; y < to; y++)
                for (var x = from; x < to; x++)
                    tile[(y * TileMap.TileSize) + x] = 255;
            return tile;
        }
        var style = new LandEdgeStyle
        {
            FillColor = "#FF3C8C4C", OutlineColor = "#FF102030", OutlineWidth = 3,
            RippleColor = "#FF0000FF", RippleWidth = 2, RippleSpacing = 10, RippleCount = 2,
            OuterShadowOpacity = .4, InnerShadowOpacity = .4, ShadowBlur = 6, FillTextureId = null,
        };
        var lower = new LandLayer(LayerId.New(), "lower", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), Square(40, 150)), style);
        var upper = new LandLayer(LayerId.New(), "upper", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), Square(100, 220)), style);
        var document = new MapDocument(DocumentId.New(), "land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"), lower, upper]);

        using (var withField = Render(document, new RenderOptions { IsExport = true }))
        using (var withFilters = Render(document, new RenderOptions { IsExport = true, CoastFilters = true }))
        {
            // Inside the overlap and deep in either piece nothing changes; the shared coast is
            // one coast: the surf of the upper piece does not cross the lower piece.
            Assert.Equal(withFilters.GetPixel(125, 125), withField.GetPixel(125, 125));
            Assert.True(withField.GetPixel(70, 70).Blue < 120 && withField.GetPixel(160, 160).Blue < 120);
            long total = 0; var count = 0;
            for (var y = 0; y < 256; y += 2)
                for (var x = 0; x < 256; x += 2)
                {
                    var a = withFilters.GetPixel(x, y); var b = withField.GetPixel(x, y);
                    total += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                    count++;
                }
            var mean = total / (3.0 * count);
            Assert.True(mean < 6, $"mean channel difference {mean:0.0} between the field and the filter passes");
        }
    }

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
