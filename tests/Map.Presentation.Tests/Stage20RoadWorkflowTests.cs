using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// The road tool from the router down: create, branch off the middle of a stretch through a
/// shared node, move that node, change widths, undo/redo exactly, pick roads and water by the
/// terrain order, and export through tiles.
/// </summary>
public sealed class Stage20RoadWorkflowTests
{
    private static (EditorViewModel Editor, CanvasInputRouter Router, LayerId Objects) NewEditor(LandLayer? land = null)
    {
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var layers = new List<MapLayer> { new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2") };
        if (land is not null) layers.Add(land);
        layers.Add(objects);
        var document = new MapDocument(DocumentId.New(), "Roads", DateTimeOffset.UnixEpoch, new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"), layers);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, new CanvasInputRouter(editor), objects.Id);
    }

    private static void Click(CanvasInputRouter router, double x, double y, bool additive = false)
    { router.Press(CanvasPointerButton.Left, x, y, additive); router.Release(x, y); }

    private static void Drag(CanvasInputRouter router, double x, double y, double toX, double toY)
    { router.Press(CanvasPointerButton.Left, x, y); router.Move(toX, toY); router.Release(toX, toY); }

    [Fact]
    public void Create_branch_off_the_middle_move_the_shared_node_widen_and_undo_redo_exactly()
    {
        var (editor, router, _) = NewEditor();
        editor.NewRoadWidth = 18;
        editor.StartRoad();
        Assert.Equal(EditorTool.Road, editor.ActiveTool);
        var before = editor.Session.Document;
        Click(router, 100, 400); Click(router, 400, 380); Click(router, 700, 440); Click(router, 1000, 400);
        Assert.Same(before, editor.Session.Document);
        Assert.True(editor.IsCreatingRoad);
        Assert.True(editor.FinishRoad(), editor.StatusDetail);
        var road = Assert.Single(editor.Roads);
        var main = Assert.Single(road.Branches);
        Assert.Equal(4, main.Vertices.Length);
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Equal(RoadSelectionLevel.Node, editor.RoadLevel);

        // A path: the first click lands on the middle of the first stretch, so a real node is
        // inserted there and shared; two more clicks and Enter finish it.
        editor.NewRoadWidth = 6;
        editor.StartRoad();
        var geometry = RoadGeometry.Evaluate(editor.Roads[0]).Branches[main.Id];
        var middle = RoadGeometry.PointAt(geometry, 0, 0.5);
        Click(router, middle.X, middle.Y);
        Assert.True(editor.HasRoadGesture);
        Click(router, 260, 250); Click(router, 240, 120);
        Assert.True(editor.FinishRoad(), editor.StatusDetail);
        road = Assert.Single(editor.Roads);
        Assert.Equal(2, road.Branches.Length);
        Assert.Equal(5, road.FindBranch(main.Id)!.Vertices.Length);
        var path = road.Branches[1];
        var shared = path.Vertices[0].NodeId;
        Assert.True(road.IsShared(shared));
        Assert.Equal(shared, road.FindBranch(main.Id)!.Vertices[1].NodeId);
        Assert.Equal(6, path.Vertices[0].Width);
        Assert.Equal(2, editor.Session.UndoDepth);
        var attached = editor.Session.Document;

        // Drag the shared node: both branches follow, in one history entry.
        editor.SelectRoad(road.Id, main.Id); editor.SelectRoadVertex(1);
        var node = road.FindNode(shared)!;
        Drag(router, node.X, node.Y, node.X + 40, node.Y - 60);
        road = editor.Roads[0];
        var movedNode = road.FindNode(shared)!;
        Assert.InRange(Math.Abs(movedNode.X - node.X - 40) + Math.Abs(movedNode.Y - node.Y + 60), 0, 1.5);
        Assert.Equal(3, editor.Session.UndoDepth);
        var set = RoadGeometry.Evaluate(road);
        Assert.Equal((movedNode.X, movedNode.Y), (set.Branches[path.Id].Start.X, set.Branches[path.Id].Start.Y));
        Assert.Equal(road.FindBranch(path.Id)!.Vertices, path.Vertices); // the path's vertices themselves are untouched
        var moved = editor.Session.Document;

        // Width of the whole path, then of one vertex of the main road.
        editor.SelectRoad(road.Id, path.Id); editor.RoadLevel = RoadSelectionLevel.Branch;
        editor.SetSelectedBranchWidth(9);
        Assert.All(editor.Roads[0].FindBranch(path.Id)!.Vertices, v => Assert.Equal(9, v.Width));
        editor.SelectRoad(road.Id, main.Id); editor.RoadLevel = RoadSelectionLevel.Node; editor.SelectRoadVertex(4);
        editor.SetSelectedRoadVertexWidth(30);
        Assert.Equal(30, editor.Roads[0].FindBranch(main.Id)!.Vertices[4].Width);
        Assert.Equal(5, editor.Session.UndoDepth);
        var widened = editor.Session.Document;

        editor.Undo(); editor.Undo(); Assert.Same(moved, editor.Session.Document);
        editor.Undo(); Assert.Same(attached, editor.Session.Document);
        editor.Redo(); Assert.Same(moved, editor.Session.Document);
        editor.Redo(); editor.Redo(); Assert.Same(widened, editor.Session.Document);

        // Detach the path from the shared node: it gets its own node; the main road keeps the shared one.
        editor.SelectRoad(road.Id, path.Id); editor.RoadLevel = RoadSelectionLevel.Node; editor.SelectRoadVertex(0);
        editor.DetachSelectedRoadVertex();
        road = editor.Roads[0];
        Assert.False(road.IsShared(shared));
        Assert.NotEqual(shared, road.FindBranch(path.Id)!.Vertices[0].NodeId);
        Assert.Equal(shared, road.FindBranch(main.Id)!.Vertices[1].NodeId);
        editor.Undo();
        Assert.True(editor.Roads[0].IsShared(shared));

        // Escape cancels an unfinished draft without touching the document or the history.
        var depth = editor.Session.UndoDepth; var current = editor.Session.Document;
        editor.StartRoad(); Click(router, 50, 700); Click(router, 200, 720);
        Assert.True(editor.HasRoadGesture);
        Assert.True(editor.HandleEscape());
        Assert.False(editor.HasRoadGesture); Assert.False(editor.IsCreatingRoad);
        Assert.Same(current, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
    }

    [Fact]
    public void A_click_on_the_first_node_closes_the_route_and_joining_another_road_merges_them_with_their_looks()
    {
        var (editor, router, _) = NewEditor();
        editor.NewRoadWidth = 10;
        editor.StartRoad();
        Click(router, 300, 100); Click(router, 500, 90); Click(router, 520, 260); Click(router, 320, 280);
        Click(router, 300, 100);
        var loop = Assert.Single(editor.Roads);
        Assert.True(loop.Branches[0].Closed);
        Assert.Equal(4, loop.Branches[0].Vertices.Length);
        Assert.False(editor.IsCreatingRoad);

        // A second road of another look that ends on the loop: one road, the branch keeps its look.
        editor.StartRoad();
        Click(router, 700, 500); Click(router, 600, 400);
        editor.ApplyRoadPreset(RoadDrawing.PenRuts); // no road selected during a draft: the preset applies to the draft's target later
        var loopGeometry = RoadGeometry.Evaluate(editor.Roads[0]).Branches[loop.Branches[0].Id];
        var onLoop = RoadGeometry.PointAt(loopGeometry, 1, 0.5);
        Click(router, onLoop.X, onLoop.Y);
        var merged = Assert.Single(editor.Roads);
        Assert.Equal(2, merged.Branches.Length);
        var joined = merged.Branches[1];
        Assert.Equal(3, joined.Vertices.Length);
        Assert.True(merged.IsShared(joined.Vertices[^1].NodeId));
        Assert.Equal(5, merged.Branches[0].Vertices.Length); // the loop gained the inserted node
        Assert.True(merged.Branches[0].Closed);
    }

    [Fact]
    public void Picking_follows_the_terrain_order_between_roads_water_and_land()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Плато", true, false, 1, TileMap.Empty(1).With(new TileKey(0, 0), coverage), new LandEdgeStyle()) { Level = 1 };
        var (editor, router, objects) = NewEditor(land);
        var low = RoadEditor.NewRoad([new(20, 100), new(230, 100)], 30, name: "low") with { Level = 0 };
        var high = RoadEditor.NewRoad([new(20, 100), new(230, 100)], 30, name: "high") with { Level = 3 };
        editor.Session.Execute(new RoadCommand(objects, high.Id, high, "high"));
        editor.Session.Execute(new RoadCommand(objects, low.Id, low, "low"));
        editor.ActiveTool = EditorTool.Select;
        Click(router, 120, 100);
        Assert.Equal(high.Id, editor.SelectedRoad?.Id);
        Assert.Equal(1, editor.RoadSelectionCount);
        // Hide the high road: the low one lies under the plateau and cannot be picked; the land is.
        editor.Session.Execute(new RoadCommand(objects, high.Id, high with { Visible = false }, "hide"));
        editor.ClearRoadSelection();
        Click(router, 120, 100);
        Assert.Equal(0, editor.RoadSelectionCount);
        Assert.Equal(land.Id, editor.SelectedLand?.Id);
        // Water over a road at one level in one layer: the water is on top by default; the explicit order flips it.
        var river = new River { Name = "Ручей", Level = 3, Channels = [RiverEditor.NewChannel([new(120, 20), new(120, 230)], 30)],
            Style = new RiverStyle { CutsLand = false, ClipToLand = false } };
        editor.Session.Execute(new RiverCommand(objects, river.Id, river, "river"));
        editor.Session.Execute(new RoadCommand(objects, high.Id, high, "show"));
        editor.ClearRoadSelection(); editor.ClearWaterSelection(); editor.ClearLandSelection();
        Click(router, 120, 100);
        Assert.Equal(1, editor.WaterSelectionCount); Assert.Equal(0, editor.RoadSelectionCount);
        editor.SelectRoadOnly(high.Id);
        Assert.True(editor.ReorderSelectedSurfaces(1, toExtreme: true));
        editor.ActiveTool = EditorTool.Select;
        editor.ClearRoadSelection(); editor.ClearWaterSelection();
        Click(router, 120, 100);
        Assert.Equal(high.Id, editor.SelectedRoad?.Id); Assert.Equal(0, editor.WaterSelectionCount);
        // A locked road refuses a reorder and a move, without touching the document.
        editor.Session.Execute(new RoadCommand(objects, high.Id, high with { Locked = true }, "lock"));
        editor.SelectRoadOnly(high.Id);
        var revision = editor.Session.Revision;
        Assert.False(editor.ReorderSelectedSurfaces(-1, true));
        Assert.False(editor.BeginRoadDrag(120, 100));
        Assert.Equal(revision, editor.Session.Revision);
        // The road appears in the object list and can be deleted from there through the ordinary Delete.
        Assert.Contains(editor.ObjectTree, row => row.Kind == ObjectTreeRowKind.Road && row.RoadId == high.Id);
        editor.Session.Execute(new RoadCommand(objects, high.Id, high with { Locked = false }, "unlock"));
        editor.SelectRoadOnly(high.Id);
        Assert.True(editor.DeleteSelection());
        Assert.DoesNotContain(editor.Roads, r => r.Id == high.Id);
        editor.Undo();
        Assert.Contains(editor.Roads, r => r.Id == high.Id);
    }

    [Fact]
    public void A_road_travels_between_layers_whole_and_is_duplicated_with_fresh_ids()
    {
        var (editor, router, objects) = NewEditor();
        var second = new ObjectLayer(LayerId.New(), "Второй", true, false, 1, []);
        editor.Session.Execute(new AddLayerCommand(second, objects));
        var road = RoadEditor.NewRoad([new(100, 100), new(400, 120), new(700, 100)], 14);
        road = RoadEditor.AddBranch(road, road.Branches[0].Vertices[1].NodeId, [new(420, 300)], null, 8, out _);
        editor.Session.Execute(new RoadCommand(objects, road.Id, road, "road"));
        editor.SelectRoadOnly(road.Id);
        Assert.True(editor.MoveSelectionToLayer(second.Id));
        Assert.Empty(((ObjectLayer)editor.Session.Document.FindLayer(objects)!).Roads);
        var travelled = ((ObjectLayer)editor.Session.Document.FindLayer(second.Id)!).Roads.Single();
        Assert.Equal(2, travelled.Branches.Length);
        Assert.True(travelled.IsShared(road.Branches[1].Vertices[0].NodeId));
        editor.Undo();
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(objects)!).Roads);

        editor.SelectRoadOnly(road.Id);
        Assert.True(editor.DuplicateSelectedRoads());
        var roads = ((ObjectLayer)editor.Session.Document.FindLayer(objects)!).Roads;
        Assert.Equal(2, roads.Length);
        var copy = roads.Single(r => r.Id != road.Id);
        Assert.Empty(copy.Nodes.Select(n => n.Id).Intersect(road.Nodes.Select(n => n.Id)));
        Assert.Equal(copy.Id, editor.SelectedRoad?.Id);
        _ = router;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-road-export-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Theory]
    [InlineData(RoadDrawing.Country)]
    [InlineData(RoadDrawing.PenRuts)]
    public async Task Export_tiles_match_the_single_image_across_a_fork_on_the_tile_boundary(RoadDrawing drawing)
    {
        using var temp = new TempDirectory();
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage).With(new TileKey(1, 0), coverage),
            new LandEdgeStyle { FillColor = "#FF8CA36B", OutlineWidth = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0, RippleCount = 0 });
        // The fork node sits on the 256-px tile boundary of the export below.
        var road = RoadEditor.NewRoad([new(30, 128), new(256, 120), new(480, 140)], 20, RoadStyle.Preset(drawing), seed: 11);
        road = RoadEditor.AddBranch(road, road.Branches[0].Vertices[1].NodeId, [new(300, 230)], null, 9, out _);
        var layer = new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "Road export", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land, layer]);
        using var stamps = new ExportStampImageSource(document, null, null);
        var engine = new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 });
        ExportRequest Request(ExportOutputMode mode) => new(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 512, 256, mode, true, false);
        var png = Path.Combine(temp.Path, "road.png"); var tiles = Path.Combine(temp.Path, "tiles");
        await engine.ExportAsync(document, engine.Plan(document, Request(ExportOutputMode.SingleImage)), png, stamps);
        await engine.ExportAsync(document, engine.Plan(document, Request(ExportOutputMode.TilePackage)), tiles, stamps);
        using var single = SKBitmap.Decode(png);
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(File.ReadAllText(Path.Combine(tiles, "manifest.json")))!;
        var hard = new List<string>(); var soft = 0; var total = 0;
        foreach (var tile in manifest.Tiles)
        {
            using var tileBitmap = SKBitmap.Decode(Path.Combine(tiles, tile.File));
            for (var y = 0; y < tile.Height; y++)
            for (var x = 0; x < tile.Width; x++)
            {
                total++;
                var expected = single.GetPixel(tile.X + x, tile.Y + y); var actual = tileBitmap.GetPixel(x, y);
                if (expected == actual) continue;
                var delta = Math.Max(Math.Max(Math.Abs(expected.Red - actual.Red), Math.Abs(expected.Green - actual.Green)), Math.Abs(expected.Blue - actual.Blue));
                if (delta <= 64) soft++; else hard.Add($"({tile.X + x},{tile.Y + y}) {expected}/{actual}");
            }
        }
        Assert.True(hard.Count == 0, "hard mismatches: " + string.Join(", ", hard.Take(10)));
        Assert.True(soft <= total * 0.005, $"{soft} antialiased pixels differ out of {total}");
        // The fork is really on the boundary and drawn on both sides of it.
        Assert.Contains(Enumerable.Range(105, 40), y => single.GetPixel(255, y).Red > 165 && single.GetPixel(256, y).Red > 165);
    }
}
