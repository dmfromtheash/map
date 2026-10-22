using System.Collections.Immutable;
using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PlayerPackageExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-r02-").FullName;
    private static ImageExportEngine Engine() => new(new ExportEngineSettings
    {
        AvailableMemoryBytes = () => 2L * 1024 * 1024 * 1024,
        AvailableDiskBytes = _ => 2L * 1024 * 1024 * 1024,
    });

    [Fact]
    public async Task Recipient_is_honoured_by_JSON_and_both_MD_commands_and_master_keeps_original_data()
    {
        var doc = World();
        var editor = Editor(doc);
        editor.ExportHexPlayers = true;
        var json = await editor.ExportHexJsonAsync();
        AssertPublic(File.ReadAllText(json!));
        Assert.Single(JsonDocument.Parse(File.ReadAllText(json!)).RootElement.GetProperty("layers").EnumerateArray());
        foreach (var explicitPlayer in new[] { false, true })
        {
            var md = await editor.ExportHexKeyAsync(explicitPlayer);
            AssertPublic(File.ReadAllText(md!));
        }
        editor.ExportHexPlayers = false;
        json = await editor.ExportHexJsonAsync();
        Assert.Contains("GM_SECRET", File.ReadAllText(json!));
        Assert.Contains("GM_TITLE", File.ReadAllText(json!));
        Assert.Contains("childMap", File.ReadAllText(json!));
        Assert.Contains("secret.mapproj", File.ReadAllText(json!));
        Assert.Contains("HIDDEN_TEXT", File.ReadAllText(json!));
        Assert.Contains("GM_SECRET", editor.HexKeyMarkdown(false));
        Assert.Contains("UNDERGROUND", File.ReadAllText(json!));
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Same(doc, editor.Session.Document);
    }

    [Fact]
    public async Task Player_vtt_has_only_public_walls_and_exactly_the_player_png_with_opaque_fog()
    {
        var doc = World();
        var engine = Engine();
        var player = Plan(engine, doc, true);
        var gm = Plan(engine, doc, false);
        var png = Path.Combine(_root, "player.png");
        var gmPng = Path.Combine(_root, "gm.png");
        await engine.ExportAsync(doc, player, png, null);
        await engine.ExportAsync(doc, gm, gmPng, null);
        var vtt = Path.Combine(_root, "player.dd2vtt");
        var result = await UniversalVttExporter.ExportAsync(engine, doc, player, vtt, null, 40);
        Assert.Equal(1, result.Walls);
        using var json = JsonDocument.Parse(File.ReadAllText(vtt));
        Assert.Single(json.RootElement.GetProperty("line_of_sight").EnumerateArray());
        Assert.Equal(File.ReadAllBytes(png), json.RootElement.GetProperty("image").GetBytesFromBase64());
        Assert.DoesNotContain("GM_", File.ReadAllText(vtt));
        Assert.DoesNotContain("HIDDEN_", File.ReadAllText(vtt));
        Assert.DoesNotContain("childMap", File.ReadAllText(vtt));
        using var playerImage = SKBitmap.Decode(png);
        using var gmImage = SKBitmap.Decode(gmPng);
        var layer = doc.Layers.OfType<HexLayer>().First();
        var (x, y) = layer.Board.Layout.Center(H(layer, 5));
        // Opaque «Облака» mist (owner 2026-10-22): blue-grey, fully covering the hidden hex.
        var fog = playerImage.GetPixel((int)x, (int)y);
        Assert.True(fog.Alpha == 255 && fog.Blue >= fog.Red && fog.Blue is > 90 and < 220, $"player fog {fog}");
        Assert.NotEqual(gmImage.GetPixel((int)x, (int)y), playerImage.GetPixel((int)x, (int)y));
        var gmVtt = await UniversalVttExporter.ExportAsync(engine, doc, gm, Path.Combine(_root, "gm.dd2vtt"), null, 40);
        Assert.Equal(2, gmVtt.Walls);
    }

    [Fact]
    public async Task Changing_only_secret_content_cannot_change_player_JSON_or_any_PNG_pixel()
    {
        var doc = World();
        var layer = doc.Layers.OfType<HexLayer>().First();
        var changed = doc.WithLayer(layer.Id, _ => layer with
        {
            Board = layer.Board.With([new(H(layer, 5), HexCell.Of("starter/sea", feature: "starter/peaks", region: 2))]),
            Notes = layer.Notes.SetItem(H(layer, 2), layer.Notes[H(layer, 2)] with { GmText = "DIFFERENT", ChildMap = "different.mapproj" })
                .SetItem(H(layer, 3), new("DIFFERENT GM", gmText: "DIFFERENT"))
                .SetItem(H(layer, 5), new("DIFFERENT HIDDEN", playerText: "DIFFERENT")),
            Regions = [new(2, "DIFFERENT hidden region", "#FF00FF00")],
            Paths = layer.Paths.SetItem(1, layer.Paths[1] with { Name = "DIFFERENT", Width = 4 }),
        });
        Assert.Equal(HexJsonExporter.ToJson(new ExportScene(doc, 0, ExportRecipient.Players)).ToJsonString(),
            HexJsonExporter.ToJson(new ExportScene(changed, 0, ExportRecipient.Players)).ToJsonString());
        var engine = Engine();
        var a = Path.Combine(_root, "a.png");
        var b = Path.Combine(_root, "b.png");
        await engine.ExportAsync(doc, Plan(engine, doc, true), a, null);
        await engine.ExportAsync(changed, Plan(engine, changed, true), b, null);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void Player_print_rebuilds_key_from_scene_instead_of_trusting_supplied_GM_or_hidden_entries()
    {
        var doc = World();
        var path = Path.Combine(_root, "player.pdf");
        var hostileKey = new[] { ("9999", new HexNote("GM INJECTED", playerText: "GM INJECTED")) };
        var options = new HexPrintOptions(Players: true, Level: -1);
        var result = HexPrintExporter.Write(doc, path, options, hostileKey);
        Assert.Equal(0, result.KeyPages); // Underground only has GM content.
        Assert.True(result.MapPages > 0);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        var master = HexPrintExporter.Write(doc, Path.Combine(_root, "gm.pdf"), options with { Players = false }, hostileKey);
        Assert.Equal(1, master.KeyPages);
    }

    [Fact]
    public void Selected_level_changes_preview_and_player_key_without_changing_the_workshop_level()
    {
        var editor = Editor(World());
        editor.ExportHexPlayers = true;
        Assert.Equal(3, editor.PlayerPackagePreview.NotesTotal);
        Assert.Equal(1, editor.PlayerPackagePreview.NotesIncluded);
        Assert.Equal(1, editor.PlayerPackagePreview.FogNotes);
        Assert.Contains("PUBLIC_TEXT", editor.HexKeyMarkdown(false));
        editor.ExportHexLevel = -1;
        Assert.Equal(1, editor.PlayerPackagePreview.NotesTotal);
        Assert.Equal(0, editor.PlayerPackagePreview.NotesIncluded);
        Assert.DoesNotContain("PUBLIC_TEXT", editor.HexKeyMarkdown(false));
        Assert.Equal(0, editor.HexLevel);
    }

    private static void AssertPublic(string text)
    {
        Assert.Contains("PUBLIC_TEXT", text);
        Assert.Contains("PUBLIC_TITLE", text);
        foreach (var secret in new[] { "GM_SECRET", "GM_TITLE", "HIDDEN_TITLE", "HIDDEN_TEXT", "HIDDEN_REGION", "HIDDEN_PATH", "secret.mapproj", "childMap", "\"gm\"", "UNDERGROUND" })
            Assert.DoesNotContain(secret, text);
    }

    private EditorViewModel Editor(MapDocument doc) => new(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(),
        new FakeFilePicker(), new FakePrompts(), new FakeLog(), imageExporter: Engine(), exportDestinationPicker: new Picker(_root));

    private static ExportPlan Plan(ImageExportEngine engine, MapDocument doc, bool players) => engine.Plan(doc,
        new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, doc.Canvas.Width, doc.Canvas.Height,
            ExportOutputMode.SingleImage, false, false) { Hex = new(players, 0) });

    private static HexCoord H(HexLayer layer, int c) => layer.Board.Layout.FromOffset(new(c, 2));

    private static MapDocument World()
    {
        var doc = HexMapFactory.Create("Player package", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Pointy, 30, 8, 6, "#FFF2E9DC");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        HexPath Wall(int c, string name) => new(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers,
            [H(layer, c), H(layer, c + 1)], [], name: name) { BlocksSight = true };
        layer = layer with
        {
            FogEnabled = true,
            Board = layer.Board.With([new(H(layer, 2), HexCell.Of("starter/grass", revealed: true)),
                new(H(layer, 3), HexCell.Of("starter/grass", revealed: true)), new(H(layer, 5), HexCell.Of("starter/peaks", region: 2)),
                new(H(layer, 6), HexCell.Of("starter/grass"))]),
            Paths = [Wall(2, "Public path"), Wall(5, "HIDDEN_PATH")],
            Regions = [new(2, "HIDDEN_REGION", "#FF880000")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty
                .Add(H(layer, 2), new("PUBLIC_TITLE", gmText: "GM_SECRET", playerText: "PUBLIC_TEXT", childMap: "secret.mapproj"))
                .Add(H(layer, 3), new("GM_TITLE", gmText: "GM_SECRET"))
                .Add(H(layer, 5), new("HIDDEN_TITLE", playerText: "HIDDEN_TEXT")),
        };
        var under = (HexLayer)(layer with { Level = -1, FogEnabled = false,
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty.Add(H(layer, 2), new("UNDERGROUND", gmText: "GM_SECRET")) }).WithNewIds();
        return doc.WithLayers([.. doc.Layers.Select(l => l.Id == layer.Id ? layer : l), under]);
    }

    private sealed class Picker(string root) : IExportDestinationPicker
    {
        public Task<string?> PickImageAsync(string name, ExportImageFormat format) => Task.FromResult<string?>(Path.Combine(root, name));
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult<string?>(Path.Combine(root, name));
        public Task<string?> PickFileAsync(string name, string extension, string description) => Task.FromResult<string?>(Path.Combine(root, name));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
