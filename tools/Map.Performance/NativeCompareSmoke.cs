using Avalonia.Controls;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] CompareRequired =
    [
        "compare-summary", "compare-note-visibility", "compare-moved-fog", "compare-marks", "before-shows-version", "after-shows-map", "resources-disposed",
    ];

    /// <summary>
    /// R30 through «Версии»: a version is taken, a town label is added and another renamed; «Сравнить»
    /// gives the summary in words and marks on the canvas; «До» shows the version, «После» the map.
    /// </summary>
    private async Task CompareOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        Func<string, double, double, SceneObject> label = (text, x, y) =>
            new SceneObject(ObjectId.New(), SceneObjectKind.Text, text, x, y, 160, 44, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
            { Text = LabelPresets.Apply(new TextStyle { Content = text, Alignment = TextAlignment.Center }, LabelPreset.City) };
        var port = label("Порт", 230, 330);
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [port]);
        // R30b: a note and an opened patch of fog that later only change in ways the old comparison missed.
        var note = new MapNote(NoteId.New(), "Засада", "", NoteAnchorKind.World, 400, 300);
        MapFog Opened(double x)
        {
            var stroke = Map.Core.Editing.MapFogEditor.Begin(new MapFog { Enabled = true });
            Map.Core.Editing.MapFogEditor.Dab(stroke, x, 400, 80, true, 1024, 768);
            return Map.Core.Editing.MapFogEditor.Finish(stroke);
        }
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, objects]).WithNotes([note]) with { Fog = Opened(300) };
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _editor.CreateCheckpoint("Версия 01"); await Settle();
        var version = _editor.Session.Document.Checkpoints.Single().Id;
        _editor.Session.Execute(new Map.Core.Editing.AddObjectCommand(objects.Id, label("Новгород", 620, 420)));
        _editor.Session.SelectedObjectId = port.Id;
        _editor.SetTextContent("Порт Ветров");
        _editor.Session.SelectedObjectId = null;
        _editor.SetNoteWiki(note.Id, null, NoteCategory.Other, true);
        _editor.Session.Execute(new Map.Core.Editing.SetFogCommand(_editor.Session.Document.Fog, Opened(700), "Туман"));
        await Settle();

        _view.OpenVersions(); await Settle();
        var versions = _view.VersionsWindowForSmoke ?? throw new InvalidOperationException("Окно версий не открылось.");
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(versions).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне версий");
        async Task Press(string name) { In<Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle(); }
        await Press("CompareCheckpoint_" + version);
        var summary = In<StackPanel>("CompareSummary").Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        _report.Checks["compare-summary"] = summary.Any(t => t.StartsWith("Подписи: +1, изменено 1", StringComparison.Ordinal));
        _report.Checks["compare-note-visibility"] = summary.Any(t => t.StartsWith("Заметки: изменено 1", StringComparison.Ordinal));
        _report.Checks["compare-moved-fog"] = Opened(300).RevealedCells == Opened(700).RevealedCells
            && summary.Any(t => t.StartsWith("Открыто тумана", StringComparison.Ordinal)) && summary.Any(t => t.StartsWith("Закрыто туманом", StringComparison.Ordinal));
        // Two labels and the note.
        _report.Checks["compare-marks"] = _editor.CompareMarks is { Count: 3 };
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        await Press("CompareSideBefore"); await Task.Delay(700); await Settle();
        _report.Checks["before-shows-version"] = _editor.CompareShowsBefore
            && _editor.RenderDocument.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Count(o => o.Kind == SceneObjectKind.Text) == 1;
        SaveVisual(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-before.png"));
        await Press("CompareSideAfter"); await Frame();
        _report.Checks["after-shows-map"] = !_editor.CompareShowsBefore && ReferenceEquals(_editor.RenderDocument, _editor.Session.Document);
        await Press("CompareCloseButton"); versions.Close();
        await Task.Delay(800); await Frame(); await Settle();
    }
}
