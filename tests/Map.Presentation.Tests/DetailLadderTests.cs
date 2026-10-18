using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// High-resolution owner art (2026-10-18): a large original gets halved steps on import, a far
/// view draws from a small step and only a close view decodes the full master.
/// </summary>
public sealed class DetailLadderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-ladder-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1408, 1408, 0)]
    [InlineData(2048, 1000, 0)]
    [InlineData(4096, 3200, 3)]
    [InlineData(3072, 2400, 3)]
    public void Depth_follows_the_long_side(int width, int height, int depth) =>
        Assert.Equal(depth, AssetCatalog.DetailLadderDepth(width, height));

    [Fact]
    public void A_large_import_gets_its_steps_and_the_view_picks_one_by_size()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "Ледяной пик.png");
        File.WriteAllBytes(source, Picture(4096, 3200));
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(source, new AssetImportOptions(), DateTimeOffset.UnixEpoch);

        for (var level = 1; level <= 3; level++)
        {
            Assert.True(catalog.TryDetailLevelPath(record, level, out var path));
            using var step = SKBitmap.Decode(path);
            Assert.Equal(4096 >> level, step.Width);
            Assert.Equal(3200 >> level, step.Height);
        }
        Assert.False(catalog.TryDetailLevelPath(record, 4, out _));

        var document = new MapDocument(DocumentId.New(), "Ladder", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")], []);
        using var images = new CatalogStampImageSource(catalog, () => document, () => null);

        // The working image is the first step: same proportions, a quarter of the pixels.
        Assert.Equal(2048, images.TryGetImage(record.Sha256)!.Width);
        // Far: the deepest step (or a mip of it); middle: the step just above; close: the master.
        Assert.Equal(512, images.TryGetImageForPixelSize(record.Sha256, 300, 234)!.Width);
        Assert.Equal(1024, images.TryGetImageForPixelSize(record.Sha256, 1000, 780)!.Width);
        Assert.Equal(4096, images.TryGetImageForPixelSize(record.Sha256, 3000, 2340)!.Width);
        Assert.Equal(256, images.TryGetImageForPixelSize(record.Sha256, 200, 156)!.Width);
    }

    [Fact]
    public void Ordinary_sizes_get_no_ladder_and_keep_the_original()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "small.png");
        File.WriteAllBytes(source, Picture(1408, 1100));
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(source, new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        Assert.False(catalog.TryDetailLevelPath(record, 1, out _));
        Assert.False(Directory.Exists(Path.Combine(_root, "catalog", "ladder")));

        var document = new MapDocument(DocumentId.New(), "Plain", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")], []);
        using var images = new CatalogStampImageSource(catalog, () => document, () => null);
        Assert.Equal(1408, images.TryGetImage(record.Sha256)!.Width);
    }

    private static byte[] Picture(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(70, 100, 130), IsAntialias = true };
        surface.Canvas.DrawOval(width / 2f, height / 2f, width * .45f, height * .4f, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
