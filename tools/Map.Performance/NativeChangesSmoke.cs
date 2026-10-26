using Avalonia.Controls;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] ChangesRequired = ["changes-exported", "changes-checked", "changes-applied", "changes-undo", "resources-disposed"];

    /// <summary>
    /// R42 through «Версии»: a version is taken, a town added and the port renamed, the changes
    /// written to .mapchanges; another copy where the port was renamed otherwise opens the package -
    /// one clean change, one conflict; «взять из пакета» on the conflict, «Применить», one undo.
    /// </summary>
    private async Task ChangesOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        Func<string, double, double, ObjectId, SceneObject> label = (text, x, y, id) =>
            new SceneObject(id, SceneObjectKind.Text, text, x, y, 160, 44, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
            { Text = LabelPresets.Apply(new TextStyle { Content = text, Alignment = TextAlignment.Center }, LabelPreset.City) };
        var portId = ObjectId.New();
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [label("Порт", 230, 330, portId)]);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, objects]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _editor.CreateCheckpoint("Сессия 3"); await Settle();
        var version = _editor.Session.Document.Checkpoints.Single().Id;
        _editor.Session.Execute(new AddObjectCommand(objects.Id, label("Новгород", 620, 420, ObjectId.New())));
        _editor.Session.SelectedObjectId = portId;
        _editor.SetTextContent("Порт Ветров");
        _editor.Session.SelectedObjectId = null; await Settle();
        var path = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "smoke" + Map.Persistence.ChangePackageStore.Extension);
        var package = await _editor.ExportChangesAsync(version, path);
        _report.Checks["changes-exported"] = package is { Count: 2 } && File.Exists(path);

        // The other copy: the same map at the version, but the port renamed its own way.
        var theirs = document with { Id = document.Id };
        theirs = theirs.WithLayers([.. theirs.Layers.Select(l => l.Id == objects.Id ? objects with { Objects = [label("Порт Бурь", 230, 330, portId)] } : l)]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(theirs); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _view.OpenVersions(); await Settle();
        var versions = _view.VersionsWindowForSmoke ?? throw new InvalidOperationException("Окно версий не открылось.");
        await _editor.OpenChangePackageAsync(path);
        _view.GetType().GetMethod("SyncPackageBlock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(_view, null);
        await Task.Delay(400); await Settle();
        T In<T>(string name) where T : Control =>
            Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(versions).OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException(name + " нет в окне версий");
        var summary = In<TextBlock>("PackageSummary").Text ?? "";
        _report.Checks["changes-checked"] = summary.Contains("без вопросов: 1", StringComparison.Ordinal) && summary.Contains("Конфликтов: 1", StringComparison.Ordinal);
        In<CheckBox>("PackageTake_" + portId.Value).IsChecked = true;
        await Task.Delay(300); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        PanelAudit.Save((Control)versions.Content!, _report.Screenshot);

        In<Button>("ApplyChangePackageButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await Settle();
        var texts = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => o.Text?.Content).ToList();
        _report.Checks["changes-applied"] = texts.Contains("Новгород") && texts.Contains("Порт Ветров") && !texts.Contains("Порт Бурь") && _editor.Session.UndoDepth == 1;
        await Frame();
        SaveVisual(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-map.png"));
        _editor.Undo(); await Settle();
        var undone = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => o.Text?.Content).ToList();
        _report.Checks["changes-undo"] = undone.SequenceEqual(["Порт Бурь"]);
        versions.Close(); File.Delete(path);
        await Task.Delay(800); await Frame(); await Settle();
    }
}
