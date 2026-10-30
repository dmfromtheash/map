using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    /// <summary>
    /// R13: --profile native-poster-export --output artifacts/r13/poster.json.
    /// Sequential native export controls for Standard and Hex, real PDFs and layer PNGs.
    /// Profile/recovery/shortcuts stay under the output workspace. No control/screen captures;
    /// Map.LiveCheck --poster-export true --map ... supplies PrintWindow images separately.
    /// </summary>
    private async Task VerifyPosterExport()
    {
        foreach (var hex in new[] { false, true })
        {
            var category = hex ? "hex" : "standard";
            var document = hex ? PlayerPackageFixture() : PosterStandardFixture();
            var destination = new PosterDestination(_workspace, category);
            _editor = new EditorViewModel(document, null, false, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
                assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false), exportDestinationPicker: destination);
            _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
            _window.Content = _view; await Settle(); _view.Canvas.FitToCanvas();
            Click(_view, "ExportButton"); await _editor.WaitForExportPreviewAsync(); await Settle();
            var export = _view.ExportWindowForSmoke!;
            var printName = hex ? "ExportPackagePrintButton" : "ExportPosterButton";
            var button = R03Control<Button>(export, printName);
            button.BringIntoView(); await Settle();
            Check("R13 poster button reachable " + category, button.IsEffectivelyVisible && button.IsEnabled);
            Click(export, printName); await Settle();
            var print = R03Window("HexPrintWindow");
            var paper = R03Control<ComboBox>(print, "HexPrintPaperComboBox");
            Check("R13 all seven paper formats " + category, paper.ItemCount == 7);
            R03Control<CheckBox>(print, "HexPrintKeyToggle").IsChecked = false;
            R03Control<CheckBox>(print, "HexPrintCropMarksToggle").IsChecked = true;
            R03Control<CheckBox>(print, "PosterPrintOverviewToggle").IsChecked = true;
            R03Control<NumericUpDown>(print, "HexPrintBleedBox").Value = 3;
            R03Control<NumericUpDown>(print, "HexPrintOverlapBox").Value = 12;
            if (!hex) R03Control<NumericUpDown>(print, "PosterPrintMapWidthBox").Value = 400;
            foreach (var format in new[] { HexPaperFormat.A0, HexPaperFormat.Letter, HexPaperFormat.Custom })
            {
                destination.Suffix = format.ToString();
                paper.SelectedIndex = (int)format;
                R03Control<NumericUpDown>(print, "HexPrintWidthBox").Value = 180;
                R03Control<NumericUpDown>(print, "HexPrintHeightBox").Value = 240;
                Click(print, "HexPrintSaveButton");
                await WaitUntil(() => File.Exists(destination.Pdf) && !_editor.ExportRunning, "R13 PDF " + category + " " + format);
                var text = Encoding.Latin1.GetString(File.ReadAllBytes(destination.Pdf));
                var layout = HexPrintExporter.Plan(new ExportScene(document, hex ? 0 : null), _editor.HexPrintOptions);
                Check("R13 PDF pages and overview " + category + " " + format,
                    Regex.Matches(text, @"/Type\s*/Page\b").Count == layout.Columns * layout.Rows + 1);
            }
            Check("R13 settings applied " + category, _editor.HexPrintOptions is { CropMarks: true, BleedMillimetres: 3,
                OverlapMillimetres: 12, PaperFormat: HexPaperFormat.Custom, CustomWidthMillimetres: 180, CustomHeightMillimetres: 240 });
            Click(print, "HexPrintCloseButton"); await Settle();
            R03Control<ToggleButton>(export, "ExportAspectToggle").IsChecked = false;
            R03Control<TextBox>(export, "ExportOutputWidthBox").Text = "640";
            R03Control<TextBox>(export, "ExportOutputHeightBox").Text = "480";
            await _editor.WaitForExportPreviewAsync(); await Settle();
            var layersButton = R03Control<Button>(export, "ExportLayersButton");
            layersButton.BringIntoView(); await Settle();
            Check("R13 layer button reachable " + category, layersButton.IsEffectivelyVisible && layersButton.IsEnabled);
            Click(export, "ExportLayersButton");
            await WaitUntil(() => Directory.Exists(destination.Folder) && !_editor.ExportRunning, "R13 layer PNGs " + category);
            var files = Directory.GetFiles(destination.Folder, "*.png").Order().ToArray();
            Check("R13 every visible layer exported " + category, files.Length == document.Layers.Count(l => l.Visible));
            Check("R13 PNGs share frame and dimensions " + category, files.All(f =>
            { using var png = SKBitmap.Decode(f); return png is { Width: 640, Height: 480 }; }));
            Check("R13 filenames retain order and layer name " + category,
                Path.GetFileName(files[0]).StartsWith("001-", StringComparison.Ordinal) && Path.GetFileName(files[1]).Contains(document.Layers[1].Name, StringComparison.Ordinal));
            Check("R13 snapshot and undo preserved " + category, ReferenceEquals(document, _editor.Session.Document) && _editor.Session.UndoDepth == 0);
            var fixture = Path.Combine(_workspace, category + ".mapproj");
            MapProjectStore.Save(document, fixture, new SaveMetadata("R13", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(document));
            Check("R13 fixture reopens " + category, MapProjectStore.Open(fixture).Document.Layers.Count == document.Layers.Count);
            Click(export, "CloseExportButton"); _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
            _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
        }
    }

    private static MapDocument PosterStandardFixture()
    {
        var islands = LandLayer.CreateEmpty(LayerId.New(), "Острова");
        foreach (var bounds in new[] { new WorldBounds(100, 90, 400, 330), new WorldBounds(430, 310, 550, 390) })
        {
            var shape = LandRasterizer.ApplyShape(islands.GeometryTiles, new LandShape { Kind = LandShapeKind.Ellipse, Bounds = bounds }, false);
            islands = islands with { GeometryTiles = islands.GeometryTiles.WithMany(shape.After) };
        }
        var mountain = OwnerMountainFixtures.Embedded(2);
        return new(DocumentId.New(), "Берега — постер", DateTimeOffset.UnixEpoch, new CanvasDefinition(640, 480, "px", "#FFFFFFFF"),
        [
            CurrentLook.Page("Фон", "#FFCAE5F2"),
            islands,
            new ObjectLayer(LayerId.New(), "Горы", true, false, 1,
                [OwnerMountainFixtures.Stamp(mountain, "Гора у порта", 190, 220, 48, 48),
                 OwnerMountainFixtures.Stamp(mountain, "Гора малого острова", 466, 318, 36, 36)]),
            new ObjectLayer(LayerId.New(), "Скрытый", false, false, 1, []),
        ], [mountain]);
    }

    private sealed class PosterDestination(string root, string category) : IExportDestinationPicker
    {
        public string Suffix { get; set; } = "poster";
        public string Pdf => Path.Combine(root, category + "-" + Suffix + ".pdf");
        public string Folder => Path.Combine(root, category + "-layers");
        public Task<string?> PickImageAsync(string name, ExportImageFormat format) => Task.FromResult<string?>(null);
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult<string?>(Folder);
        public Task<string?> PickFileAsync(string name, string extension, string description) => Task.FromResult<string?>(Pdf);
    }
}
