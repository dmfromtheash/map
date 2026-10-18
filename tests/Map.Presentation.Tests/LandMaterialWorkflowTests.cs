using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class LandMaterialWorkflowTests
{
    [Fact]
    public void Material_brush_uses_strokes_in_region_mode_and_undo_eraser_and_cancel_preserve_land()
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля") with
        { GeometryTiles = TileMap.Empty(1).With(new(0, 0), Enumerable.Repeat((byte)255, 65536).ToArray()) };
        var doc = new MapDocument(DocumentId.New(), "Paint", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land]);
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        var router = new CanvasInputRouter(editor);
        Assert.True(editor.LandPaintWithColor);
        Assert.Empty(editor.RecentLandMaterials);
        Assert.False(editor.ChooseLandMaterial("meadow"));
        editor.SetLandPaintAction("Texture");
        Assert.False(editor.LandPaintWithColor);
        Assert.Equal(MapFillTextures.OwnerSnow, editor.LandMaterialId);
        editor.SelectLayer(land.Id); editor.ActiveTool = EditorTool.Land;
        editor.ActiveLandShape = LandShapeKind.Rectangle;
        editor.SetLandPaintMode(true); editor.SetLandPaintAction("Color");
        editor.BrushSettings = editor.BrushSettings with { Radius = 16, Softness = 0, Color = "#FFB08040" };
        Assert.False(editor.LandDrawsRegion);
        editor.Session.Viewport.Set(0, 0, 1);
        router.Press(CanvasPointerButton.Left, 40, 40); router.Move(80, 40); router.Release(80, 40);
        var painted = (LandLayer)editor.Session.Document.FindLayer(land.Id)!;
        Assert.Same(land.GeometryTiles, painted.GeometryTiles); Assert.True(painted.PaintTiles.TileCount > 0);
        var bytes = painted.PaintTiles.TryGet(new(0, 0))!;
        editor.Undo(); Assert.Equal(0, ((LandLayer)editor.Session.Document.FindLayer(land.Id)!).PaintTiles.TileCount);
        editor.Redo(); Assert.Equal(bytes, ((LandLayer)editor.Session.Document.FindLayer(land.Id)!).PaintTiles.TryGet(new(0, 0)));
        editor.SetLandPaintAction("Erase");
        router.Press(CanvasPointerButton.Left, 40, 40); router.Release(40, 40);
        var erased = (LandLayer)editor.Session.Document.FindLayer(land.Id)!;
        Assert.Equal(0, erased.PaintTiles.TryGet(new(0, 0))![(40 * 256 + 40) * 4 + 3]);
        Assert.Same(land.GeometryTiles, erased.GeometryTiles);
        router.Press(CanvasPointerButton.Left, 120, 40);
        var beforeModeChange = editor.Session.Document;
        editor.SetLandPaintMode(false); router.Release(120, 40);
        Assert.Same(beforeModeChange, editor.Session.Document); Assert.True(editor.LandDrawsRegion);
        editor.Session.Execute(UpdateLayerCommand.SetLocked((LandLayer)editor.Session.Document.FindLayer(land.Id)!, true));
        editor.SetLandPaintMode(true);
        Assert.False(editor.BeginStroke(100, 100));
    }

    [Fact]
    public void New_land_gets_watercolor_and_first_paint_mode_opens_with_a_usable_material()
    {
        var oldLand = new LandLayer(LayerId.New(), "Старая земля", true, false, 1, TileMap.Empty(1), new LandEdgeStyle());
        var doc = new MapDocument(DocumentId.New(), "Defaults", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), oldLand]);
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.AddLandLayer();
        var newLand = Assert.IsType<LandLayer>(editor.ActiveLayer);
        Assert.Equal(MapFillTextures.OwnerSnow, newLand.EdgeStyle.FillTextureId);
        Assert.Same(oldLand.EdgeStyle, ((LandLayer)editor.Session.Document.FindLayer(oldLand.Id)!).EdgeStyle);
        Assert.Null(oldLand.EdgeStyle.FillTextureId);
        editor.SetLandPaintMode(true);
        Assert.False(editor.LandPaintWithColor);
        Assert.Equal(MapFillTextures.OwnerSnow, editor.LandMaterialId);
        Assert.True(editor.ChooseLandMaterial(MapFillTextures.OwnerSnow));
        Assert.False(editor.ChooseLandMaterial(MapFillTextures.SoftMeadow));
        Assert.False(editor.ChooseLandMaterial(MapFillTextures.SoftSand));
        Assert.False(editor.ChooseLandMaterial(MapFillTextures.SoftMoss));
        Assert.False(editor.ChooseLandMaterial("watercolour-water"));
        var router = new CanvasInputRouter(editor);
        editor.Session.Viewport.Set(0, 0, 1);
        router.Press(CanvasPointerButton.Left, 40, 40); router.Move(80, 40); router.Release(80, 40);
        var painted = (LandLayer)editor.Session.Document.FindLayer(newLand.Id)!;
        Assert.True(painted.PaintTiles.TileCount > 0);
        Assert.Same(newLand.GeometryTiles, painted.GeometryTiles);
        editor.SetLandPaintAction("Color");
        editor.SetLandPaintMode(false); editor.SetLandPaintMode(true);
        Assert.True(editor.LandPaintWithColor);
    }
}
