using System.Text.Json;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Platform.Windows;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    // R03: --profile native-export-wins / native-library-wins --output artifacts/r03/<name>.json.
    // Only the isolated workspace is used. No capture here: Map.LiveCheck takes PrintWindow shots.
    private Window R03Window(string name) => ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.Single(w => w.Name == name);
    private static T R03Control<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private async Task VerifyExportWins()
    {
        var document = PlayerPackageFixture();
        var destination = new PlayerPackageDestination(_workspace) { Recipient = "r03" };
        _editor = new EditorViewModel(document, null, true, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false), exportDestinationPicker: destination);
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view; await Settle();
        var json = """{"format":"map-hex-json","layers":[{"name":"Import","cells":[{"at":[14,10],"terrain":"starter/grass"},{"at":[1,1],"terrain":"missing/tile"}]}]}""";
        var importTask = _view.ShowHexImportForSmokeAsync(json); await Settle();
        var import = R03Window("HexImportWindow");
        R03Control<ComboBox>(import, "HexImportTargetComboBox").SelectedIndex = 1;
        Click(import, "HexImportApplyButton"); await Settle();
        var result = R03Window("HexImportResultWindow");
        Check("R03 import result reports imported skipped and unknown id", Text(result, "HexImportResultText").Contains("Импортировано 1 гексов, пропущено 1", StringComparison.Ordinal)
            && Text(result, "HexImportResultText").Contains("missing/tile", StringComparison.Ordinal));
        Check("R03 import creates selected new layer and expands canvas", _editor.Session.Document.Layers.OfType<HexLayer>().Count() == 2
            && _editor.ActiveHexLayer!.Board.Columns == 15 && _editor.Session.Document.Canvas.Width > document.Canvas.Width);
        Click(result, "HexImportResultCloseButton"); await importTask;
        _editor.Undo(); await Settle();
        Check("R03 import is one undo step", ReferenceEquals(document, _editor.Session.Document));
        _editor.Redo(); await Settle();
        Check("R03 import redo restores new layer", _editor.Session.Document.Layers.OfType<HexLayer>().Count() == 2);
        var importedPath = Path.Combine(_workspace, "imported.mapproj");
        MapProjectStore.Save(_editor.Session.Document, importedPath, new SaveMetadata("r03", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(_editor.Session.Document));
        var importedBack = MapProjectStore.Open(importedPath).Document;
        Check("R03 imported layer and enlarged canvas survive reopen", importedBack.Layers.OfType<HexLayer>().Count() == 2
            && importedBack.Layers.OfType<HexLayer>().Single(l => l.Level == -1).Board.Columns == 15
            && importedBack.Canvas == _editor.Session.Document.Canvas);
        _editor.Undo(); await Settle(); _editor.HexLevel = 0;
        Click(_view, "ExportButton"); await _editor.WaitForExportPreviewAsync(); await Settle();
        var export = _view.ExportWindowForSmoke!;
        R03Control<ComboBox>(export, "ExportSizeUnitComboBox").SelectedIndex = 1;
        R03Control<ToggleButton>(export, "ExportAspectToggle").IsChecked = false;
        R03Control<TextBox>(export, "ExportOutputWidthBox").Text = "210";
        R03Control<TextBox>(export, "ExportOutputHeightBox").Text = "297";
        R03Control<TextBox>(export, "ExportDpiBox").Text = "300";
        await _editor.WaitForExportPreviewAsync(); await Settle();
        Check("R03 mm and DPI controls produce A4 pixels", _editor.CurrentExportPlan is { OutputWidth: 2480, OutputHeight: 3508 });
        R03Control<ComboBox>(export, "ExportSizeUnitComboBox").SelectedIndex = 2;
        await _editor.WaitForExportPreviewAsync();
        Check("R03 inches preserve pixel size and aspect setting", !_editor.ExportAspectLocked && _editor.CurrentExportPlan is { OutputWidth: 2480, OutputHeight: 3508 });
        // Export a bounded real PNG through the shared planner, then both print formats.
        R03Control<TextBox>(export, "ExportDpiBox").Text = "30";
        await _editor.WaitForExportPreviewAsync(); await Settle();
        Click(export, "StartExportButton"); await WaitUntil(() => File.Exists(destination.File("png")), "R03 PNG");
        using (var png = SkiaSharp.SKBitmap.Decode(destination.File("png"))) Check("R03 actual PNG uses converted dimensions", png.Width == 248 && png.Height == 351);
        Click(export, "ExportPackagePrintButton"); await Settle();
        var print = R03Window("HexPrintWindow");
        R03Control<ComboBox>(print, "HexPrintPaperComboBox").SelectedIndex = 2;
        R03Control<CheckBox>(print, "HexPrintCropMarksToggle").IsChecked = true;
        R03Control<NumericUpDown>(print, "HexPrintBleedBox").Value = 3;
        Click(print, "HexPrintSaveButton"); await WaitUntil(() => File.Exists(destination.File("pdf")), "R03 Letter PDF");
        Check("R03 print controls apply Letter crop marks and bleed", _editor.HexPrintOptions is { PaperFormat: HexPaperFormat.Letter, CropMarks: true, BleedMillimetres: 3 });
        destination.Recipient = "r03-custom";
        R03Control<ComboBox>(print, "HexPrintPaperComboBox").SelectedIndex = 3;
        R03Control<NumericUpDown>(print, "HexPrintWidthBox").Value = 180;
        R03Control<NumericUpDown>(print, "HexPrintHeightBox").Value = 240;
        Click(print, "HexPrintSaveButton"); await WaitUntil(() => File.Exists(destination.File("pdf")), "R03 custom PDF");
        Check("R03 custom sheet controls are enabled and applied", R03Control<NumericUpDown>(print, "HexPrintWidthBox").IsEnabled
            && _editor.HexPrintOptions is { CustomWidthMillimetres: 180, CustomHeightMillimetres: 240 });
        Click(print, "HexPrintCloseButton"); Click(export, "CloseExportButton"); await Settle();
        var fixture = Path.Combine(_workspace, "hex.mapproj");
        MapProjectStore.Save(document, fixture, new SaveMetadata("r03", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(document));
        using var archive = ZipFile.OpenRead(fixture); using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Check("R03 fixture remains schema v14", manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 14);
        Check("R03 fixture reopens", MapProjectStore.Open(fixture).Document.Layers.OfType<HexLayer>().Single().Board.CellCount == 80);
        _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
    }

    private async Task VerifyLibraryWins()
    {
        var folder = Path.Combine(_workspace, "library"); var other = Path.Combine(_workspace, "other-library");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(other);
        var hex = PlayerPackageFixture();
        var standard = new MapDocument(DocumentId.New(), "Стандартная карта", DateTimeOffset.UnixEpoch,
            hex.Canvas, [hex.Layers[0]], category: MapCategoryId.Standard);
        foreach (var (doc, name) in new[] { (hex, "hex"), (standard, "standard") })
            MapProjectStore.Save(doc, Path.Combine(folder, name + ".mapproj"), new SaveMetadata("r03", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(doc));
        MapProjectStore.Save(standard, Path.Combine(other, "other.mapproj"), new SaveMetadata("r03", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(standard));
        var settings = new AppSettingsStore(Path.Combine(_workspace, "profile", "settings.json"));
        var model = new DashboardViewModel(_store, _recent, new LocalLog(), library: new MapLibrary(folder, Path.Combine(_workspace, "favorites.json")),
            persistLibraryFolder: p => settings.Save(settings.Load() with { MapsRoot = p }));
        model.Refresh();
        var view = new DashboardView(model, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
        _window.Content = view; await Settle();
        var badges = view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "LibraryCardCategoryBadge").Select(b => ((TextBlock)b.Child!).Text).ToArray();
        Check("R03 home shows category badges", badges.Contains("Стандартный") && badges.Contains("Гексовый"));
        R03Control<ComboBox>(view, "LibraryCategoryFilterHome").SelectedIndex = 2; await Settle();
        Check("R03 home category filter responds", model.LatestCards.Count == 1 && model.LatestCards[0].CategoryLabel == "Гексовый");
        Click(view, "RailMapsButton"); await Settle();
        R03Control<ComboBox>(view, "LibraryCategoryFilterMaps").SelectedIndex = 1; await Settle();
        Check("R03 library filter responds", model.RecentCards.Count == 1 && model.RecentCards[0].CategoryLabel == "Стандартный");
        Check("R03 folder picker command is reachable", R03Control<Button>(view, "LibraryFolderButton").IsEffectivelyVisible);
        Check("R03 changing library reloads list", model.ChangeLibraryFolder(other) && model.RecentCards.Count == 1 && model.RecentCards[0].Path.StartsWith(other, StringComparison.Ordinal));
        await Settle();
        Check("R03 library folder survives settings reload", new AppSettingsStore(settings.Path).Load().MapsRoot == other && Text(view, "LibraryFolderLabel").Contains(other, StringComparison.Ordinal));
        model.LibraryCategoryFilter = "hex";
        Click(view, "RailHomeButton"); await Settle();
        Check("R03 empty filter remains reachable", model.LatestCards.Count == 0 && R03Control<ComboBox>(view, "LibraryCategoryFilterHome").IsEffectivelyVisible);
        R03Control<ComboBox>(view, "LibraryCategoryFilterHome").SelectedIndex = 0; await Settle();
        Check("R03 clearing empty filter restores cards", model.LatestCards.Count == 1);
        Check("R03 old library files remain", File.Exists(Path.Combine(folder, "hex.mapproj")) && File.Exists(Path.Combine(folder, "standard.mapproj")));
    }
}
