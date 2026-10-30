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
/// external disk) in MAP_GIANT_MODES (comma list: png, tiles, mbtiles, viewer; default png) at
/// MAP_GIANT_SCALE (1, 2 or 4; default 1) or MAP_GIANT_EDGE (exact output side in pixels, e.g. 98304 to
/// fit the download into a size budget) and reports time, size and peak memory per mode.
/// MAP_OWNER_ART_LIBRARY = a COPY of %LOCALAPPDATA%\Map\Assets (the owner's mountains).
/// MAP_GIANT_VERIFY=1 re-exports each PNG on the one-worker route and compares the unpacked rows
/// (holds the packed data in memory: for MAP_GIANT_REGION samples and edges up to 16 384).
/// Every id and seed of the map is fixed, so separate runs give byte-identical rows too.
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
        // MAP_GIANT_REGION = "x,y,side": export only that square of the canvas (scaled to MAP_GIANT_EDGE) — a sample for codec trials.
        var region = Environment.GetEnvironmentVariable("MAP_GIANT_REGION")?.Split(',') is [var rx, var ry, var rs]
            ? new Map.Core.Editing.CanvasRegion(int.Parse(rx), int.Parse(ry), int.Parse(rs), int.Parse(rs)) : (Map.Core.Editing.CanvasRegion?)null;
        var exactEdge = int.TryParse(Environment.GetEnvironmentVariable("MAP_GIANT_EDGE"), out var e) && e > 0 ? e : 0;
        var count = int.TryParse(Environment.GetEnvironmentVariable("MAP_GIANT_OBJECTS"), out var n) ? n : 40_000;
        Directory.CreateDirectory(output);

        var clock = Stopwatch.StartNew();
        var catalog = new AssetCatalog(library);
        var mountains = catalog.Records.Where(r => r.Pack == "Мои горы").ToArray();
        if (mountains.Length == 0) throw new InvalidOperationException("The library copy has no «Мои горы».");
        var document = Build(mountains, count, report);
        report.Map["build-seconds"] = clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"map built in {clock.Elapsed.TotalSeconds:0.0} s: {string.Join(", ", report.Map.Select(p => p.Key + "=" + p.Value))}");

        // MAP_EXPORT_WORKERS=1 measures the serial route (one render and one compression worker).
        var engine = int.TryParse(Environment.GetEnvironmentVariable("MAP_EXPORT_WORKERS"), out var workers) && workers > 0
            ? new ImageExportEngine(new ExportEngineSettings { RenderParallelism = workers, CompressionParallelism = workers })
            : new ImageExportEngine();
        report.Map["export-workers"] = workers > 0 ? workers.ToString(System.Globalization.CultureInfo.InvariantCulture) : "default";
        foreach (var mode in modes)
        {
            var outputMode = mode switch
            {
                "tiles" => ExportOutputMode.TilePackage,
                "mbtiles" => ExportOutputMode.MbTiles,
                "viewer" => ExportOutputMode.ViewerFolder,
                _ => ExportOutputMode.SingleImage,
            };
            var edge = exactEdge > 0 ? exactEdge : Size * scale;
            var request = new ExportRequest(ExportImageFormat.Png, region is null ? ExportAreaMode.WholeCanvas : ExportAreaMode.Region, region, edge, edge, outputMode,
                TransparentBackground: false, IncludeGrid: false);
            var plan = engine.Plan(document, request);
            var name = (exactEdge > 0 ? $"giant-{edge}px" : $"giant-{scale}x") + outputMode switch
            {
                ExportOutputMode.TilePackage => "-tiles",
                ExportOutputMode.MbTiles => MbTilesWriter.Extension,
                ExportOutputMode.ViewerFolder => "-viewer",
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
            // MAP_GIANT_VERIFY=1: draw the same PNG again on the one-worker route and compare the unpacked
            // rows (repeatability, owner 2026-10-29: "the pixels must not change"). The copy is deleted at once.
            if (outputMode == ExportOutputMode.SingleImage && Environment.GetEnvironmentVariable("MAP_GIANT_VERIFY") == "1")
            {
                var check = Path.Combine(output, "verify-" + name);
                var serial = new ImageExportEngine(new ExportEngineSettings { RenderParallelism = 1, CompressionParallelism = 1 });
                using (var again = new ExportStampImageSource(document, null, catalog))
                    await serial.ExportAsync(document, serial.Plan(document, request), check, again);
                try { row["same-pixels-as-serial"] = (UnpackedRowsHash(result.DestinationPath) == UnpackedRowsHash(check)) ? "yes" : "NO"; }
                finally { File.Delete(check); }
                if (row["same-pixels-as-serial"] != "yes") report.Failure = "The serial and default exports differ in pixels.";
            }
            report.Exports.Add(row);
            Console.WriteLine("DONE " + string.Join(", ", row.Select(p => p.Key + "=" + p.Value)));
        }
    }

    /// <summary>SHA-256 of the inflated IDAT stream: filter bytes and rows, independent of how deflate packed them.</summary>
    private static string UnpackedRowsHash(string png)
    {
        using var file = File.OpenRead(png);
        file.Position = 8;
        using var idat = new MemoryStream();
        var header = new byte[8];
        while (file.Read(header) == 8)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
            if (System.Text.Encoding.ASCII.GetString(header, 4, 4) == "IDAT")
            {
                var data = new byte[length];
                file.ReadExactly(data);
                idat.Write(data);
            }
            else file.Position += length;
            file.Position += 4;
        }
        idat.Position = 0;
        using var rows = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rows));
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
            Name = "Серебрянка", Style = CurrentLook.River(),
            Channels = [RiverEditor.NewChannel([new(c - 30_000, c - 30_000), new(c - 12_000, c - 9_000), new(c - 2_000, c + 6_000), new(c + 14_000, c + 20_000), new(c + 46_000, c + 30_000), new(c + 60_000, c + 31_000)], 420)],
        };
        var road = RoadEditor.NewRoad([new(c - 40_000, c + 12_000), new(c - 15_000, c + 3_000), new(c + 5_000, c + 9_000), new(c + 30_000, c - 4_000)], 160, RoadStyle.Country(), "Королевский тракт");
        // River and road details are seeded from their ids: fixed ids make every run draw the same
        // pixels, so two runs (or this map in a test) can be compared byte for byte.
        var ids = new Random(20261030);
        Guid Id() { var bytes = new byte[16]; ids.NextBytes(bytes); return new Guid(bytes); }
        river = river with { Id = Id(), Channels = [.. river.Channels.Select(ch => ch with { Id = Id(), Nodes = [.. ch.Nodes.Select(n => n with { Id = Id() })] })] };
        road = road with { Id = Id(), Seed = 20261030, Branches = [.. road.Branches.Select(b => b with { Id = Id() })] };
        land = land with { Id = new LayerId(Id()) };
        high = high with { Id = new LayerId(Id()) };

        var random = new Random(20261021);
        var objects = new List<SceneObject>(count + 3);
        while (objects.Count < count)
        {
            var record = mountains[random.Next(mountains.Length)];
            var w = 700 + random.NextDouble() * 1_800;
            var h = w * record.Height / Math.Max(1, record.Width);
            var x = random.NextDouble() * (Size - w); var y = random.NextDouble() * (Size - h);
            if (Value(continent, x + w / 2, y + h * 0.85) < 1) continue;
            objects.Add(new SceneObject(new ObjectId(Id()), SceneObjectKind.Stamp, record.DisplayName, x, y, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha256) });
        }

        var water = new ObjectLayer(new LayerId(Id()), "Реки и дороги", true, false, 1, []) { Rivers = [river], Roads = [road] };
        var art = new ObjectLayer(new LayerId(Id()), "Горы", true, false, 1, objects);
        var labels = new ObjectLayer(new LayerId(Id()), "Подписи", true, false, 1, []);
        var document = new MapDocument(new DocumentId(Id()), "Самая большая карта", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Size, Size, "px", "#FF6F9CB4"),
            [CurrentLook.Page("Море", "#FF6F9CB4", new LayerId(Id())), land, high, water, art, labels]);

        SceneObject Label(string content, LabelPreset preset, double size, TextPathBinding? path, double x = 0, double y = 0, double w = 1, double h = 1) =>
            new(new ObjectId(Id()), SceneObjectKind.Text, content, x, y, w, h, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
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
