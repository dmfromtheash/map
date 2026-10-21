using System.Diagnostics;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Performance;

/// <summary>
/// The largest map and its full-size download (`--profile native-giant-export`): a 131 072 × 131 072
/// canvas with a sea, a continent and a raised plateau on the owner's grounds, a river, a road,
/// labels along both (R07), a country name and MAP_GIANT_OBJECTS (default 40 000) of the owner's
/// mountains on the land. Exports with the production engine to MAP_GIANT_OUT (a folder, e.g. the
/// external disk) in MAP_GIANT_MODES (comma list: png, tiles, mbtiles; default png) at
/// MAP_GIANT_SCALE (1, 2 or 4; default 1) and reports time, size and peak memory per mode.
/// MAP_OWNER_ART_LIBRARY = a COPY of %LOCALAPPDATA%\Map\Assets (the owner's mountains).
/// <code>dotnet tools/Map.Performance/bin/Release/net10.0/win-x64/Map.Performance.dll --profile native-giant-export --output artifacts/giant-export/report.json</code>
/// </summary>
internal sealed class NativeGiantExportReport
{
    public Dictionary<string, string> Map { get; } = [];
    public List<Dictionary<string, string>> Exports { get; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Exports.Count > 0;
}

internal static class NativeGiantExport
{
    private const int Size = CanvasDimensionPolicy.Maximum;

    public static NativeGiantExportReport Run(PerformanceOptions options)
    {
        var report = new NativeGiantExportReport();
        try
        {
            RunCore(report).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            report.Failure = ex.ToString();
            Console.WriteLine(ex);
        }

        return report;
    }

    private static async Task RunCore(NativeGiantExportReport report)
    {
        var output = Environment.GetEnvironmentVariable("MAP_GIANT_OUT") ?? throw new InvalidOperationException("MAP_GIANT_OUT is not set");
        var library = Environment.GetEnvironmentVariable("MAP_OWNER_ART_LIBRARY") ?? throw new InvalidOperationException("MAP_OWNER_ART_LIBRARY is not set");
        var modes = (Environment.GetEnvironmentVariable("MAP_GIANT_MODES") ?? "png").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scale = int.TryParse(Environment.GetEnvironmentVariable("MAP_GIANT_SCALE"), out var s) ? s : 1;
        var count = int.TryParse(Environment.GetEnvironmentVariable("MAP_GIANT_OBJECTS"), out var n) ? n : 40_000;
        Directory.CreateDirectory(output);

        var clock = Stopwatch.StartNew();
        var catalog = new AssetCatalog(library);
        var mountains = catalog.Records.Where(r => r.Pack == "Мои горы").ToArray();
        if (mountains.Length == 0) throw new InvalidOperationException("The library copy has no «Мои горы».");
        var document = Build(mountains, count, report);
        report.Map["build-seconds"] = clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"map built in {clock.Elapsed.TotalSeconds:0.0} s: {string.Join(", ", report.Map.Select(p => p.Key + "=" + p.Value))}");

        var engine = new ImageExportEngine();
        foreach (var mode in modes)
        {
            var outputMode = mode switch
            {
                "tiles" => ExportOutputMode.TilePackage,
                "mbtiles" => ExportOutputMode.MbTiles,
                _ => ExportOutputMode.SingleImage,
            };
            var edge = Size * scale;
            var request = new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, edge, edge, outputMode,
                TransparentBackground: false, IncludeGrid: false);
            var plan = engine.Plan(document, request);
            var name = $"giant-{scale}x" + outputMode switch
            {
                ExportOutputMode.TilePackage => "-tiles",
                ExportOutputMode.MbTiles => MbTilesWriter.Extension,
                _ => ".png",
            };
            var destination = Path.Combine(output, name);
            var started = Stopwatch.StartNew();
            var peak = 0L;
            var lastPrinted = -1;
            var progress = new Progress<ExportProgress>(update =>
            {
                using var process = Process.GetCurrentProcess();
                peak = Math.Max(peak, process.WorkingSet64);
                var percent = (int)(update.Fraction * 100);
                if (percent == lastPrinted) return;
                lastPrinted = percent;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {mode} {percent,3}% {update.Phase} · {started.Elapsed.TotalMinutes:0.0} min · {peak / (1024 * 1024)} MiB");
            });
            using var stamps = new ExportStampImageSource(document, null, catalog);
            var result = await engine.ExportAsync(document, plan, destination, stamps, progress);
            started.Stop();
            var bytes = File.Exists(result.DestinationPath)
                ? new FileInfo(result.DestinationPath).Length
                : Directory.EnumerateFiles(result.DestinationPath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var row = new Dictionary<string, string>
            {
                ["mode"] = mode, ["scale"] = scale.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pixels"] = $"{edge}×{edge}", ["path"] = result.DestinationPath, ["files"] = result.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["bytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["gib"] = (bytes / 1073741824.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["minutes"] = started.Elapsed.TotalMinutes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                ["peak-working-set-mib"] = (peak / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            report.Exports.Add(row);
            Console.WriteLine("DONE " + string.Join(", ", row.Select(p => p.Key + "=" + p.Value)));
        }
    }

    private static MapDocument Build(AssetRecord[] mountains, int count, NativeGiantExportReport report)
    {
        const double c = Size / 2.0;
        (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] continent =
            [(c, c, 50_000, 44_000, 0.12, 7), (c - 38_000, c + 34_000, 9_000, 7_000, 0.2, 5), (c + 41_000, c - 36_000, 7_500, 6_000, 0.2, 6)];
        (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] plateau = [(c + 9_000, c - 6_000, 16_000, 11_000, 0.15, 9)];
        var land = Land("Материк", 0, continent, MapFillTextures.OwnerSnow, report);
        var high = Land("Плато", 1, plateau, MapFillTextures.OwnerSnowSparkle, report);

        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new(c - 30_000, c - 30_000), new(c - 12_000, c - 9_000), new(c - 2_000, c + 6_000), new(c + 14_000, c + 20_000), new(c + 46_000, c + 30_000), new(c + 60_000, c + 31_000)], 420)],
        };
        var road = RoadEditor.NewRoad([new(c - 40_000, c + 12_000), new(c - 15_000, c + 3_000), new(c + 5_000, c + 9_000), new(c + 30_000, c - 4_000)], 160, RoadStyle.Country(), "Королевский тракт");

        var random = new Random(20261021);
        var objects = new List<SceneObject>(count + 3);
        while (objects.Count < count)
        {
            var record = mountains[random.Next(mountains.Length)];
            var w = 700 + random.NextDouble() * 1_800;
            var h = w * record.Height / Math.Max(1, record.Width);
            var x = random.NextDouble() * (Size - w); var y = random.NextDouble() * (Size - h);
            if (Value(continent, x + w / 2, y + h * 0.85) < 1) continue;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName, x, y, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha256) });
        }

        var water = new ObjectLayer(LayerId.New(), "Реки и дороги", true, false, 1, []) { Rivers = [river], Roads = [road] };
        var art = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects);
        var labels = new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Самая большая карта", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Size, Size, "px", "#FF6F9CB4"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF6F9CB4"), land, high, water, art, labels]);

        SceneObject Label(string content, LabelPreset preset, double size, TextPathBinding? path, double x = 0, double y = 0, double w = 1, double h = 1) =>
            new(ObjectId.New(), SceneObjectKind.Text, content, x, y, w, h, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
            { Text = LabelPresets.Apply(new TextStyle { Content = content, Alignment = TextAlignment.Center }, preset) with { FontSize = size, Path = path } };
        var riverLabel = TextOnPath.Layout(document, Label("Серебрянка", LabelPreset.River, 900,
            new TextPathBinding { Target = TextPathTarget.River, TargetId = river.Id, PartId = river.Channels[0].Id, Position = 0.45, Gap = 200 }));
        var roadLabel = TextOnPath.Layout(document, Label("Королевский тракт", LabelPreset.Village, 700,
            new TextPathBinding { Target = TextPathTarget.Road, TargetId = road.Id, PartId = road.Branches[0].Id, Position = 0.3, Gap = 160 }));
        var country = Label("АЛЬВАРИЯ", LabelPreset.Country, 6_000, null, c - 30_000, c - 26_000, 60_000, 9_000);
        document = document.WithLayer(labels.Id, l => ((ObjectLayer)l) with { Objects = [riverLabel, roadLabel, country] });
        report.Map["canvas"] = $"{Size}×{Size}";
        report.Map["mountains"] = objects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return document;
    }

    /// <summary>
    /// Land of the whole giant canvas without holding 16 GiB: every tile wholly inside shares one
    /// full coverage array; only the tiles the coast crosses get their own pixels.
    /// </summary>
    private static LandLayer Land(string name, int level, (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] shapes, string texture, NativeGiantExportReport report)
    {
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        var tilesPerSide = Size / TileMap.TileSize;
        int inside = 0, edge = 0;
        for (var ty = 0; ty < tilesPerSide; ty++)
        for (var tx = 0; tx < tilesPerSide; tx++)
        {
            double x0 = tx * TileMap.TileSize, y0 = ty * TileMap.TileSize, t = TileMap.TileSize;
            var samples = new[] { Value(shapes, x0, y0), Value(shapes, x0 + t, y0), Value(shapes, x0, y0 + t), Value(shapes, x0 + t, y0 + t), Value(shapes, x0 + t / 2, y0 + t / 2) };
            if (samples.All(v => v <= 0)) continue;
            if (samples.All(v => v >= 1)) { tiles = tiles.With(new TileKey(tx, ty), full); inside++; continue; }
            var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
            for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < TileMap.TileSize; x++)
                bytes[y * TileMap.TileSize + x] = (byte)Math.Clamp(Value(shapes, x0 + x, y0 + y) * 255, 0, 255);
            tiles = tiles.With(new TileKey(tx, ty), bytes);
            edge++;
        }

        report.Map[name + "-tiles"] = $"{inside} inside + {edge} coast";
        var style = Presentation.ViewModels.EditorViewModel.WithGroundTexture(LandEdgeStyle.SoftWatercolor(), texture);
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, style) { Level = level };
    }

    private static double Value((double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] shapes, double x, double y)
    {
        var best = 0.0;
        foreach (var s in shapes)
        {
            var angle = Math.Atan2(y - s.Cy, x - s.Cx);
            var wobble = 1 + s.Wobble * Math.Sin(angle * s.Lobes) + s.Wobble * .6 * Math.Sin(angle * (s.Lobes * 2 + 3) + 1.3);
            var distance = Math.Sqrt(Math.Pow((x - s.Cx) / s.Rx, 2) + Math.Pow((y - s.Cy) / s.Ry, 2)) / wobble;
            // A soft coast about 24 px wide.
            best = Math.Max(best, Math.Clamp((1 - distance) * Math.Max(s.Rx, s.Ry) / 24, 0, 1));
        }

        return best;
    }
}
