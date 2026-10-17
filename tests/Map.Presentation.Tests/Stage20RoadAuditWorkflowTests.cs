using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The road workflow defects of the 2026-09-08 review, fixed in the audit: a branch of one
/// network does not silently merge into a network on another layer or of another level, and a
/// road on a layer at 0 % opacity cannot be picked up by a new draft.
/// </summary>
public sealed class Stage20RoadAuditWorkflowTests
{
    private static (EditorViewModel Editor, CanvasInputRouter Router, ObjectLayer First, ObjectLayer Second) NewEditor(double secondOpacity = 1)
    {
        var first = new ObjectLayer(LayerId.New(), "Нижний", true, false, 1, []);
        var second = new ObjectLayer(LayerId.New(), "Верхний", true, false, secondOpacity, []);
        var document = new MapDocument(DocumentId.New(), "Roads", DateTimeOffset.UnixEpoch, new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), first, second]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, new CanvasInputRouter(editor), first, second);
    }

    private static void Click(CanvasInputRouter router, double x, double y)
    { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }

    private static Road Draw(EditorViewModel editor, CanvasInputRouter router, LayerId layer, params (double X, double Y)[] points)
    {
        editor.SelectLayer(layer);
        editor.NewRoadWidth = 12;
        editor.StartRoad();
        foreach (var (x, y) in points) Click(router, x, y);
        Assert.True(editor.FinishRoad(), editor.StatusDetail);
        return editor.SelectedRoad!;
    }

    [Fact]
    public void A_branch_of_a_network_on_one_layer_is_refused_a_join_to_a_network_on_another_layer()
    {
        var (editor, router, first, second) = NewEditor();
        var lower = Draw(editor, router, first.Id, (100, 400), (500, 400));
        var upper = Draw(editor, router, second.Id, (100, 100), (500, 100));
        var before = editor.Session.Document;
        var depth = editor.Session.UndoDepth;

        // A new branch from the upper road's end, dropped onto the lower road.
        editor.SelectRoad(upper.Id);
        editor.StartRoad();
        Click(router, 500, 100);
        Click(router, 500, 250);
        Click(router, 300, 400);

        Assert.Same(before, editor.Session.Document);
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Contains("разных слоях", editor.StatusDetail);
        Assert.Equal(2, editor.Roads.Count);
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(first.Id)!).Roads);
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(second.Id)!).Roads);
        Assert.Equal(lower.Branches.Length, editor.Roads.Single(r => r.Id == lower.Id).Branches.Length);
    }

    [Fact]
    public void Networks_of_different_levels_do_not_merge_into_one_level()
    {
        var (editor, router, first, _) = NewEditor();
        var high = Draw(editor, router, first.Id, (100, 400), (500, 400));
        editor.SelectRoadOnly(high.Id);
        Assert.True(editor.SetSelectedRoadLevel(3));
        var low = Draw(editor, router, first.Id, (100, 100), (500, 100));
        editor.SelectRoadOnly(low.Id);
        Assert.True(editor.SetSelectedRoadLevel(-2));
        var before = editor.Session.Document;

        editor.SelectRoad(high.Id);
        editor.StartRoad();
        Click(router, 500, 400);
        Click(router, 500, 250);
        Click(router, 300, 100);

        Assert.Same(before, editor.Session.Document);
        Assert.Contains("разных уровней", editor.StatusDetail);
        Assert.Equal(3, editor.Roads.Single(r => r.Id == high.Id).Level);
        Assert.Equal(-2, editor.Roads.Single(r => r.Id == low.Id).Level);
    }

    [Fact]
    public void A_road_on_a_layer_at_zero_opacity_is_not_picked_up_by_a_new_draft()
    {
        var (editor, router, first, second) = NewEditor(secondOpacity: 0);
        Draw(editor, router, second.Id, (100, 100), (500, 100));
        editor.SelectLayer(first.Id);
        editor.ClearRoadSelection();
        editor.StartRoad();
        // The first click lands exactly on the invisible road; a visible layer is active.
        Click(router, 300, 100);
        Click(router, 300, 300);
        Assert.True(editor.FinishRoad(), editor.StatusDetail);
        var created = editor.SelectedRoad!;
        Assert.Equal(2, editor.Roads.Count);
        Assert.Contains(created, ((ObjectLayer)editor.Session.Document.FindLayer(first.Id)!).Roads);
        Assert.Single(created.Branches);
        Assert.False(editor.Roads.Any(r => r.Id != created.Id && r.Branches.Length != 1), "the invisible road was not joined");
    }
}
