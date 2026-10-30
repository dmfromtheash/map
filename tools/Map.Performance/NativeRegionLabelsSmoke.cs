using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] RegionLabelsRequired =
    [
        "label-from-shape", "label-inside", "follows-outline", "importance-set", "far-hides-minor", "far-keeps-region", "resources-disposed",
    ];

    /// <summary>
    /// R07b through the panel: «Подписать область» on a selected shape names it after its card
    /// inside the figure; moving the figure carries the name; the label's importance is set; with
    /// «Подписи по масштабу» on, far away the hamlet's label hides and the region's stays.
    /// </summary>
    private async Task RegionLabelsOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        var valley = VectorGeometry.CreateFreeShape(ObjectId.New(), "Долина",
            [new(170, 300), new(520, 250), new(840, 330), new(820, 470), new(500, 430), new(190, 480)]);
        var hamlet = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Хутор", 600, 520, 110, 34, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = LabelPresets.Apply(new TextStyle { Content = "Хутор", Alignment = TextAlignment.Center }, LabelPreset.Village) with { Importance = 1 } };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [valley, hamlet]);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [CurrentLook.Page("Море", page), island, objects]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _editor.Session.SelectedObjectId = valley.Id; await Settle();
        var card = _editor.CreateEntity(EntityKind.Region);
        _editor.EditEntity(card, "Долина Ветров", EntityKind.Region, "", false);
        _editor.ActiveTool = Presentation.ViewModels.EditorTool.Select;
        _editor.Session.SelectedObjectId = valley.Id; await Settle();
        await Click("ShapeRegionLabelButton");
        var label = Region();
        _report.Checks["label-from-shape"] = label is { Text.Content: "Долина Ветров" } && label.Text.Region!.EntityId == card;
        _report.Checks["label-inside"] = label is not null && RegionLabels.Outline(_editor.Session.Document, label.Text!.Region!) is { } outline
            && Inside(outline, label.X + (label.Width / 2), label.Y + (label.Height / 2));

        var shape = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == valley.Id);
        _editor.Session.Execute(new TransformObjectCommand(shape, SceneObjectClone.At(shape, shape.X + 40, shape.Y + 30)));
        await Settle();
        var moved = Region();
        _report.Checks["follows-outline"] = label is not null && moved is not null && Math.Abs(moved.X - label.X - 40) < 2 && Math.Abs(moved.Y - label.Y - 30) < 2;

        _editor.Session.SelectedObjectId = moved!.Id; await Settle();
        var byScale = Find<Avalonia.Controls.Primitives.ToggleButton>("TextLabelsByScaleToggle");
        byScale.IsChecked = true; byScale.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        _report.Checks["importance-set"] = _editor.Session.SelectedObject?.Text?.Importance == 4 && _editor.LabelSettings.ByScale;
        _editor.Session.SelectedObjectId = null; await Settle();
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        _editor.Session.Viewport.Set(-1200, -900, 0.2); await Settle(); await Frame();
        var hidden = LabelPriority.Hidden(_editor.Session.Document, _editor.Session.Viewport.Scale);
        _report.Checks["far-hides-minor"] = hidden.Contains(hamlet.Id);
        _report.Checks["far-keeps-region"] = !hidden.Contains(moved.Id);
        SaveVisual(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-far.png"));
        await Task.Delay(800); await Frame(); await Settle();

        SceneObject? Region() => _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).FirstOrDefault(o => o.Text?.Region is not null);
    }

    private static bool Inside(IReadOnlyList<VectorPoint> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > y) != (polygon[j].Y > y)
                && x < ((polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X)
                inside = !inside;
        return inside;
    }
}
