using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Stage 20 roads on the real scene renderer: one settlement scene with an S-bend, an acute Y, a
/// T, a shared X, a narrow path into a wide road, a closed block loop and two looks at one node;
/// the terrain order table for land and water; determinism; export-relevant invariants.
/// </summary>
public sealed class Stage20RoadRenderingTests
{
    // ------------------------------------------------------------------ the scene

    internal sealed record Scene(MapDocument Document, LayerId LandId, LayerId ObjectsId, Road Main, Road Block, River River,
        Guid MainBranch, Guid ForkBranch, Guid PathBranch, Guid CrossBranch, Guid PainterlyBranch, Guid ForkNode, Guid PathNode);

    internal static Scene Settlement(RoadDrawing drawing = RoadDrawing.Country, bool withRiver = true, bool withHouses = true)
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 0; x < 4; x++) for (var y = 0; y < 3; y++) tiles = tiles.With(new TileKey(x, y), coverage);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles,
            new LandEdgeStyle { FillColor = "#FF9DB36F", OutlineWidth = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0, RippleCount = 0 });

        // The main road: an S-bend across the map, wide.
        var main = RoadEditor.NewRoad([new(60, 430), new(300, 380), new(520, 470), new(760, 380), new(980, 430)], 22,
            RoadStyle.Preset(drawing), "Тракт", seed: 20260908);
        var mainBranch = main.Branches[0].Id;
        var forkNode = main.Branches[0].Vertices[2].NodeId;
        // An acute Y from the third node, narrower.
        main = RoadEditor.AddBranch(main, forkNode, [new(640, 640), new(700, 740)], null, 14, out var forkBranch, "Развилка");
        // A narrow path into the middle of the first stretch: the node is inserted on the segment.
        var geometry = RoadGeometry.Evaluate(main).Branches[mainBranch];
        var at = RoadGeometry.PointAt(geometry, 0, 0.55);
        main = RoadEditor.InsertVertex(main, mainBranch, 0, 0.55, at, out var pathNode);
        main = RoadEditor.AddBranch(main, pathNode, [new(230, 300), new(210, 200)], null, 6, out var pathBranch, "Тропа");
        // An X: a branch that passes through the fourth node of the main road.
        var crossNode = main.Branches[0].Vertices[4].NodeId;
        main = RoadEditor.AddBranch(main, null, [new(700, 250)], crossNode, 12, out var crossBranch, "Пересечение");
        main = RoadEditor.AddBranch(main, crossNode, [new(830, 520)], null, 12, out var crossBranch2, "Пересечение · далее");
        _ = crossBranch2;
        // A branch of another look leaving the second node.
        var painterlyNode = main.Branches[0].Vertices[1].NodeId;
        main = RoadEditor.AddBranch(main, painterlyNode, [new(200, 520), new(120, 640)], null, 16, out var painterlyBranch, "Живописная",
            RoadStyle.Painterly());
        main.Validate();

        // A closed loop around a block, in the pen look.
        var block = RoadEditor.NewRoad([new(330, 90), new(470, 80), new(490, 220), new(340, 240)], 9, RoadStyle.PenRuts(), "Квартал", seed: 77);
        block = RoadEditor.SetClosed(block, block.Branches[0].Id, true);
        block.Validate();

        var river = new River
        {
            Name = "Река", Level = 0,
            Channels = [RiverEditor.NewChannel([new VectorPoint(880, 20), new VectorPoint(860, 300), new VectorPoint(900, 560), new VectorPoint(880, 760)], 24)],
            Style = RiverStyle.Illustrated() with { CutsLand = false, ClipToLand = false, TextureId = null },
        };
        river.Validate();

        var houses = new List<SceneObject>();
        if (withHouses)
        {
            var spots = new[] { (120, 340, 46, 34, 10.0), (360, 300, 52, 40, -8.0), (590, 380, 44, 44, 0.0), (610, 560, 40, 30, 20.0), (760, 300, 48, 36, -12.0), (300, 160, 56, 40, 4.0), (410, 150, 40, 40, -6.0) };
            var index = 0;
            foreach (var (x, y, w, h, angle) in spots)
                houses.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Дом " + ++index, x, y, w, h, angle,
                    "#FF8E6B4E", "#FF3A2A1B", 1.5, true, false));
        }
        var objects = new ObjectLayer(LayerId.New(), "Поселение", true, false, 1, houses)
        {
            Roads = [main, block],
            Rivers = withRiver ? [river] : [],
        };
        var document = new MapDocument(DocumentId.New(), "Дороги", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FF9DB36F"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DB36F"), land, objects]);
        return new Scene(document, land.Id, objects.Id, main, block, river, mainBranch, forkBranch, pathBranch, crossBranch, painterlyBranch, forkNode, pathNode);
    }

    internal static SKBitmap Render(MapDocument document, double scale = 1, WorldBounds? view = null, int width = 1024, int height = 768,
        bool export = false, IReadOnlyList<Road>? overrides = null, LayerId? previewLayer = null)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var visible = view ?? new WorldBounds(0, 0, width / scale, height / scale);
        canvas.Scale((float)scale);
        canvas.Translate((float)-visible.Left, (float)-visible.Top);
        SceneRenderer.Render(canvas, document, new RenderOptions
        {
            ViewScale = scale, VisibleWorld = visible, IsExport = export,
            RoadOverrides = overrides, RoadPreviewLayerId = previewLayer,
        });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    internal static void SaveForReview(SKBitmap bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MAP_STAGE20_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(directory, name)); data.SaveTo(file);
    }

    private static bool Near(SKColor a, SKColor b, int tolerance) =>
        Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance;

    // ------------------------------------------------------------------ drawing

    [Theory]
    [InlineData(RoadDrawing.Country)]
    [InlineData(RoadDrawing.PenRuts)]
    [InlineData(RoadDrawing.Painterly)]
    public void Settlement_scene_draws_every_fork_as_one_surface(RoadDrawing drawing)
    {
        var scene = Settlement(drawing);
        using var whole = Render(scene.Document);
        SaveForReview(whole, $"settlement-{drawing}.png");
        var set = RoadGeometry.Evaluate(scene.Main);
        var grass = whole.GetPixel(40, 40);

        // Walk the fork branch into the main road: every axis pixel is road, none is grass.
        var fork = set.Branches[scene.ForkBranch];
        for (var i = 0; i < Math.Min(30, fork.Samples.Length); i++)
        {
            var s = fork.Samples[i];
            Assert.False(Near(whole.GetPixel((int)s.X, (int)s.Y), grass, 6), $"grass at fork sample {i} ({s.X:0},{s.Y:0})");
        }
        // The same for the narrow path entering the wide road, and for the crossing.
        var path = set.Branches[scene.PathBranch];
        for (var i = 0; i < Math.Min(24, path.Samples.Length); i++)
        {
            var s = path.Samples[i];
            Assert.False(Near(whole.GetPixel((int)s.X, (int)s.Y), grass, 6), $"grass at path sample {i}");
        }
        var cross = set.Branches[scene.CrossBranch];
        // A free end may taper and dissolve (the painterly look does): judge the crossing away from its ends.
        foreach (var s in cross.Samples.Where(s => s.Distance > cross.StartTaper + 2 && cross.Length - s.Distance > cross.EndTaper + 2))
            Assert.False(Near(whole.GetPixel((int)s.X, (int)s.Y), grass, 6), $"grass on the crossing at {s.X:0},{s.Y:0}");

        // A close look at the Y fork at 2x, for the eye.
        var node = set.Nodes[scene.ForkNode];
        using var closeup = Render(scene.Document, 2.5, new WorldBounds(node.X - 200, node.Y - 150, node.X + 200, node.Y + 150), 1000, 750);
        SaveForReview(closeup, $"fork-closeup-{drawing}.png");
        using var pathCloseup = Render(scene.Document, 3, new WorldBounds(120, 220, 420, 420), 900, 600);
        SaveForReview(pathCloseup, $"path-closeup-{drawing}.png");
    }

    [Fact]
    public void A_translucent_road_has_no_darker_overlap_at_its_forks()
    {
        var scene = Settlement(RoadDrawing.Country, withRiver: false, withHouses: false);
        var translucent = scene.Main with { Style = scene.Main.Style with { Opacity = 0.5, ToneVariation = 0, ShoulderOpacity = 0, EdgeOpacity = 0, RutOpacity = 0, DetailOpacity = 0 },
            Branches = [.. scene.Main.Branches.Select(b => b with { Style = null })] };
        var document = scene.Document.WithLayer(scene.ObjectsId, layer => ((ObjectLayer)layer) with { Roads = [translucent] });
        using var bitmap = Render(document);
        SaveForReview(bitmap, "translucent.png");
        var set = RoadGeometry.Evaluate(translucent);
        var main = set.Branches[scene.MainBranch];
        var plain = bitmap.GetPixel((int)main.Samples[8].X, (int)main.Samples[8].Y);
        var node = set.Nodes[scene.ForkNode];
        var atFork = bitmap.GetPixel((int)node.X, (int)node.Y);
        var fork = set.Branches[scene.ForkBranch];
        var insideFork = bitmap.GetPixel((int)fork.Samples[4].X, (int)fork.Samples[4].Y);
        Assert.True(Near(plain, atFork, 4), $"fork {atFork} differs from plain road {plain}");
        Assert.True(Near(plain, insideFork, 4), $"buried branch end {insideFork} differs from plain road {plain}");
    }

    [Fact]
    public void Drawing_is_deterministic_and_does_not_depend_on_the_camera()
    {
        var scene = Settlement(RoadDrawing.PenRuts);
        using var first = Render(scene.Document, 1, new WorldBounds(200, 200, 712, 584), 512, 384);
        using var second = Render(scene.Document, 1, new WorldBounds(200, 200, 712, 584), 512, 384);
        Assert.Equal(first.Pixels, second.Pixels);
        // A different camera over the same world: the same details, shifted by a whole number of pixels.
        using var shifted = Render(scene.Document, 1, new WorldBounds(232, 216, 744, 600), 512, 384);
        var mismatches = 0;
        for (var y = 0; y < 300; y++)
            for (var x = 0; x < 400; x++)
                if (first.GetPixel(x + 32, y + 16) != shifted.GetPixel(x, y)) mismatches++;
        Assert.True(mismatches < 400 * 300 * 0.004, $"{mismatches} pixels changed with the camera");
    }

    [Fact]
    public void Two_looks_at_one_node_meet_without_a_plug_across_the_main_road()
    {
        var scene = Settlement(RoadDrawing.Country, withRiver: false, withHouses: false);
        using var bitmap = Render(scene.Document);
        SaveForReview(bitmap, "two-looks.png");
        var set = RoadGeometry.Evaluate(scene.Main);
        var main = set.Branches[scene.MainBranch];
        var node = scene.Main.FindNode(scene.Main.Branches[0].Vertices[1].NodeId)!;
        // On the main road's axis, just past the node in both directions: the main road's tone,
        // not the painterly branch's lighter tone.
        var index = main.NodeSampleIndex[1];
        var before = main.Samples[index - 6]; var after = main.Samples[index + 6];
        var painterly = SKColor.Parse(RoadStyle.Painterly().SurfaceColor);
        var country = SKColor.Parse(scene.Main.Style.SurfaceColor);
        foreach (var s in new[] { before, after })
        {
            var pixel = bitmap.GetPixel((int)s.X, (int)s.Y);
            Assert.True(Math.Abs(pixel.Red - country.Red) + Math.Abs(pixel.Green - country.Green) < Math.Abs(pixel.Red - painterly.Red) + Math.Abs(pixel.Green - painterly.Green) + 40,
                $"the main road at ({s.X:0},{s.Y:0}) reads {pixel}, closer to the painterly branch than to itself");
        }
        _ = node;
    }

    // ------------------------------------------------------------------ the terrain order table

    private static (MapDocument Document, LayerId LandId, LayerId ObjectsId, Road Road, River River) Terrace()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Плато", true, false, 1, TileMap.Empty(1).With(new TileKey(0, 0), coverage),
            new LandEdgeStyle { FillColor = "#FF3C8C4C", OutlineWidth = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0, RippleCount = 0 }) { Level = 1 };
        var road = RoadEditor.NewRoad([new(20, 128), new(200, 128), new(500, 128)], 30, RoadStyle.Country() with
        { ToneVariation = 0, ShoulderOpacity = 0, EdgeOpacity = 0, RutOpacity = 0, DetailOpacity = 0, EdgeRoughness = 0, SurfaceColor = "#FFE0C080" }, "Дорога");
        var river = new River
        {
            Level = 0, Channels = [RiverEditor.NewChannel([new VectorPoint(380, 10), new VectorPoint(380, 250)], 40)],
            Style = new RiverStyle { CutsLand = false, ClipToLand = false, BankWidth = 0, BankShadowOpacity = 0, ShallowWidth = 0, HighlightOpacity = 0, TextureId = null, WaterColor = "#FF2040C0" },
        };
        var objects = new ObjectLayer(LayerId.New(), "Поверхности", true, false, 1, []) { Roads = [road], Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "Terrace", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land, objects]);
        return (document, land.Id, objects.Id, road, river);
    }

    [Fact]
    public void Road_below_land_is_covered_only_where_the_higher_land_is_and_stays_visible_outside()
    {
        var (document, _, objectsId, road, _) = Terrace();
        var plateau = SKColor.Parse("#FF3C8C4C"); var surface = SKColor.Parse("#FFE0C080");
        // Road at level 0 under the plateau of level 1: covered on the tile, visible beyond it.
        using var below = Render(document.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 0 }], Rivers = [] }), 1, null, 512, 256);
        Assert.True(Near(below.GetPixel(120, 128), plateau, 4), $"under the plateau: {below.GetPixel(120, 128)}");
        Assert.True(Near(below.GetPixel(400, 128), surface, 6), $"outside the plateau the road remains: {below.GetPixel(400, 128)}");
        // Same level: the road lies on the plateau.
        using var same = Render(document.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 1 }], Rivers = [] }), 1, null, 512, 256);
        Assert.True(Near(same.GetPixel(120, 128), surface, 6), $"on the plateau: {same.GetPixel(120, 128)}");
        // Above: the road covers the lower land.
        using var above = Render(document.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 3 }], Rivers = [] }), 1, null, 512, 256);
        Assert.True(Near(above.GetPixel(120, 128), surface, 6), $"above the plateau: {above.GetPixel(120, 128)}");
        // A road never carves the land: the plateau beside it is untouched at every level.
        Assert.True(Near(same.GetPixel(120, 40), plateau, 2));
    }

    [Fact]
    public void Road_and_water_order_follows_levels_layers_and_the_explicit_surface_order()
    {
        var (document, _, objectsId, road, river) = Terrace();
        // Since R06 a road over a river carries a bridge by default; this test is about the order
        // of the surfaces themselves, so the crossing is set to «нет» (RoadCrossingRenderingTests covers the bridge).
        road = RoadEditor.SetCrossingKind(road, road.Branches[0].Id, river.Channels[0].Id, 0, RoadCrossingKind.None);
        var water = SKColor.Parse("#FF2040C0"); var surface = SKColor.Parse("#FFE0C080");
        var crossing = (380, 128);
        // Same layer, same level, default order: water lies over the road.
        using var plain = Render(document.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 1 }], Rivers = [river with { Level = 1 }] }), 1, null, 512, 256);
        Assert.True(Near(plain.GetPixel(crossing.Item1, crossing.Item2), water, 4), $"default: {plain.GetPixel(380, 128)}");
        // Explicit order: the road on top.
        var ordered = document.WithLayer(objectsId, l => LayerSurfaceOrder.Ordered(((ObjectLayer)l) with { Roads = [road with { Level = 1 }], Rivers = [river with { Level = 1 }] }, [river.Id, road.Id]));
        Assert.False(((ObjectLayer)ordered.FindLayer(objectsId)!).SurfaceOrder.IsDefaultOrEmpty);
        using var reordered = Render(ordered, 1, null, 512, 256);
        Assert.True(Near(reordered.GetPixel(crossing.Item1, crossing.Item2), surface, 6), $"explicit: {reordered.GetPixel(380, 128)}");
        // Different levels: the higher one wins regardless of the order.
        using var roadHigher = Render(ordered.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 2 }], Rivers = [river with { Level = 1 }], SurfaceOrder = [road.Id, river.Id] }), 1, null, 512, 256);
        Assert.True(Near(roadHigher.GetPixel(crossing.Item1, crossing.Item2), surface, 6));
        using var waterHigher = Render(ordered.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 1 }], Rivers = [river with { Level = 2 }] }), 1, null, 512, 256);
        Assert.True(Near(waterHigher.GetPixel(crossing.Item1, crossing.Item2), water, 4));
        // Same level, different layers: the layer stack decides.
        var lower = new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Roads = [road with { Level = 1 }] };
        var upper = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river with { Level = 1 }] };
        var stacked = document.WithLayers([document.Layers[0], document.Layers[1], lower, upper]);
        using var byStack = Render(stacked, 1, null, 512, 256);
        Assert.True(Near(byStack.GetPixel(crossing.Item1, crossing.Item2), water, 4));
        var swapped = document.WithLayers([document.Layers[0], document.Layers[1], upper, lower]);
        using var bySwappedStack = Render(swapped, 1, null, 512, 256);
        Assert.True(Near(bySwappedStack.GetPixel(crossing.Item1, crossing.Item2), surface, 6));
    }

    [Fact]
    public void Owner_opacity_mask_and_visibility_hide_the_whole_road_including_its_shoulder()
    {
        var (document, _, objectsId, road, _) = Terrace();
        var styled = road with { Level = 1, Style = road.Style with { ShoulderOpacity = 0.6, ShoulderWidth = 8, EdgeOpacity = 0.7 } };
        var withRoad = document.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [styled], Rivers = [] });
        using var opaque = Render(withRoad, 1, null, 512, 256);
        using var half = Render(withRoad.WithLayer(objectsId, l => l with { Opacity = 0.5 }), 1, null, 512, 256);
        using var none = Render(withRoad.WithLayer(objectsId, l => l with { Opacity = 0 }), 1, null, 512, 256);
        using var hidden = Render(withRoad.WithLayer(objectsId, l => l with { Visible = false }), 1, null, 512, 256);
        var hiddenTile = new byte[TileMap.TileSize * TileMap.TileSize];
        using var masked = Render(withRoad.WithLayer(objectsId, l => l.WithMask(new LayerMask(
            TileMap.Empty(1).With(new TileKey(0, 0), hiddenTile).With(new TileKey(1, 0), hiddenTile), true))), 1, null, 512, 256);
        using var absent = Render(withRoad.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [] }), 1, null, 512, 256);
        // Body and shoulder both react to the owner's opacity, and vanish with it.
        Assert.NotEqual(opaque.GetPixel(120, 128), half.GetPixel(120, 128));
        Assert.NotEqual(opaque.GetPixel(120, 128 + 18), half.GetPixel(120, 128 + 18));
        Assert.Equal(absent.Pixels, none.Pixels);
        Assert.Equal(absent.Pixels, hidden.Pixels);
        Assert.Equal(absent.Pixels, masked.Pixels);
        Assert.NotEqual(absent.GetPixel(120, 128 + 18), opaque.GetPixel(120, 128 + 18));
    }

    [Fact]
    public void A_road_draft_and_a_substitution_draw_without_touching_the_document()
    {
        var (document, _, objectsId, road, _) = Terrace();
        var withRoad = document.WithLayer(objectsId, l => ((ObjectLayer)l) with { Roads = [road with { Level = 1 }], Rivers = [] });
        using var plain = Render(withRoad, 1, null, 512, 256);
        var wider = ((ObjectLayer)withRoad.FindLayer(objectsId)!).Roads[0] with { WidthScale = 2 };
        using var overridden = Render(withRoad, 1, null, 512, 256, overrides: [wider]);
        Assert.NotEqual(plain.Pixels, overridden.Pixels);
        // The draft lies at level 0 under the plateau of level 1 on the left tile, so it is
        // judged where it leaves the plateau: the preview obeys the terrain order like the result.
        var draft = RoadEditor.NewRoad([new(20, 230), new(400, 235)], 10);
        using var withDraft = Render(withRoad, 1, null, 512, 256, overrides: [draft], previewLayer: objectsId);
        Assert.True(withDraft.GetPixel(330, 233) != plain.GetPixel(330, 233), "the draft is drawn beyond the plateau");
        Assert.Equal(plain.GetPixel(150, 232), withDraft.GetPixel(150, 232));
        Assert.Single(((ObjectLayer)withRoad.FindLayer(objectsId)!).Roads);
    }
}
