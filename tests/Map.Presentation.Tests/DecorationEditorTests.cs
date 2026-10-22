using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R08 in the editor: decorations and the map scale are one Undo step each; «Измерить» speaks map units.</summary>
public sealed class DecorationEditorTests
{
    private static EditorViewModel NewEditor()
    {
        var river = new River { Name = "Серебрянка", Channels = [RiverEditor.NewChannel([new(100, 300), new(900, 300)], 20)] };
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "Северные земли", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), layer]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.SelectLayer(layer.Id);
        return editor;
    }

    [Theory]
    [InlineData(DecorationKind.ScaleBar)]
    [InlineData(DecorationKind.Compass)]
    [InlineData(DecorationKind.Cartouche)]
    [InlineData(DecorationKind.Legend)]
    public void Adding_a_decoration_is_one_undo_step_and_selects_it(DecorationKind kind)
    {
        var editor = NewEditor();
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.AddDecoration(kind));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var added = editor.Session.SelectedObject!;
        Assert.Equal(SceneObjectKind.Decoration, added.Kind);
        Assert.Equal(kind, added.Decoration!.Kind);
        Assert.Equal(DecorationStyle.DefaultName(kind), added.Name);
        if (kind == DecorationKind.Cartouche) Assert.Equal("Северные земли", added.Decoration.Title);
        if (kind == DecorationKind.Legend) Assert.Contains(added.Decoration.Entries, e => e.Kind == LegendEntryKind.River);
        Assert.Equal(EditorTool.Select, editor.ActiveTool);

        editor.Session.Undo();
        Assert.DoesNotContain(editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects), o => o.Kind == SceneObjectKind.Decoration);
    }

    [Fact]
    public void The_scale_bar_follows_the_scale_set_in_the_panel_and_measuring_uses_it()
    {
        var editor = NewEditor();
        editor.AddDecoration(DecorationKind.ScaleBar);
        var barId = editor.Session.SelectedObjectId!.Value;
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.SetMapScale(25, "миль"));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(new MapScale(0.25, "миль"), editor.MapScale);
        Assert.Equal(25, editor.MapScalePer100Pixels);
        var bar = editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == barId);
        Assert.Equal(new MapScale(0.25, "миль"), bar.Decoration!.Scale);

        editor.ActiveTool = EditorTool.Measure;
        editor.BeginMeasure(100, 100);
        editor.UpdateMeasure(400, 500); // 500 px
        Assert.Equal((100d, 100d, 400d, 500d), editor.MeasureLine);
        Assert.StartsWith("Расстояние: 125 миль", editor.MeasureText);

        editor.Session.Undo();
        Assert.Null(editor.MapScale);
        Assert.True(editor.SetMapScale(0, ""));
        editor.UpdateMeasure(400, 500);
        Assert.Contains("500 px", editor.MeasureText);
    }

    [Fact]
    public void Decoration_style_edits_are_undoable_and_the_measure_tool_routes_a_drag()
    {
        var editor = NewEditor();
        editor.AddDecoration(DecorationKind.Compass);
        editor.SetDecorationVariant(2);
        editor.SetDecorationInk("#FF6E1E1E");
        Assert.Equal(2, editor.SelectedDecoration!.Variant);
        Assert.Equal("#FF6E1E1E", editor.SelectedDecoration.InkColor);
        editor.Session.Undo();
        Assert.Equal(DecorationStyle.DefaultName(DecorationKind.Compass), editor.Session.SelectedObject!.Name);
        Assert.NotEqual("#FF6E1E1E", editor.SelectedDecoration!.InkColor);

        Assert.Contains(EditorSurfaceLayout.Rail, entry => entry.Tool == EditorTool.Measure && entry.AutomationId == "MeasureToolButton");
        Assert.Equal(ObjectKindSection.Decoration, EditorSurfaceLayout.SectionFor(SceneObjectKind.Decoration));
        Assert.Equal("Масштаб и украшения", EditorLabels.Tool(EditorTool.Measure));
    }
}
