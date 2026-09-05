using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class SchemaV4ObjectChunkTests
{
    [Fact]
    public void Writer_chunks_by_count_and_reopen_preserves_exact_order_and_ids()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("chunked.mapproj");
        var document = DocumentWithObjects(MapProjectFormat.ObjectChunkTargetObjects + 8);

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.OpenRead(path))
        {
            var sceneBytes = ReadEntry(archive, MapProjectFormat.SceneEntry);
            Assert.True(sceneBytes.Length < 64 * 1024);
            using var scene = JsonDocument.Parse(sceneBytes);
            var objectLayer = scene.RootElement.GetProperty("layers")[1];
            Assert.False(objectLayer.TryGetProperty("objects", out _));
            var descriptors = objectLayer.GetProperty("objectChunks").EnumerateArray().ToArray();
            Assert.Equal(2, descriptors.Length);
            Assert.Equal([MapProjectFormat.ObjectChunkTargetObjects, 8],
                descriptors.Select(item => item.GetProperty("count").GetInt32()));
            Assert.Equal([0, 1], descriptors.Select(item => item.GetProperty("ordinal").GetInt32()));
            foreach (var descriptor in descriptors)
            {
                var name = descriptor.GetProperty("entry").GetString()!;
                var bytes = ReadEntry(archive, name);
                Assert.InRange(bytes.LongLength, 2, MapProjectFormat.ObjectChunkTargetBytes);
                Assert.Equal(bytes.LongLength, descriptor.GetProperty("byteLength").GetInt64());
                Assert.Equal(Sha256(bytes), descriptor.GetProperty("sha256").GetString());
            }
        }

        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().Single().Objects;
        var expected = document.Layers.OfType<ObjectLayer>().Single().Objects;
        Assert.Equal(expected.Count, reopened.Count);
        Assert.Equal(expected.Select(item => item.Id), reopened.Select(item => item.Id));
        Assert.Equal(expected.Select(item => item.Name), reopened.Select(item => item.Name));
    }

    [Fact]
    public void Checkpoint_snapshot_uses_the_same_object_chunk_format()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("checkpoint.mapproj");
        var baseDocument = DocumentWithObjects(300);
        var checkpoint = new MapCheckpoint(
            CheckpointId.Parse("60000000-0000-0000-0000-000000000001"),
            "chunked snapshot",
            ProjectFixtures.FixedModifiedUtc,
            MapCheckpointSnapshot.Capture(baseDocument),
            ProjectFixtures.PreviewPng());
        var document = baseDocument.WithCheckpoints([checkpoint]);

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using var archive = ZipFile.OpenRead(path);
        var root = MapProjectFormat.CheckpointPrefix + checkpoint.Id + "/";
        using var scene = JsonDocument.Parse(ReadEntry(archive, root + "scene.json"));
        var layer = scene.RootElement.GetProperty("layers")[1];
        Assert.False(layer.TryGetProperty("objects", out _));
        var descriptor = Assert.Single(layer.GetProperty("objectChunks").EnumerateArray());
        Assert.StartsWith(root + MapProjectFormat.ObjectPrefix,
            descriptor.GetProperty("entry").GetString(), StringComparison.Ordinal);
        Assert.Equal(300, descriptor.GetProperty("count").GetInt32());
        var reopened = MapProjectStore.Open(path).Document;
        Assert.Equal(300, Assert.Single(reopened.Checkpoints).Snapshot.Layers
            .OfType<ObjectLayer>().Single().Objects.Count);
    }

    [Fact]
    public void Reader_reports_chunk_checksum_damage_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("checksum.mapproj");
        MapProjectStore.Save(
            DocumentWithObjects(4), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, scene => FirstDescriptor(scene)["sha256"] = new string('0', 64));

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Equal(ProjectFileStatus.Corrupted, MapProjectStore.Inspect(path).Status);
    }

    [Fact]
    public void Reader_reports_chunk_count_budget_as_resource_limit()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("count-limit.mapproj");
        MapProjectStore.Save(
            DocumentWithObjects(4), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, scene =>
            FirstDescriptor(scene)["count"] = MapProjectFormat.ObjectChunkTargetObjects + 1);

        Assert.Throws<MapProjectResourceLimitException>(() => MapProjectStore.Open(path));
        Assert.Equal(ProjectFileStatus.ResourceLimit, MapProjectStore.Inspect(path).Status);
    }

    [Fact]
    public void Reader_rejects_chunk_bounds_that_could_hide_stored_objects()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("bounds.mapproj");
        MapProjectStore.Save(
            DocumentWithObjects(4), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteScene(path, scene =>
            FirstDescriptor(scene)["bounds"]!["left"] = 1_000_000);

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Equal(ProjectFileStatus.Corrupted, MapProjectStore.Inspect(path).Status);
    }

    [Fact]
    public void Reader_rejects_an_unlisted_object_chunk_entry()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("extra.mapproj");
        MapProjectStore.Save(
            DocumentWithObjects(4), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            WriteEntry(
                archive,
                MapProjectFormat.ObjectPrefix + ProjectFixtures.FixedLayerId + "/99999999.json",
                "[]"u8.ToArray());
        }

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void Resource_refusal_does_not_replace_the_existing_target()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("atomic.mapproj");
        MapProjectStore.Save(
            DocumentWithObjects(1), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var original = File.ReadAllBytes(path);
        var oversized = DocumentWithObjects(1, new string('x', 9 * 1024 * 1024));

        Assert.Throws<MapProjectResourceLimitException>(() => MapProjectStore.Save(
            oversized, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng()));

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static MapDocument DocumentWithObjects(int count, string? firstName = null)
    {
        var objects = new SceneObject[count];
        for (var index = 0; index < count; index++)
        {
            objects[index] = new SceneObject(
                new ObjectId(DeterministicGuid(index + 1)),
                index % 2 == 0 ? SceneObjectKind.Rectangle : SceneObjectKind.Ellipse,
                index == 0 && firstName is not null ? firstName : "object-" + index,
                index % 1024,
                index / 1024,
                8,
                6,
                index % 360,
                "#FF223344",
                "#FF000000",
                1,
                visible: true,
                locked: false);
        }

        return new MapDocument(
            ProjectFixtures.FixedDocumentId,
            "schema v4 chunks",
            ProjectFixtures.FixedCreatedUtc,
            new CanvasDefinition(131072, 131072, "px", "#FF10131A"),
            [
                new BaseColorLayer(
                    ProjectFixtures.FixedBaseLayerId, "background", true, false, 1, "#FF10131A"),
                new ObjectLayer(
                    ProjectFixtures.FixedLayerId, "objects", true, false, 1, objects),
            ]);
    }

    private static Guid DeterministicGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return new Guid(bytes);
    }

    private static JsonObject FirstDescriptor(JsonObject scene) =>
        scene["layers"]![1]!["objectChunks"]![0]!.AsObject();

    private static void RewriteScene(string path, Action<JsonObject> mutate)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var sceneEntry = archive.GetEntry(MapProjectFormat.SceneEntry)!;
        var scene = JsonNode.Parse(ReadEntry(archive, MapProjectFormat.SceneEntry))!.AsObject();
        mutate(scene);
        var sceneBytes = JsonSerializer.SerializeToUtf8Bytes(scene);
        sceneEntry.Delete();
        WriteEntry(archive, MapProjectFormat.SceneEntry, sceneBytes);

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        var manifest = JsonNode.Parse(ReadEntry(archive, MapProjectFormat.ManifestEntry))!.AsObject();
        manifest["sceneSha256"] = Sha256(sceneBytes);
        manifestEntry.Delete();
        WriteEntry(archive, MapProjectFormat.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
