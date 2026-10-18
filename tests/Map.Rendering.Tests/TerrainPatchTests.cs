using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// After an edit of a few land tiles, the exact terrain is rebuilt only around them and laid into
/// the previous image (2026-10-18). The result must be the image a rebuild of the whole view gives:
/// coasts, cliffs below a raised edge, materials and the edge of the redrawn part included.
/// </summary>
[Collection("Land cliff fields")]
public sealed class TerrainPatchTests
{
    private const int Size = 2048;
    private const double Scale = .25;

    [Fact]
    public async Task A_stroke_rebuilds_only_its_surroundings_and_matches_a_whole_rebuild()
    {
        var island = Land("Остров", 1, 1024, 1024, 820, 640);
        var plateau = Land("Плато", 2, 800, 880, 330, 250);
        var before = Scene(island, plateau);
        // A blob added at the plateau's southern edge: its coast and its cliff wall change.
        var edited = before.WithLayer(plateau.Id, _ => plateau with { GeometryTiles = AddDisc(plateau.GeometryTiles, 860, 1125, 60) });

        using var terrain = new RenderImageCache();
        using var worker = new TerrainRefinement();
        var settled = await Refined(before, terrain, worker);
        settled.Dispose();
        var stats = new RenderStats();
        using var patched = await Refined(edited, terrain, worker, stats);
        Assert.Equal(1, stats.TerrainPatches);

        using var freshTerrain = new RenderImageCache();
        using var freshWorker = new TerrainRefinement();
        using var whole = await Refined(edited, freshTerrain, freshWorker);

        var difference = MaxDifference(whole, patched, out var where);
        Assert.True(difference <= 0, $"the patched terrain differs from a whole rebuild by {difference} at {where}");
        // The edit is visible: the blob changed pixels below the old edge.
        using var old = await Refined(before, new RenderImageCache(), new TerrainRefinement());
        Assert.True(MaxDifference(old, patched, out _) > 20, "the edit shows in the patched terrain");
    }

    [Fact]
    public async Task Painting_material_near_a_coast_rebuilds_only_its_surroundings_and_matches_a_whole_rebuild()
    {
        var island = Land("Остров", 1, 1024, 1024, 820, 640);
        var before = Scene(island);
        // Opaque paint over a disc that crosses the island's eastern coast.
        var paint = TileMap.Empty(4);
        for (var ty = 3; ty <= 4; ty++)
            for (var tx = 7; tx <= 7; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize * 4];
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = (tx * TileMap.TileSize) + x, wy = (ty * TileMap.TileSize) + y;
                        if (Math.Sqrt(Math.Pow(wx - 1840, 2) + Math.Pow(wy - 1024, 2)) > 90) continue;
                        var i = ((y * TileMap.TileSize) + x) * 4;
                        bytes[i] = 150; bytes[i + 1] = 60; bytes[i + 2] = 40; bytes[i + 3] = 255;
                    }
                paint = paint.With(new TileKey(tx, ty), bytes);
            }
        var painted = before.WithLayer(island.Id, _ => island with { PaintTiles = paint });

        using var terrain = new RenderImageCache();
        using var worker = new TerrainRefinement();
        (await Refined(before, terrain, worker)).Dispose();
        var stats = new RenderStats();
        using var patched = await Refined(painted, terrain, worker, stats);
        Assert.Equal(1, stats.TerrainPatches);
        using var whole = await Refined(painted, new RenderImageCache(), new TerrainRefinement());
        var difference = MaxDifference(whole, patched, out var where);
        Assert.True(difference <= 0, $"the patched terrain differs from a whole rebuild by {difference} at {where}");
    }

    [Fact]
    public async Task A_stroke_beside_a_river_mouth_matches_a_whole_rebuild()
    {
        var island = Land("Остров", 0, 1024, 1024, 820, 640);
        // A river from the interior out through the eastern coast: its mouth reads the coast.
        var water = new River
        {
            Style = RiverStyle.Illustrated() with { EdgeRoughness = 0 },
            Channels = [RiverEditor.NewChannel([new VectorPoint(1300, 1000), new VectorPoint(1700, 1010), new VectorPoint(1980, 1020)], 30)],
        };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [water] };
        var before = Scene(island, objects);
        var edited = before.WithLayer(island.Id, _ => island with { GeometryTiles = AddDisc(island.GeometryTiles, 1830, 940, 50) });

        using var terrain = new RenderImageCache();
        using var worker = new TerrainRefinement();
        (await Refined(before, terrain, worker)).Dispose();
        var stats = new RenderStats();
        using var patched = await Refined(edited, terrain, worker, stats);
        Assert.Equal(1, stats.TerrainPatches);
        using var whole = await Refined(edited, new RenderImageCache(), new TerrainRefinement());
        // The water is drawn from vector paths in each build's own view: its antialiasing may
        // round one channel by a single unit differently (measured: one pixel, 1/255).
        var difference = MaxDifference(whole, patched, out var where);
        Assert.True(difference <= 1, $"the patched terrain differs from a whole rebuild by {difference} at {where}");
    }

    [Fact]
    public async Task A_style_change_rebuilds_the_whole_view()
    {
        var island = Land("Остров", 1, 1024, 1024, 820, 640);
        var before = Scene(island);
        var restyled = before.WithLayer(island.Id, _ => island with { EdgeStyle = island.EdgeStyle with { OutlineWidth = 6 } });
        using var terrain = new RenderImageCache();
        using var worker = new TerrainRefinement();
        (await Refined(before, terrain, worker)).Dispose();
        var stats = new RenderStats();
        using var _ = await Refined(restyled, terrain, worker, stats);
        Assert.Equal(0, stats.TerrainPatches);
    }

    /// <summary>Renders frames until the background terrain is adopted; returns the settled frame.</summary>
    private static async Task<SKBitmap> Refined(MapDocument document, RenderImageCache terrain, TerrainRefinement worker,
        RenderStats? total = null)
    {
        var view = new WorldBounds(0, 0, Size, Size);
        for (var frame = 0; frame < 400; frame++)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stats = new RenderStats();
            using var surface = SKSurface.Create(new SKImageInfo((int)(Size * Scale), (int)(Size * Scale)))!;
            surface.Canvas.Scale((float)Scale);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                ViewScale = Scale, VisibleWorld = view, Stats = stats,
                Terrain = terrain, TerrainRefinement = worker, RequestRefinedFrame = () => ready.TrySetResult(),
            });
            if (total is not null) total.TerrainPatches += stats.TerrainPatches;
            if (stats.TerrainRefinementsPending == 0)
            {
                using var image = surface.Snapshot();
                return SKBitmap.FromImage(image);
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        throw new InvalidOperationException("The terrain was never refined.");
    }

    private static MapDocument Scene(params MapLayer[] layers)
    {
        const string sea = "#FF8FB0BA";
        return new MapDocument(DocumentId.New(), "Patch", DateTimeOffset.UnixEpoch, new CanvasDefinition(Size, Size, "px", sea),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, sea), .. layers]);
    }

    private static LandLayer Land(string name, int level, double cx, double cy, double rx, double ry)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < Size; ty++)
            for (var tx = 0; tx * TileMap.TileSize < Size; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = (tx * TileMap.TileSize) + x, wy = (ty * TileMap.TileSize) + y;
                        var angle = Math.Atan2(wy - cy, wx - cx);
                        var d = Math.Sqrt(Math.Pow((wx - cx) / rx, 2) + Math.Pow((wy - cy) / ry, 2)) / (1 + (.08 * Math.Sin(angle * 7)));
                        var b = (byte)Math.Clamp((1 - d) * Math.Max(rx, ry) / 2 * 255, 0, 255);
                        bytes[(y * TileMap.TileSize) + x] = b; any |= b > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = level };
    }

    /// <summary>The coverage with a disc added: only the tiles the disc touches are new arrays.</summary>
    private static TileMap AddDisc(TileMap tiles, double cx, double cy, double radius)
    {
        var result = tiles;
        for (var ty = (int)((cy - radius) / TileMap.TileSize); ty <= (int)((cy + radius) / TileMap.TileSize); ty++)
            for (var tx = (int)((cx - radius) / TileMap.TileSize); tx <= (int)((cx + radius) / TileMap.TileSize); tx++)
            {
                var key = new TileKey(tx, ty);
                var bytes = tiles.TryGet(key) is { } existing ? (byte[])existing.Clone() : new byte[TileMap.TileSize * TileMap.TileSize];
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = (tx * TileMap.TileSize) + x, wy = (ty * TileMap.TileSize) + y;
                        var d = Math.Sqrt(((wx - cx) * (wx - cx)) + ((wy - cy) * (wy - cy)));
                        var b = (byte)Math.Clamp((radius - d) * 64, 0, 255);
                        var i = (y * TileMap.TileSize) + x;
                        bytes[i] = Math.Max(bytes[i], b);
                    }
                result = result.With(key, bytes);
            }
        return result;
    }

    private static int MaxDifference(SKBitmap a, SKBitmap b, out string where)
    {
        var worst = 0; where = "";
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Max(Math.Abs(p.Blue - q.Blue), Math.Abs(p.Alpha - q.Alpha))));
                if (d > worst) { worst = d; where = $"({x},{y}) {p} vs {q}"; }
            }
        return worst;
    }
}
