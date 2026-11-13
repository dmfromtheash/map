using System.Diagnostics;
using System.Security.Cryptography;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>Same production image source and raster draws, old/new entry limits, identical byte budget.
/// Reads mounted owner catalogs; never imports assets or touches recent/variant metadata.</summary>
internal static class StampCacheProbe
{
    public static object Run(PerformanceOptions options)
    {
        var profile = Environment.GetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT")
            ?? throw new InvalidOperationException("Set an isolated MAP_UI_TEST_PROFILE_ROOT.");
        var catalog = new AssetCatalog(Path.Combine(profile, "catalog"));
        var root = Environment.GetEnvironmentVariable("MAP_DRIVE_LIBRARY_ROOTS") ?? @"D:\MapLibrary";
        catalog.SetMounts(Directory.GetDirectories(root).Where(p => File.Exists(Path.Combine(p, "catalog.json"))).Order().ToArray());
        var records = catalog.Records.OrderBy(r => r.DisplayName, StringComparer.Ordinal).Take(options.ObjectCount).ToArray();
        if (records.Length != options.ObjectCount) throw new InvalidOperationException("Insufficient distinct library images.");
        var objects = records.Select((r, i) => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, r.DisplayName,
            i % 10 * 96, i / 10 * 64, 64, 64.0 * r.Height / r.Width, i % 3 * 11,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(r.Sha256) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Cache probe", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", CurrentLook.PageColor),
            [CurrentLook.Page(), new ObjectLayer(LayerId.New(), "Objects", true, false, 1, objects)]);
        var passes = new List<object>();
        string? expected = null;
        foreach (var capacity in new[] { 64, 4096 })
        {
            using var source = new CatalogStampImageSource(catalog, () => document, () => null, capacity);
            using var surface = SKSurface.Create(new SKImageInfo(1024, 768))!;
            for (var pass = 0; pass < 3; pass++)
            {
                var before = source.CacheStatistics;
                var watch = Stopwatch.StartNew();
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = source });
                surface.Canvas.Flush();
                watch.Stop();
                var after = source.CacheStatistics;
                using var snapshot = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(snapshot);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bitmap.Bytes));
                expected ??= hash;
                var row = new { capacity, pass, ms = watch.Elapsed.TotalMilliseconds,
                    misses = after.Misses - before.Misses, evictions = after.Evictions - before.Evictions,
                    after.Entries, after.ResidentBytes, hash, samePixels = hash == expected };
                passes.Add(row);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(row));
            }
        }
        return new { objects = records.Length, library = root, passes };
    }
}
