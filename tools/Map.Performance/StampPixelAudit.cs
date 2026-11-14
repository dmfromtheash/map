using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Performance;

/// <summary>Auxiliary source-pixel diagnosis of the medium fixture, never live FPS evidence.</summary>
internal static class StampPixelAudit
{
    public static object Run(AssetCatalog catalog, PerformanceOptions options)
    {
        var records = catalog.Records.OrderBy(r => r.Sha256, StringComparer.Ordinal).ToArray();
        var random = new Random(options.Seed);
        random.Shuffle(records);
        var points = new (double X, double Y)[] { (1130,222), (1084,472), (700,526), (476,613), (1083,632), (916,881) };
        const double scale = .0214921875 * 1.25;
        var document = new MapDocument(DocumentId.New(), "Pixel diagnosis", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        using var old = new CatalogStampImageSource(catalog, () => document, () => null);
        using var prepared = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Environment.GetEnvironmentVariable("MAP_PERF_DERIVED_ROOT"));
        var rows = new List<object>();
        for (var i = 0; i < 100000; i++)
        {
            var r = records[i % records.Length];
            var width = 64 * (.5 + random.NextDouble() * 1.5);
            var x = random.NextDouble() * (32768 - width);
            var y = random.NextDouble() * (32768 - width);
            var height = width * r.Height / r.Width;
            var angle = random.Next(-180, 181);
            var radius = Math.Sqrt(width * width + height * height) * scale / 2 + 4;
            if (!points.Any(p => Math.Abs(p.X - (295 + (x + width / 2) * scale)) < radius
                && Math.Abs(p.Y - (18 + (y + height / 2) * scale)) < radius)) continue;
            // Reproduce DrawStamp's float rectangle and rotated matrix, not the double preflight.
            var rect = new SKRect((float)x, (float)y, (float)(x + width), (float)(y + height));
            var matrix = SKMatrix.CreateScale((float)scale, (float)scale)
                .PreConcat(SKMatrix.CreateRotationDegrees(angle));
            var actualScale = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
            var pw = rect.Width * actualScale; var ph = rect.Height * actualScale;
            var expected = old.TryGetImageForPixelSize(r.Sha256, pw, ph)!;
            SKImage? actual = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (actual is null && DateTime.UtcNow < deadline)
            {
                using (prepared.BeginFrame())
                {
                    prepared.PrepareForPixelSizes([(r.Sha256, pw, ph)], () => { });
                    actual = prepared.TryGetImageForPixelSize(r.Sha256, pw, ph);
                    if (actual is not null)
                    {
                        using var a = new SKBitmap(new SKImageInfo(expected.Width, expected.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                        using var b = new SKBitmap(new SKImageInfo(actual.Width, actual.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                        expected.ReadPixels(a.Info, a.GetPixels(), a.RowBytes, 0, 0);
                        actual.ReadPixels(b.Info, b.GetPixels(), b.RowBytes, 0, 0);
                        var same = a.Width == b.Width && a.Height == b.Height && a.Bytes.AsSpan().SequenceEqual(b.Bytes);
                        var row = new { i, r.Sha256, r.DisplayName, r.Width, r.Height, pw, ph,
                            expectedSize = new[] { a.Width, a.Height }, actualSize = new[] { b.Width, b.Height }, same };
                        rows.Add(row); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(row));
                    }
                }
                if (actual is null) Thread.Sleep(5);
            }
            if (actual is null) throw new TimeoutException("Pixel preparation: " + r.Sha256);
        }
        prepared.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return new { mode = "БЕЗ ОКНА: auxiliary mip comparison", rows };
    }
}
