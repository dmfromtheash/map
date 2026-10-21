using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

/// <summary>
/// A hex map as plain, readable JSON (HexLands users edit theirs in a text editor; Hexxmap plans
/// JSON export): the board, every level, every hex with its tile, turn, feature, region and fog,
/// the paths, the regions, the hex key and the tile legend. <see cref="Read"/> takes it back.
/// Positions are board columns and rows from 0, the printed hex number is given as well.
/// </summary>
public static partial class HexJsonExporter
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
                if (cell.Feature is { } f) used.Add(f);
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
                keys.Add(item);
            }

            layers.Add(new JsonObject
            {
                ["name"] = layer.Name,
                ["level"] = layer.Level,
                ["fog"] = layer.FogEnabled,
                ["travel"] = new JsonObject
                {
                    ["perHex"] = layer.Travel.DistancePerHex, ["unit"] = layer.Travel.Unit, ["perDay"] = layer.Travel.DistancePerDay,
                },
                ["regions"] = new JsonArray([.. layer.Regions.Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["name"] = r.Name, ["color"] = r.Color })]),
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

/// <summary>Paper and scale of a printed hex map.</summary>
/// <param name="HexMillimetres">Width of one hex across its flat sides on paper.</param>
/// <param name="Landscape">Pages lie on their long side.</param>
/// <param name="A3">A3 paper instead of A4.</param>
/// <param name="Players">Print the players' view (opaque fog, player keys only).</param>
/// <param name="Level">The level of the world to print; null for every level as the editor shows it.</param>
/// <param name="IncludeKey">Add the hex key after the map pages.</param>
public sealed record HexPrintOptions(
    double HexMillimetres = 25,
    bool Landscape = true,
    bool A3 = false,
    bool Players = false,
    int? Level = null,
    bool IncludeKey = true,
    double OverlapMillimetres = 8,
    HexPaperFormat? PaperFormat = null,
    double CustomWidthMillimetres = 210,
    double CustomHeightMillimetres = 297,
    bool CropMarks = false,
    double BleedMillimetres = 0,
    double MapWidthMillimetres = 600,
    bool IncludeOverview = true,
    bool IncludeGrid = true);

/// <summary>What a print export wrote.</summary>
public sealed record HexPrintResult(string Path, int MapPages, int KeyPages, int Columns, int Rows)
{
    public int OverviewPages { get; init; }
    public int TotalPages => MapPages + KeyPages + OverviewPages;
}

/// <summary>
/// A hex map printed page by page (asked of Pixel Hex: big tiles to print and cut; Hexroll prints
/// its sandbox as a PDF): the board at a chosen hex size on paper, split into sheets with a little
/// overlap, each sheet labelled with its place (A1, A2 … B1) and the hex numbers it holds, then the
/// hex key. Drawn into Skia's PDF document, so lines and text stay sharp at any printer resolution.
/// </summary>
public static partial class HexPrintExporter
{
    private const double PointsPerMillimetre = 72 / 25.4;

    public static HexPrintResult Write(MapDocument document, string path, HexPrintOptions options, IReadOnlyList<(string Number, HexNote Note)>? key = null) =>
        Write(new ExportScene(document, options.Level, options.Players ? ExportRecipient.Players : ExportRecipient.GameMaster), path, options, key);

    /// <summary>Shared poster writer for Standard and Hex. The legacy name keeps R03 callers compatible.</summary>
    public static HexPrintResult Write(ExportScene scene, string path, HexPrintOptions options,
        IReadOnlyList<(string Number, HexNote Note)>? key = null, IStampImageSource? stamps = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = scene.RenderDocument();
        var layer = scene.Layers.OfType<HexLayer>().FirstOrDefault(l => l.Level == (scene.HexLevel ?? 0))
            ?? scene.Layers.OfType<HexLayer>().FirstOrDefault();
        var board = scene.Category.Value == "hex" ? layer?.Board : null;
        // A caller's key can contain GM-only entries or belong to another level. For players
        // derive it exclusively from the sanitized scene, never from that untrusted list.
        if (board is not null && layer is not null && (scene.Players || key is null))
            key = layer.Notes.Where(p => !p.Value.IsEmpty).OrderBy(p => board.Layout.ToOffset(p.Key).Column)
                .ThenBy(p => board.Layout.ToOffset(p.Key).Row)
                .Select(p => (HexLayerRenderer.NumberOf(board, board.Layout.ToOffset(p.Key)), p.Value)).ToList();
        var layout = Plan(scene, options);
        var (pageW, pageH, bleed, printW, printH, scale, area, stepW, stepH, overlap, columns, rows) = layout;
        using var tiles = new TileImageCache(128);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var keyPages = 0;
        try
        {
            using (var stream = new SKFileWStream(temporary))
            using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
            {
                Title = document.Title, Creator = "Map", Producer = "Map", RasterDpi = 300, EncodingQuality = 92,
            }))
            {
                using var typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
                using var labelFont = new SKFont(typeface, 10);
                using var labelPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0x30, 0x28, 0x20) };
                using var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, Color = new SKColor(0x80, 0x78, 0x70) };
                if (options.IncludeOverview) WriteOverview(pdf, scene, options, layout, stamps, tiles, typeface);
                for (var row = 0; row < rows; row++)
                {
                    for (var column = 0; column < columns; column++)
                    {
                        var left = area.Left + column * (stepW - overlap);
                        var top = area.Top + row * (stepH - overlap);
                        var view = new WorldBounds(left, top, left + stepW, top + stepH);
                        var canvas = pdf.BeginPage(pageW, pageH);
                        canvas.Clear(SKColors.White);
                        var name = SheetName(row, column);
                        var numbers = board is null ? "" : Numbers(board, view);
                        canvas.DrawText($"{document.Title} · лист {name} ({row + 1}-{column + 1} из {rows}×{columns})" + numbers,
                            Margin, Margin + 12, SKTextAlign.Left, labelFont, labelPaint);
                        canvas.Save();
                        canvas.Translate(Margin + bleed, Margin + Header + bleed);
                        canvas.ClipRect(new SKRect(-bleed, -bleed, printW + bleed, printH + bleed));
                        canvas.Scale((float)scale);
                        canvas.Translate((float)-left, (float)-top);
                        SceneRenderer.Render(canvas, document, new RenderOptions
                        {
                            IsExport = true,
                            IncludeBaseColor = true,
                            IncludeExportGrids = options.IncludeGrid,
                            VisibleWorld = new WorldBounds(view.Left - bleed / scale, view.Top - bleed / scale, view.Right + bleed / scale, view.Bottom + bleed / scale),
                            ViewScale = scale * 300 / 72.0,
                            HexPlayerView = scene.Players,
                            HexLevel = scene.HexLevel,
                            TileCache = tiles,
                            Stamps = stamps,
                        });
                        canvas.Restore();
                        var trim = new SKRect(Margin + bleed, Margin + Header + bleed,
                            Margin + bleed + printW, Margin + Header + bleed + printH);
                        canvas.DrawRect(trim, frame);
                        if (options.CropMarks) DrawCropMarks(canvas, trim, bleed);
                        pdf.EndPage();
                    }
                }

                if (board is not null && options.IncludeKey && key is { Count: > 0 })
                {
                    keyPages = WriteKey(pdf, document.Title, key, pageW, pageH, Margin, typeface, options.Players);
                }

                pdf.Close();
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return new HexPrintResult(path, columns * rows, keyPages, columns, rows) { OverviewPages = options.IncludeOverview ? 1 : 0 };
    }

    /// <summary>The first and last hex number fully on a sheet, for its label.</summary>
    private static string Numbers(HexBoard board, WorldBounds view)
    {
        var hexes = board.HexesIn(view).Where(hex =>
        {
            var b = board.Layout.Bounds(hex);
            return b.Left >= view.Left && b.Right <= view.Right && b.Top >= view.Top && b.Bottom <= view.Bottom;
        }).Select(board.Layout.ToOffset).ToList();
        if (hexes.Count == 0) return "";
        var first = hexes.MinBy(o => (o.Column, o.Row));
        var last = hexes.MaxBy(o => (o.Column, o.Row));
        return $" · гексы {HexLayerRenderer.NumberOf(board, first)}–{HexLayerRenderer.NumberOf(board, last)}";
    }

    private static int WriteKey(SKDocument pdf, string title, IReadOnlyList<(string Number, HexNote Note)> key,
        float pageW, float pageH, float margin, SKTypeface typeface, bool players)
    {
        using var heading = new SKFont(typeface, 16) { Embolden = true };
        using var entryTitle = new SKFont(typeface, 11.5f) { Embolden = true };
        using var body = new SKFont(typeface, 10.5f);
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0x20, 0x1A, 0x14) };
        using var faint = new SKPaint { IsAntialias = true, Color = new SKColor(0x70, 0x60, 0x50) };
        var pages = 0;
        SKCanvas? canvas = null;
        var y = 0f;
        var width = pageW - 2 * margin;
        void NewPage()
        {
            if (canvas is not null) pdf.EndPage();
            canvas = pdf.BeginPage(pageW, pageH);
            canvas.Clear(SKColors.White);
            pages++;
            y = margin + 18;
            canvas.DrawText($"{title} — ключ гексов{(players ? " (для игроков)" : "")}", margin, y, SKTextAlign.Left, heading, ink);
            y += 22;
        }

        void Line(string text, SKFont font, SKPaint paint)
        {
            foreach (var wrapped in Wrap(text, font, width))
            {
                if (canvas is null || y + font.Size > pageH - margin) NewPage();
                y += font.Size * 1.35f;
                canvas!.DrawText(wrapped, margin, y, SKTextAlign.Left, font, paint);
            }
        }

        NewPage();
        foreach (var (number, note) in key)
        {
            y += 6;
            Line($"{number}{(note.Title.Length > 0 ? " — " + note.Title : "")}", entryTitle, ink);
            if (!players && note.GmText.Length > 0) Line("Мастеру: " + note.GmText.Trim(), body, ink);
            if (note.PlayerText.Length > 0) Line((players ? "" : "Игрокам: ") + note.PlayerText.Trim(), body, players ? ink : faint);
        }

        pdf.EndPage();
        return pages;
    }

    /// <summary>Words wrapped into lines that fit a width; explicit line breaks are kept.</summary>
    private static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        foreach (var paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureText(candidate) <= width || line.Length == 0)
                {
                    line = candidate;
                    continue;
                }

                yield return line;
                line = word;
            }

            yield return line;
        }
    }
}
