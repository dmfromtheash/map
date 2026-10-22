using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// R10 «Красота» in the editor: an older area starts with every switch off, each switch and slider
/// is one Undo step, all off stores nothing; a density brush stroke (press, drag, release) is one
/// Undo step that thins the place it went over; the brush needs the natural layout.
/// </summary>
public sealed class PlacementAreaBeautyEditorTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Beauty_switches_are_undoable_steps_and_all_off_stores_nothing()
    {
        var editor = NewEditor();
        Assert.False(editor.EditableAreaBeauty.Any);
        var plain = editor.SelectedEditableArea!.MemberCount;

        var depth = editor.Session.UndoDepth;
        editor.SetEditableAreaSoftEdge(true);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.True(editor.EditableAreaBeauty.SoftEdge);
        Assert.True(editor.SelectedEditableArea!.MemberCount < plain);
        editor.SetEditableAreaEdgeWidth(300);
        Assert.Equal(300, editor.EditableAreaBeauty.EdgeWidth);
        editor.SetEditableAreaGroundShadow(true);
        Assert.Contains(Objects(editor), o => o.Stamp!.GroundShadow is not null);
        Assert.Equal(depth + 3, editor.Session.UndoDepth);

        editor.Session.Undo();
        Assert.DoesNotContain(Objects(editor), o => o.Stamp!.GroundShadow is not null);
        editor.SetEditableAreaSoftEdge(false);
        Assert.Null(editor.SelectedEditableArea!.Beauty);
        Assert.Equal(plain, editor.SelectedEditableArea!.MemberCount);
        editor.SelectedEditableArea!.ValidateMembers();
    }

    [Fact]
    public void A_density_brush_stroke_is_one_undo_step_and_thins_where_it_went()
    {
        var editor = NewEditor();
        var plain = editor.SelectedEditableArea!.MemberCount;
        editor.SetAreaDensityBrush(true);
        Assert.True(editor.IsAreaDensityBrushActive);
        editor.SetAreaDensityBrushMode(PlacementAreaDensityBrushMode.Thinner);
        editor.SetAreaDensityBrushRadius(200);
        editor.SetAreaDensityBrushStrength(100);
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.BeginAreaDensityStroke(200, 500));
        Assert.NotNull(editor.AreaDensityOverlayToDraw);
        for (var x = 220; x <= 800; x += 30) editor.ContinueAreaDensityStroke(x, 500);
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.True(editor.EndAreaDensityStroke());
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.True(editor.SelectedEditableArea!.MemberCount < plain);
        Assert.True(editor.EditableAreaHasDensityMask);
        Assert.DoesNotContain(Objects(editor), o => Math.Abs(o.Y + o.Height / 2 - 500) < 60 && o.X + o.Width / 2 is > 300 and < 700);
        editor.SelectedEditableArea!.ValidateMembers();

        editor.ClearEditableAreaDensityMask();
        Assert.Equal(plain, editor.SelectedEditableArea!.MemberCount);
        editor.Session.Undo();
        Assert.True(editor.EditableAreaHasDensityMask);
    }

    [Fact]
    public void Brush_and_beauty_need_the_natural_layout()
    {
        var editor = NewEditor(layoutVersion: 1);
        editor.SetAreaDensityBrush(true);
        Assert.False(editor.IsAreaDensityBrushActive);
        var depth = editor.Session.UndoDepth;
        editor.SetEditableAreaSoftEdge(true);
        Assert.Equal(depth, editor.Session.UndoDepth);
    }

    [Fact]
    public void A_new_grove_gets_the_new_area_defaults_and_a_ridge_no_soft_edge()
    {
        var groves = PlacementAreaBeauty.ForNewArea(40);
        Assert.True(groves is { SoftEdge: true, Clumps: true, SpeciesPatches: true, GroundShadow: true, Underlay: true, ColorVariation: true });
        Assert.Equal(80, groves.EdgeWidth);
        var ridge = PlacementAreaBeauty.ForNewArea(200, PlacementAreaPattern.Ridge);
        Assert.True(ridge is { SoftEdge: false, Clumps: false, SpeciesPatches: false, GroundShadow: true, Underlay: true, ColorVariation: true });
    }

    private static IEnumerable<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Stamp);

    private static EditorViewModel NewEditor(int layoutVersion = 4)
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var asset = new MapAssetReference(Sha, "tree", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: Sha);
        var document = new MapDocument(DocumentId.New(), "Красота", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 1000, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), layer], [asset]);
        var area = new PlacementArea
        {
            Name = "Лес", LayoutVersion = layoutVersion, Density = layoutVersion >= 3 ? 80 : 4, Seed = 7,
            Pattern = PlacementAreaPattern.Groups,
            Nodes = [new(Guid.NewGuid(), 20, 20), new(Guid.NewGuid(), 980, 20), new(Guid.NewGuid(), 980, 980), new(Guid.NewGuid(), 20, 980)],
            Variants = [new(Sha, 32, 32)], Style = new(Sha), Request = new() { BaseWidth = 30 },
        };
        if (layoutVersion >= 3) area = PlacementAreaPlanner.InitializeComposition(area);
        var session = new EditingSession(document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(session.Document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.SelectLayer(layer.Id);
        editor.SelectEditableArea(area.Id);
        return editor;
    }
}
