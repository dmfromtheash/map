using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class ScratchLakeProbe
{
    private static (EditorViewModel Editor, CanvasInputRouter Router) NewEditor(bool withObjectLayer = true)
    {
        var layers = new List<MapLayer> { new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2") };
        if (withObjectLayer) layers.Add(new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []));
        var document = new MapDocument(DocumentId.New(), "Lakes", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"), layers);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.Session.Viewport.Set(0, 0, 1);
        return (editor, new CanvasInputRouter(editor));
    }

    private static void Click(CanvasInputRouter router, double x, double y)
    { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }

    [Fact]
    public void Lake_first_action_on_a_fresh_map()
    {
        var (editor, router) = NewEditor();
        editor.StartLake();
        var log = $"tool={editor.ActiveTool} creating={editor.IsCreatingRiver} lake={editor.IsCreatingLake} ";
        Click(router, 300, 100);
        log += $"afterFirstClick draft={editor.DisplayedRiver is not null} nodes={editor.DisplayedRiver?.Channels[0].Nodes.Length} closed={editor.DisplayedRiver?.Channels[0].Closed} ";
        Click(router, 420, 80); Click(router, 470, 170); Click(router, 380, 250);
        log += $"nodes={editor.DisplayedRiver?.Channels[0].Nodes.Length} summary=[{editor.RiverSummary}] ";
        var finished = editor.FinishRiver();
        log += $"finished={finished} rivers={editor.Rivers.Count} status=[{editor.StatusDetail}]";
        Assert.True(false, log);
    }

    [Fact]
    public void Lake_first_action_without_object_layer()
    {
        var (editor, router) = NewEditor(withObjectLayer: false);
        editor.StartLake();
        Click(router, 300, 100); Click(router, 420, 80); Click(router, 470, 170);
        var finished = editor.FinishRiver();
        Assert.True(false, $"finished={finished} rivers={editor.Rivers.Count} preview={editor.RiverPreviewLayerId is not null} status=[{editor.StatusDetail}]");
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
        Assert.True(false, $"rivers={editor.Rivers.Count} creating={editor.IsCreatingRiver} status=[{editor.StatusDetail}]");
    }
}
