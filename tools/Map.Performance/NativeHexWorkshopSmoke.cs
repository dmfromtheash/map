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
    /// `native-hex-workshop`: every workshop tool of the hex category through the real panel and
    /// the real canvas - tiles that turn, tall and living tiles, a region, a river and a road with
    /// its bridge, the fog of war, a hex key typed into the panel, the travel ruler, a selection
    /// copied and pasted, inserted columns, an underground level, the players' window - then the
    /// map saves at schema v13 and opens back the same.
    /// </summary>
    /// <summary>A property toggle as the user flips it (a raised Click does not flip a toggle).</summary>
    private static void Toggle(Control root, string name)
    {
        var toggle = root.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == name);
        if (!toggle.IsEffectivelyEnabled || !toggle.IsEffectivelyVisible) throw new InvalidOperationException("Toggle not available to the user: " + name);
        toggle.IsChecked = toggle.IsChecked != true;
    }

    private async Task VerifyHexWorkshop()
    {
        var document = HexMapFactory.Create("Мастерская · проверка", DateTimeOffset.UtcNow, MapCategories.Hex.Id,
            HexOrientation.Flat, 48, 30, 20, "#FFF2E9DC");
        _editor = new EditorViewModel(document, null, true, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view;
        await Settle();
        _view.Canvas.FitToCanvas(); await Settle();

        HexLayer Layer(int level = 0) => _editor.Session.Document.Layers.OfType<HexLayer>().Single(l => l.Level == level);
        (double X, double Y) Screen(int column, int row)
        {
            var layout = Layer().Board.Layout;
            var (x, y) = layout.Center(layout.FromOffset(new HexOffset(column, row)));
            var point = _editor.Session.Viewport.WorldToScreen(x, y);
            return (point.X, point.Y);
        }

        void Drag(int c0, int r0, int c1, int r1) =>
            _view.Canvas.PressMoveReleaseForSmoke(Screen(c0, r0).X, Screen(c0, r0).Y, Screen(c1, r1).X, Screen(c1, r1).Y);
        void ClickHex(int c, int r) => _view.Canvas.ClickForSmoke(Screen(c, r).X, Screen(c, r).Y);
        HexCoord Hex(int c, int r) => Layer().Board.Layout.FromOffset(new HexOffset(c, r));

        Check("the panel offers the terrain and the workshop modes", new[] { "Place", "Fill", "Erase", "Pick", "Region", "Path", "Fog", "Key", "Measure", "Select" }
            .All(mode => Visible(_view, "HexMode" + mode)));

        // Ground: grass everywhere, a sea on the left, tall peaks and a living forest.
        Click(_view, "HexModeFill"); Click(_view, "HexTile_grass"); Click(_view, "HexModeFill"); await Settle();
        ClickHex(15, 10); await Settle();
        Check("a fill covers the board", Layer().Board.CellCount == 600);
        Click(_view, "HexModePlace"); Click(_view, "HexTile_sea"); await Settle();
        _editor.HexRadius = 1;
        Drag(1, 0, 1, 19); Drag(3, 0, 3, 19); await Settle();
        Click(_view, "HexTile_peaks"); _editor.HexRadius = 0;
        Drag(20, 2, 26, 2); Drag(21, 3, 25, 3); await Settle();
        Click(_view, "HexTile_live-forest"); _editor.HexRadius = 2;
        Drag(18, 13, 24, 14); await Settle();
        Click(_view, "HexTile_canyon"); _editor.HexRadius = 0; await Settle();
        Click(_view, "HexRotateRightButton"); Click(_view, "HexRotateRightButton"); await Settle();
        ClickHex(10, 16); await Settle();
        Check("the turn buttons turn the next tile", Layer().Board[Hex(10, 16)] is { Terrain: "starter/canyon", Rotation: 2 });
        Check("a tall tile is painted", Layer().Board[Hex(22, 2)]?.Terrain == "starter/peaks");
        Capture(_view, "workshop-01-ground");

        // A region painted through the canvas.
        Click(_view, "HexModeRegion"); await Settle();
        Click(_view, "HexAddRegionButton"); await Settle();
        _editor.HexRadius = 2;
        Drag(8, 5, 14, 9); await Settle();
        var region = Layer().Regions.Single();
        Check("a region is created and painted with the brush", Layer().Board.Cells.Count(p => p.Value.Region == region.Id) >= 19);

        // A river and a road crossing it.
        Click(_view, "HexModePath"); Click(_view, "HexPathKindRiver"); await Settle();
        Drag(24, 5, 5, 12); await Settle();
        Click(_view, "HexPathKindRoad"); await Settle();
        Drag(12, 1, 12, 18); await Settle();
        var paths = Layer().Paths;
        Check("a river and a road are drawn as paths", paths.Count == 2 && paths.Any(p => p.Kind == HexPathKind.River) && paths.Any(p => p.Kind == HexPathKind.Road));
        Check("the road crosses the river (a bridge)", paths[0].Hexes.Intersect(paths[1].Hexes).Any());
        _editor.Session.Viewport.ZoomAt(Screen(12, 8).X, Screen(12, 8).Y, 2.2);
        await Settle(); await Settle(); Capture(_view, "workshop-02-river-road-region");
        _view.Canvas.FitToCanvas(); await Settle();

        // The fog of war.
        Click(_view, "HexModeFog"); await Settle();
        Toggle(_view, "HexFogToggle"); await Settle();
        Check("the fog switch turns the fog on", Layer().FogEnabled);
        _editor.HexRadius = 3;
        Drag(8, 8, 14, 8); await Settle();
        var revealed = Layer().Board.Cells.Count(p => p.Value.Revealed);
        Check("the fog brush reveals hexes", revealed >= 37);
        Capture(_view, "workshop-03-fog");

        // A hex key typed into the panel.
        Click(_view, "HexModeKey"); await Settle();
        ClickHex(12, 8); await Settle();
        var boxes = _view.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes.Single(b => b.Name == "HexKeyTitleBox").Text = "Брод";
        boxes.Single(b => b.Name == "HexKeyGmBox").Text = "Под мостом живёт тролль.";
        boxes.Single(b => b.Name == "HexKeyPlayerBox").Text = "Старый мост через реку.";
        Click(_view, "HexKeySaveButton"); await Settle();
        Check("the key typed into the panel is stored on its hex", Layer().Notes.TryGetValue(Hex(12, 8), out var note)
            && note.Title == "Брод" && note.GmText.Contains("тролль", StringComparison.Ordinal) && note.PlayerText.Length > 0);
        Check("the key table lists it", Visible(_view, "HexKeyRow_" + HexLayerRenderer.NumberOf(Layer().Board, new HexOffset(12, 8))));

        // The travel ruler.
        Click(_view, "HexModeMeasure"); await Settle();
        Drag(6, 10, 28, 10); await Settle();
        Check("the ruler names hexes, distance and days", _editor.HexRoute is { Steps: > 0 } && Text(_view, "HexRouteText").Contains("дн.", StringComparison.Ordinal));
        Capture(_view, "workshop-04-ruler");

        // Selection, copy and paste, inserted columns.
        Click(_view, "HexModeSelect"); await Settle();
        Drag(20, 1, 24, 4); await Settle();
        var selected = _editor.HexSelection.Count;
        Check("a drag selects a rectangle of hexes", selected >= 15);
        Click(_view, "HexCopyButton"); Click(_view, "HexPasteButton"); await Settle();
        _editor.UpdateHexHover(Layer().Board.Layout.Center(Hex(22, 16)).X, Layer().Board.Layout.Center(Hex(22, 16)).Y);
        var depth = _editor.Session.UndoDepth;
        ClickHex(22, 16); await Settle();
        var pastedPeaks = Map.Core.Editing.HexSelection.Rectangle(Layer().Board, Hex(19, 13), Hex(25, 19)).Count(h => Layer().Board[h]?.Terrain == "starter/peaks");
        Check("a paste is one undo step and puts the copied peaks down", _editor.Session.UndoDepth == depth + 1 && !_editor.HasHexPaste && pastedPeaks >= 5);
        Click(_view, "HexInsertColumnsButton"); await Settle();
        Check("inserting columns widens every hex layer and the page", Layer().Board.Columns == 32
            && _editor.Session.Document.Canvas.Width >= (int)Layer().Board.PixelWidth - 1);
        Capture(_view, "workshop-05-select-paste");

        // An underground level on the same grid.
        Click(_view, "HexAddLevelBelowButton"); await Settle();
        Check("a level is added under the surface", _editor.HexLevel == -1 && Visible(_view, "HexLevel_m1"));
        Click(_view, "HexModePlace"); Click(_view, "HexTile_mountains"); _editor.HexRadius = 1; await Settle();
        Drag(10, 6, 16, 6); await Settle();
        Check("the level holds its own hexes", Layer(-1).Board.CellCount > 0 && Layer(0).Board[Hex(12, 6)]?.Terrain != "starter/mountains");
        Capture(_view, "workshop-06-underground");
        Click(_view, "HexLevel_0"); await Settle();
        Check("switching back shows the surface", _editor.HexLevel == 0 && _editor.ActiveHexLayer?.Level == 0);

        // The players' window.
        var player = new HexPlayerWindow(_editor)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000),
        };
        player.Show(); await Settle(); await Task.Delay(400); await Settle();
        Capture(player, "workshop-07-player");
        Check("the players' window opens", player.IsVisible);
        player.Close();

        var path = Path.Combine(_workspace, "hex-workshop.mapproj");
        MapProjectStore.Save(_editor.Session.Document, path, new SaveMetadata("smoke", DateTimeOffset.UtcNow), PreviewPng());
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
        {
            Check("a workshop map saves at schema v13", manifest.RootElement.GetProperty("schemaVersion").GetInt32() == MapProjectFormat.HexWorkshopSchemaVersion);
        }

        var reopened = MapProjectStore.Open(path).Document;
        var surface = reopened.Layers.OfType<HexLayer>().Single(l => l.Level == 0);
        Console.WriteLine($"reopen: cells {surface.Board.Cells.SequenceEqual(Layer().Board.Cells)} paths {surface.Paths.Count} regions {surface.Regions.Count} notes {surface.Notes.Count} fog {surface.FogEnabled}");
        Check("it opens back with its hexes, paths, regions, keys, fog and level", surface.Board.Cells.SequenceEqual(Layer().Board.Cells)
            && surface.Paths.Count == 2 && surface.Regions.Count == 1 && surface.Notes.Count == 1 && surface.FogEnabled
            && reopened.Layers.OfType<HexLayer>().Any(l => l.Level == -1));
        _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
    }
}
