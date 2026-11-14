using System.Diagnostics;
using Map.Assets;
using Map.Presentation.Services;

namespace Map.Performance;

internal static class DerivedBuildProbe
{
    public static async Task<object> RunAsync(PerformanceOptions options)
    {
        if (options.Catalogs.Count == 0) throw new ArgumentException("derived-build requires explicit --catalogs paths.");
        var roots = options.Catalogs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots)
            if (!File.Exists(Path.Combine(root, "catalog.json"))) throw new DirectoryNotFoundException("No catalog.json: " + root);
        var parent = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
        var workspace = Path.Combine(parent, "derived-build-work-" + Guid.NewGuid().ToString("N"));
        var rows = new List<object>();
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(workspace);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(workspace, "catalog"));
            for (var index = 0; index < roots.Length; index++)
            {
                catalog.SetMounts([roots[index]]);
                var records = catalog.Records.OrderBy(record => record.Sha256, StringComparer.Ordinal).ToArray();
                long bytes = 0;
                if (!options.DryRun)
                {
                    await using var store = new StampDerivedAssetStore(catalog, Path.Combine(workspace, "machine-" + index));
                    var completed = 0;
                    foreach (var record in records)
                    {
                        var path = await store.BuildPortableAsync(record.Sha256).ConfigureAwait(false);
                        bytes += new FileInfo(path).Length;
                        Console.WriteLine($"derived-build: {roots[index]} {++completed}/{records.Length} {record.Sha256}");
                    }
                }
                rows.Add(new { catalog = roots[index], images = records.Length, bytes, dryRun = options.DryRun });
            }
        }
        finally
        {
            // Only the newly created private workspace is removed, never a catalog or its derivatives.
            if (!Path.GetFullPath(workspace).StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected preparation workspace.");
            Directory.Delete(workspace, recursive: true);
        }
        return new { mode = "БЕЗ ОКНА: explicit portable-asset preparation", elapsedMs = clock.Elapsed.TotalMilliseconds, catalogs = rows };
    }
}
