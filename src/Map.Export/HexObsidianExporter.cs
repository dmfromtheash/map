using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.Export;

public sealed record HexObsidianResult(string Path, int Hexes, int Regions)
{
    public int Files => Hexes + Regions + 1;
}

/// <summary>
/// A standalone folder of linked Markdown notes, built only from the chosen export scene.
/// Filenames use immutable layer ids and coordinates, never mutable titles, terrain or levels.
/// The destination must be new: mixing a player export with an old GM vault could leak secrets.
/// </summary>
public static class HexObsidianExporter
{
    private static readonly JsonSerializerOptions Quoting = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static HexObsidianResult Write(ExportScene scene, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var layers = scene.Layers.OfType<HexLayer>().ToArray();
        if (layers.Length == 0) throw new InvalidOperationException("На карте нет видимого слоя гексов.");
        var fullPath = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new IOException("Папка результата уже существует. Выберите новое имя; существующие данные не изменены.");
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Родительская папка результата не существует. Выберите другую папку.");
        var temporary = System.IO.Path.Combine(parent, $".obsidian-{Guid.NewGuid():N}.tmp");
        var hexCount = 0;
        var regionCount = 0;
        try
        {
            Directory.CreateDirectory(temporary);
            var index = new StringBuilder().AppendLine("---");
            Property(index, "id", scene.Document.Id.ToString());
            Property(index, "title", scene.Document.Title);
            Property(index, "recipient", scene.Players ? "players" : "gm");
            Tags(index, ["карта/оглавление"]);
            index.AppendLine("---").AppendLine().AppendLine("# " + Inline(scene.Document.Title)).AppendLine()
                .AppendLine(scene.Players ? "Пакет для игроков." : "Пакет ведущего.").AppendLine()
                .AppendLine("Координаты столбца и строки начинаются с 0. Пустые гексы без заметок пропущены.").AppendLine();
            foreach (var layer in layers)
            {
                var board = layer.Board;
                var hexes = board.Cells.Select(p => p.Key)
                    .Concat(layer.Notes.Where(p => !p.Value.IsEmpty && board.Contains(p.Key)).Select(p => p.Key))
                    .Distinct().OrderBy(h => board.Layout.ToOffset(h).Column).ThenBy(h => board.Layout.ToOffset(h).Row).ToArray();
                var included = hexes.ToHashSet();
                var regions = layer.Regions.ToDictionary(r => r.Id, r => r.Name);
                foreach (var hex in hexes)
                    if (board[hex] is { Region: > 0 } cell) regions.TryAdd(cell.Region, $"Регион {cell.Region}");
                var groups = hexes.ToLookup(h => board[h]?.Region ?? 0);
                index.AppendLine($"## {Inline(layer.Name)} · уровень {Number(layer.Level)}").AppendLine();
                foreach (var (id, name) in regions.OrderBy(p => p.Key))
                {
                    var members = groups[id].ToArray();
                    var region = new StringBuilder().AppendLine("---");
                    Property(region, "id", RegionStem(layer, id));
                    Property(region, "title", name);
                    Property(region, "layer_id", layer.Id.ToString());
                    region.AppendLine("level: " + Number(layer.Level)).AppendLine("region_id: " + Number(id));
                    Tags(region, ["регион/" + Slug(name)]);
                    region.AppendLine("---").AppendLine().AppendLine("# " + Inline(name)).AppendLine()
                        .AppendLine("[[index|Карта — оглавление]]").AppendLine()
                        .AppendLine("Уровень: " + Number(layer.Level)).AppendLine()
                        .AppendLine("## Гексы").AppendLine();
                    foreach (var hex in members) region.AppendLine("- " + HexLink(layer, hex));
                    if (members.Length == 0) region.AppendLine("Нет гексов с содержимым.");
                    Save(temporary, RegionStem(layer, id), region);
                    regionCount++;
                    index.AppendLine($"### {Link(RegionStem(layer, id), name)} ({members.Length})").AppendLine();
                    foreach (var hex in members) index.AppendLine("- " + HexLink(layer, hex));
                    index.AppendLine();
                }
                if (groups[0].Any())
                {
                    index.AppendLine("### Без региона").AppendLine();
                    foreach (var hex in groups[0]) index.AppendLine("- " + HexLink(layer, hex));
                    index.AppendLine();
                }
                foreach (var hex in hexes)
                {
                    Save(temporary, HexStem(layer, hex), HexNoteText(layer, hex, regions, included, scene.Players));
                    hexCount++;
                }
            }
            Save(temporary, "index", index);
            Directory.Move(temporary, fullPath);
            return new(fullPath, hexCount, regionCount);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static StringBuilder HexNoteText(HexLayer layer, HexCoord hex, IReadOnlyDictionary<int, string> regions,
        HashSet<HexCoord> included, bool players)
    {
        var cell = layer.Board[hex];
        layer.Notes.TryGetValue(hex, out var note);
        var offset = layer.Board.Layout.ToOffset(hex);
        var terrain = cell is null ? "Не задана" : TileName(cell.Terrain);
        var tags = new List<string> { "гекс/" + (cell is null ? "без-местности" : Slug(terrain)), "уровень/" + Number(layer.Level) };
        if (cell?.Feature is { } feature) tags.Add("надстройка/" + Slug(TileName(feature)));
        if (note is not null) tags.Add("категория/" + Slug(note.Category.ToString()));
        var region = cell is { Region: > 0 } ? cell.Region : 0;
        if (region > 0) tags.Add("регион/" + Slug(regions[region]));
        var text = new StringBuilder().AppendLine("---");
        Property(text, "id", HexStem(layer, hex));
        Property(text, "title", HexTitle(layer, hex));
        Property(text, "layer_id", layer.Id.ToString());
        text.AppendLine("column: " + Number(offset.Column)).AppendLine("row: " + Number(offset.Row))
            .AppendLine("q: " + Number(hex.Q)).AppendLine("r: " + Number(hex.R)).AppendLine("level: " + Number(layer.Level));
        Property(text, "terrain", cell?.Terrain);
        Property(text, "terrain_name", terrain);
        Property(text, "feature", cell?.Feature);
        text.AppendLine("region_id: " + Number(region));
        Property(text, "region", region == 0 ? null : Link(RegionStem(layer, region), regions[region]));
        if (note is not null) Property(text, "category", note.Category.ToString());
        if (!players && note?.ChildMap is { } childMap) Property(text, "childMap", childMap);
        Tags(text, tags);
        text.AppendLine("---").AppendLine().AppendLine("# " + Inline(HexTitle(layer, hex))).AppendLine()
            .AppendLine("[[index|Карта — оглавление]]").AppendLine()
            .AppendLine($"- Координаты: {Number(offset.Column)}, {Number(offset.Row)} (q={Number(hex.Q)}, r={Number(hex.R)})")
            .AppendLine("- Уровень: " + Number(layer.Level))
            .AppendLine("- Местность: " + Inline(terrain))
            .AppendLine("- Надстройка: " + (cell?.Feature is { } f ? Inline(TileName(f)) : "Нет"))
            .AppendLine("- Регион: " + (region == 0 ? "Нет" : Link(RegionStem(layer, region), regions[region])))
            .AppendLine().AppendLine(string.Join(" ", tags.Select(t => "#" + t))).AppendLine();
        if (!players && !string.IsNullOrWhiteSpace(note?.GmText))
            text.AppendLine("## Ведущему").AppendLine().AppendLine(note.GmText.Trim()).AppendLine();
        if (!string.IsNullOrWhiteSpace(note?.PlayerText))
            text.AppendLine("## Игрокам").AppendLine().AppendLine(note.PlayerText.Trim()).AppendLine();
        text.AppendLine("## Соседние гексы").AppendLine();
        var neighbors = hex.Neighbors().Where(included.Contains).ToArray();
        foreach (var neighbor in neighbors) text.AppendLine("- " + HexLink(layer, neighbor));
        if (neighbors.Length == 0) text.AppendLine("Нет соседей в этом пакете.");
        return text;
    }

    private static string HexStem(HexLayer layer, HexCoord hex)
    {
        var offset = layer.Board.Layout.ToOffset(hex);
        return $"hex-{layer.Id.Value:N}-c{Number(offset.Column)}-r{Number(offset.Row)}";
    }

    private static string RegionStem(HexLayer layer, int id) => $"region-{layer.Id.Value:N}-{Number(id)}";
    private static string HexTitle(HexLayer layer, HexCoord hex)
    {
        var number = HexLayerRenderer.NumberOf(layer.Board, layer.Board.Layout.ToOffset(hex));
        return layer.Notes.TryGetValue(hex, out var note) && !string.IsNullOrWhiteSpace(note.Title)
            ? number + " — " + note.Title : "Гекс " + number;
    }

    private static string HexLink(HexLayer layer, HexCoord hex) => Link(HexStem(layer, hex), HexTitle(layer, hex));
    private static string Link(string stem, string label) => "[[" + stem + "|" + Inline(label) + "]]";
    // Titles cannot close a generated link, inject a heading or introduce an embedded note.
    private static string Inline(string text) => System.Net.WebUtility.HtmlEncode(text)
        .Replace("[", "&#91;", StringComparison.Ordinal).Replace("]", "&#93;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
    private static string TileName(string id) => HexStarterTiles.Find(id)?.Name ?? id;
    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);
    private static string Slug(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_') result.Append(c);
            else if (result.Length > 0 && result[^1] != '-') result.Append('-');
        }
        return result.ToString().Trim('-') is { Length: > 0 } slug ? slug : "без-имени";
    }

    private static void Property(StringBuilder text, string name, string? value) =>
        text.Append(name).Append(": ").AppendLine(JsonSerializer.Serialize(value, Quoting));
    private static void Tags(StringBuilder text, IEnumerable<string> tags)
    {
        text.AppendLine("tags:");
        foreach (var tag in tags) text.Append("  - ").AppendLine(JsonSerializer.Serialize(tag, Quoting));
    }
    private static void Save(string folder, string stem, StringBuilder text) =>
        File.WriteAllText(System.IO.Path.Combine(folder, stem + ".md"), text.ToString(), new UTF8Encoding(false));
}
