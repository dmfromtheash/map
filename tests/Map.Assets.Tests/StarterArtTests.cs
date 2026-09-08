using Map.Assets;
using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class StarterArtTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-starter-art-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Every_set_renders_deterministically_with_content_inside_the_requested_edge()
    {
        foreach (var set in StarterArtForge.Sets)
        {
            var first = StarterArtForge.Render(set, 0, 192);
            var second = StarterArtForge.Render(set, 0, 192);
            Assert.Equal(first, second);
            using var bitmap = SKBitmap.Decode(first);
            Assert.NotNull(bitmap);
            Assert.InRange(Math.Max(bitmap.Width, bitmap.Height), 96, 200);
            var opaque = bitmap.Pixels.Count(pixel => pixel.Alpha > 200);
            Assert.True(opaque > bitmap.Width * bitmap.Height / 8, set.Name + " is nearly empty");
            Assert.True(bitmap.Pixels.Any(pixel => pixel.Alpha == 0), set.Name + " has no transparent background");
        }
    }

    [Fact]
    public void Variants_of_one_set_differ_from_each_other()
    {
        var set = StarterArtForge.Sets[0];
        Assert.NotEqual(StarterArtForge.Render(set, 0, 160), StarterArtForge.Render(set, 1, 160));
    }

    [Fact]
    public void Art_pack_is_missing_until_imported_and_then_forms_ordered_sets_in_three_collections()
    {
        var catalog = new AssetCatalog(_root);
        var missing = StarterPackBootstrapper.MissingArtSets(catalog);
        Assert.Equal(StarterArtForge.Sets.Count, missing.Count);

        var sourceRoot = Path.Combine(_root, "starter-pack-source", "art");
        Directory.CreateDirectory(sourceRoot);
        var rendered = missing.Take(3).Select(set =>
        {
            var files = Enumerable.Range(0, set.VariantCount).Select(variant =>
            {
                var path = Path.Combine(sourceRoot, StarterArtForge.FileName(set, variant));
                File.WriteAllBytes(path, StarterArtForge.Render(set, variant, 128));
                return path;
            }).ToArray();
            return (set, files);
        }).ToArray();

        var imported = StarterPackBootstrapper.ImportArt(catalog, rendered, DateTimeOffset.UnixEpoch);
        Assert.Equal(3, imported);
        Assert.Equal(StarterArtForge.Sets.Count - 3, StarterPackBootstrapper.MissingArtSets(catalog).Count);
        Assert.Equal(0, StarterPackBootstrapper.ImportArt(catalog, rendered, DateTimeOffset.UnixEpoch));

        var set = catalog.Sets.Single(candidate => candidate.Name == rendered[0].set.Name);
        Assert.Equal(rendered[0].set.VariantCount, set.VariantIds.Count);
        Assert.Contains(catalog.Collections, collection => collection.Name == rendered[0].set.Collection);
        var record = catalog.RecordForVariant(set.VariantIds[0]);
        Assert.NotNull(record);
        Assert.Contains("mountain", record!.Tags);
        Assert.Contains("map-original", record.Tags);
    }
}
