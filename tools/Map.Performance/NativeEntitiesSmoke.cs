using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] EntitiesRequired =
    [
        "card-from-selection", "card-text-and-link", "link-creates-card", "backlink-shown", "wiki-preview", "note-settings", "resources-disposed",
    ];

    /// <summary>
    /// R26 through the Notes tool: a card made from a selected town label takes its name and place;
    /// its text gets a [[link]], the link makes the second card, which shows the first among its
    /// backlinks; a note gets a category and «Видно игрокам». The card is captured.
    /// </summary>
    private async Task EntitiesOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 440, 300);
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ильград", 440, 360, 150, 44, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = LabelPresets.Apply(new TextStyle { Content = "Ильград", Alignment = TextAlignment.Center }, LabelPreset.City) };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [label]);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, objects]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _editor.Session.SelectedObjectId = label.Id; await Settle();
        _view.OpenNotes(); await Settle();
        // With the label selected the panel shows the label; the user switches it to the tool.
        if (_view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ContextScopeTool") is { IsEffectivelyVisible: true })
            await Click("ContextScopeTool");
        await Click("NewEntityButton");
        var card = _editor.Entities.SingleOrDefault();
        _report.Checks["card-from-selection"] = card is { Name: "Ильград" } && card.Bindings.Single().TargetId == label.Id.Value;

        var text = Find<TextBox>("EntityMarkdownBox");
        text.Text = string.Join(Environment.NewLine, "# Ильград", "Столица у реки [[Сельва]]. Здесь заседает **совет**.", "- рынок по *четвергам*", "- порт", "1. совет", "2. гильдии");
        await Click("SaveEntityButton");
        card = _editor.Entities.Single();
        _report.Checks["card-text-and-link"] = card.LinkedNames().SequenceEqual(["Сельва"]) && Find<Control>("EntityLinks").IsEffectivelyVisible;
        await Click("EntityLink_Сельва");
        var river = _editor.Entities.FirstOrDefault(e => e.Name == "Сельва");
        _report.Checks["link-creates-card"] = river is not null && _editor.Entities.Length == 2;
        _report.Checks["backlink-shown"] = Find<StackPanel>("EntityBacklinks").Children.OfType<Button>().Any();
        await Click("Entity_" + card.Id.ToString("N"));
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        var wiki = Find<Control>("EntityWikiPreview").GetVisualDescendants().OfType<TextBlock>().ToList();
        static string Words(TextBlock t) => t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "";
        _report.Checks["wiki-preview"] = wiki.Any(t => Words(t) == "Ильград" && t.FontSize >= 18) && wiki.Any(t => t.Text == "Сельва")
            && wiki.Any(t => t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "совет" && r.FontWeight == Avalonia.Media.FontWeight.Bold) == true);
        PanelAudit.Save(Find<Control>("EntityCard"), Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-card.png"));

        var note = _editor.AddNoteAt(300, 300);
        _editor.SetNoteWiki(note, null, NoteCategory.Quest, true);
        _report.Checks["note-settings"] = _editor.Notes.Single().Category == NoteCategory.Quest && _editor.Notes.Single().PlayerVisible;
        await Task.Delay(800); await Frame(); await Settle();
    }
}
