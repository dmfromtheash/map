using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// A raised piece of land drops as a cliff below its southern edge, one step per level above the
/// ground or sea beneath it; land at level 0 over the sea keeps exactly its former look.
/// </summary>
public sealed class LandCliffRenderingTests
{
    private const int Width = 768;
    private const int Height = 512;
    private const string Sea = "#FF9DBBC4";

    /// <summary>Solid cliffs without gaps, so a probe below an edge always meets the wall.</summary>
    private static readonly LandCliffStyle Solid = new() { Gaps = 0, HeightVariation = 0, FootRoughness = 0, Foot = CliffFoot.Plain };

    [Fact]
    public void The_wall_shader_compiles()
    {
        Assert.True(LandCliffShader.Available, LandCliffShader.LastError);
    }

    [Fact]
    public void Land_at_sea_level_has_no_cliff()
    {
        var island = Land("Остров", 0, (x, y) => Blob(x, y, 384, 230, 250, 150));
        using var with = Render(Document(island));
        using var without = Render(Document(Styled(island, new LandCliffStyle { Height = 0 })));
        AssertSame(with, without);
    }

    [Fact]
    public void A_raised_island_drops_to_the_sea_and_the_wall_steps_with_the_levels()
    {
        // The island ends at y = 380 in the middle column; below it lies open sea.
        var island = Styled(Land("Остров", 1, (x, y) => Blob(x, y, 384, 230, 250, 150)), Solid);
        using var flat = Render(Document(island with { Level = 0 }));
        using var raised = Render(Document(island));
        Assert.Equal(flat.GetPixel(384, 250), raised.GetPixel(384, 250));
        Assert.Equal(flat.GetPixel(384, 40), raised.GetPixel(384, 40));
        // Just below the southern edge: rock, not sea.
        var rock = raised.GetPixel(384, 395);
        Assert.NotEqual(flat.GetPixel(384, 395), rock);
        Assert.True(rock.Red > rock.Blue, $"the wall is warm rock, got {rock}");
        // One level is one step of 34 units, and its shadow: well below that the sea shows again.
        Assert.Equal(flat.GetPixel(384, 480), raised.GetPixel(384, 480));

        using var higher = Render(Document(island with { Level = 2 }));
        var secondStep = higher.GetPixel(384, 425);
        Assert.NotEqual(flat.GetPixel(384, 425), secondStep);
        Assert.True(secondStep.Red > secondStep.Blue, $"the second step is rock as well, got {secondStep}");
    }

    [Fact]
    public void A_plateau_casts_its_wall_over_the_lower_land_only_where_it_is_higher()
    {
        var island = Styled(Land("Остров", 1, (x, y) => Blob(x, y, 384, 260, 330, 210)), Solid);
        var plateau = Styled(Land("Плато", 2, (x, y) => Blob(x, y, 330, 210, 140, 90)), Solid);
        using var lower = Render(Document(island));
        using var both = Render(Document(island, plateau));
        // Below the plateau's southern edge (y = 300) the lower land is covered by the wall.
        Assert.NotEqual(lower.GetPixel(330, 312), both.GetPixel(330, 312));
        // Same level: no wall and the same land as before.
        using var level = Render(Document(island, plateau with { Level = 1 }));
        Assert.Equal(lower.GetPixel(330, 330), level.GetPixel(330, 330));
    }

    [Fact]
    public void A_pit_cut_into_raised_land_shows_its_wall_at_the_far_side()
    {
        // A hole in a raised plateau: the sea shows inside, and the wall hangs from the hole's
        // northern rim, as in the pits of the third sheet.
        var plateau = Styled(Land("Плато", 1, (x, y) => (byte)Math.Min(Blob(x, y, 384, 256, 330, 220),
            255 - Blob(x, y, 384, 250, 90, 60))), Solid);
        using var image = Render(Document(plateau));
        using var flat = Render(Document(plateau with { Level = 0 }));
        Assert.NotEqual(flat.GetPixel(384, 200), image.GetPixel(384, 200)); // just below the north rim of the pit
        Assert.Equal(flat.GetPixel(384, 300), image.GetPixel(384, 300));   // the south rim has no wall inside
    }

    [Fact]
    public void Fields_are_kept_and_a_slider_does_not_recompute_them()
    {
        LandCliffField.Clear();
        var island = Styled(Land("Остров", 1, (x, y) => Blob(x, y, 384, 230, 250, 150)), Solid);
        using (Render(Document(island))) { }
        var first = LandCliffField.Computed;
        using (Render(Document(Styled(island, Solid with { Hatching = .2, Color = "#FF888888", Height = 30 })))) { }
        Assert.Equal(first, LandCliffField.Computed);
    }

    [Fact]
    public void Cliff_settings_are_bounded()
    {
        var wild = new LandCliffStyle
        {
            Height = 9999, Strata = 99, Hatching = -1, HatchSpacing = 0.1, FootRoughness = 7, BaseShadow = 2,
            Lean = 5, Gaps = 3, Foot = (CliffFoot)42, InkWidth = double.NaN,
        }.Bounded();
        Assert.Equal(LandCliffStyle.MaximumHeight, wild.Height);
        Assert.Equal(LandCliffStyle.MaximumStrata, wild.Strata);
        Assert.Equal(0, wild.Hatching);
        Assert.Equal(0.8, wild.HatchSpacing);
        Assert.Equal(1, wild.FootRoughness);
        Assert.Equal(1, wild.BaseShadow);
        Assert.Equal(1, wild.Lean);
        Assert.Equal(0.95, wild.Gaps);
        Assert.Equal(CliffFoot.Fade, wild.Foot);
        Assert.Equal(new LandCliffStyle().InkWidth, wild.InkWidth);
        Assert.Equal(2, new LandCliffStyle().Steps(2, 0));
        Assert.Equal(0, new LandCliffStyle().Steps(1, 1));
        Assert.Equal(0, new LandCliffStyle { Height = 0 }.Steps(3, 0));
    }

    /// <summary>
    /// MAP_CLIFF_PNG=&lt;folder&gt; writes one sheet per ready look - islands, plateaus, a pit and
    /// scattered rocks on parchment, like the owner's tier sheets - for side-by-side comparison.
    /// </summary>
    [Fact]
    public void Look_sheets_for_review()
    {
        if (Environment.GetEnvironmentVariable("MAP_CLIFF_PNG") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        foreach (var look in LandCliffStyle.Looks)
        {
            using var sheet = RenderSheet(look, 1.6);
            Save(sheet, Path.Combine(folder, $"look-{look.Id}.png"));
            using var close = RenderSheet(look, 4);
            using var crop = new SKBitmap(1100, 700);
            close.ExtractSubset(crop, SKRectI.Create(700, 1280, 1100, 700));
            Save(crop, Path.Combine(folder, $"look-{look.Id}-close.png"));
        }
    }

    public static SKBitmap RenderSheet(LandCliffLook look, double scale)
    {
        const string paper = "#FFE9DCC0";
        LandLayer Piece(string name, int level, Func<int, int, byte> shape)
        {
            var land = Land(name, level, shape);
            return land with
            {
                EdgeStyle = land.EdgeStyle with
                {
                    FillColor = look.Id == "atlas" ? "#FFD0D8A8" : paper, FillTextureId = null,
                    OutlineColor = look.Rim ?? land.EdgeStyle.OutlineColor, OutlineWidth = look.RimWidth ?? land.EdgeStyle.OutlineWidth,
                    RippleCount = 0, OuterShadowOpacity = 0, InnerShadowOpacity = 0,
                    Cliff = look.Style,
                },
            };
        }
        var island = Piece("Остров", 1, (x, y) => Math.Max(Blob(x, y, 250, 200, 200, 130, 3), Blob(x, y, 560, 330, 170, 110, 5)));
        var plateau = Piece("Плато", 2, (x, y) => Math.Min(Blob(x, y, 230, 170, 110, 70, 7), (byte)(255 - Blob(x, y, 230, 160, 40, 25, 2))));
        var rocks = Piece("Скалы", 1, (x, y) => Math.Max(Math.Max(Blob(x, y, 640, 90, 50, 26, 9), Blob(x, y, 120, 430, 40, 22, 4)),
            Blob(x, y, 420, 440, 60, 28, 6)));
        var document = new MapDocument(DocumentId.New(), "Лист", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Width, Height, "px", look.Id == "atlas" ? "#FF9DBBC4" : paper),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, look.Id == "atlas" ? "#FF9DBBC4" : paper), island, plateau, rocks]);
        var info = new SKImageInfo((int)(Width * scale), (int)(Height * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale, IsExport = true });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static LandLayer Styled(LandLayer land, LandCliffStyle cliff) => land with { EdgeStyle = land.EdgeStyle with { Cliff = cliff } };

    private static byte Blob(int x, int y, double cx, double cy, double rx, double ry, int seed = 0)
    {
        var angle = Math.Atan2(y - cy, x - cx);
        var wobble = 1 + (0.08 * Math.Sin(angle * 5 + seed)) + (0.05 * Math.Sin((angle * 11) + 1.3 + seed * 2)) + 0.03 * Math.Sin(angle * 23 + seed);
        var distance = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2)) / wobble;
        return (byte)Math.Clamp((1 - distance) * 255 * 40, 0, 255);
    }

    private static LandLayer Land(string name, int level, Func<int, int, byte> coverage)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < Height; ty++)
            for (var tx = 0; tx * TileMap.TileSize < Width; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var value = coverage((tx * TileMap.TileSize) + x, (ty * TileMap.TileSize) + y);
                        bytes[(y * TileMap.TileSize) + x] = value;
                        any |= value > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = level };
    }

    private static MapDocument Document(params LandLayer[] lands) => new(DocumentId.New(), "Ярусы", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(Width, Height, "px", Sea),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, Sea), .. lands]);

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(Width, Height);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions());
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static void AssertSame(SKBitmap expected, SKBitmap actual)
    {
        for (var y = 0; y < Height; y += 3)
            for (var x = 0; x < Width; x += 3)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
    }

    public static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
