using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
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
    /// R02: dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll
    /// --profile native-player-package --output artifacts/r02/player-package.json
    /// Isolated profile/recovery/shortcuts under that output's workspace. No screen captures:
    /// Map.LiveCheck --map &lt;workspace&gt;/player-package.mapproj --player-package true takes PrintWindow shots.
    /// </summary>
    private async Task VerifyPlayerPackage()
    {
        var document = PlayerPackageFixture();
        var destination = new PlayerPackageDestination(_workspace);
        _editor = new EditorViewModel(document, null, true, _store, _recent, _picker, new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false),
            exportDestinationPicker: destination);
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view;
        await Settle();
        _view.Canvas.FitToCanvas();
        Click(_view, "ExportButton");
        await _editor.WaitForExportPreviewAsync();
        await Settle();
        var export = _view.ExportWindowForSmoke!;
        var recipient = export.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ExportRecipientComboBox");
        Check("R02 recipient starts as game master", recipient.SelectedIndex == 0);
        recipient.SelectedIndex = 1;
        await _editor.WaitForExportPreviewAsync();
        await Settle();
        var panel = export.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Name == "ExportPackagePreviewPanel");
        var notes = export.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "ExportPackagePreviewNotesText");
        Check("R02 player selection opens the package preview", _editor.ExportHexPlayers && panel.IsEffectivelyVisible);
        Check("R02 preview counts public private and fogged notes", notes.Text == "Заметки: всего 3 · в пакете 1 · скрыто 2"
            && _editor.PlayerPackagePreview.FogNotes == 1 && _editor.PlayerPackagePreview.FogHexes == 60);

        async Task Export(string button, string extension)
        {
            var control = export.GetVisualDescendants().OfType<Button>().Single(c => c.Name == button);
            control.BringIntoView(); await Settle();
            Click(export, button);
            await WaitUntil(() => File.Exists(destination.File(extension)), button);
            await Settle();
        }
        await Export("ExportPackageJsonButton", "json");
        await Export("ExportPackageMarkdownButton", "md");
        foreach (var extension in new[] { "json", "md" })
        {
            var text = File.ReadAllText(destination.File(extension));
            Check("R02 player " + extension + " has public text and no secrets", text.Contains("Знакомая переправа", StringComparison.Ordinal)
                && !new[] { "GM_SECRET", "GM_TITLE", "HIDDEN_TEXT", "HIDDEN_TITLE", "secret.mapproj", "childMap", "\"gm\"" }
                    .Any(secret => text.Contains(secret, StringComparison.Ordinal)));
        }
        await Export("StartVttExportButton", "dd2vtt");
        using var vtt = JsonDocument.Parse(File.ReadAllBytes(destination.File("dd2vtt")));
        Check("R02 player dd2vtt contains only the revealed wall", vtt.RootElement.GetProperty("line_of_sight").GetArrayLength() == 1);
        await Export("StartExportButton", "png");
        var png = File.ReadAllBytes(destination.File("png"));
        Check("R02 player dd2vtt image equals player PNG", png.SequenceEqual(vtt.RootElement.GetProperty("image").GetBytesFromBase64()));
        Click(export, "ExportPackagePrintButton"); await Settle();
        var print = R03Window("HexPrintWindow");
        Click(print, "HexPrintSaveButton");
        await WaitUntil(() => File.Exists(destination.File("pdf")), "player PDF");
        Click(print, "HexPrintCloseButton"); await Settle();
        Check("R02 player PDF was written", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(destination.File("pdf")), 0, 4) == "%PDF");

        destination.Recipient = "gm";
        recipient.SelectedIndex = 0;
        await _editor.WaitForExportPreviewAsync(); await Settle();
        await Export("ExportPackageJsonButton", "json");
        await Export("StartExportButton", "png");
        var gm = File.ReadAllText(destination.File("json"));
        Check("R02 master JSON retains GM text hidden entries and child map", gm.Contains("GM_SECRET", StringComparison.Ordinal)
            && gm.Contains("HIDDEN_TEXT", StringComparison.Ordinal) && gm.Contains("secret.mapproj", StringComparison.Ordinal));
        using var playerImage = SKBitmap.Decode(png);
        using var gmImage = SKBitmap.Decode(destination.File("png"));
        var layer = document.Layers.OfType<HexLayer>().Single();
        var (x, y) = layer.Board.Layout.Center(layer.Board.Layout.FromOffset(new(7, 5)));
        Check("R02 hidden PNG pixel is opaque fog and differs from GM", playerImage.GetPixel((int)x, (int)y) == new SKColor(0x22, 0x20, 0x26)
            && playerImage.GetPixel((int)x, (int)y) != gmImage.GetPixel((int)x, (int)y));
        Check("R02 exporting preserves document and undo history", ReferenceEquals(document, _editor.Session.Document) && _editor.Session.UndoDepth == 0);
        Click(export, "CloseExportButton"); await Settle();

        var fixture = Path.Combine(_workspace, "player-package.mapproj");
        MapProjectStore.Save(document, fixture, new SaveMetadata("r02-smoke", DateTimeOffset.UtcNow), ThumbnailRenderer.RenderPng(document));
        var reopened = MapProjectStore.Open(fixture).Document;
        Check("R02 fixture reopens with private data still in owner document", reopened.Layers.OfType<HexLayer>().Single().Notes.Count == 3);
        using var archive = ZipFile.OpenRead(fixture);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Check("R02 does not introduce v15", manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 14);
        _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
    }

    private static MapDocument PlayerPackageFixture()
    {
        var doc = HexMapFactory.Create("Долина переправы", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Pointy, 40, 10, 8, "#FFF2E9DC");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        HexCoord H(int c, int r) => layout.FromOffset(new(c, r));
        var cells = from c in Enumerable.Range(0, 10) from r in Enumerable.Range(0, 8)
            select new KeyValuePair<HexCoord, HexCell?>(H(c, r), HexCell.Of(c > 6 ? "starter/peaks" : "starter/grass", revealed: c < 5 && r < 4));
        layer = layer with
        {
            FogEnabled = true, Board = layer.Board.With(cells),
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty
                .Add(H(2, 2), new("Переправа", gmText: "GM_SECRET", playerText: "Знакомая переправа", childMap: "secret.mapproj"))
                .Add(H(3, 2), new("GM_TITLE", gmText: "GM_SECRET"))
                .Add(H(7, 5), new("HIDDEN_TITLE", playerText: "HIDDEN_TEXT")),
            Paths =
            [
                new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [H(1, 1), H(2, 1), H(3, 1)], []) { BlocksSight = true },
                new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [H(7, 5), H(8, 5)], []) { BlocksSight = true },
            ],
        };
        return doc.WithLayer(layer.Id, _ => layer);
    }

    private sealed class PlayerPackageDestination(string root) : IExportDestinationPicker
    {
        public string Recipient { get; set; } = "player";
        public string File(string extension) => Path.Combine(root, Recipient + "." + extension);
        public Task<string?> PickImageAsync(string name, ExportImageFormat format) => Task.FromResult<string?>(File("png"));
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult<string?>(Path.Combine(root, Recipient + "-tiles"));
        public Task<string?> PickVttAsync(string name) => Task.FromResult<string?>(File("dd2vtt"));
        public Task<string?> PickFileAsync(string name, string extension, string description) => Task.FromResult<string?>(File(extension));
    }
}
