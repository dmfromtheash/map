using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Brush, Land and Mask reported as "not working". They demanded that the user had already made a
/// layer of exactly the right kind active, while every vector tool falls back to a usable layer on
/// its own - and a new map starts on its object layer, so all three refused every stroke.
/// </summary>
public sealed class RasterToolTargetTests
{
    // -------------------------------------------------------------- the reported state

    [Theory]
    [InlineData(EditorTool.Brush)]
    [InlineData(EditorTool.Land)]
    [InlineData(EditorTool.Mask)]
    public void A_new_map_says_what_is_missing_instead_of_swallowing_the_stroke(EditorTool tool)
    {
        // The wizard makes exactly a background and an object layer, so none of the three tools
        // has anywhere to paint. That is a legitimate state - it just has to be legible.
        var editor = EditorForNewMap();
        editor.ActiveTool = tool;

        Assert.False(editor.RasterToolReady);
        Assert.False(editor.BeginStroke(100, 100));
        Assert.NotEqual("", editor.StatusDetail);
        Assert.NotEqual("", editor.RasterToolHint);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Theory]
    [InlineData(EditorTool.Brush)]
    [InlineData(EditorTool.Land)]
    public void The_panel_can_create_the_missing_layer_and_carry_straight_on(EditorTool tool)
    {
        var editor = EditorForNewMap();
        editor.ActiveTool = tool;

        Assert.True(editor.CreateLayerForActiveTool());

        Assert.True(editor.RasterToolReady);
        Assert.True(editor.BeginStroke(100, 100));
        editor.ContinueStroke(160, 160);
        editor.EndStroke();
        Assert.Equal(2, editor.Session.UndoDepth); // the new layer, then the stroke
    }

    [Fact]
    public void The_panel_can_add_the_missing_mask_and_carry_straight_on()
    {
        var editor = EditorForNewMap();
        editor.ActiveTool = EditorTool.Mask;

        Assert.True(editor.CreateLayerForActiveTool());

        Assert.True(editor.RasterToolReady);
        Assert.True(editor.BeginStroke(100, 100));
        editor.ContinueStroke(160, 160);
        editor.EndStroke();
        Assert.Equal(2, editor.Session.UndoDepth);
    }

    // ------------------------------------------------------------ the everyday failure

    [Fact]
    public void The_brush_paints_the_brush_layer_even_while_the_object_layer_is_active()
    {
        // This is the case that made the tool look broken on a map that already had what it
        // needed: adding a brush layer and then touching anything else left the object layer
        // active, and every later stroke was refused.
        var editor = EditorForNewMap();
        editor.AddBrushLayer();
        var brush = editor.Session.Document.Layers.OfType<BrushLayer>().Single();
        var objects = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        editor.SelectLayer(objects.Id);

        editor.ActiveTool = EditorTool.Brush;
        Assert.True(editor.BeginStroke(120, 120));
        editor.ContinueStroke(180, 180);
        editor.EndStroke();

        // The stroke landed on the brush layer, and the panel now agrees with what was painted.
        Assert.Equal(brush.Id, editor.ActiveLayerId);
        Assert.True(editor.Session.Document.Layers.OfType<BrushLayer>().Single().Tiles.TileCount > 0);
    }

    [Fact]
    public void Land_shapes_reach_the_land_layer_from_any_active_layer()
    {
        var editor = EditorForNewMap();
        editor.AddLandLayer();
        var land = editor.Session.Document.Layers.OfType<LandLayer>().Single();
        editor.SelectLayer(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Id);
        editor.ActiveTool = EditorTool.Land;

        Assert.True(editor.ApplyLandShape(
            LandShapeKind.Rectangle, new WorldBounds(100, 100, 400, 400)));

        Assert.Equal(land.Id, editor.ActiveLayerId);
        Assert.True(
            editor.Session.Document.Layers.OfType<LandLayer>().Single().GeometryTiles.TileCount > 0);
    }

    [Fact]
    public void A_hidden_or_locked_target_is_reported_as_such_and_never_silently_replaced()
    {
        // Falling back must not smuggle the stroke onto a different layer when the user has
        // deliberately locked the one they are working on.
        var editor = EditorForNewMap();
        editor.AddBrushLayer();
        editor.ToggleActiveLayerLock();
        editor.ActiveTool = EditorTool.Brush;

        Assert.False(editor.RasterToolReady);
        Assert.False(editor.BeginStroke(120, 120));
        Assert.Equal(0, editor.Session.Document.Layers.OfType<BrushLayer>().Single().Tiles.TileCount);
    }

    [Fact]
    public void An_active_layer_that_already_fits_is_left_alone()
    {
        var editor = EditorForNewMap();
        editor.AddBrushLayer();
        editor.AddBrushLayer();
        var chosen = editor.Session.Document.Layers.OfType<BrushLayer>().First();
        editor.SelectLayer(chosen.Id);
        editor.ActiveTool = EditorTool.Brush;

        Assert.True(editor.BeginStroke(120, 120));
        editor.ContinueStroke(180, 180);
        editor.EndStroke();

        Assert.Equal(chosen.Id, editor.ActiveLayerId);
        Assert.True(editor.Session.Document.FindLayer(chosen.Id) is BrushLayer { Tiles.TileCount: > 0 });
    }

    // ------------------------------------------------------------------------ setup

    private static EditorViewModel EditorForNewMap()
    {
        // The document the wizard actually produces, not a fixture built for the test.
        var document = new NewMapWizardViewModel().Create(DateTimeOffset.UnixEpoch)!;
        return new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
    }
}
