using System.Diagnostics;
using System.Globalization;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R12 (`--profile giant-coast-ab`): the coast on the largest map, 131 072 × 131 072 (the map of
/// <see cref="NativeGiantExport"/>, without mountains). Each place on its coast is drawn twice at full size — as one
/// distance field (A) and in field tiles, the way a view over <see cref="CoastDistanceField.PixelBudget"/> is drawn (B) —
/// and the two pictures are compared pixel by pixel; then a view over the budget is drawn the only way it can be, in
/// tiles, and timed. Writes <c>&lt;output&gt;.&lt;place&gt;-a.jpg</c> and <c>-b.jpg</c> and the numbers into the report.
/// </summary>
internal static class GiantCoastAb
{
    private const int Edge = 3072;

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var clock = Stopwatch.StartNew();
        var document = NativeGiantExport.Build([], 0, new NativeGiantExportReport());
        report.Notes["build-seconds"] = clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

        const double c = CanvasDimensionPolicy.Maximum / 2.0;
        // The shore of the continent to the west and to the south, and the wall of the plateau.
        (string Key, double X, double Y, double Scale)[] places =
        [
            ("west", NativeGiantExport.Shore(0, c, c, -1, 0), c, 1),
            ("south", c, NativeGiantExport.Shore(0, c, c, 0, 1), 1),
            ("plateau", c + 9_000, NativeGiantExport.Shore(1, c + 9_000, c - 6_000, 0, 1), 1),
            // The same shore from far away: a sixteenth of the size, 49 152 map units across.
            ("west-far", NativeGiantExport.Shore(0, c, c, -1, 0), c, 1 / 16.0),
        ];
        var (budget, tile) = (CoastDistanceField.Budget, CoastDistanceField.Tile);
        var same = true;
        try
        {
            foreach (var (key, x, y, scale) in places)
            {
                var half = Edge / scale / 2;
                var view = new WorldBounds(x - half, y - half, x + half, y + half);
                CoastDistanceField.Budget = CoastDistanceField.PixelBudget; CoastDistanceField.Tile = CoastDistanceField.TileEdge;
                using var whole = Draw(document, view, Edge, out var wholeMs);
                CoastDistanceField.Budget = 1; CoastDistanceField.Tile = 1024;
                using var tiled = Draw(document, view, Edge, out var tiledMs);
                var (share, worst) = Difference(whole, tiled);
                // The filter passes (no field at all) draw the coast a little differently: that they differ from A
                // while the tiles do not shows B really went through the tiles.
                CoastDistanceField.Tile = 1;
                using var filters = Draw(document, view, Edge, out _);
                var (filterShare, _) = Difference(whole, filters);
                report.Notes[key] = string.Create(CultureInfo.InvariantCulture,
                    $"one-field={wholeMs:0} ms tiles={tiledMs:0} ms differing-pixels={share:0.0000}% worst-channel={worst} filters-differ={filterShare:0.00}%");
                // A pixel in ten thousand may round the other way where two field tiles meet.
                same &= share <= 0.01 && worst <= 12;
                Save(whole, Path.ChangeExtension(options.OutputPath, "." + key + "-a.jpg"), report);
                Save(tiled, Path.ChangeExtension(options.OutputPath, "." + key + "-b.jpg"), report);
            }

            // Over the budget (8 192² is 67 megapixels): only the tiles can draw it.
            CoastDistanceField.Budget = CoastDistanceField.PixelBudget; CoastDistanceField.Tile = CoastDistanceField.TileEdge;
            var wide = new WorldBounds(places[0].X - 4096, c - 4096, places[0].X + 4096, c + 4096);
            using var large = Draw(document, wide, 8192, out var largeMs);
            report.Notes["west-8192"] = string.Create(CultureInfo.InvariantCulture, $"tiles={largeMs:0} ms");
            using var process = Process.GetCurrentProcess();
            report.Notes["peak-working-set-mib"] = (process.PeakWorkingSet64 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        }
        finally { CoastDistanceField.Budget = budget; CoastDistanceField.Tile = tile; }
        report.GatePassed = same && report.Images.Count == places.Length * 2;
        return report;
    }

    private static SKBitmap Draw(MapDocument document, WorldBounds view, int edge, out double milliseconds)
    {
        var info = new SKImageInfo(edge, edge, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var scale = edge / (view.Right - view.Left);
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        var started = Stopwatch.GetTimestamp();
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale });
        surface.Flush();
        milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    /// <summary>Share of pixels (per cent) with any channel more than 2 apart, and the largest channel difference.</summary>
    private static (double Share, int Worst) Difference(SKBitmap a, SKBitmap b)
    {
        var first = a.GetPixelSpan(); var second = b.GetPixelSpan();
        long differing = 0; var worst = 0;
        for (var i = 0; i < first.Length; i += 4)
        {
            var d = Math.Max(Math.Abs(first[i] - second[i]), Math.Max(Math.Abs(first[i + 1] - second[i + 1]), Math.Abs(first[i + 2] - second[i + 2])));
            if (d > 2) differing++;
            if (d > worst) worst = d;
        }
        return (differing * 100.0 / (first.Length / 4), worst);
    }

    private static void Save(SKBitmap bitmap, string path, AreaBeautySheetReport report)
    {
        using var small = bitmap.Resize(new SKImageInfo(1200, 1200), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = SKImage.FromBitmap(small); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using (var output = File.Create(path)) data.SaveTo(output);
        report.Images.Add(path);
    }
}
