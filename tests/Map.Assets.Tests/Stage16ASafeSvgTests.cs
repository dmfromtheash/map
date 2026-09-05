using Map.Assets;
using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class Stage16ASafeSvgTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-stage16a-svg", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Static_svg_is_rasterized_to_a_png_asset_without_changing_source()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "safe.svg");
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='32' height='24' viewBox='0 0 32 24'>" +
                  "<rect x='1' y='2' width='30' height='20' rx='3' fill='#336699' stroke='#ffffff' stroke-width='2'/></svg>";
        File.WriteAllText(source, svg);
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));

        var record = catalog.Import(source, new AssetImportOptions(), DateTimeOffset.UnixEpoch);

        Assert.Equal("png", record.Extension);
        Assert.Equal("image/png", record.MediaType);
        Assert.Equal(32, record.Width);
        Assert.Equal(24, record.Height);
        Assert.Equal(svg, File.ReadAllText(source));
        using var bitmap = SKBitmap.Decode(catalog.Resolve(record).Path);
        Assert.NotNull(bitmap);
        Assert.Equal(32, bitmap.Width);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><image href='https://example.com/a.png'/></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='5000' height='10'><rect width='1' height='1'/></svg>")]
    public void Active_external_and_oversized_svg_are_rejected_without_catalog_record(string svg)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(source, svg);
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));

        Assert.Throws<InvalidDataException>(() =>
            catalog.Import(source, new AssetImportOptions(), DateTimeOffset.UnixEpoch));
        Assert.Empty(catalog.Records);
    }

    [Fact]
    public void Missing_linked_asset_can_only_be_repaired_by_exact_original_bytes()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "original.png");
        var copy = Path.Combine(_root, "copy.png");
        var wrong = Path.Combine(_root, "wrong.png");
        File.WriteAllBytes(original, Image(SKColors.Green));
        File.Copy(original, copy);
        File.WriteAllBytes(wrong, Image(SKColors.Red));
        var catalog = new AssetCatalog(Path.Combine(_root, "repair-catalog"));
        var record = catalog.Import(
            original, new AssetImportOptions { Linked = true }, DateTimeOffset.UnixEpoch);
        File.Delete(original);

        Assert.Throws<InvalidDataException>(() => catalog.RepairSource(record, wrong));
        Assert.Equal(AssetResolutionStatus.Missing, catalog.Resolve(record).Status);

        var repaired = catalog.RepairSource(record, copy);
        Assert.Equal(AssetResolutionStatus.Available, catalog.Resolve(repaired).Status);
        Assert.Equal(Path.GetFullPath(copy), repaired.LinkedPath);
    }

    [Fact]
    public void Starter_pack_is_original_bounded_and_carries_explicit_license()
    {
        var catalog = new AssetCatalog(Path.Combine(_root, "starter"));
        StarterPackBootstrapper.EnsureInstalled(catalog, DateTimeOffset.UnixEpoch);

        Assert.Equal(5, catalog.Records.Count);
        Assert.All(catalog.Records, record =>
        {
            Assert.Equal("map-original-generated-starter", record.Provenance.Source);
            Assert.Equal(AssetLicenseStatus.UserOwned, record.Provenance.LicenseStatus);
            Assert.True(record.Width <= 2048 && record.Height <= 2048);
        });
        Assert.Contains(catalog.Records, record => record.DisplayName == "Горы");
        Assert.Contains(catalog.Records, record => record.DisplayName == "Дерево");
        Assert.Equal(3, catalog.Sets.Count);
        Assert.Contains(catalog.Sets, set => set.Name == "Горы");
        Assert.Contains(catalog.Sets, set => set.Name == "Дерево");
    }

    private static byte[] Image(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32))!;
        surface.Canvas.Clear(color);
        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
