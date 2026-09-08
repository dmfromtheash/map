using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class AtlasArtPackTests
{
    [Fact]
    public async Task Art_is_transparent_grouped_searchable_and_installed_only_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-atlas-art-tests", Guid.NewGuid().ToString("N"));
        var catalog = new AssetCatalog(root);
        try
        {
            AtlasArtPack.EnsureInstalled(catalog, DateTimeOffset.UnixEpoch);
            Assert.Equal(9, catalog.Records.Count);
            Assert.Equal(9, catalog.CountSets(AssetSourceFilter.BuiltIn));
            Assert.Equal(0, catalog.CountSets(AssetSourceFilter.Mine));
            foreach (var art in AtlasArtPack.Artworks)
            {
                var record = Assert.Single(catalog.Records, r => r.Sha256 == art.Sha256);
                Assert.True(record.HasAlpha);
                Assert.True(record.Width >= 1200 && record.Height >= 800);
                using var image = SKBitmap.Decode(catalog.Resolve(record).Path!);
                Assert.Equal(0, image.GetPixel(0, 0).Alpha);
                Assert.True(image.Pixels.Count(p => p.Alpha > 220) > image.Width * image.Height / 10);
            }
            var styles = catalog.CollectionTree();
            Assert.Equal(3, styles.Count);
            foreach (var style in styles)
            {
                Assert.Equal(3, style.TotalCount);
                Assert.Equal(3, style.Children.Count);
                Assert.Equal(3, catalog.Query(new AssetQuery { CollectionId = style.Id }).TotalSets);
            }
            Assert.Equal(2, catalog.Query(new AssetQuery
            { Tags = ["Стиль/Атлас пером", "Тема/Горы", "Тема/Деревья"] }).TotalSets);
            var first = catalog.Sets[0];
            catalog.RenameSet(first.Id, "Моё название");
            await catalog.FlushMetadataAsync();
            AtlasArtPack.EnsureInstalled(catalog, DateTimeOffset.UtcNow);
            Assert.Equal(9, catalog.Records.Count);
            Assert.Equal("Моё название", catalog.FindSet(first.Id)!.Name);
            var reloaded = new AssetCatalog(root);
            Assert.Equal(9, reloaded.Query(new AssetQuery { Source = AssetSourceFilter.BuiltIn }).TotalSets);
            await reloaded.DisposeAsync();
        }
        finally
        {
            await catalog.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
