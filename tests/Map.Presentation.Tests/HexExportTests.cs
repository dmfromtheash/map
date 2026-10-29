using System.Collections.Immutable;
using System.Text;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class HexExportTests
{
    private static (MapDocument Document, HexLayer Layer) World()
    {
        var document = HexMapFactory.Create("Экспорт", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Pointy, 40, 24, 16, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var world = HexWorldGenerator.RunAll(layer, new HexGeneratorSettings { Seed = 21, SettlementsPerThousand = 15 });
        var layout = world.Board.Layout;
        world = world with
        {
            Regions = [new HexRegion(1, "Север", "#FF3060B0")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty.Add(layout.FromOffset(new HexOffset(5, 5)),
                new HexNote("Башня", HexNoteCategory.Landmark, "Секрет мага", "Высокая башня", "tower.mapproj")),
            Paths = world.Paths.Add(new HexPath(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
                [.. HexPaths.EdgeWalk(layout, new HexVertex(layout.FromOffset(new HexOffset(2, 2)), 0), new HexVertex(layout.FromOffset(new HexOffset(2, 9)), 2))]) { BlocksSight = true }),
            FogEnabled = true,
        };
        return (document.WithLayer(layer.Id, _ => world), world);
    }

    [Fact]
    public void The_hex_json_goes_out_and_comes_back_the_same()
    {
        var (document, layer) = World();
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "world.hex.json");
        HexJsonFormat.Write(document, path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"format\": \"map-hex-json\"", text);
        Assert.Contains("Башня", text);
        var empty = layer with { Board = HexBoard.FromCells(layer.Board.Layout, layer.Board.Columns, layer.Board.Rows, []), Paths = [], Notes = ImmutableDictionary<HexCoord, HexNote>.Empty, Regions = [] };
        var back = HexJsonFormat.Read(text, empty);
        Assert.Equal(layer.Board.Cells.ToList(), back.Board.Cells.ToList());
        Assert.Equal(layer.Paths.Count, back.Paths.Count);
        Assert.Equal(layer.Regions, back.Regions);
        Assert.Equal(layer.Notes.Values.Single(), back.Notes.Values.Single());
        Assert.Throws<InvalidDataException>(() => HexJsonFormat.Read("{\"format\":\"other\"}", empty));
    }

    [Fact]
    public void Hex_json_from_2534315_reads_and_writes_identical_bytes()
    {
        // Produced by the unmodified HexJsonExporter at 2534315 before the type/file rename.
        using var fixture = typeof(HexExportTests).Assembly.GetManifestResourceStream(
            "Map.Presentation.Tests.Fixtures.hex-json-2534315.json")!;
        using var bytes = new MemoryStream();
        fixture.CopyTo(bytes);
        var expected = bytes.ToArray();
        var document = HexMapFactory.Create("Совместимость 2534315", DateTimeOffset.UnixEpoch,
            new MapCategoryId("hex"), HexOrientation.Pointy, 16, 4, 3, "#FFF2E9DC");
        var target = document.Layers.OfType<HexLayer>().Single();
        var result = HexJsonFormat.ReadWithReport(Encoding.UTF8.GetString(expected), target);

        Assert.Equal("map-hex-json", HexJsonFormat.Format);
        Assert.Equal(1, HexJsonFormat.Version);
        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.SkippedPaths);
        Assert.Equal(0, result.SkippedNotes);
        Assert.Single(result.Layer.Paths);
        Assert.Single(result.Layer.Regions);
        Assert.Equal("Секрет мага", Assert.Single(result.Layer.Notes).Value.GmText);
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "compatibility.hex.json");
        HexJsonFormat.Write(document.WithLayer(target.Id, _ => result.Layer), path);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_print_pdf_holds_one_page_per_sheet_and_the_key()
    {
        var (document, layer) = World();
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "print.pdf");
        var key = layer.Notes.Select(pair => ("0606", pair.Value)).ToList();
        var result = HexPrintExporter.Write(document, path, new HexPrintOptions(HexMillimetres: 30), key);
        Assert.True(result.MapPages >= 2, $"{result.MapPages} sheets");
        Assert.Equal(1, result.KeyPages);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        var pages = System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(bytes), @"/Type\s*/Page\b").Count;
        Assert.Equal(result.TotalPages, pages);
    }

    [Fact]
    public void Edge_paths_marked_as_sight_blockers_become_walls_and_the_foundry_grid_follows_the_orientation()
    {
        var (document, _) = World();
        var walls = VttGeometry.Collect(document);
        Assert.Single(walls.Walls);
        Assert.True(walls.Walls[0].Count >= 4);
        Assert.Equal(2, EditorViewModel.FoundryGridType(HexOrientation.Pointy).Type);
        Assert.Equal(4, EditorViewModel.FoundryGridType(HexOrientation.Flat).Type);
    }

    [Fact]
    public async Task A_hex_map_exports_for_tabletops_with_hex_cells_walls_and_a_foundry_hint()
    {
        var (document, layer) = World();
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "hex.dd2vtt");
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), imageExporter: new ImageExportEngine(), exportDestinationPicker: new Picker(path));
        editor.ActiveTool = EditorTool.Hex;
        editor.BeginExport();
        await editor.WaitForExportPreviewAsync();
        Assert.Equal(layer.Board.Layout.Size * HexLayout.Sqrt3, editor.ExportVttGridSize, 3);
        Assert.Contains("Стен: 1", editor.ExportVttSummary);
        Assert.True(await editor.StartVttExportAsync());
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var resolution = json.RootElement.GetProperty("resolution");
        Assert.True(resolution.GetProperty("pixels_per_grid").GetInt32() > 0);
        Assert.Single(json.RootElement.GetProperty("line_of_sight").EnumerateArray());
        var hint = File.ReadAllText(Path.ChangeExtension(path, ".foundry.txt"));
        Assert.Contains("type: 2", hint);

        // The players' export: the fog is opaque where the master's picture shows land.
        var gm = Decode(json.RootElement.GetProperty("image").GetBytesFromBase64());
        editor.ExportHexPlayers = true;
        await editor.WaitForExportPreviewAsync();
        Assert.True(await editor.StartVttExportAsync());
        using var players = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var hidden = Decode(players.RootElement.GetProperty("image").GetBytesFromBase64());
        // The fog is the owner's «Облака» mist (2026-10-22), not a dark fill: the players' picture
        // differs from the master's over the hidden land.
        Assert.True(Differs(hidden, gm) > 0.2, $"share of changed pixels {Differs(hidden, gm):0.00}");
    }

    private static double Differs(SKBitmap a, SKBitmap b)
    {
        var changed = 0; var total = 0;
        for (var y = 0; y < Math.Min(a.Height, b.Height); y += 3)
            for (var x = 0; x < Math.Min(a.Width, b.Width); x += 3)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                total++;
                if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > 40) changed++;
            }
        return total == 0 ? 0 : (double)changed / total;
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png);

    /// <summary>Share of near-black (fog) pixels.</summary>
    private static double Darkness(SKBitmap bitmap)
    {
        var dark = 0;
        var total = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                var c = bitmap.GetPixel(x, y);
                total++;
                if (c.Red < 0x30 && c.Green < 0x30 && c.Blue < 0x30) dark++;
            }
        }

        return dark / (double)total;
    }

    private sealed class Picker(string path) : IExportDestinationPicker
    {
        public Task<string?> PickImageAsync(string suggestedFileName, ExportImageFormat format) => Task.FromResult<string?>(null);
        public Task<string?> PickTilePackageAsync(string suggestedFolderName) => Task.FromResult<string?>(null);
        public Task<string?> PickVttAsync(string suggestedFileName) => Task.FromResult<string?>(path);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-hex-export-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
