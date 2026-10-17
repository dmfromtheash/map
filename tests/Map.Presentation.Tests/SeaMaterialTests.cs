using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class SeaMaterialTests
{
    private static EditorViewModel Editor(MapDocument? document = null) => new(document ?? TestData.Document(), null, false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());

    [Fact]
    public void The_sea_refuses_removed_materials_and_can_clear_a_legacy_material_with_undo()
    {
        var editor = Editor();
        Assert.Null(editor.Session.Document.BaseColor.TextureId);

        Assert.False(editor.SetBaseMaterial("regional-meadow"));
        Assert.False(editor.SetBaseMaterial("no-such-material"));
        Assert.Equal(0, editor.Session.UndoDepth);

        Assert.False(editor.SetBaseMaterial("regional-water"));
        Assert.False(editor.SetBaseMaterial("watercolour-water"));
        Assert.Null(editor.Session.Document.BaseColor.TextureId);
        Assert.Equal(0, editor.Session.UndoDepth);

        var legacy = TestData.Document();
        editor = Editor(legacy.WithLayers(legacy.Layers.Select(layer => layer is BaseColorLayer sea
            ? sea with { TextureId = "regional-water" } : layer).ToArray()));
        Assert.True(editor.SetBaseMaterial(null));
        Assert.Null(editor.Session.Document.BaseColor.TextureId);
        Assert.Equal(1, editor.Session.UndoDepth);
        editor.Undo();
        Assert.Equal("regional-water", editor.Session.Document.BaseColor.TextureId);
        editor.Redo();
        Assert.Null(editor.Session.Document.BaseColor.TextureId);
    }
}
