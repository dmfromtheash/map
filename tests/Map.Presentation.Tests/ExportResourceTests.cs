using System.IO.Compression;
using Map.Core.Documents;
using Map.Export;

namespace Map.Presentation.Tests;

public sealed class ExportResourceTests
{
    [Theory]
    [InlineData(16_384, 403_016_696L)]
    [InlineData(131_072, 19_685_812_725L)]
    public void Opaque_png_budget_uses_rgb_compression_band_and_reserve(int edge, long expected)
    {
        var plan = Plan(edge);
        Assert.True(plan.ExpectsOpaquePng);
        Assert.Equal(expected, plan.RequiredFreeDiskBytes);
        Assert.True(plan.RequiredFreeDiskCeilingBytes > (long)edge * edge * 4);
        Assert.True(plan.RequiredFreeDiskBytes < plan.RequiredFreeDiskCeilingBytes / 2);
    }

    [Theory]
    [InlineData(false, true, 1, "#FF304050", true)]
    [InlineData(true, true, 1, "#FF304050", false)]
    [InlineData(false, false, 1, "#FF304050", false)]
    [InlineData(false, true, .5, "#FF304050", false)]
    [InlineData(false, true, 1, "#80304050", false)]
    public void Disk_budget_follows_the_actual_background_route(bool transparent, bool visible,
        double opacity, string color, bool opaque)
    {
        var document = Document();
        document = document.WithLayer(document.Layers[0].Id,
            layer => ((BaseColorLayer)layer) with { Visible = visible, Opacity = opacity, Color = color });
        var engine = new ImageExportEngine();
        var plan = engine.Plan(document, Request(16_384) with { TransparentBackground = transparent });
        Assert.Equal(opaque, plan.ExpectsOpaquePng);
        Assert.Equal(opaque, ImageExportEngine.ExpectOpaque(plan.CreateScene(document).RenderDocument(), plan));
        Assert.Equal(opaque ? 403_016_696L : 503_799_087L, plan.RequiredFreeDiskBytes);
    }

    [Fact]
    public void Excluded_background_does_not_get_the_rgb_budget()
    {
        var document = Document();
        var plan = new ImageExportEngine().Plan(document, Request(16_384) with { VisibleLayerIds = [] });
        Assert.False(plan.ExpectsOpaquePng);
    }

    [Theory]
    [InlineData(ExportOutputMode.MbTiles, 16_384, 1_219_563_319L)]
    [InlineData(ExportOutputMode.ViewerFolder, 16_384, 1_219_563_319L)]
    // The measured giant (10.9 GB PNG) fits the owner's 79 GB free disk under this budget.
    [InlineData(ExportOutputMode.MbTiles, 131_072, 73_874_750_276L)]
    public void Pyramid_budget_includes_source_png_levels_and_sibling_result(
        ExportOutputMode mode, int edge, long expected)
    {
        var plan = Plan(edge, mode);
        var disk = ExportDiskEstimate.Create(plan);
        Assert.Equal(expected, plan.RequiredFreeDiskBytes);
        Assert.True(disk.PyramidBytes > (long)edge * edge * 1.125 * 1.33);
        Assert.True(disk.ResultBytes > disk.PyramidBytes);
        Assert.Equal(disk.PngBytes + disk.PyramidBytes + disk.ResultBytes + ExportDiskEstimate.ReserveBytes,
            disk.CombinedBytes);
    }

    [Fact]
    public void Thin_odd_pyramid_counts_all_levels_instead_of_assuming_four_thirds()
    {
        var plan = new ImageExportEngine().Plan(Document(), Request(2049, ExportOutputMode.MbTiles) with { OutputHeight = 1 });
        var disk = ExportDiskEstimate.Create(plan);
        Assert.True(disk.PyramidBytes > (2049L + 1025 + 513) * 9 / 8 + 6 * 4096);
    }

    [Theory]
    [InlineData(ExportOutputMode.SingleImage)]
    [InlineData(ExportOutputMode.MbTiles)]
    [InlineData(ExportOutputMode.ViewerFolder)]
    public void Shared_disk_is_checked_for_the_combined_peak_once(ExportOutputMode mode)
    {
        var plan = Plan(16_384, mode);
        var probes = new List<string>();
        var settings = Settings(path => { probes.Add(path); return plan.RequiredFreeDiskBytes; });
        var locations = ExportResourceAdmission.Check(plan, @"D:\result\map.png", settings, @"D:\temp");
        Assert.Single(probes);
        Assert.Equal(@"D:\temp", locations.ScratchRoot);
        Assert.Equal(@"D:\temp", locations.RasterRoot);
        Assert.Throws<ExportValidationException>(() => ExportResourceAdmission.Check(plan, @"D:\result\map.png",
            Settings(_ => plan.RequiredFreeDiskBytes - 1), @"D:\temp"));
    }

    [Theory]
    [InlineData(ExportOutputMode.SingleImage)]
    [InlineData(ExportOutputMode.MbTiles)]
    [InlineData(ExportOutputMode.ViewerFolder)]
    public void Separate_disks_are_admitted_with_their_own_peak(ExportOutputMode mode)
    {
        var plan = Plan(16_384, mode);
        var disk = ExportDiskEstimate.Create(plan);
        var probes = new List<string>();
        var settings = Settings(path =>
        {
            probes.Add(path);
            return path.StartsWith("E:", StringComparison.Ordinal) ? disk.TemporaryBytes : disk.DestinationBytes;
        });
        var locations = ExportResourceAdmission.Check(plan, @"D:\result\map.png", settings, @"E:\temp");
        Assert.Equal(new[] { @"E:\temp", @"D:\result\map.png" }, probes);
        Assert.Equal(@"E:\temp", locations.ScratchRoot);
        Assert.Throws<ExportValidationException>(() => ExportResourceAdmission.Check(plan, @"D:\result\map.png",
            Settings(path => path.StartsWith("E:", StringComparison.Ordinal) ? disk.TemporaryBytes : disk.DestinationBytes - 1), @"E:\temp"));
    }

    [Theory]
    [InlineData(ExportOutputMode.SingleImage)]
    [InlineData(ExportOutputMode.MbTiles)]
    [InlineData(ExportOutputMode.ViewerFolder)]
    public void Insufficient_temp_falls_back_only_if_destination_holds_the_combined_peak(ExportOutputMode mode)
    {
        var plan = Plan(16_384, mode);
        var disk = ExportDiskEstimate.Create(plan);
        var locations = ExportResourceAdmission.Check(plan, @"D:\result\map.png",
            Settings(path => path.StartsWith("E:", StringComparison.Ordinal) ? disk.TemporaryBytes - 1 : disk.CombinedBytes), @"E:\temp");
        Assert.Equal(@"D:\result", locations.ScratchRoot);
        Assert.Equal(@"D:\result", locations.RasterRoot);
        Assert.Throws<ExportValidationException>(() => ExportResourceAdmission.Check(plan, @"D:\result\map.png",
            Settings(path => path.StartsWith("E:", StringComparison.Ordinal) ? disk.TemporaryBytes - 1 : disk.CombinedBytes - 1), @"E:\temp"));
    }

    [Fact]
    public void Unreadable_temp_falls_back_to_the_admitted_destination()
    {
        var plan = Plan(16_384, ExportOutputMode.MbTiles);
        var locations = ExportResourceAdmission.Check(plan, @"D:\result\map.mbtiles",
            Settings(path => path.StartsWith("E:", StringComparison.Ordinal) ? throw new IOException("offline") : plan.RequiredFreeDiskBytes), @"E:\temp");
        Assert.Equal(@"D:\result", locations.ScratchRoot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Incompressible_noise_can_exceed_the_budget_but_fits_the_documented_ceiling(bool opaque)
    {
        var plan = Plan(512) with { ExpectsOpaquePng = opaque };
        using var stream = new MemoryStream();
        using (var writer = new StreamingPngWriter(stream, 512, 512, opaque, CompressionLevel.Optimal, parallelism: 2))
        {
            var random = new Random(71);
            var row = new byte[512 * 4];
            for (var y = 0; y < 512; y++)
            {
                random.NextBytes(row);
                if (opaque) for (var i = 3; i < row.Length; i += 4) row[i] = 255;
                writer.WriteRow(row);
            }
            writer.Complete();
        }
        Assert.True(stream.Length > ExportDiskEstimate.Create(plan).ResultBytes);
        Assert.True(stream.Length <= ExportDiskEstimate.Create(plan, incompressible: true).ResultBytes);
    }

    private static ExportEngineSettings Settings(Func<string, long> disk) => new()
    {
        AvailableMemoryBytes = () => long.MaxValue,
        AvailableDiskBytes = disk,
    };

    private static ExportPlan Plan(int edge, ExportOutputMode mode = ExportOutputMode.SingleImage) =>
        new ImageExportEngine().Plan(Document(), Request(edge, mode));

    private static ExportRequest Request(int edge, ExportOutputMode mode = ExportOutputMode.SingleImage) =>
        new(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, edge, edge, mode, false, false);

    private static MapDocument Document() => new(DocumentId.New(), "Disk estimate", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(131_072, 131_072, "px", "#FF304050"),
        [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF304050")]);
}
