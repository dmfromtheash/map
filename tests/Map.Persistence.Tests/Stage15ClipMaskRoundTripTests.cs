using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage15ClipMaskRoundTripTests
{
    [Fact]
    public void Stage15ClipMask_project_and_checkpoint_round_trip_exact_identity_state_and_foreground()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage15-clip.mapproj");
        var document = Document(enabled: true);
        var checkpoint = new MapCheckpoint(
            CheckpointId.New(),
            "clip",
            DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(document),
            ProjectFixtures.PreviewPng());
        document = document.WithCheckpoints([checkpoint]);

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;
        var stamp = reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single().Stamp!;

        Assert.True(stamp.ClipMaskEnabled);
        Assert.Equal(MaskSha, stamp.MaskAssetSha256);
        Assert.Equal(2, reopened.Assets.Count);
        Assert.Equal(ForegroundTile(), reopened.StampForeground!.Tiles.TryGet(new TileKey(0, 0)));
        Assert.Equal(ForegroundTile(), reopened.Checkpoints.Single().Snapshot
            .StampForeground!.Tiles.TryGet(new TileKey(0, 0)));
        using var archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry("rasters/foreground/0_0.bin"));
        Assert.NotNull(archive.GetEntry($"checkpoints/{checkpoint.Id}/rasters/foreground/0_0.bin"));
    }

    [Fact]
    public void Stage15ClipMask_missing_fields_open_disabled_without_foreground()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage15-clip-legacy.mapproj");
        MapProjectStore.Save(Document(enabled: true), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, root =>
        {
            root.Remove("stampForeground");
            var stamp = root["layers"]!.AsArray()[1]!["objects"]!.AsArray()[0]!["stamp"]!.AsObject();
            stamp.Remove("maskAssetSha256");
            stamp.Remove("clipMaskEnabled");
        });

        var reopened = MapProjectStore.Open(path).Document;
        var style = reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single().Stamp!;
        Assert.Null(style.MaskAssetSha256);
        Assert.False(style.ClipMaskEnabled);
        Assert.Null(reopened.StampForeground);
    }

    [Fact]
    public void Stage15ClipMask_enabled_without_identity_is_rejected_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage15-clip-invalid.mapproj");
        MapProjectStore.Save(Document(enabled: true), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, root =>
        {
            var stamp = root["layers"]!.AsArray()[1]!["objects"]!.AsArray()[0]!["stamp"]!.AsObject();
            stamp.Remove("maskAssetSha256");
        });

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    private static readonly byte[] VisualBytes = ProjectFixtures.PreviewPng();
    private static readonly byte[] MaskBytes = [.. ProjectFixtures.PreviewPng(), 0];
    private static readonly string VisualSha = Convert.ToHexStringLower(SHA256.HashData(VisualBytes));
    private static readonly string MaskSha = Convert.ToHexStringLower(SHA256.HashData(MaskBytes));

    private static MapDocument Document(bool enabled)
    {
        var source = ProjectFixtures.Document("clip");
        var objectLayer = (ObjectLayer)source.Layers[1];
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "clip", 32, 32, 96, 64, 25,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(VisualSha)
            {
                MaskAssetSha256 = MaskSha,
                ClipMaskEnabled = enabled,
            },
        };
        var foreground = new StampForegroundSurface(
            TileMap.Empty(4).With(new TileKey(0, 0), ForegroundTile()));
        return source
            .WithAssets([
                Asset(VisualSha, "visual", VisualBytes),
                Asset(MaskSha, "mask", MaskBytes),
            ])
            .WithLayer(objectLayer.Id, current =>
                ((ObjectLayer)current) with { Objects = [stamp] })
            .WithStampForeground(foreground);
    }

    private static MapAssetReference Asset(string sha, string name, byte[] bytes) => new(
        sha, name, "image/png", "png", ProjectAssetStorage.Embedded,
        DateTimeOffset.UnixEpoch, "test", "test", bytes);

    private static byte[] ForegroundTile()
    {
        var bytes = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = 20;
            bytes[offset + 1] = 80;
            bytes[offset + 2] = 140;
            bytes[offset + 3] = 255;
        }
        return bytes;
    }

    private static void RewriteScene(string path, Action<JsonObject> change)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var sceneEntry = archive.GetEntry(MapProjectFormat.SceneEntry)!;
        string json;
        using (var reader = new StreamReader(sceneEntry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
        var root = JsonNode.Parse(json)!.AsObject();
        change(root);
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        using (var stream = sceneEntry.Open())
        {
            stream.SetLength(0);
            stream.Write(bytes);
        }
        if (root["stampForeground"] is null)
        {
            archive.GetEntry("rasters/foreground/0_0.bin")?.Delete();
        }

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        string manifest;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8)) manifest = reader.ReadToEnd();
        manifest = Regex.Replace(
            manifest,
            "\\\"sceneSha256\\\"\\s*:\\s*\\\"[0-9a-f]+\\\"",
            $"\"sceneSha256\": \"{MapProjectRoundTripTests.Sha256Hex(bytes)}\"");
        using var writer = new StreamWriter(manifestEntry.Open());
        writer.BaseStream.SetLength(0);
        writer.Write(manifest);
    }
}
