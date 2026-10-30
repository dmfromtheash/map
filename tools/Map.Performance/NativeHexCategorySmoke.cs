using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    /// <summary>
    /// `native-hex-category`: the hex constructor end to end in its own off-screen windows. The
    /// create dialog offers it as the second category and asks for a hex board instead of a pixel
    /// canvas; the editor opens with the hex rail and the hex panel only; a drag through the real
    /// canvas lays a line of forest as one undo step, a fill paints the empty rest of the board,
    /// a village stands on terrain; the map saves at schema v12 and opens back hex for hex.
    /// </summary>
    private async Task VerifyHexCategory()
    {
        var wizard = new WizardWindow(_catalog)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000),
        };
        wizard.Show(); await Settle();
        Check("step 1 lists the hex constructor as a second category",
            wizard.GetVisualDescendants().OfType<ToggleButton>().Any(b => b.Name == "WizardCategoryHex") && MapCategories.All.Count >= 2 && MapCategories.All[1] == MapCategories.Hex);
        Click(wizard, "WizardCategoryHex"); await Settle(); Capture(wizard, "hex-01-category");
        Check("choosing the card describes the hex constructor", wizard.SelectedCategory == MapCategories.Hex.Id
            && Text(wizard, "WizardCategoryTitle") == MapCategories.Hex.Title);
        Click(wizard, "ChooseCategoryButton"); await Settle();
        Check("step 2 asks for a hex board instead of a pixel canvas", Visible(wizard, "WizardHexBoard")
            && !Visible(wizard, "WizardPresetStandard") && !Visible(wizard, "WizardStartingPoint"));
        var boxes = wizard.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes.Single(t => t.Name == "WizardHexColumnsBox").Text = "24";
        boxes.Single(t => t.Name == "WizardHexRowsBox").Text = "16";
        boxes.Single(t => t.Name == "WizardTitleBox").Text = "Гексы · проверка";
        await Settle(); Capture(wizard, "hex-02-board");
        Check("the board hint names the canvas the board needs", Text(wizard, "WizardHexCanvasHint").Contains("×"));
        Click(wizard, "CreateMapButton");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (wizard.CreatedDocument is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        var document = wizard.CreatedDocument ?? throw new InvalidOperationException("The wizard did not create the hex map.");
        wizard.Close();
        var created = document.Layers.OfType<HexLayer>().Single().Board;
        Check("the created map is a 24 × 16 hex board of the hex category", document.Category == MapCategories.Hex.Id
            && created.Columns == 24 && created.Rows == 16 && created.CellCount == 0);

        _editor = new EditorViewModel(document, null, true, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view;
        await Settle();
        _view.Canvas.FitToCanvas(); await Settle();
        Capture(_view, "hex-03-editor");
        Check("the editor opens on the hex tool", _editor.ActiveTool == EditorTool.Hex && Visible(_view, "HexToolPanel"));
        Check("the rail is the hex rail only", Visible(_view, "HexToolButton") && Visible(_view, "TextToolButton")
            && !Visible(_view, "LandToolButton") && !Visible(_view, "RiverToolButton") && !Visible(_view, "RoadToolButton")
            && !Visible(_view, "AssetsLibraryButton") && !Visible(_view, "BrushToolButton"));
        Check("the palette shows every starter tile", HexStarterTiles.All.All(tile =>
            ReachableHex(_view, "HexTile_" + tile.Id[(tile.Id.IndexOf('/') + 1)..])));

        HexLayer Layer() => _editor.Session.Document.Layers.OfType<HexLayer>().Single();
        (double X, double Y) Screen(int column, int row)
        {
            var layout = Layer().Board.Layout;
            var (x, y) = layout.Center(layout.FromOffset(new HexOffset(column, row)));
            var point = _editor.Session.Viewport.WorldToScreen(x, y);
            return (point.X, point.Y);
        }

        PressHex(_view, "HexTile_forest"); await Settle();
        var depth = _editor.Session.UndoDepth;
        var (ax, ay) = Screen(2, 4);
        var (bx, by) = Screen(14, 4);
        _view.Canvas.PressMoveReleaseForSmoke(ax, ay, bx, by);
        await Settle(); Capture(_view, "hex-04-forest-line");
        var line = Layer().Board;
        Check("a drag through the canvas lays a line of forest", line.CellCount >= 12
            && line.Cells.All(pair => pair.Value.Terrain == "starter/forest"));
        Check("the whole drag is one undo step", _editor.Session.UndoDepth == depth + 1);

        PressHex(_view, "HexModeFill"); PressHex(_view, "HexTile_grass"); await Settle();
        PressHex(_view, "HexModeFill"); await Settle();
        var (fx, fy) = Screen(0, 15);
        _view.Canvas.ClickForSmoke(fx, fy);
        await Settle();
        var filled = Layer().Board;
        Check("a fill paints the empty rest of the board", filled.CellCount == 24 * 16
            && filled.Cells.Count(pair => pair.Value.Terrain == "starter/forest") == line.CellCount);

        PressHex(_view, "HexModePlace"); PressHex(_view, "HexTile_village"); await Settle();
        var (vx, vy) = Screen(8, 4);
        _view.Canvas.ClickForSmoke(vx, vy);
        PressHex(_view, "HexTile_mountains");
        _view.Canvas.PressMoveReleaseForSmoke(Screen(18, 9).Item1, Screen(18, 9).Item2, Screen(22, 12).Item1, Screen(22, 12).Item2);
        PressHex(_view, "HexTile_sea");
        _view.Canvas.PressMoveReleaseForSmoke(Screen(3, 11).Item1, Screen(3, 11).Item2, Screen(9, 14).Item1, Screen(9, 14).Item2);
        await Settle(); Capture(_view, "hex-05-painted");
        var village = Layer().Board[Layer().Board.Layout.FromOffset(new HexOffset(8, 4))];
        Check("a village stands on the forest it was put on", village is { Terrain: "starter/forest", Feature: "starter/village" });

        _editor.Session.Viewport.ZoomAt(Screen(8, 4).Item1, Screen(8, 4).Item2, 2.5);
        await Settle(); Capture(_view, "hex-06-close");

        var path = Path.Combine(_workspace, "hex-category.mapproj");
        MapProjectStore.Save(_editor.Session.Document, path, new SaveMetadata("smoke", DateTimeOffset.UtcNow), PreviewPng());
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
        {
            Check("a hex map saves at schema v12 with its category",
                manifest.RootElement.GetProperty("schemaVersion").GetInt32() == MapProjectFormat.HexLayersSchemaVersion
                && manifest.RootElement.GetProperty("category").GetString() == "hex");
        }

        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single().Board;
        Check("a hex map opens back hex for hex", reopened.CellCount == Layer().Board.CellCount
            && reopened.Cells.SequenceEqual(Layer().Board.Cells));

        _editor.Undo(); _editor.Undo(); _editor.Undo(); _editor.Undo();
        Check("undo takes the fill back and keeps the forest line", Layer().Board.CellCount == line.CellCount);
        _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
    }
}
