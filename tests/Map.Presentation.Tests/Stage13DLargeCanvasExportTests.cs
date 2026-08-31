using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage13DLargeCanvasExportTests
{
    [Fact]
    public void Export_planner_keeps_single_at_16k_and_bounds_the_131k_tile_package()
    {
        var document = Document();
        var exporter = new ImageExportEngine();

        var package = exporter.Plan(document, Request(
            131_072, 131_072, ExportOutputMode.TilePackage));
        var single = exporter.Plan(document, Request(
            16_384, 16, ExportOutputMode.SingleImage));

        Assert.Equal((64, 64, 4_096), (package.Columns, package.Rows, package.TileCount));
        Assert.Equal(131_072, ExportEngineSettings.DefaultMaxOutputEdge);
        Assert.Equal(16_384, single.OutputWidth);
        Assert.True(package.TileEdge + (2 * package.EffectBleedPixels)
            <= new ExportEngineSettings().MaxTileSurfaceEdge);
        Assert.Throws<ExportValidationException>(() => exporter.Plan(
            document, Request(16_385, 16, ExportOutputMode.SingleImage)));
        Assert.Throws<ExportValidationException>(() => exporter.Plan(
            document, Request(131_073, 16, ExportOutputMode.TilePackage)));
    }

    [Fact]
    public async Task Narrow_16k_single_and_over_16k_tile_package_are_written_and_validated()
    {
        using var temp = new TempDirectory();
        var allocations = new List<ExportSurfaceAllocation>();
        var settings = new ExportEngineSettings { SurfaceAllocated = allocations.Add };
        var exporter = new ImageExportEngine(settings);
        var document = Document();

        var singlePath = Path.Combine(temp.Path, "boundary-16k.png");
        var singlePlan = exporter.Plan(document, Request(
            16_384, 16, ExportOutputMode.SingleImage));
        var singleResult = await exporter.ExportAsync(
            document, singlePlan, singlePath, stamps: null);
        using (var bitmap = SKBitmap.Decode(singlePath))
        {
            Assert.Equal((16_384, 16), (bitmap.Width, bitmap.Height));
        }
        Assert.Equal(1, singleResult.FileCount);

        var packagePath = Path.Combine(temp.Path, "over-16k-tiles");
        var packagePlan = exporter.Plan(document, Request(
            20_000, 16, ExportOutputMode.TilePackage));
        var packageResult = await exporter.ExportAsync(
            document, packagePlan, packagePath, stamps: null);
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(
            await File.ReadAllBytesAsync(Path.Combine(packagePath, "manifest.json")))!;

        Assert.Equal((20_000, 16), (manifest.OutputWidth, manifest.OutputHeight));
        Assert.Equal((10, 1, 10), (manifest.Columns, manifest.Rows, manifest.Tiles.Count));
        Assert.Equal(11, packageResult.FileCount);
        Assert.All(manifest.Tiles, tile =>
        {
            Assert.True(File.Exists(Path.Combine(packagePath, tile.File)));
            Assert.InRange(tile.Width, 1, settings.TileEdge);
            Assert.InRange(tile.Height, 1, settings.TileEdge);
        });
        Assert.DoesNotContain(allocations, allocation =>
            allocation.Purpose == ExportSurfacePurpose.TileWithBleed
            && (allocation.Width > packagePlan.TileEdge + (2 * packagePlan.EffectBleedPixels)
                || allocation.Height > packagePlan.TileEdge + (2 * packagePlan.EffectBleedPixels)));
    }

    private static ExportRequest Request(
        int width, int height, ExportOutputMode mode) => new(
        ExportImageFormat.Png,
        ExportAreaMode.WholeCanvas,
        null,
        width,
        height,
        mode,
        TransparentBackground: false,
        IncludeGrid: true);

    private static MapDocument Document() => new(
        DocumentId.New(),
        "Stage 13D export",
        DateTimeOffset.UnixEpoch,
        new CanvasDefinition(131_072, 131_072, "px", "#FFCAD2DB"),
        [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFCAD2DB")]);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "map-stage13d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
