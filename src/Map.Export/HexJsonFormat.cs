using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Rendering;

namespace Map.Export;

/// <summary>
/// A hex map as plain, readable JSON (HexLands users edit theirs in a text editor; Hexxmap plans
/// JSON export): the board, every level, every hex with its tile, turn, feature, region and fog,
/// the paths, the regions, the hex key and the tile legend. <see cref="Read"/> takes it back.
/// Positions are board columns and rows from 0, the printed hex number is given as well.
/// </summary>
public static partial class HexJsonFormat
{
    public const string Format = "map-hex-json";

    public const int Version = 1;

    public static void Write(MapDocument document, string path) => Write(new ExportScene(document), path);

    public static void Write(ExportScene scene, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = ToJson(scene);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temporary, path, overwrite: true);
    }

    public static JsonObject ToJson(MapDocument document) => ToJson(new ExportScene(document));

    public static JsonObject ToJson(ExportScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var document = scene.Document;
        var layers = new JsonArray();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var layer in scene.Layers.OfType<HexLayer>())
        {
            var board = layer.Board;
            JsonArray Position(HexCoord hex)
            {
                var offset = board.Layout.ToOffset(hex);
                return [offset.Column, offset.Row];
            }

            var cells = new JsonArray();
            foreach (var (hex, cell) in board.Cells)
            {
                var offset = board.Layout.ToOffset(hex);
                used.Add(cell.Terrain);
                foreach (var point in cell.PointsOfInterest) used.Add(point.Type);
                var item = new JsonObject
                {
                    ["at"] = Position(hex),
                    ["number"] = HexLayerRenderer.NumberOf(board, offset),
                    ["terrain"] = cell.Terrain,
                };
                if (cell.Variant != 0) item["variant"] = cell.Variant;
                if (cell.Rotation != 0) item["turn"] = cell.Rotation;
                if (cell.Feature is not null)
                {
                    item["feature"] = cell.Feature;
                    if (cell.FeatureVariant != 0) item["featureVariant"] = cell.FeatureVariant;
                }

                if (!cell.PointsOfInterest.IsEmpty)
                    item["pointsOfInterest"] = new JsonArray([.. cell.PointsOfInterest.Select(p => (JsonNode)new JsonObject
                    { ["type"] = p.Type, ["name"] = p.Name, ["playerVisible"] = p.PlayerVisible, ["variant"] = p.Variant, ["rumor"] = p.Rumor, ["approximateLabel"] = p.ApproximateLabel })]);
                if (cell.Region != 0) item["region"] = cell.Region;
                if (cell.Revealed) item["revealed"] = true;
                cells.Add(item);
            }

            var paths = new JsonArray();
            foreach (var p in layer.Paths)
            {
                var item = new JsonObject { ["kind"] = p.Kind.ToString(), ["route"] = p.Style.ToString() };
                if (p.Name.Length > 0) item["name"] = p.Name;
                if (p.Style == HexRouteStyle.Edges)
                {
                    item["corners"] = new JsonArray([.. p.Vertices.Select(v =>
                    {
                        var o = board.Layout.ToOffset(v.Hex);
                        return (JsonNode)new JsonArray(o.Column, o.Row, v.Corner);
                    })]);
                }
                else
                {
                    item["hexes"] = new JsonArray([.. p.Hexes.Select(h => (JsonNode)Position(h))]);
                }

                if (p.Width != 1) item["width"] = p.Width;
                if (p.Color is not null) item["color"] = p.Color;
                // Explicit values distinguish a new non-blocking border from old JSON's defaults.
                item["blocksSight"] = p.BlocksSight;
                item["blocksMove"] = p.BlocksMove;
                item["blocksLight"] = p.BlocksLight;
                paths.Add(item);
            }

            var keys = new JsonArray();
            foreach (var (hex, note) in layer.Notes.OrderBy(pair => board.Layout.ToOffset(pair.Key).Column).ThenBy(pair => board.Layout.ToOffset(pair.Key).Row))
            {
                var item = new JsonObject
                {
                    ["at"] = Position(hex),
                    ["number"] = HexLayerRenderer.NumberOf(board, board.Layout.ToOffset(hex)),
                    ["title"] = note.Title,
                    ["category"] = note.Category.ToString(),
                };
                if (note.GmText.Length > 0) item["gm"] = note.GmText;
                if (note.PlayerText.Length > 0) item["players"] = note.PlayerText;
                if (note.ChildMap is not null) item["childMap"] = note.ChildMap;
                item["revealCondition"] = note.RevealCondition.ToString();
                item["manuallyRevealed"] = note.ManuallyRevealed; item["revealDay"] = note.RevealDay;
                item["rumor"] = note.Rumor; item["approximateLabel"] = note.ApproximateLabel;
                keys.Add(item);
            }

            layers.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["level"] = layer.Level,
                ["fog"] = layer.FogEnabled,
                ["campaignDay"] = layer.CampaignDay,
                ["party"] = new JsonObject
                {
                    ["position"] = layer.Party.Position is { } h ? Position(h) : null,
                    ["radius"] = layer.Party.SightRadius,
                    ["history"] = new JsonArray([.. layer.Party.History.Select(h => (JsonNode)Position(h))]),
                    ["visited"] = new JsonArray([.. layer.Party.Visited.OrderBy(h => h.Q).ThenBy(h => h.R).Select(h => (JsonNode)Position(h))]),
                },
                ["travel"] = new JsonObject
                {
                    ["perHex"] = layer.Travel.DistancePerHex, ["unit"] = layer.Travel.Unit, ["perDay"] = layer.Travel.DistancePerDay,
                },
                ["regions"] = new JsonArray([.. layer.Regions.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["name"] = r.Name, ["color"] = r.Color, ["parentRegion"] = r.ParentRegion, ["rumor"] = r.Rumor, ["approximateLabel"] = r.ApproximateLabel })]),
                ["cells"] = cells,
                ["paths"] = paths,
                ["key"] = keys,
            });
        }

        var first = scene.Layers.OfType<HexLayer>().FirstOrDefault()?.Board;
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["title"] = document.Title,
            ["board"] = first is null ? null : new JsonObject
            {
                ["orientation"] = first.Orientation == HexOrientation.Flat ? "flat (odd columns lower)" : "pointy (odd rows to the right)",
                ["columns"] = first.Columns,
                ["rows"] = first.Rows,
                ["hexSize"] = first.Layout.Size,
            },
            ["tiles"] = new JsonArray([.. used.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = HexStarterTiles.Find(id)?.Name ?? id })]),
            ["layers"] = layers,
        };
    }


}
