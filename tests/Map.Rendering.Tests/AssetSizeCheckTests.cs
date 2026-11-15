using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// PC trace 2026-11-15 (wheel at 10 000 objects): every frame past the dense-tile scale asked the size of all
/// 16 208 assets under the image source's gate - 6.2 of 16.6 s of the slow frames. The check now resumes.
/// </summary>
public sealed class AssetSizeCheckTests
{
    private sealed class Sizes : IStampImageSource
    {
        public readonly HashSet<string> Known = [];
        public int Lookups;
        public long Revision;
        public long SourceRevision => Revision;
        public SKImage? TryGetImage(string sha256) => null;
        public (int Width, int Height)? TryGetImageSize(string sha256)
        {
            Lookups++;
            return Known.Contains(sha256) ? (64, 64) : null;
        }
    }

    private static MapDocument Document(int assets) => new MapDocument(DocumentId.New(), "Sizes", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")])
        .WithAssets([.. Enumerable.Range(0, assets).Select(i => new MapAssetReference(i.ToString("x64"), "A" + i, "image/png", ".png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "user-owned", catalogId: i.ToString("x64")))]);

    [Fact]
    public void Repeated_frames_do_not_ask_every_asset_size_again()
    {
        var document = Document(5_000);
        var source = new Sizes();
        foreach (var asset in document.Assets) source.Known.Add(asset.Sha256);
        Assert.True(SceneRenderer.AllAssetSizesKnown(document, source));
        var first = source.Lookups;
        for (var frame = 0; frame < 20; frame++) Assert.True(SceneRenderer.AllAssetSizesKnown(document, source));
        Assert.Equal(5_000, first);
        Assert.Equal(first, source.Lookups); // twenty more frames, no lookups
    }

    [Fact]
    public void An_unknown_size_is_retried_alone_and_a_changed_source_rechecks_all()
    {
        var document = Document(1_000);
        var source = new Sizes();
        foreach (var asset in document.Assets.Skip(1)) source.Known.Add(asset.Sha256);
        Assert.False(SceneRenderer.AllAssetSizesKnown(document, source));
        var before = source.Lookups;
        for (var frame = 0; frame < 10; frame++) Assert.False(SceneRenderer.AllAssetSizesKnown(document, source));
        Assert.Equal(before + 10, source.Lookups); // only the unknown one each frame
        source.Known.Add(document.Assets[0].Sha256);
        Assert.True(SceneRenderer.AllAssetSizesKnown(document, source));
        source.Known.Remove(document.Assets[500].Sha256);
        source.Revision++; // the source changed (a file was replaced)
        Assert.False(SceneRenderer.AllAssetSizesKnown(document, source));
    }
}
