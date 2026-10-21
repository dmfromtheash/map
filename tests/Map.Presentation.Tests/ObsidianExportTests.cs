using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class ObsidianExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-r19-").FullName;

    [Theory]
    [InlineData(HexOrientation.Pointy)]
    [InlineData(HexOrientation.Flat)]
    public void Folder_contains_hexes_regions_yaml_neighbors_tags_and_region_index(HexOrientation orientation)
    {
        var doc = World(orientation);
        var layer = Surface(doc);
        var result = HexObsidianExporter.Write(new ExportScene(doc), Path.Combine(_root, "master"));
        Assert.Equal(6, result.Hexes); // Four painted surface cells, a note-only cell, one underground cell.
        Assert.Equal(4, result.Regions); // Includes an empty GM region; ids are scoped to each layer.
        var files = ReadFolder(result.Path);
        Assert.Equal(result.Files, files.Count);
        var first = FindHex(files, layer, 1, 1);
        var second = FindHex(files, layer, 2, 1);
        var yaml = FrontMatter(first.Value);
        Assert.Equal(1, yaml["column"].GetInt32());
        Assert.Equal(1, yaml["row"].GetInt32());
        Assert.Equal(H(layer, 1, 1).Q, yaml["q"].GetInt32());
        Assert.Equal(H(layer, 1, 1).R, yaml["r"].GetInt32());
        Assert.Equal(0, yaml["level"].GetInt32());
        Assert.Equal("starter/forest", yaml["terrain"].GetString());
        Assert.Equal("starter/village", yaml["feature"].GetString());
        Assert.Equal("Settlement", yaml["category"].GetString());
        Assert.Contains("#гекс/", first.Value);
        Assert.Contains("#надстройка/", first.Value);
        Assert.Contains("#категория/settlement", first.Value);
        Assert.Contains("#уровень/0", first.Value);
        Assert.Contains("tags:\n  - ", first.Value.Replace("\r\n", "\n"));
        Assert.Contains("PUBLIC_TEXT", first.Value);
        Assert.Contains("GM_SECRET", first.Value);
        Assert.Contains("secret.mapproj", yaml["childMap"].GetString());
        Assert.Contains("[[" + second.Key + "|", first.Value);
        Assert.Contains("[[" + first.Key + "|", second.Value);
        var region = files.Single(p => p.Key.StartsWith("region-", StringComparison.Ordinal)
            && FrontMatter(p.Value)["layer_id"].GetString() == layer.Id.ToString()
            && FrontMatter(p.Value)["region_id"].GetInt32() == 1);
        Assert.Contains("[[" + region.Key + "|", first.Value);
        Assert.Contains("[[" + first.Key + "|", region.Value);
        Assert.Contains("[[" + region.Key + "|", files["index"]);
        Assert.Contains("[[" + first.Key + "|", files["index"]);
        Assert.Contains("Без региона", files["index"]);
        Assert.Equal(JsonValueKind.Null, FrontMatter(FindHex(files, layer, 4, 3).Value)["terrain"].ValueKind);
        AssertLinksResolve(files);
    }

    [Fact]
    public async Task Player_button_command_uses_scene_level_and_leaves_no_secret_files_or_links()
    {
        var doc = World();
        var layer = Surface(doc);
        var editor = Editor(doc, new Picker(Path.Combine(_root, "players")));
        editor.ExportHexPlayers = true;
        var result = await editor.ExportObsidianAsync();
        Assert.NotNull(result);
        Assert.Equal(3, result.Hexes);
        Assert.Equal(1, result.Regions);
        var files = ReadFolder(result.Path);
        var text = string.Join('\n', files.Values);
        Assert.Contains("PUBLIC_TEXT", text);
        foreach (var secret in new[] { "GM_SECRET", "GM_TITLE", "GM_CATEGORY", "HIDDEN_TITLE", "HIDDEN_TEXT", "HIDDEN_REGION",
            "EMPTY_SECRET_REGION", "NOTE_ONLY_SECRET", "UNDERGROUND_SECRET", "HIDDEN_LAYER_SECRET", "secret.mapproj", "childMap", "Ведущему" })
            Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain(files, p => p.Key.EndsWith("-c3-r2", StringComparison.Ordinal));
        Assert.DoesNotContain(files, p => p.Key.EndsWith("-c4-r3", StringComparison.Ordinal));
        Assert.DoesNotContain("category:", FindHex(files, layer, 0, 0).Value);
        Assert.All(files.Where(p => p.Key != "index"), p => Assert.Equal(0, FrontMatter(p.Value)["level"].GetInt32()));
        AssertLinksResolve(files);
        Assert.Same(doc, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void Changing_only_secrets_leaves_every_player_filename_and_byte_unchanged()
    {
        var doc = World();
        var layer = Surface(doc);
        var changed = doc.WithLayer(layer.Id, _ => layer with
        {
            Notes = layer.Notes.SetItem(H(layer, 1, 1), layer.Notes[H(layer, 1, 1)] with { GmText = "NEW_SECRET", ChildMap = "new.mapproj" })
                .SetItem(H(layer, 0, 0), new("NEW_PRIVATE_TITLE", HexNoteCategory.Danger, gmText: "NEW_SECRET"))
                .SetItem(H(layer, 3, 2), new("NEW_HIDDEN_TITLE", playerText: "NEW_HIDDEN_TEXT")),
            Board = layer.Board.With([new(H(layer, 3, 2), HexCell.Of("starter/sea", region: 2))]),
            Regions = [layer.Regions[0], new(2, "NEW_HIDDEN_REGION", "#FF112233"), layer.Regions[2]],
        });
        var a = HexObsidianExporter.Write(new ExportScene(doc, 0, ExportRecipient.Players), Path.Combine(_root, "a"));
        var b = HexObsidianExporter.Write(new ExportScene(changed, 0, ExportRecipient.Players), Path.Combine(_root, "b"));
        Assert.Equal(ReadFolder(a.Path).OrderBy(p => p.Key), ReadFolder(b.Path).OrderBy(p => p.Key));
    }

    [Fact]
    public void Renaming_hex_region_layer_and_map_does_not_change_filenames_or_link_targets()
    {
        var doc = World();
        var layer = Surface(doc);
        var renamed = doc.WithLayer(layer.Id, _ => layer with
        {
            Name = "Renamed layer",
            Notes = layer.Notes.SetItem(H(layer, 1, 1), layer.Notes[H(layer, 1, 1)] with { Title = "CON: renamed / .. \\ ? *" }),
            Regions = layer.Regions.SetItem(0, layer.Regions[0] with { Name = "renamed region" }),
        }) with { Title = "Renamed map" };
        var a = ReadFolder(HexObsidianExporter.Write(new ExportScene(doc), Path.Combine(_root, "a")).Path);
        var b = ReadFolder(HexObsidianExporter.Write(new ExportScene(renamed), Path.Combine(_root, "b")).Path);
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var name in a.Keys) Assert.Equal(LinkTargets(a[name]), LinkTargets(b[name]));
        Assert.Contains("CON: renamed", FindHex(b, layer, 1, 1).Value);
        AssertLinksResolve(b);
    }

    [Fact]
    public void Quoted_yaml_preserves_punctuation_and_unicode_and_names_are_filesystem_safe()
    {
        var doc = World();
        var layer = Surface(doc);
        const string title = "Башня: \"Да\" #tag | [[нет]]\n---\nтекст \\ путь";
        doc = doc.WithLayer(layer.Id, _ => layer with
        {
            Notes = layer.Notes.SetItem(H(layer, 1, 1), new(title, playerText: "Публичный текст")),
            Regions = layer.Regions.SetItem(0, layer.Regions[0] with { Name = title }),
        });
        var files = ReadFolder(HexObsidianExporter.Write(new ExportScene(doc), Path.Combine(_root, "safe")).Path);
        Assert.EndsWith(title, FrontMatter(FindHex(files, layer, 1, 1).Value)["title"].GetString());
        Assert.All(files.Keys, name => Assert.Matches("^[a-z0-9-]+$", name));
        AssertLinksResolve(files);
    }

    [Fact]
    public void Selected_scene_excludes_other_levels_and_invisible_layers()
    {
        var doc = World();
        var result = HexObsidianExporter.Write(new ExportScene(doc, -1), Path.Combine(_root, "under"));
        Assert.Equal(1, result.Hexes);
        Assert.Equal(1, result.Regions);
        var files = ReadFolder(result.Path);
        Assert.All(files.Where(p => p.Key != "index"), p => Assert.Equal(-1, FrontMatter(p.Value)["level"].GetInt32()));
        Assert.DoesNotContain("HIDDEN_LAYER_SECRET", string.Join('\n', files.Values));
        AssertLinksResolve(files);
    }

    [Fact]
    public void Fog_disabled_includes_note_only_hexes_and_an_empty_board_still_has_an_index()
    {
        var doc = World();
        var layer = Surface(doc);
        doc = doc.WithLayer(layer.Id, _ => layer with { FogEnabled = false });
        var files = ReadFolder(HexObsidianExporter.Write(new ExportScene(doc, 0, ExportRecipient.Players), Path.Combine(_root, "clear")).Path);
        Assert.Contains("NOTE_ONLY_SECRET", FindHex(files, layer, 4, 3).Value); // It is player text; only fog made it private.
        AssertLinksResolve(files);
        doc = HexMapFactory.Create("Пустая", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 30, 2, 2, "#FFFFFFFF");
        var empty = HexObsidianExporter.Write(new ExportScene(doc), Path.Combine(_root, "empty"));
        Assert.Equal(1, empty.Files);
        Assert.Single(ReadFolder(empty.Path));
    }

    [Fact]
    public async Task Existing_folder_is_never_merged_or_overwritten_and_cancel_writes_nothing()
    {
        var destination = Path.Combine(_root, "existing");
        Directory.CreateDirectory(destination);
        var ownerNote = Path.Combine(destination, "my-note.md");
        File.WriteAllText(ownerNote, "GM_SECRET_DO_NOT_OVERWRITE");
        var editor = Editor(World(), new Picker(destination));
        editor.ExportHexPlayers = true;
        Assert.Null(await editor.ExportObsidianAsync());
        Assert.Contains("уже существует", editor.StatusDetail);
        Assert.Equal("GM_SECRET_DO_NOT_OVERWRITE", File.ReadAllText(ownerNote));
        Assert.Single(Directory.GetFiles(destination));
        Assert.Null(await Editor(World(), new Picker(null)).ExportObsidianAsync());
        Assert.Single(Directory.GetDirectories(_root));
        Assert.Empty(Directory.GetFiles(_root));
    }

    private static Dictionary<string, string> ReadFolder(string path) => Directory.GetFiles(path, "*.md")
        .ToDictionary(p => Path.GetFileNameWithoutExtension(p), File.ReadAllText, StringComparer.Ordinal);

    // The exporter emits a deliberately small YAML subset: JSON-quoted scalars and a block tags list.
    // Parsing every scalar also catches unescaped quotes, embedded newlines and accidental extra fields.
    private static Dictionary<string, JsonElement> FrontMatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("---", lines[0]);
        var end = Array.IndexOf(lines, "---", 1);
        Assert.True(end > 1);
        var fields = new Dictionary<string, JsonElement>();
        foreach (var line in lines[1..end])
        {
            if (line == "tags:") continue;
            if (line.StartsWith("  - ", StringComparison.Ordinal)) { using var tag = JsonDocument.Parse(line[4..]); Assert.Equal(JsonValueKind.String, tag.RootElement.ValueKind); continue; }
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            Assert.True(colon > 0, line);
            using var scalar = JsonDocument.Parse(line[(colon + 2)..]);
            fields.Add(line[..colon], scalar.RootElement.Clone());
        }
        return fields;
    }

    private static KeyValuePair<string, string> FindHex(Dictionary<string, string> files, HexLayer layer, int c, int r) =>
        files.Single(p => p.Key.StartsWith("hex-", StringComparison.Ordinal) && FrontMatter(p.Value)["layer_id"].GetString() == layer.Id.ToString()
            && FrontMatter(p.Value)["column"].GetInt32() == c && FrontMatter(p.Value)["row"].GetInt32() == r);
    private static string[] LinkTargets(string text) => Regex.Matches(text, @"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]")
        .Select(m => m.Groups[1].Value).Order().ToArray();
    private static void AssertLinksResolve(Dictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var yaml = FrontMatter(text);
            var end = text.IndexOf("---", 3, StringComparison.Ordinal) + 3;
            // A title can itself contain literal [[text]]. Check the links generated by the exporter,
            // not arbitrary strings copied from titles or child-map paths in YAML.
            var generated = text[end..] + (yaml.TryGetValue("region", out var region) ? region.GetString() : "");
            foreach (var target in LinkTargets(generated)) Assert.True(files.ContainsKey(target), name + " -> " + target);
        }
    }

    private static HexLayer Surface(MapDocument doc) => doc.Layers.OfType<HexLayer>().First();
    private static HexCoord H(HexLayer layer, int c, int r) => layer.Board.Layout.FromOffset(new(c, r));
    private static MapDocument World(HexOrientation orientation = HexOrientation.Pointy)
    {
        var doc = HexMapFactory.Create("Долина", DateTimeOffset.UnixEpoch, new("hex"), orientation, 30, 5, 4, "#FFF2E9DC");
        var layer = Surface(doc);
        layer = layer with
        {
            FogEnabled = true,
            Board = layer.Board.With([
                new(H(layer, 1, 1), HexCell.Of("starter/forest", feature: "starter/village", region: 1, revealed: true)),
                new(H(layer, 2, 1), HexCell.Of("starter/grass", region: 1, revealed: true)),
                new(H(layer, 0, 0), HexCell.Of("starter/grass", revealed: true)),
                new(H(layer, 3, 2), HexCell.Of("starter/peaks", region: 2))]),
            Regions = [new(1, "Берег", "#FF112233"), new(2, "HIDDEN_REGION", "#FF332211"), new(3, "EMPTY_SECRET_REGION", "#FF445566")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty
                .Add(H(layer, 1, 1), new("Переправа", HexNoteCategory.Settlement, "GM_SECRET", "PUBLIC_TEXT", "secret.mapproj"))
                .Add(H(layer, 0, 0), new("GM_TITLE", HexNoteCategory.Treasure, gmText: "GM_CATEGORY"))
                .Add(H(layer, 3, 2), new("HIDDEN_TITLE", playerText: "HIDDEN_TEXT"))
                .Add(H(layer, 4, 3), new("Заметка без плитки", playerText: "NOTE_ONLY_SECRET")),
        };
        var under = (HexLayer)(layer with
        {
            Level = -1, FogEnabled = false,
            Board = HexBoard.Create(orientation, 30, 5, 4).With([new(H(layer, 1, 1), HexCell.Of("starter/sea", region: 1))]),
            Regions = [new(1, "Подземелье", "#FF222222")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty.Add(H(layer, 1, 1), new("UNDERGROUND_SECRET", gmText: "GM_SECRET")),
        }).WithNewIds();
        var invisible = (HexLayer)(under with { Visible = false, Name = "HIDDEN_LAYER_SECRET" }).WithNewIds();
        return doc.WithLayers([.. doc.Layers.Select(l => l.Id == layer.Id ? layer : l), under, invisible]);
    }

    private static EditorViewModel Editor(MapDocument doc, Picker picker) => new(doc, null, false, new FakeProjectStore(),
        new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(), exportDestinationPicker: picker);
    private sealed class Picker(string? path) : IExportDestinationPicker
    {
        public Task<string?> PickImageAsync(string name, ExportImageFormat format) => throw new NotSupportedException();
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult(path);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
