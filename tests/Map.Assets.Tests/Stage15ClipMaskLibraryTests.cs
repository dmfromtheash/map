using Map.Assets;
using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class Stage15ClipMaskLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-stage15-clip-library", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stage15ClipMask_assign_replace_remove_is_variant_owned_atomic_and_persistent()
    {
        var visual = Image("visual", 48, 32, SKColors.CornflowerBlue);
        var firstMask = Image("mask-one", 48, 32, SKColors.White);
        var secondMask = Image("mask-two", 48, 32, SKColors.Gray);
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var set = catalog.ImportSet([visual], "clip", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var variantId = set.VariantIds.Single();

        var assigned = catalog.AssignVariantMask(variantId, firstMask, DateTimeOffset.UnixEpoch);
        Assert.NotNull(assigned.MaskSha256);
        Assert.NotEqual(assigned.Sha256, assigned.MaskSha256);
        Assert.Equal(assigned.MaskSha256, catalog.MaskRecordForVariant(variantId)!.Sha256);

        var replaced = catalog.AssignVariantMask(
            variantId, secondMask, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.NotEqual(assigned.MaskSha256, replaced.MaskSha256);
        Assert.Equal(replaced.MaskSha256,
            new AssetCatalog(catalog.Root).FindVariant(variantId)!.MaskSha256);

        catalog.RemoveVariantMask(variantId);
        Assert.Null(new AssetCatalog(catalog.Root).FindVariant(variantId)!.MaskSha256);
    }

    [Fact]
    public void Stage15ClipMask_invalid_or_mismatched_input_does_not_change_variant_identity()
    {
        var visual = Image("visual", 48, 32, SKColors.CornflowerBlue);
        var mismatched = Image("wrong-size", 32, 48, SKColors.White);
        var invalid = Path.Combine(_root, "invalid.png");
        Directory.CreateDirectory(_root);
        File.WriteAllText(invalid, "not an image");
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var set = catalog.ImportSet([visual], "clip", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var variantId = set.VariantIds.Single();

        Assert.Throws<InvalidDataException>(() =>
            catalog.AssignVariantMask(variantId, mismatched, DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidDataException>(() =>
            catalog.AssignVariantMask(variantId, invalid, DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidDataException>(() =>
            catalog.AssignVariantMask(variantId, visual, DateTimeOffset.UnixEpoch));

        Assert.Null(new AssetCatalog(catalog.Root).FindVariant(variantId)!.MaskSha256);
    }

    private string Image(string name, int width, int height, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
