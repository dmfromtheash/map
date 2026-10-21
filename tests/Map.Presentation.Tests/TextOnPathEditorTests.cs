using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R07 in the editor: «Подпись вдоль…», presets and «Имя» with one Undo step each; the label follows the river.</summary>
public sealed class TextOnPathEditorTests
{
    private static (EditorViewModel Editor, River River, Road Road, LayerId Layer) NewEditor()
    {
        var river = new River { Name = "Серебрянка", Channels = [RiverEditor.NewChannel([new(100, 300), new(900, 300)], 20)] };
        var road = RoadEditor.NewRoad([new(100, 600), new(900, 600)], 12, RoadStyle.Country(), "Тракт");
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river], Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "Подписи", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1000, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), layer]);
        var memory = new WorkingMemoryBudget(() => new WorkingMemorySnapshot(
            16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB));
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), memory: memory);
        editor.SelectLayer(layer.Id);
        return (editor, river, road, layer.Id);
    }

    [Fact]
    public void A_river_label_is_added_named_after_it_and_follows_the_river()
    {
        var (editor, river, _, layerId) = NewEditor();
        editor.SelectRiver(river.Id);
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.AddLabelAlongSelectedPath(TextPathTarget.River));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var label = editor.Session.SelectedObject!;
        Assert.Equal("Серебрянка", label.Text!.Content);
        Assert.Equal(river.Id, label.Text.Path!.TargetId);
        Assert.Equal(LabelPresets.Apply(new TextStyle(), LabelPreset.River).FontSize, label.Text.FontSize);
        var y = label.Y;

        var lowered = river with { Channels = [river.Channels[0] with { Nodes = [.. river.Channels[0].Nodes.Select(n => n with { Y = n.Y + 100 })] }] };
        editor.Session.Execute(new RiverCommand(layerId, river.Id, lowered, "move"));
        var followed = editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == label.Id);
        Assert.Equal(y + 100, followed.Y, 1);
        Assert.True(editor.Session.Undo());
        Assert.Equal(y, editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == label.Id).Y, 1);
    }

    [Fact]
    public void Free_text_binds_to_the_nearest_line_slides_and_unbinds()
    {
        var (editor, _, road, _) = NewEditor();
        editor.AddText();
        var text = editor.Session.SelectedObject!;
        // Drag the new text next to the road.
        editor.Session.Execute(new ReplaceObjectsCommand([text], [SceneObjectClone.At(text, 300 - text.Width / 2, 640 - text.Height / 2)], "move"));
        Assert.True(editor.BindSelectedTextToNearestPath());
        var bound = editor.SelectedTextPath!;
        Assert.Equal(TextPathTarget.Road, bound.Target);
        Assert.Equal(road.Id, bound.TargetId);
        Assert.InRange(bound.Position, 0.2, 0.3);

        editor.SetTextPathPosition(0.8);
        Assert.Equal(0.8, editor.SelectedTextPath!.Position);
        editor.SetTextPathSide(TextPathSide.Below);
        Assert.Equal(TextPathSide.Below, editor.SelectedTextPath!.Side);
        Assert.True(TextOnPath.BaselineCentre(editor.Session.SelectedObject!).Y > 600);
        editor.FlipTextPathDirection();
        Assert.False(editor.SelectedTextPath!.KeepUpright);

        editor.UnbindSelectedTextPath();
        Assert.Null(editor.SelectedTextPath);
        Assert.True(editor.Session.Undo());
        Assert.NotNull(editor.SelectedTextPath);
    }

    [Fact]
    public void Presets_and_names_are_one_undo_step_each()
    {
        var (editor, _, _, _) = NewEditor();
        editor.AddText();
        var before = editor.Session.SelectedObject!.Text!;
        var depth = editor.Session.UndoDepth;

        editor.ApplyLabelPreset(LabelPreset.Country);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(64, editor.Session.SelectedObject!.Text!.FontSize);
        Assert.Equal(before.Content, editor.Session.SelectedObject.Text.Content);

        var name = editor.GenerateLabelName(LabelPreset.City);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(name, editor.Session.SelectedObject!.Text!.Content);
        Assert.Equal(name, editor.Session.SelectedObject.Name);
        Assert.Equal(depth + 2, editor.Session.UndoDepth);
        var second = editor.GenerateLabelName(LabelPreset.City);
        Assert.NotEqual(name, second);

        Assert.True(editor.Session.Undo());
        Assert.True(editor.Session.Undo());
        Assert.True(editor.Session.Undo());
        Assert.Equal(before, editor.Session.SelectedObject!.Text);
    }

    [Fact]
    public void Without_a_line_nearby_binding_explains_itself()
    {
        var (editor, _, _, _) = NewEditor();
        editor.AddText();
        var text = editor.Session.SelectedObject!;
        editor.Session.Execute(new ReplaceObjectsCommand([text], [SceneObjectClone.At(text, 5000, 5000)], "far"));
        Assert.False(editor.BindSelectedTextToNearestPath());
        Assert.Contains("Рядом нет", editor.StatusDetail);
        Assert.Null(editor.SelectedTextPath);
    }
    [Fact]
    public void A_moved_bound_label_stays_in_the_object_list()
    {
        var (editor, river, _, _) = NewEditor();
        editor.SelectRiver(river.Id);
        Assert.True(editor.AddLabelAlongSelectedPath(TextPathTarget.River));
        var label = editor.Session.SelectedObject!;
        Assert.Contains(editor.ObjectTree, row => row.Object?.Id == label.Id);
        editor.Session.Execute(new ReplaceObjectsCommand([label], [SceneObjectClone.At(label, label.X + 200, label.Y - 80)], "drag"));
        Assert.Contains(editor.ObjectTree, row => row.Object?.Id == label.Id);
        foreach (var section in Enum.GetValues<PanelSection>())
        {
            editor.PanelSection = section;
            var rows = editor.ObjectTree.Where(r => r.Object is not null).Select(r => r.Object!.Name).ToList();
            Console.WriteLine(section + ": " + string.Join(", ", rows));
        }
    }
}
