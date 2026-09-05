using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class MetadataQueueTests
{
    [Fact]
    public async Task Recent_writes_coalesce_with_favorite_and_rename_without_losing_the_latest_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var path = Path.Combine(root, "tree.png");
            using (var surface = SKSurface.Create(new SKImageInfo(12, 12))!)
            {
                surface.Canvas.Clear(SKColors.Green);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, data.ToArray());
            }
            var set = catalog.ImportSet([path], "Tree", new(), DateTimeOffset.UnixEpoch);
            catalog.RememberVariant(set.Id, set.CoverVariantId);
            Assert.Empty(catalog.Recent);
            for (var i = 0; i < 40; i++) catalog.TouchRecent(set.Id, set.CoverVariantId, DateTimeOffset.UnixEpoch.AddSeconds(i));
            catalog.SetSetFavorite(set.Id, true);
            catalog.RenameSet(set.Id, "My tree");
            catalog.TouchRecent(set.Id, set.CoverVariantId, DateTimeOffset.UnixEpoch.AddSeconds(50));
            await catalog.FlushMetadataAsync();
            var reopened = new AssetCatalog(catalog.Root);
            Assert.Equal("My tree", reopened.FindSet(set.Id)!.Name);
            Assert.True(reopened.FindSet(set.Id)!.Favorite);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(50), Assert.Single(reopened.Recent).UsedUtc);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Metadata_failure_is_reported_by_flush_and_never_replaces_the_valid_index()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new AssetCatalog(root);
            catalog.ReplaceIndex([]);
            var path = Path.Combine(root, "catalog.json");
            var before = File.ReadAllBytes(path);
            // An exclusive live-index handle makes atomic replace fail without corrupting the target.
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<IOException>(() => catalog.ReplaceIndex([]));
            await Assert.ThrowsAsync<IOException>(() => catalog.FlushMetadataAsync());
            Assert.Equal(before, File.ReadAllBytes(path));
            catalog.ReplaceIndex([]);
            await catalog.FlushMetadataAsync();
            Assert.Null(catalog.MetadataSaveError);
        }
        finally { Directory.Delete(root, true); }
    }
}
