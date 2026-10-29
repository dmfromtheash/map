using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Map.App.Views;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class NativeEditorScenarioApplication
{
    private static readonly string[] FogRequired =
    [
        "fog-on", "brush-reveals", "one-undo-step", "player-window-hides", "player-knowledge", "resources-disposed",
    ];

    /// <summary>
    /// R27 through the Notes tool: «Туман» switched on, the brush drags a path open over the port,
    /// one Ctrl+Z takes it back, redo returns it; the players' window shows the map with opaque fog,
    /// keeps the label under the fog out and lists only the note meant for players. Both captured.
    /// </summary>
    private async Task FogOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        Func<string, double, double, LabelPreset, SceneObject> label = (text, x, y, preset) =>
            new SceneObject(ObjectId.New(), SceneObjectKind.Text, text, x, y, 160, 44, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
            { Text = LabelPresets.Apply(new TextStyle { Content = text, Alignment = TextAlignment.Center }, preset) };
        var port = label("Порт", 230, 330, LabelPreset.City);
        var lair = label("Логово", 700, 420, LabelPreset.Village);
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [port, lair]);
        var tavern = new MapNote(NoteId.New(), "Таверна «Якорь»", "Здесь можно нанять лодку.", NoteAnchorKind.World, 300, 360) { PlayerVisible = true };
        var trap = new MapNote(NoteId.New(), "Засада", "Только для ведущего.", NoteAnchorKind.World, 300, 360);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, objects], notes: [tavern, trap]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _view.OpenNotes(); await Settle();
        var on = Find<Avalonia.Controls.Primitives.ToggleButton>("FogEnabledToggle");
        on.IsChecked = true; await Settle();
        _report.Checks["fog-on"] = _editor.Fog.Enabled && !_editor.Fog.Sees(300, 360);

        var brush = Find<Avalonia.Controls.Primitives.ToggleButton>("FogBrushToggle");
        brush.IsChecked = true; await Settle();
        _editor.FogBrushRadius = 110;
        Drag(160, 360, 420, 360); await Settle();
        _report.Checks["brush-reveals"] = _editor.Fog.Sees(300, 360) && !_editor.Fog.Sees(760, 440);
        _editor.Undo(); await Settle();
        var undone = !_editor.Fog.Sees(300, 360);
        _editor.Redo(); await Settle();
        _report.Checks["one-undo-step"] = undone && _editor.Fog.Sees(300, 360);
        brush.IsChecked = false; await Settle(); await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);

        var player = new HexPlayerWindow(_editor) { ShowActivated = false, ShowInTaskbar = false, Width = 1100, Height = 720,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        player.Show(); await Settle(); await Task.Delay(600); await Settle();
        var texts = player.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        _report.Checks["player-knowledge"] = texts.Any(t => t.Contains("Таверна")) && !texts.Any(t => t.Contains("Засада"));
        var players = Map.Core.Editing.PlayerScene.Sanitize(_editor.Session.Document, sanitizeHex: false);
        var shown = players.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => o.Text?.Content).ToArray();
        _report.Checks["player-window-hides"] = shown.Contains("Порт") && !shown.Contains("Логово");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(1100, 720)))
        {
            bitmap.Render(player);
            using var file = File.Create(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, Path.GetFileNameWithoutExtension(Options.OutputPath) + "-player.png"));
            bitmap.Save(file);
        }
        player.Close(); await Settle();
        await Task.Delay(800); await Frame(); await Settle();
    }
}
