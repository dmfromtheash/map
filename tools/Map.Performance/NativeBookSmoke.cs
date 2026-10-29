using Avalonia;
using Avalonia.Controls;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] BookRequired = ["book-window", "book-pdf", "resources-disposed"];

    /// <summary>R40 through «Книга и язык»: the window opens with its two parts; the atlas book of the map is written. Captured.</summary>
    private async Task BookOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ильград", 440, 360, 160, 44, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = LabelPresets.Apply(new TextStyle { Content = "Ильград", Alignment = TextAlignment.Center }, LabelPreset.City) };
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [label])],
            notes: [new MapNote(NoteId.New(), "Таверна", "Хозяин знает всё.", NoteAnchorKind.World, 300, 300)]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        await Click("WorldSectionButton"); await Click("BookButton"); await Task.Delay(300); await Settle();
        var window = _view.GetType().GetField("_bookWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_view) as Window
            ?? throw new InvalidOperationException("Окно «Книга и язык» не открылось.");
        var names = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<Control>().Select(c => c.Name).ToHashSet();
        _report.Checks["book-window"] = names.Contains("ArtLanguageBox") && names.Contains("AtlasBookMasterButton");
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(640, 560)))
        {
            bitmap.Render(window);
            using var file = File.Create(Path.ChangeExtension(Options.OutputPath, ".png"));
            bitmap.Save(file);
        }
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        var pdf = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-book.pdf");
        var pages = await _editor.ExportAtlasBookAsync(pdf, players: false);
        _report.Checks["book-pdf"] = pages == 4 && File.Exists(pdf);
        window.Close(); await Settle();
        await Task.Delay(800); await Settle();
    }
}
