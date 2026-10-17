using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The kept images of the finished land and of the whole terrain: a frame drawn from them is
/// the frame drawn without them, an unchanged scene hits them, and any edit to the land, its
/// painting or a carving river is a miss - the cache can never show a stale coast or texture.
/// </summary>
public sealed class RenderImageCacheTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Solid_interior_matches_the_same_crop_of_the_full_coast_render(bool windingRiver, bool partialTiles)
    {
        var pixels=Enumerable.Repeat((byte)255,TileMap.TileSize*TileMap.TileSize).ToArray();
        var coverage=TileMap.Empty(1);
        for(var y=0;y<3;y++) for(var x=0;x<3;x++)
        {
            var tile = pixels.ToArray();
            if (partialTiles)
                for (var py=0;py<TileMap.TileSize;py++) for (var px=0;px<TileMap.TileSize;px++)
                    if (x*TileMap.TileSize+px<64 || x*TileMap.TileSize+px>=704
                        || y*TileMap.TileSize+py<64 || y*TileMap.TileSize+py>=704)
                        tile[py*TileMap.TileSize+px]=0;
            coverage=coverage.With(new(x,y),tile);
        }
        var land=new LandLayer(LayerId.New(),"Земля",true,false,1,coverage,new LandEdgeStyle {
            FillColor="#FFBEC69D",FillTextureId="regional-meadow",FillTextureScale=320,FillTextureOpacity=.3,
            OuterShadowOpacity=.2,InnerShadowOpacity=.2,ShadowBlur=4,OutlineWidth=2,RippleCount=2,RippleSpacing=7 });
        var river=new River { Style=RiverStyle.Illustrated(),Channels=[RiverEditor.NewChannel([
            new VectorPoint(35,55),new VectorPoint(680,55),new VectorPoint(690,680)],18)] };
        var doc=new MapDocument(DocumentId.New(),"Interior",DateTimeOffset.UnixEpoch,new(768,768,"px","#FF89ACBD"),[
            new BaseColorLayer(LayerId.New(),"Море",true,false,1,"#FF89ACBD"),land,
            new ObjectLayer(LayerId.New(),"Вода",true,false,1,[]){Rivers=windingRiver?[river]:[]}]);
        SKBitmap Draw(int edge,int offset)
        {
            using var surface=SKSurface.Create(new SKImageInfo(edge,edge))!;
            surface.Canvas.Translate(-offset,-offset);
            SceneRenderer.Render(surface.Canvas,doc,new(){ViewScale=1,VisibleWorld=new(offset,offset,offset+edge,offset+edge)});
            var bitmap=new SKBitmap(edge,edge);surface.ReadPixels(bitmap.Info,bitmap.GetPixels(),bitmap.RowBytes,0,0);return bitmap;
        }
        using var full=Draw(768,0);using var local=Draw(256,256);
        for(var y=0;y<256;y+=4) for(var x=0;x<256;x+=4)
        {
            var a=full.GetPixel(x+256,y+256);var b=local.GetPixel(x,y);
            Assert.True(Math.Abs(a.Red-b.Red)<=2 && Math.Abs(a.Green-b.Green)<=2 && Math.Abs(a.Blue-b.Blue)<=2,
                $"Interior pixel ({x},{y}) changed: {a} / {b}");
        }
    }

    private static MapDocument Scene(LandEdgeStyle? style = null, bool river = true)
    {
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < 200; x++) coverage[y * TileMap.TileSize + x] = 255;
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage),
            style ?? new LandEdgeStyle { FillColor = "#FF7FA35A", FillTextureId = "regional-ochre", FillTextureOpacity = 0.7, FillTextureScale = 128 });
        var water = new River
        {
            Style = RiverStyle.Illustrated() with { EdgeRoughness = 0 },
            Channels = [RiverEditor.NewChannel([new VectorPoint(20, 128), new VectorPoint(246, 128)], 30)],
        };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = river ? [water] : [] };
        return new MapDocument(DocumentId.New(), "cache", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FF6E9FBF"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF6E9FBF"), land, objects]);
    }

    private static SKBitmap Render(MapDocument document, RenderImageCache? land, RenderImageCache? terrain, StrokePreview? preview = null)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            StyledLand = land, Terrain = terrain, Preview = preview, ViewScale = 1,
            VisibleWorld = new WorldBounds(0, 0, 256, 256),
        });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static int MaxDifference(SKBitmap a, SKBitmap b) => MaxDifference(a, b, out _);

    private static int MaxDifference(SKBitmap a, SKBitmap b, out string where)
    {
        var worst = 0; where = "";
        for (var y = 0; y < a.Height; y += 2)
            for (var x = 0; x < a.Width; x += 2)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                if (d > worst) { worst = d; where = $"({x},{y}) {p} vs {q}"; }
            }
        return worst;
    }

    [Fact]
    public void A_frame_drawn_from_the_kept_images_equals_the_frame_drawn_without_them()
    {
        var document = Scene();
        using var direct = Render(document, null, null);
        using var land = new RenderImageCache();
        using var terrain = new RenderImageCache();
        using var first = Render(document, land, terrain);
        using var second = Render(document, land, terrain);

        Assert.Equal(1, terrain.Misses); Assert.Equal(1, terrain.Hits);
        Assert.Equal(1, land.Misses);
        // Land, sea, material and the carved mouth are the same pixels. Only the blurred bank
        // passes of the water round differently on the half-float intermediate: a few levels,
        // within the bank band, never on the land or in open water.
        var difference = MaxDifference(direct, first, out var where);
        Assert.True(difference <= 8, $"cached frame differs by {difference} at {where}");
        for (var y = 0; y < 256; y += 2)
            for (var x = 0; x < 250; x += 2)
            {
                if (y is > 96 and < 160) continue; // the river runs at y = 128, half width 15, bank decor around it
                var p = direct.GetPixel(x, y); var q = first.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                Assert.True(d <= 2, $"land or sea pixel ({x},{y}) differs: {p} vs {q}");
            }
        Assert.Equal(0, MaxDifference(first, second));
        Assert.True(first.GetPixel(100, 40) != new SKColor(0x7F, 0xA3, 0x5A), "the land carries its material through the cache");
    }

    [Fact]
    public void Magnification_above_four_times_reuses_terrain_detail_but_export_bypasses_it()
    {
        var document = Scene(river: false);
        var view = new WorldBounds(176, 80, 208, 112);
        using var terrain = new RenderImageCache();
        void Draw(double scale, bool export = false)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)(32 * scale), (int)(32 * scale)))!;
            surface.Canvas.Scale((float)scale); surface.Canvas.Translate(-176, -80);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            { ViewScale = scale, VisibleWorld = view, Terrain = terrain, IsExport = export });
        }
        Draw(4);
        var misses = terrain.Misses; var hits = terrain.Hits;
        Draw(16);
        Assert.Equal(misses, terrain.Misses);
        Assert.Equal(hits + 1, terrain.Hits);
        Draw(16, export: true);
        Assert.Equal(misses, terrain.Misses);
        Assert.Equal(hits + 1, terrain.Hits);
    }

    [Fact]
    public async Task A_refined_zoom_keeps_the_complete_material_and_coast_of_a_direct_frame()
    {
        var document = Scene();
        using var terrain = new RenderImageCache();
        using var worker = new TerrainRefinement();
        using var initial = Render(document, null, terrain);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new WorldBounds(32, 32, 160, 160);
        SKBitmap Zoom(bool cached, RenderStats? stats = null)
        {
            using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
            surface.Canvas.Scale(2); surface.Canvas.Translate(-32, -32);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                ViewScale = 2, VisibleWorld = view, Stats = stats,
                Terrain = cached ? terrain : null, TerrainRefinement = cached ? worker : null,
                RequestRefinedFrame = () => ready.TrySetResult(),
            });
            using var image = surface.Snapshot();
            return SKBitmap.FromImage(image);
        }
        var stats = new RenderStats();
        using var preview = Zoom(true, stats);
        Assert.Equal(1, stats.TerrainRefinementsPending);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        SKBitmap RefineAll()
        {
            for (var frame = 0; frame < 128; frame++)
            {
                var bitmap = Zoom(true, stats);
                if (stats.TerrainRefinementsPending == 0) return bitmap;
                bitmap.Dispose();
            }
            throw new InvalidOperationException("Terrain refinement never completed.");
        }
        using var refined = RefineAll();
        Assert.Equal(0, stats.TerrainRefinementsPending);
        using var direct = Zoom(false);
        var difference = MaxDifference(direct, refined, out var where);
        Assert.True(difference <= 8, $"refined terrain differs by {difference} at {where}");
    }

    [Fact]
    public void Every_edit_of_the_land_or_its_water_is_a_miss_and_a_pan_never_enters_the_cache()
    {
        var document = Scene();
        using var land = new RenderImageCache();
        using var terrain = new RenderImageCache();
        using var _ = Render(document, land, terrain);

        var piece = document.Layers.OfType<LandLayer>().Single();
        using var restyled = Render(document.WithLayer(piece.Id, l => piece with { EdgeStyle = piece.EdgeStyle with { FillColor = "#FFB03030" } }), land, terrain);
        Assert.Equal(2, terrain.Misses); Assert.Equal(2, land.Misses);
        Assert.True(restyled.GetPixel(100, 40).Red > restyled.GetPixel(100, 40).Green, "a new colour is drawn, not the kept green");

        var painted = piece with { PaintTiles = TileMap.Empty(4).With(new TileKey(0, 0), new byte[TileMap.TileSize * TileMap.TileSize * 4]) };
        using var __ = Render(document.WithLayer(piece.Id, _ => painted), land, terrain);
        Assert.Equal(3, land.Misses);

        var objects = document.Layers.OfType<ObjectLayer>().Single();
        var moved = objects.Rivers[0] with { Channels = [RiverEditor.NewChannel([new VectorPoint(20, 160), new VectorPoint(246, 160)], 30)] };
        using var ___ = Render(document.WithLayer(objects.Id, _ => objects with { Rivers = [moved] }), land, terrain);
        Assert.Equal(4, terrain.Misses); Assert.Equal(4, land.Misses);

        var hitsBefore = terrain.Hits; var missesBefore = terrain.Misses;
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        { StyledLand = land, Terrain = terrain, FastTerrain = true, VisibleWorld = new WorldBounds(0, 0, 256, 256) });
        Assert.Equal(hitsBefore, terrain.Hits); Assert.Equal(missesBefore, terrain.Misses);
    }

    [Fact]
    public void The_land_shape_preview_carries_the_material_and_the_paint_preview_bypasses_the_cache()
    {
        var document = Scene(river: false);
        var piece = document.Layers.OfType<LandLayer>().Single();
        using var terrain = new RenderImageCache();
        using var land = new RenderImageCache();
        var stroke = new RasterStrokeSession(piece.GeometryTiles, new BrushSettings { Radius = 30, Softness = 0, Color = piece.EdgeStyle.FillColor });
        stroke.AddPoint(230, 200);
        var preview = new StrokePreview
        {
            LayerId = piece.Id, Coverage = stroke.Coverage, Color = piece.EdgeStyle.FillColor, Opacity = 1,
            PaintSettings = new BrushSettings { Radius = 30, Softness = 0, Color = piece.EdgeStyle.FillColor },
        };
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        { StyledLand = land, Terrain = terrain, Preview = preview, VisibleWorld = new WorldBounds(0, 0, 256, 256) });
        using var frame = new SKBitmap(info);
        surface.ReadPixels(info, frame.GetPixels(), info.RowBytes, 0, 0);
        var grown = frame.GetPixel(230, 200);
        Assert.NotEqual(new SKColor(0x6E, 0x9F, 0xBF), grown);
        Assert.NotEqual(new SKColor(0x7F, 0xA3, 0x5A), grown);
        Assert.Equal(0, terrain.Misses);

        var paint = new StrokePreview { LayerId = piece.Id, IsLandPaint = true, Coverage = stroke.Coverage, Color = "#FFFF0000", Opacity = 1 };
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        { StyledLand = land, Terrain = terrain, Preview = paint, VisibleWorld = new WorldBounds(0, 0, 256, 256) });
        // The live paint frame composes the land in full: neither cache is consulted for it.
        Assert.Equal(0, terrain.Misses); Assert.Equal(0, terrain.Hits);
        Assert.Equal(1, land.Misses); Assert.Equal(0, land.Hits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_and_cold_caches_never_hide_freeze_or_retain_a_shape_stroke(bool eraser)
    {
        var document = Scene(river: false);
        var piece = document.Layers.OfType<LandLayer>().Single();
        var x = eraser ? 100 : 230;
        var stroke = new RasterStrokeSession(piece.GeometryTiles, new BrushSettings { Radius = 15, Softness = 0 });
        stroke.AddPoint(x, 60);
        var preview = new StrokePreview
        {
            LayerId = piece.Id, Coverage = stroke.Coverage, Color = piece.EdgeStyle.FillColor,
            Opacity = 1, Eraser = eraser,
        };
        using var land = new RenderImageCache();
        using var warm = new RenderImageCache();
        using var cold = new RenderImageCache();
        using var idle = Render(document, land, warm);
        using var first = Render(document, land, warm, preview);
        using var firstCold = Render(document, land, cold, preview);
        Assert.NotEqual(idle.GetPixel(x, 60), first.GetPixel(x, 60));
        Assert.Equal(0, MaxDifference(first, firstCold));

        // The real gesture mutates its coverage dictionary between frames, without a document edit.
        stroke.AddPoint(x, 180);
        using var grown = Render(document, land, warm, preview);
        using var grownCold = Render(document, land, cold, preview);
        Assert.NotEqual(idle.GetPixel(x, 180), grown.GetPixel(x, 180));
        Assert.Equal(first.GetPixel(x, 60), grown.GetPixel(x, 60));
        Assert.Equal(0, MaxDifference(grown, grownCold));
        Assert.Equal(1, warm.Misses); Assert.Equal(0, warm.Hits);
        Assert.Equal(0, cold.Misses); Assert.Equal(0, cold.Hits);

        using var cancelled = Render(document, land, warm);
        using var cancelledCold = Render(document, land, cold);
        Assert.Equal(0, MaxDifference(idle, cancelled));
        Assert.Equal(0, MaxDifference(idle, cancelledCold));
    }

    [Theory]
    [InlineData(1.0, 1.0, false)]
    [InlineData(0.5, 1.0, false)]
    [InlineData(0.5, 0.6, false)]
    [InlineData(1.0, 1.0, true)]
    public void New_ground_uses_the_committed_material_formula_even_on_an_empty_layer(double opacity, double layerOpacity, bool empty)
    {
        var document = Scene(new LandEdgeStyle
        {
            FillColor = "#FF7FA35A", FillTextureId = "regional-ochre", FillTextureOpacity = 0.7,
            FillTextureScale = 128, OutlineWidth = 0, RippleCount = 0,
            OuterShadowOpacity = 0, InnerShadowOpacity = 0,
        }, river: false);
        var piece = document.Layers.OfType<LandLayer>().Single();
        piece = piece with { GeometryTiles = empty ? TileMap.Empty(1) : piece.GeometryTiles, Opacity = layerOpacity };
        document = document.WithLayer(piece.Id, _ => piece);
        var settings = new BrushSettings { Radius = 20, Softness = 0.5, Opacity = opacity, Color = "#FFFF0000", Saturation = -1 };
        var stroke = new RasterStrokeSession(piece.GeometryTiles, settings);
        stroke.AddPoint(230, 140);
        var preview = new StrokePreview
        {
            LayerId = piece.Id, Coverage = stroke.Coverage, Color = piece.EdgeStyle.FillColor,
            Opacity = opacity, PaintSettings = settings,
        };
        using var terrain = new RenderImageCache();
        using var land = new RenderImageCache();
        using var idle = Render(document, land, terrain);
        using var held = Render(document, land, terrain, preview);
        var (_, after) = stroke.CommitCoverage(0, subtract: false);
        var committed = document.WithLayer(piece.Id, _ => piece with { GeometryTiles = piece.GeometryTiles.WithMany(after) });
        using var released = Render(committed, land, terrain);
        using var direct = Render(committed, null, null);
        Assert.NotEqual(idle.GetPixel(230, 140), held.GetPixel(230, 140));
        var worst = 0; var where = ""; var directWorst = 0;
        for (var y = 120; y <= 160; y++)
            for (var x = 210; x <= 250; x++)
            {
                var p = held.GetPixel(x, y); var q = released.GetPixel(x, y);
                var difference = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                if (difference > worst) { worst = difference; where = $"({x},{y}): {p} vs {q}"; }
                var d = direct.GetPixel(x, y);
                directWorst = Math.Max(directWorst, Math.Max(Math.Abs(p.Red - d.Red), Math.Max(Math.Abs(p.Green - d.Green), Math.Abs(p.Blue - d.Blue))));
            }
        Assert.True(directWorst <= 2, $"held/direct committed material differs by {directWorst}");
        // The finished terrain uses F16 intermediates; a soft edge composed directly into the
        // 8-bit frame can round by three levels, while the material colour remains the same.
        Assert.True(worst <= 3, $"held/cached committed material differs by {worst} at {where}");
        Assert.Same(piece, document.FindLayer(piece.Id));
    }
}

public sealed class RenderImageCacheDiagnostics
{
    [Fact]
    public void Where_the_cached_frame_differs()
    {
        var lines = new List<string>();
        foreach (var (label, style) in new (string, RiverStyle)[]
        {
            ("full", RiverStyle.Illustrated() with { EdgeRoughness = 0 }),
            ("no-halo", RiverStyle.Illustrated() with { EdgeRoughness = 0, BankShadowOpacity = 0 }),
            ("no-relief", RiverStyle.Illustrated() with { EdgeRoughness = 0, BankReliefWidth = 0 }),
            ("no-shallow", RiverStyle.Illustrated() with { EdgeRoughness = 0, ShallowWidth = 0 }),
            ("no-highlight", RiverStyle.Illustrated() with { EdgeRoughness = 0, HighlightOpacity = 0 }),
            ("no-clip", RiverStyle.Illustrated() with { EdgeRoughness = 0, ClipToLand = false }),
            ("no-cut", RiverStyle.Illustrated() with { EdgeRoughness = 0, CutsLand = false }),
            ("plain", new RiverStyle { TextureId = null, ShallowWidth = 0, HighlightOpacity = 0, BankShadowOpacity = 0, BankReliefWidth = 0, ClipToLand = false, CutsLand = false }),
        })
        {
            var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
            for (var y = 0; y < TileMap.TileSize; y++) for (var x = 0; x < 200; x++) coverage[y * TileMap.TileSize + x] = 255;
            var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, TileMap.Empty(1).With(new TileKey(0, 0), coverage),
                new LandEdgeStyle { FillColor = "#FF7FA35A", FillTextureId = "regional-ochre", FillTextureOpacity = 0.7, FillTextureScale = 128 });
            var water = new River { Style = style, Channels = [RiverEditor.NewChannel([new VectorPoint(20, 128), new VectorPoint(246, 128)], 30)] };
            var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [water] };
            var document = new MapDocument(DocumentId.New(), "cache", DateTimeOffset.UnixEpoch, new CanvasDefinition(256, 256, "px", "#FF6E9FBF"),
                [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF6E9FBF"), land, objects]);
            SKBitmap Render(RenderImageCache? terrain)
            {
                var info = new SKImageInfo(256, 256);
                using var surface = SKSurface.Create(info)!;
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Terrain = terrain, ViewScale = 1, VisibleWorld = new WorldBounds(0, 0, 256, 256) });
                var bitmap = new SKBitmap(info); surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0); return bitmap;
            }
            using var direct = Render(null);
            using var terrain = new RenderImageCache();
            using var cached = Render(terrain);
            var worst = 0; var where = ""; var count = 0;
            for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
            {
                var p = direct.GetPixel(x, y); var q = cached.GetPixel(x, y);
                var d = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                if (d > 2) count++;
                if (d > worst) { worst = d; where = $"({x},{y}) {p} vs {q}"; }
            }
            lines.Add($"{label}: worst={worst} at {where}, pixels>2: {count}");
        }
        Assert.Fail(string.Join("\n", lines));
    }
}
