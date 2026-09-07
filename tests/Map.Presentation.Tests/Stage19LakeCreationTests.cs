using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>Lake creation as the first editing action. Memory admission is tested separately;
/// a fixed budget keeps these workflow tests independent of other applications on the PC.</summary>
public sealed class Stage19LakeCreationTests
{
    private static (EditorViewModel Editor, CanvasInputRouter Router) NewEditor(bool withObjectLayer = true)
    {
        var layers = new List<MapLayer> { new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2") };
        if (withObjectLayer) layers.Add(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []));
        var document = new MapDocument(DocumentId.New(), "Lakes", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"), layers);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, new CanvasInputRouter(editor));
    }

    private static void Click(CanvasInputRouter router, double x, double y)
    { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }

    [Fact]
    public void Lake_first_action_on_a_fresh_map()
    {
        var (editor, router) = NewEditor();
        var before = editor.Session.Document;
        editor.StartLake();
        Assert.Equal(EditorTool.River, editor.ActiveTool);
        Assert.True(editor.IsCreatingLake);
        Click(router, 300, 100);
        var draft = Assert.Single(editor.DisplayedRiver!.Channels);
        Assert.True(draft.Closed);
        Assert.Single(draft.Nodes);
        Click(router, 420, 80); Click(router, 470, 170); Click(router, 380, 250);
        Assert.Same(before, editor.Session.Document);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var lake = Assert.Single(Assert.Single(editor.Rivers).Channels);
        Assert.True(lake.Closed);
        Assert.Equal(4, lake.Nodes.Length);
        Assert.False(editor.IsCreatingRiver);
        Assert.Equal(1, editor.Session.UndoDepth);
        var committed = editor.Session.Document;
        editor.Undo();
        Assert.Same(before, editor.Session.Document);
        editor.Redo();
        Assert.Same(committed, editor.Session.Document);
    }

    [Fact]
    public void Lake_first_action_without_object_layer()
    {
        var (editor, router) = NewEditor(withObjectLayer: false);
        var before = editor.Session.Document;
        editor.StartLake();
        Click(router, 300, 100); Click(router, 420, 80); Click(router, 470, 170);
        Assert.True(editor.FinishRiver(), editor.StatusDetail);
        var layer = Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>());
        Assert.True(Assert.Single(Assert.Single(layer.Rivers).Channels).Closed);
        Assert.Null(editor.RiverPreviewLayerId);
        Assert.Equal(1, editor.Session.UndoDepth);
        editor.Undo();
        // The composite command removes the newly created layer and reconstructs the document.
        Assert.Equal(before.Layers, editor.Session.Document.Layers);
        Assert.Empty(editor.Rivers);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void Lake_in_freehand_mode()
    {
        var (editor, router) = NewEditor();
        editor.RiverDrawMode = RiverDrawMode.Freehand;
        editor.StartLake();
        router.Press(CanvasPointerButton.Left, 300, 100);
        for (var i = 1; i <= 20; i++) router.Move(300 + Math.Cos(i / 3.0) * 80, 150 + Math.Sin(i / 3.0) * 80);
        router.Release(300, 100);
        var lake = Assert.Single(Assert.Single(editor.Rivers).Channels);
        Assert.True(lake.Closed);
        Assert.True(lake.Nodes.Length >= 3);
        Assert.False(editor.IsCreatingRiver);
        Assert.Equal(1, editor.Session.UndoDepth);
    }
}
