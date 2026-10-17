namespace Map.Assets.Tests;

public sealed class EmptyAssetLibraryTests
{
    [Fact]
    public async Task Fresh_and_reopened_catalog_stay_empty_without_bundled_artwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-empty-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var catalog = new AssetCatalog(root))
            {
                Assert.Equal(AssetLibraryStatus.Ready, catalog.Status);
                Assert.Empty(catalog.Records);
                Assert.Empty(catalog.Sets);
                Assert.Empty(catalog.Collections);
                Assert.Empty(catalog.Variants);
                Assert.Empty(catalog.Recent);
                Assert.Equal(0, catalog.CountSets(AssetSourceFilter.BuiltIn));
            }
            await using var reopened = new AssetCatalog(root);
            Assert.Empty(reopened.Records);
            Assert.Empty(reopened.Sets);
            Assert.DoesNotContain(typeof(AssetCatalog).Assembly.GetManifestResourceNames(),
                name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            Assert.False(RegionalArtPack.IsAvailable);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
