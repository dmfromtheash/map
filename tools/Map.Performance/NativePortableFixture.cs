using System.Diagnostics;
using Map.Assets;
using Map.Presentation.Services;

namespace Map.Performance;

public sealed partial class ZoomAssetsApplication
{
    // Opt-in evidence fixture: all writes stay in the disposable profile, never the
    // owner's mounted library. Both modes import identical SHA-sorted owner originals.
    private async Task<AssetRecord[]> PreparePortableFixture(AssetRecord[] records)
    {
        var mode = Environment.GetEnvironmentVariable("MAP_PERF_PORTABLE_FIXTURE");
        if (mode is null) return records;
        if (mode is not ("plain" or "portable"))
            throw new ArgumentException("MAP_PERF_PORTABLE_FIXTURE: plain or portable.");
        var clock = Stopwatch.StartNew();
        var paths = records.Take(100).Select(record => _catalog.SourcePath(record)).ToArray();
        _catalog.SetMounts([]);
        var imported = await Task.Run(() => paths.Select(path =>
            _catalog.Import(path, new(), DateTimeOffset.UnixEpoch)).ToArray());
        if (mode == "portable")
        {
            await using var builder = new StampDerivedAssetStore(_catalog, Path.Combine(_workspace, "portable-build-cache"));
            foreach (var record in imported)
                await builder.BuildPortableAsync(record.Sha256);
        }
        await _catalog.FlushMetadataAsync();
        // No machine cache from preparation is exposed to the measured reader.
        Environment.SetEnvironmentVariable("MAP_PERF_DERIVED_ROOT", Path.Combine(_workspace, "portable-reader-cache"));
        _report.Environment["portable-fixture"] = mode;
        _report.Environment["portable-fixture-images"] = imported.Length.ToString();
        _report.Environment["portable-fixture-preparation-ms"] = clock.Elapsed.TotalMilliseconds.ToString("F1");
        Console.WriteLine($"portable fixture: {mode}, {imported.Length} owner images, prepared before timing in {clock.Elapsed.TotalSeconds:F1}s");
        return imported;
    }
}
