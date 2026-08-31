using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed partial class AssetRoundTripTests
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    [GeneratedRegex("\"relativePath\"\\s*:\\s*\"[^\"]+\"")]
    private static partial Regex RelativePathPattern();

    [GeneratedRegex("\"assetManifestSha256\"\\s*:\\s*\"[0-9a-f]+\"")]
    private static partial Regex AssetManifestHashPattern();

    [Fact]
    public void Embedded_and_linked_asset_metadata_round_trip_with_stable_stamp_id()
    {
        using var workspace = new TempWorkspace();
        var embeddedBytes = ProjectFixtures.PreviewPng();
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

    [Theory]
    [InlineData(@"C:\secrets\tree.png")]
    [InlineData("C:secrets/tree.png")]
    [InlineData(@"\secrets\tree.png")]
    [InlineData(@"\\server\share\tree.png")]
    [InlineData(@"\\?\C:\secrets\tree.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("assets/tree.png:stream")]
    public void Unsafe_saved_linked_paths_are_reported_as_corruption(string unsafePath)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("unsafe-linked.mapproj");
        var hash = new string('a', 64);
        var document = ProjectFixtures.Document() with
        {
            Assets =
            [
                new MapAssetReference(
                    hash, "tree", "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "user-imported", "owned",
                    catalogId: hash, relativePath: "assets/tree.png"),
            ],
        };
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        ReplaceAssetManifestPath(path, unsafePath);

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("safe project-relative path", corruption.Message);
    }

    [Theory]
    [InlineData("../shared/tree.png")]
    [InlineData("assets/../tree.png")]
    public void Parent_relative_link_syntax_is_preserved_without_granting_escape_resolution(
        string relativePath)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("relative-linked.mapproj");
        var hash = new string('a', 64);
        var document = ProjectFixtures.Document() with
        {
            Assets =
            [
                new MapAssetReference(
                    hash, "tree", "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "user-imported", "owned",
                    catalogId: hash, relativePath: "assets/tree.png"),
            ],
        };
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        ReplaceAssetManifestPath(path, relativePath);

        var reopened = MapProjectStore.Open(path).Document.Assets.Single();

        Assert.Equal(relativePath, reopened.RelativePath);
        Assert.Equal(
            !relativePath.StartsWith("..", StringComparison.Ordinal),
            ProjectLinkedAssetPath.TryResolve(path, relativePath, out _));
    }

    [Fact]
    public void Linked_asset_cannot_alias_another_assets_embedded_content_entry()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("linked-alias.mapproj");
        var embeddedBytes = ProjectFixtures.PreviewPng();
        var embeddedHash = Convert.ToHexStringLower(SHA256.HashData(embeddedBytes));
        var linkedHash = new string('b', 64);
        var document = ProjectFixtures.Document() with
        {
            Assets =
            [
                new MapAssetReference(
                    embeddedHash, "embedded", "image/png", "png", ProjectAssetStorage.Embedded,
                    DateTimeOffset.UnixEpoch, "user-imported", "owned", embeddedBytes),
                new MapAssetReference(
                    linkedHash, "linked", "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "user-imported", "owned",
                    catalogId: linkedHash, relativePath: "assets/linked.png"),
            ],
        };
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteAssetManifest(path, root =>
        {
            var linked = root["assets"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(asset => asset["storage"]!.GetValue<string>() == "Linked");
            linked["contentEntry"] = $"assets/{embeddedHash}.png";
        });

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));

        Assert.Contains("Linked asset", corruption.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content entry", corruption.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Orphan_thumbnail_checksum_is_rejected_as_manifest_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("orphan-thumbnail-hash.mapproj");
        var linkedHash = new string('c', 64);
        var document = ProjectFixtures.Document() with
        {
            Assets =
            [
                new MapAssetReference(
                    linkedHash, "linked", "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "user-imported", "owned",
                    catalogId: linkedHash, relativePath: "assets/linked.png"),
            ],
        };
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteAssetManifest(path, root =>
        {
            root["assets"]!.AsArray()[0]!.AsObject()["thumbnailSha256"] = new string('d', 64);
        });

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));

        Assert.Contains("thumbnail entry and checksum together", corruption.Message);
    }

    private static void ReplaceAssetManifestPath(string path, string relativePath)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var assetManifestEntry = archive.GetEntry(MapProjectFormat.AssetManifestEntry)!;
        string assetManifest;
        using (var reader = new StreamReader(assetManifestEntry.Open()))
        {
            assetManifest = reader.ReadToEnd();
        }

        assetManifest = RelativePathPattern().Replace(
            assetManifest,
            "\"relativePath\": " + System.Text.Json.JsonSerializer.Serialize(relativePath));
        var bytes = System.Text.Encoding.UTF8.GetBytes(assetManifest);
        using (var stream = assetManifestEntry.Open())
        {
            stream.SetLength(0);
            stream.Write(bytes);
        }

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        string manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = reader.ReadToEnd();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        manifest = AssetManifestHashPattern().Replace(
            manifest,
            $"\"assetManifestSha256\": \"{hash}\"");
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest);
        using var manifestStream = manifestEntry.Open();
        manifestStream.SetLength(0);
        manifestStream.Write(manifestBytes);
    }

    private static void RewriteAssetManifest(string path, Action<JsonObject> rewrite)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var assetManifestEntry = archive.GetEntry(MapProjectFormat.AssetManifestEntry)!;
        JsonObject root;
        using (var reader = new StreamReader(assetManifestEntry.Open()))
        {
            root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        }

        rewrite(root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, IndentedJsonOptions);
        using (var stream = assetManifestEntry.Open())
        {
            stream.SetLength(0);
            stream.Write(bytes);
        }

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        string manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = reader.ReadToEnd();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        manifest = AssetManifestHashPattern().Replace(
            manifest,
            $"\"assetManifestSha256\": \"{hash}\"");
        using var manifestStream = manifestEntry.Open();
        manifestStream.SetLength(0);
        using var writer = new StreamWriter(manifestStream);
        writer.Write(manifest);
    }
}
