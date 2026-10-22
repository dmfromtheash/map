using System.Security.Cryptography;
using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StreamingPngExportTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task Rows_tiles_alpha_and_enlargement_preserve_existing_renderer_and_original_asset_detail(int scale, bool transparent)
    {
        using var temp = new TempDirectory();
        var document = Fixture();
        var allocations = new List<ExportSurfaceAllocation>();
        var exporter = new ImageExportEngine(new ExportEngineSettings { TileEdge = 64, SurfaceAllocated = allocations.Add });
        var plan = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, 160 * scale, 96 * scale, ExportOutputMode.SingleImage, transparent, false));
        var path = Path.Combine(temp.Path, "streamed.png");
        using var stamps = new ExportStampImageSource(document, null, null);
        await exporter.ExportAsync(document, plan, path, stamps);

        using var actual = SKBitmap.Decode(path);
        using var reference = SKSurface.Create(new SKImageInfo(plan.OutputWidth, plan.OutputHeight,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        reference.Canvas.Clear(SKColors.Transparent);
        reference.Canvas.Scale(scale);
        SceneRenderer.Render(reference.Canvas, document, new RenderOptions
        {
            IsExport = true, IncludeBaseColor = !transparent, IncludeExportGrids = false,
            ViewScale = scale, Stamps = stamps,
        });
        using var snapshot = reference.Snapshot();
        using var referenceBytes = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var expected = SKBitmap.Decode(referenceBytes);
        // Skia's analytic antialiasing of a rotated curve can vary with clipping. Verify the
        // streaming codec against the existing separately encoded tile route, and verify the
        // original stamp independently against the direct whole-scene render below.
        var packagePath = Path.Combine(temp.Path, "tiles");
        var packagePlan = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, plan.OutputWidth, plan.OutputHeight, ExportOutputMode.TilePackage, transparent, false));
        await exporter.ExportAsync(document, packagePlan, packagePath, stamps);
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(
            File.ReadAllText(Path.Combine(packagePath, "manifest.json")))!;
        foreach (var tile in manifest.Tiles)
        {
            using var pixels = SKBitmap.Decode(Path.Combine(packagePath, tile.File));
            for (var y = 0; y < tile.Height; y++)
            for (var x = 0; x < tile.Width; x++)
                Assert.Equal(pixels.GetPixel(x, y), actual.GetPixel(tile.X + x, tile.Y + y));
        }
        for (var y = 24 * scale; y < 56 * scale; y++)
        for (var x = 48 * scale; x < 80 * scale; x++)
            Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        Assert.DoesNotContain(allocations, a => a.Purpose == ExportSurfacePurpose.SingleImage);
        Assert.All(allocations, a => Assert.True(a.Width <= plan.TileEdge + 2 * plan.EffectBleedPixels
            && a.Height <= plan.RasterTileHeight + 2 * plan.EffectBleedPixels));
        Assert.Single(Directory.GetFiles(temp.Path));
        if (scale == 4)
        {
            // The embedded 128px original is drawn into a 32-unit stamp. At 4x its 2px stripes
            // reappear at native resolution; upscaling the 1x preview would not recover them.
            var left = actual.GetPixel(48 * 4 + 40, 24 * 4 + 40);
            var next = actual.GetPixel(48 * 4 + 42, 24 * 4 + 40);
            Assert.True(Math.Abs(left.Red - next.Red) > 200);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_or_write_failure_after_a_band_preserves_old_file_and_removes_scratch(bool cancel)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "old.png");
        byte[] old = [9, 8, 7, 6];
        File.WriteAllBytes(path, old);
        using var cancellation = new CancellationTokenSource();
        var exporter = new ImageExportEngine(new ExportEngineSettings
        {
            TileEdge = 32,
            FaultInjector = point =>
            {
                if (point != ExportFaultPoint.AfterPngBand) return;
                if (cancel) cancellation.Cancel();
                else throw new IOException("Interrupted staged band");
            },
        });
        var document = Fixture();
        var plan = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, 160, 96, ExportOutputMode.SingleImage, false, false));
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                exporter.ExportAsync(document, plan, path, null, cancellationToken: cancellation.Token));
        else
            await Assert.ThrowsAsync<ExportOperationException>(() => exporter.ExportAsync(document, plan, path, null));
        Assert.Equal(old, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task Full_giga_width_png_is_encoded_with_small_surfaces_and_decodes_normally()
    {
        using var temp = new TempDirectory();
        var allocations = new List<ExportSurfaceAllocation>();
        var exporter = new ImageExportEngine(new ExportEngineSettings { SurfaceAllocated = allocations.Add });
        var document = new MapDocument(DocumentId.New(), "wide", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(131_072, 16, "px", "#FF304A60"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF304A60")]);
        var plan = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, 131_072, 16, ExportOutputMode.SingleImage, false, false));
        var path = Path.Combine(temp.Path, "wide.png");
        await exporter.ExportAsync(document, plan, path, null);
        using var decoded = SKBitmap.Decode(path);
        Assert.NotNull(decoded);
        Assert.Equal((131_072, 16), (decoded.Width, decoded.Height));
        Assert.Equal(new SKColor(0x30, 0x4a, 0x60), decoded.GetPixel(131_071, 15));
        Assert.DoesNotContain(allocations, a => a.Purpose == ExportSurfacePurpose.SingleImage);
    }

    [Theory]
    [InlineData(false, true, 2)]   // opaque page: RGB, a quarter fewer bytes before compression
    [InlineData(true, true, 6)]    // transparent background requested: RGBA
    [InlineData(false, false, 6)]  // page colour hidden: pixels may be transparent, RGBA
    public async Task An_opaque_map_is_written_as_rgb_with_the_same_pixels(bool transparent, bool pageVisible, int colorType)
    {
        using var temp = new TempDirectory();
        var document = Fixture();
        if (!pageVisible) document = document.WithLayer(document.Layers[0].Id, l => l with { Visible = false });
        var exporter = new ImageExportEngine(new ExportEngineSettings { TileEdge = 64 });
        var plan = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, 160, 96, ExportOutputMode.SingleImage, transparent, false));
        var path = Path.Combine(temp.Path, "map.png");
        using var stamps = new ExportStampImageSource(document, null, null);
        await exporter.ExportAsync(document, plan, path, stamps);
        var header = new byte[26];
        using (var file = File.OpenRead(path)) file.ReadExactly(header);
        Assert.Equal(colorType, header[25]);

        var tilesPath = Path.Combine(temp.Path, "tiles");
        await exporter.ExportAsync(document, exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
            null, 160, 96, ExportOutputMode.TilePackage, transparent, false)), tilesPath, stamps);
        var manifest = JsonSerializer.Deserialize<TilePackageManifest>(File.ReadAllText(Path.Combine(tilesPath, "manifest.json")))!;
        using var actual = SKBitmap.Decode(path);
        foreach (var tile in manifest.Tiles)
        {
            using var pixels = SKBitmap.Decode(Path.Combine(tilesPath, tile.File));
            for (var y = 0; y < tile.Height; y++)
            for (var x = 0; x < tile.Width; x++)
                Assert.Equal(pixels.GetPixel(x, y), actual.GetPixel(tile.X + x, tile.Y + y));
        }
    }

    private static MapDocument Fixture()
    {
        using var original = new SKBitmap(128, 128);
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 128; x++)
            original.SetPixel(x, y, x % 4 < 2 ? SKColors.White : SKColors.Black);
        using var image = SKImage.FromBitmap(original);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "original detail",
            48, 24, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha) };
        var shape = new SceneObject(ObjectId.New(), SceneObjectKind.Ellipse, "alpha across tiles",
            113, 51, 37, 37, 19, "#8860B0D0", "#CCFF6040", 2, true, false);
        return new MapDocument(DocumentId.New(), "full detail", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(160, 96, "px", "#FF304A60"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF304A60"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [stamp, shape])],
            [new MapAssetReference(sha, "test stripes", "image/png", ".png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "test", "Map tests", bytes)]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-png-test-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
