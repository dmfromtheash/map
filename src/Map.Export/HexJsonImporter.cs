using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.Export;

/// <summary>Each input cell is either imported or reported once; unknown tile ids are never hidden.</summary>
public sealed record HexImportResult(HexLayer Layer, int Imported, int Outside, int UnknownTiles, int Duplicates,
    int SkippedPaths, int SkippedNotes, IReadOnlyList<string> UnknownTileIds, bool Expanded)
{
    public int Skipped => Outside + UnknownTiles + Duplicates;
    public string Summary => $"Импортировано {Imported} гексов, пропущено {Skipped} "
        + $"(вне поля / неверные координаты: {Outside}; неизвестный тайл: {UnknownTiles}; повтор координат: {Duplicates})."
        + $" Поле: {Layer.Board.Columns} × {Layer.Board.Rows}" + (Expanded ? " (расширено)." : ".")
        + $" Пропущено путей: {SkippedPaths}, ключей: {SkippedNotes}."
        + (UnknownTileIds.Count > 0 ? " Неизвестные id тайлов: " + string.Join(", ", UnknownTileIds) + "." : "");
}

public static partial class HexJsonExporter
{
    public static IReadOnlyList<string> ImportLayerNames(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["format"]?.GetValue<string>() != Format) throw new InvalidDataException("Это не JSON гексовой карты Map.");
            return (root["layers"]?.AsArray() ?? throw new InvalidDataException("В файле нет слоёв гексов."))
                .Select((item, i) => item?["name"]?.GetValue<string>() ?? $"Слой {i + 1}").ToArray();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { throw new InvalidDataException("Не удалось прочитать JSON: " + e.Message, e); }
    }

    /// <summary>
    /// The hexes, paths, regions and key of one JSON layer laid onto a hex layer of the same board
    /// (the board grows to fit; every rejected item is counted). Throws <see cref="InvalidDataException"/> on a file that is not this format.
    /// </summary>
    public static HexLayer Read(string json, HexLayer target, int layerIndex = 0) =>
        ReadWithReport(json, target, layerIndex).Layer;

    public static HexImportResult ReadWithReport(string json, HexLayer target, int layerIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Файл не похож на JSON гексовой карты: " + error.Message, error);
        }

        try
        {
            if (root?["format"]?.GetValue<string>() != Format)
                throw new InvalidDataException("Это не JSON гексовой карты Map.");
            var layers = root["layers"]?.AsArray() ?? throw new InvalidDataException("В файле нет слоёв гексов.");
            if (layerIndex < 0 || layerIndex >= layers.Count) throw new InvalidDataException("В файле нет такого слоя.");
            var source = layers[layerIndex]!;
            var board = target.Board;
            var columns = board.Columns;
            var rows = board.Rows;
            void Include(JsonNode? node)
            {
                if (node is not JsonArray { Count: >= 2 } pair) return;
                var x = pair[0]!.GetValue<int>(); var y = pair[1]!.GetValue<int>();
                if (x < 0 || y < 0 || x >= HexBoard.MaxDimension || y >= HexBoard.MaxDimension) return;
                columns = Math.Max(columns, x + 1); rows = Math.Max(rows, y + 1);
            }
            foreach (var item in source["cells"]?.AsArray() ?? []) Include(item?["at"]);
            foreach (var item in source["key"]?.AsArray() ?? []) Include(item?["at"]);
            foreach (var item in source["paths"]?.AsArray() ?? [])
                foreach (var at in item?["corners"]?.AsArray() ?? item?["hexes"]?.AsArray() ?? []) Include(at);
            board = HexBoard.FromCells(board.Layout, columns, rows, []);
            var outside = 0; var unknown = 0; var duplicates = 0; var skippedPaths = 0; var skippedNotes = 0;
            var unknownIds = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<HexCoord>();
            HexCoord? At(JsonNode? node)
            {
                if (node is not JsonArray { Count: >= 2 } pair) return null;
                var offset = new HexOffset(pair[0]!.GetValue<int>(), pair[1]!.GetValue<int>());
                return board.Contains(offset) ? board.Layout.FromOffset(offset) : null;
            }

            var cells = new List<KeyValuePair<HexCoord, HexCell>>();
            foreach (var item in source["cells"]?.AsArray() ?? [])
            {
                var terrain = item?["terrain"]?.GetValue<string>() ?? "";
                var feature = item?["feature"]?.GetValue<string>();
                var missing = false;
                foreach (var id in new[] { terrain, feature })
                    if (id is not null && HexStarterTiles.Find(id) is null) { unknownIds.Add(id); missing = true; }
                if (At(item?["at"]) is not { } hex) { outside++; continue; }
                if (missing) { unknown++; continue; }
                if (!seen.Add(hex)) { duplicates++; continue; }
                var cell = HexCell.Of(
                    item!["terrain"]!.GetValue<string>(),
                    item["variant"]?.GetValue<int>() ?? 0,
                    item["feature"]?.GetValue<string>(),
                    item["featureVariant"]?.GetValue<int>() ?? 0,
                    item["turn"]?.GetValue<int>() ?? 0,
                    item["region"]?.GetValue<int>() ?? 0,
                    item["revealed"]?.GetValue<bool>() ?? false);
                if (item["pointsOfInterest"] is JsonArray points)
                    cell = cell.WithPointsOfInterest(points.Select(p => new HexPoi(
                        p!["type"]!.GetValue<string>(), p["name"]?.GetValue<string>() ?? "",
                        p["playerVisible"]?.GetValue<bool>() ?? true, p["variant"]?.GetValue<int>() ?? 0)));
                cells.Add(new(hex, cell));
            }

            var paths = ImmutableList.CreateBuilder<HexPath>();
            foreach (var item in source["paths"]?.AsArray() ?? [])
            {
                var kind = Enum.Parse<HexPathKind>(item!["kind"]!.GetValue<string>());
                var route = Enum.Parse<HexRouteStyle>(item["route"]!.GetValue<string>());
                var width = item["width"]?.GetValue<double>() ?? 1;
                var color = item["color"]?.GetValue<string>();
                var name = item["name"]?.GetValue<string>() ?? "";
                if (route == HexRouteStyle.Edges)
                {
                    var corners = (item["corners"]?.AsArray() ?? []).Select(node => node is JsonArray { Count: 3 } c && At(c) is { } hex
                        ? board.Layout.Canonical(new HexVertex(hex, c[2]!.GetValue<int>())) : (HexVertex?)null).ToList();
                    if (corners.Any(c => c is null) || corners.Count < 2) { skippedPaths++; continue; }
                    paths.Add(new HexPath(HexPathId.New(), kind, route, [], [.. corners.Select(c => c!.Value)], width, color, name)
                    {
                        BlocksSight = item["blocksSight"]?.GetValue<bool>() ?? (kind == HexPathKind.Border && route == HexRouteStyle.Edges),
                        BlocksMove = item["blocksMove"]?.GetValue<bool>() ?? false,
                        BlocksLight = item["blocksLight"]?.GetValue<bool>() ?? false,
                    });
                }
                else
                {
                    var hexes = (item["hexes"]?.AsArray() ?? []).Select(At).ToList();
                    if (hexes.Any(h => h is null) || hexes.Count < 2) { skippedPaths++; continue; }
                    paths.Add(new HexPath(HexPathId.New(), kind, route, [.. Map.Core.Editing.HexPaths.Walk(hexes.Select(h => h!.Value))], [], width, color, name)
                    {
                        BlocksSight = item["blocksSight"]?.GetValue<bool>() ?? (kind == HexPathKind.Border && route == HexRouteStyle.Edges),
                        BlocksMove = item["blocksMove"]?.GetValue<bool>() ?? false,
                        BlocksLight = item["blocksLight"]?.GetValue<bool>() ?? false,
                    });
                }
            }

            var regions = ImmutableList.CreateBuilder<HexRegion>();
            foreach (var item in source["regions"]?.AsArray() ?? [])
            {
                regions.Add(new HexRegion(item!["id"]!.GetValue<int>(), item["name"]?.GetValue<string>() ?? "", item["color"]!.GetValue<string>()) { ParentRegion = item["parentRegion"]?.GetValue<int>() ?? 0 });
            }

            if (regions.Any(r => !HexRegionHierarchy.CanParent(regions, r.Id, r.ParentRegion)))
                throw new ArgumentException("Цикл или неизвестный родитель региона.");
            var notes = ImmutableDictionary.CreateBuilder<HexCoord, HexNote>();
            foreach (var item in source["key"]?.AsArray() ?? [])
            {
                if (At(item?["at"]) is not { } hex) { skippedNotes++; continue; }
                notes[hex] = new HexNote(item!["title"]?.GetValue<string>() ?? "",
                    Enum.TryParse<HexNoteCategory>(item["category"]?.GetValue<string>(), out var category) ? category : HexNoteCategory.Other,
                    item["gm"]?.GetValue<string>() ?? "", item["players"]?.GetValue<string>() ?? "", item["childMap"]?.GetValue<string>());
            }

            var imported = target with
            {
                Board = HexBoard.FromCells(board.Layout, board.Columns, board.Rows, cells),
                Paths = paths.ToImmutable(),
                Regions = regions.ToImmutable(),
                Notes = notes.ToImmutable(),
                FogEnabled = source["fog"]?.GetValue<bool>() ?? target.FogEnabled,
            };
            return new HexImportResult(imported, cells.Count, outside, unknown, duplicates, skippedPaths, skippedNotes,
                unknownIds.ToArray(), target.Board.Columns != columns || target.Board.Rows != rows);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or NullReferenceException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("Не удалось прочитать JSON гексовой карты: " + error.Message, error);
        }
    }
}
