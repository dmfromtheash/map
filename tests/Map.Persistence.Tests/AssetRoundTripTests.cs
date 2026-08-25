using System.IO.Compression;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class AssetRoundTripTests
{
    [Fact]
    public void Embedded_and_linked_asset_metadata_round_trip_with_stable_stamp_id()
    {
        using var workspace = new TempWorkspace();
        var embeddedBytes = Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();
        var embeddedHash = Convert.ToHexStringLower(SHA256.HashData(embeddedBytes));
        var linkedHash = new string('a', 64);
        var embedded = new MapAssetReference(
            embeddedHash, "tree", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "user-imported", "owned", embeddedBytes,
            fallbackThumbnailPng: ProjectFixtures.PreviewPng());
        var linked = new MapAssetReference(
            linkedHash, "mountain", "image/webp", "webp", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "user-imported", "unknown",
            catalogId: linkedHash, relativePath: "assets/mountain.webp",
            fallbackThumbnailPng: ProjectFixtures.PreviewPng());
        var stampId = ObjectId.New();
        var document = new MapDocument(
            DocumentId.New(), "assets", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1,
                [
                    new SceneObject(
                        stampId, SceneObjectKind.Stamp, "tree", 10, 20, 100, 100, 0,
                        "#FFFFFFFF", "#00000000", 0, true, false)
                    {
                        Stamp = new StampStyle(embeddedHash) { ShadowOpacity = 0.5, Hue = 30 },
                    },
                ]),
            ],
            [embedded, linked]);
        var path = workspace.File("assets.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        Assert.Equal(2, reopened.Assets.Count);
        Assert.Equal(embeddedBytes, reopened.Assets.Single(asset => asset.Sha256 == embeddedHash).EmbeddedContent);
        var linkedAgain = reopened.Assets.Single(asset => asset.Sha256 == linkedHash);
        Assert.Equal(ProjectAssetStorage.Linked, linkedAgain.Storage);
        Assert.Equal("assets/mountain.webp", linkedAgain.RelativePath);
        var stamp = reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single();
        Assert.Equal(stampId, stamp.Id);
        Assert.Equal(embeddedHash, stamp.Stamp!.AssetSha256);
        Assert.Equal(0.5, stamp.Stamp.ShadowOpacity);

        using var archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry($"assets/{embeddedHash}.png"));
        Assert.Null(archive.GetEntry($"assets/{linkedHash}.webp"));
    }
}
