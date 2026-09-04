using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage12DMigrationTests
{
    [Fact]
    public void Stage12D_current_v3_is_ready_and_writer_json_is_deterministic()
    {
        using var workspace = new TempWorkspace();
        var first = workspace.File("first.mapproj");
        var second = workspace.File("second.mapproj");
        var document = ProjectFixtures.Document();

        MapProjectStore.Save(document, first, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        MapProjectStore.Save(document, second, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var inspection = MapProjectStore.Inspect(first);
        Assert.Equal(ProjectFileStatus.Ready, inspection.Status);
        Assert.Equal("FILE_READY", inspection.Code);
        Assert.False(inspection.SourceWasModified);
        Assert.NotNull(inspection.Project);
        foreach (var name in new[]
                 {
                     MapProjectFormat.ManifestEntry,
                     MapProjectFormat.SceneEntry,
                     MapProjectFormat.AssetManifestEntry,
                 })
        {
            Assert.Equal(ReadEntry(first, name), ReadEntry(second, name));
        }
    }

    [Fact]
    public void Stage12D_early_v1_requires_migration_and_copy_preserves_source_and_stable_geometry()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.File("early-v1.mapproj");
        var destination = workspace.File("migrated-v3.mapproj");
        WriteEarlyV1(source, unknownKind: null);
        var beforeBytes = File.ReadAllBytes(source);
        var beforeTimestamp = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, beforeTimestamp);

        var inspection = MapProjectStore.Inspect(source);
        Assert.Equal(ProjectFileStatus.MigrationRequired, inspection.Status);
        Assert.Equal(1, inspection.SourceSchema);
        Assert.Equal(MapProjectFormat.SchemaVersion, inspection.TargetSchema);

        var migrated = MapProjectStore.MigrateCopy(source, destination);
        var document = migrated.Project.Document;
        Assert.Equal(beforeBytes, File.ReadAllBytes(source));
        Assert.Equal(beforeTimestamp, File.GetLastWriteTimeUtc(source));
        Assert.Equal(ProjectFileStatus.Ready, MapProjectStore.Inspect(destination).Status);
        Assert.Equal(ProjectFixtures.FixedDocumentId, document.Id);
        Assert.IsType<BaseColorLayer>(document.Layers[0]);
        var objects = Assert.IsType<ObjectLayer>(document.Layers[1]);
        var item = Assert.Single(objects.Objects);
        Assert.Equal(ProjectFixtures.FixedRectangleId, item.Id);
        Assert.Equal((10d, 20d, 30d, 40d), (item.X, item.Y, item.Width, item.Height));

        var second = workspace.File("migrated-v3-second.mapproj");
        MapProjectStore.MigrateCopy(source, second);
        Assert.Equal(File.ReadAllBytes(destination), File.ReadAllBytes(second));
    }

    [Fact]
    public void Stage12D_unknown_fields_and_exact_unknown_object_payload_survive_migration_and_normal_save()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.File("unknown-v1.mapproj");
        var destination = workspace.File("unknown-v3.mapproj");
        WriteEarlyV1(source, unknownKind: "FuturePortal");
        var sourceScene = Encoding.UTF8.GetString(ReadEntry(source, MapProjectFormat.SceneEntry));
        using var sourceJson = JsonDocument.Parse(sourceScene);
        var raw = sourceJson.RootElement.GetProperty("layers")[0].GetProperty("objects")[0].GetRawText();

        var migrated = MapProjectStore.MigrateCopy(source, destination);
        var placeholder = Assert.Single(Assert.IsType<ObjectLayer>(migrated.Project.Document.Layers[1]).Objects);
        Assert.Equal(SceneObjectKind.Placeholder, placeholder.Kind);
        Assert.Equal("FuturePortal", placeholder.OpaquePayload?.OriginalKind);
        Assert.Equal(raw, placeholder.OpaquePayload?.RawJson);

        using (var migratedScene = JsonDocument.Parse(ReadEntry(destination, MapProjectFormat.SceneEntry)))
        {
            var rootObject = migratedScene.RootElement.GetProperty("futureRoot").GetString();
            var objectField = migratedScene.RootElement.GetProperty("layers")[1]
                .GetProperty("objects")[0].GetProperty("futureObject").GetInt32();
            Assert.Equal("kept", rootObject);
            Assert.Equal(42, objectField);
        }

        var saved = workspace.File("normal-save-v3.mapproj");
        MapProjectStore.Save(
            migrated.Project.Document,
            saved,
            ProjectFixtures.Metadata(),
            ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(saved).Document;
        var reopenedPlaceholder = Assert.Single(Assert.IsType<ObjectLayer>(reopened.Layers[1]).Objects);
        Assert.Equal(raw, reopenedPlaceholder.OpaquePayload?.RawJson);
    }

    [Fact]
    public void Stage12D_full_current_v1_preserves_layers_payloads_assets_notes_composition_and_checkpoint()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.File("full-v1.mapproj");
        var destination = workspace.File("full-v3.mapproj");
        var expected = RichDocument();
        MapProjectStore.Save(expected, source, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteManifest(source, root => root["schemaVersion"] = 1);

        var actual = MapProjectStore.MigrateCopy(source, destination).Project.Document;

        ProjectFixtures.AssertSemanticallyEqual(expected, actual);
        Assert.Equal(expected.Assets.Count, actual.Assets.Count);
        Assert.Equal(expected.Assets[0].EmbeddedContent, actual.Assets[0].EmbeddedContent);
        Assert.Equal(expected.Assets[0].FallbackThumbnailPng, actual.Assets[0].FallbackThumbnailPng);
        var checkpoint = Assert.Single(actual.Checkpoints);
        Assert.Equal("before migration", checkpoint.Name);
        Assert.Equal(expected.Checkpoints[0].Id, checkpoint.Id);
        Assert.Equal(expected.Checkpoints[0].CreatedUtc, checkpoint.CreatedUtc);
        Assert.Equal(expected.Checkpoints[0].PreviewPng.ToArray(), checkpoint.PreviewPng.ToArray());
        Assert.Empty(checkpoint.Snapshot.Materialize(actual.Id).Checkpoints);
    }

    [Fact]
    public void Stage12D_copy_preflight_and_faults_never_overwrite_source_or_destination()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.File("source.mapproj");
        var existing = workspace.File("existing.mapproj");
        WriteEarlyV1(source, unknownKind: null);
        File.WriteAllText(existing, "owner bytes");
        var sourceBytes = File.ReadAllBytes(source);

        Assert.Throws<ArgumentException>(() => MapProjectStore.MigrateCopy(source, source));
        Assert.Throws<IOException>(() => MapProjectStore.MigrateCopy(source, existing));
        Assert.Equal("owner bytes", File.ReadAllText(existing));

        var failedDestination = workspace.File("failed.mapproj");
        Assert.Throws<InvalidOperationException>(() => MapProjectStore.MigrateCopy(
            source,
            failedDestination,
            new MigrationFaultInjection
            {
                AfterReadBackBeforePublish = () => throw new InvalidOperationException("injected"),
            }));
        Assert.False(File.Exists(failedDestination));
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp-*"));
    }

    [Fact]
    public void Stage12D_oversized_unknown_object_payload_is_a_resource_limit_without_destination()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.File("oversized-v1.mapproj");
        var destination = workspace.File("oversized-v3.mapproj");
        WriteEarlyV1(source, unknownKind: "FuturePortal");
        RewriteJsonEntry(source, MapProjectFormat.SceneEntry, root =>
        {
            var item = root["layers"]![0]!["objects"]![0]!.AsObject();
            item["futurePayload"] = new string('x', OpaqueSceneObjectPayload.MaxUtf8Bytes + 1);
        });

        Assert.Throws<MapProjectResourceLimitException>(() =>
            MapProjectStore.MigrateCopy(source, destination));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, "*.tmp-*"));
    }

    [Fact]
    public void Stage12D_inspection_distinguishes_malformed_future_corrupt_resource_and_missing()
    {
        using var workspace = new TempWorkspace();
        var future = workspace.File("future.mapproj");
        MapProjectStore.Save(
            ProjectFixtures.Document(), future, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteManifest(
            future,
            root => root["schemaVersion"] = MapProjectFormat.SchemaVersion + 1);
        Assert.Equal(ProjectFileStatus.UnsupportedVersion, MapProjectStore.Inspect(future).Status);

        var malformed = workspace.File("malformed.mapproj");
        File.Copy(future, malformed);
        RewriteManifest(malformed, root => root["schemaVersion"] = "one");
        Assert.Equal(ProjectFileStatus.NotMapProject, MapProjectStore.Inspect(malformed).Status);

        var corrupt = workspace.File("corrupt.mapproj");
        MapProjectStore.Save(
            ProjectFixtures.Document(), corrupt, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteJsonEntry(corrupt, MapProjectFormat.SceneEntry, root => root["documentId"] = Guid.NewGuid());
        Assert.Equal(ProjectFileStatus.Corrupted, MapProjectStore.Inspect(corrupt).Status);

        var tooSmall = workspace.File("not-zip.bin");
        File.WriteAllText(tooSmall, "not a zip");
        Assert.Equal(ProjectFileStatus.NotMapProject, MapProjectStore.Inspect(tooSmall).Status);

        var resource = workspace.File("resource.mapproj");
        MapProjectStore.Save(
            ProjectFixtures.Document(), resource, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var archive = ZipFile.Open(resource, ZipArchiveMode.Update))
        {
            var bytes = ReadEntry(archive, MapProjectFormat.PreviewEntry);
            bytes[16] = 0;
            bytes[17] = 0;
            bytes[18] = 0x23;
            bytes[19] = 0x28;
            archive.GetEntry(MapProjectFormat.PreviewEntry)!.Delete();
            Write(archive, MapProjectFormat.PreviewEntry, bytes);
        }
        Assert.Equal(ProjectFileStatus.ResourceLimit, MapProjectStore.Inspect(resource).Status);
        Assert.Equal(ProjectFileStatus.NotFound, MapProjectStore.Inspect(workspace.File("missing.mapproj")).Status);
    }

    [Fact]
    public void Stage12D_registry_rejects_duplicate_reverse_gap_and_missing_chain()
    {
        Assert.Throws<ArgumentException>(() => new ProjectMigrationRegistry(
            [new NoOpStep(1, 2), new NoOpStep(1, 2)], currentVersion: 3));
        Assert.Throws<ArgumentException>(() => new ProjectMigrationRegistry(
            [new NoOpStep(2, 1)], currentVersion: 3));
        Assert.Throws<ArgumentException>(() => new ProjectMigrationRegistry(
            [new NoOpStep(1, 2), new NoOpStep(3, 4)], currentVersion: 4));
        Assert.Throws<ArgumentException>(() => new ProjectMigrationRegistry(
            [new NoOpStep(2, 3)], currentVersion: 2));
    }

    private static void WriteEarlyV1(string path, string? unknownKind)
    {
        var kind = unknownKind ?? nameof(SceneObjectKind.Rectangle);
        var scene = Encoding.UTF8.GetBytes($$"""
            {
              "documentId": "{{ProjectFixtures.FixedDocumentId}}",
              "futureRoot": "kept",
              "layers": [
                {
                  "id": "{{ProjectFixtures.FixedLayerId}}",
                  "name": "objects",
                  "visible": true,
                  "locked": false,
                  "opacity": 1,
                  "objects": [
                    {
                      "id": "{{ProjectFixtures.FixedRectangleId}}",
                      "kind": "{{kind}}",
                      "name": "legacy",
                      "x": 10, "y": 20, "width": 30, "height": 40,
                      "rotationDegrees": 0,
                      "fillColor": "#FFFFFFFF",
                      "strokeColor": "#FF000000",
                      "strokeWidth": 0,
                      "visible": true,
                      "locked": false,
                      "futureObject": 42
                    }
                  ]
                }
              ]
            }
            """);
        var assets = Encoding.UTF8.GetBytes("{\"assets\":[]}");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = MapProjectFormat.FormatId,
            schemaVersion = 1,
            appVersion = "0.2.0",
            documentId = ProjectFixtures.FixedDocumentId.ToString(),
            title = "legacy",
            createdUtc = ProjectFixtures.FixedCreatedUtc.ToString("O"),
            modifiedUtc = ProjectFixtures.FixedModifiedUtc.ToString("O"),
            canvas = new { width = 1024, height = 768, unit = "px", background = "#FF203040" },
            preset = "blank",
            sceneSha256 = Sha256(scene),
            assetManifestSha256 = Sha256(assets),
        });
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, MapProjectFormat.ManifestEntry, manifest);
        Write(archive, MapProjectFormat.SceneEntry, scene);
        Write(archive, MapProjectFormat.AssetManifestEntry, assets);
        Write(archive, MapProjectFormat.PreviewEntry, ProjectFixtures.PreviewPng());
    }

    private static MapDocument RichDocument()
    {
        var png = ProjectFixtures.PreviewPng();
        var assetHash = Sha256(png);
        var asset = new MapAssetReference(
            assetHash,
            "embedded",
            "image/png",
            "png",
            ProjectAssetStorage.Embedded,
            ProjectFixtures.FixedCreatedUtc,
            "owner",
            "private",
            embeddedContent: png,
            fallbackThumbnailPng: png);
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "stamp",
            5, 6, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(assetHash),
        };
        var line = VectorGeometry.CreateLine(
            ObjectId.New(), "line", [new VectorPoint(1, 1), new VectorPoint(20, 20)]);
        var shape = VectorGeometry.CreateShape(
            ObjectId.New(), "shape", ShapePrimitiveKind.Rectangle, new WorldBounds(30, 30, 80, 70));
        var text = new SceneObject(
            ObjectId.New(), SceneObjectKind.Text, "text",
            90, 20, 140, 60, 0, "#FF101010", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle { Content = "kept", RequestedFontFamily = "Arial" },
        };
        var compositionChild = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "child",
            1, 2, 10, 11, 0, "#FF223344", "#FF000000", 1, true, false);
        var composition = new SceneObject(
            ObjectId.New(), SceneObjectKind.Composition, "composition",
            200, 200, 64, 64, 0, "#FFFFFFFF", "#FF000000", 0, true, false)
        {
            Composition = new EditableComposition(
                CompositionId.New(), DocumentId.New(), "nested", 64, 64, [compositionChild]),
        };
        var brushPixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        brushPixels[3] = 255;
        var landPixels = new byte[TileMap.TileSize * TileMap.TileSize];
        landPixels[^1] = 255;
        var maskPixels = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        maskPixels[0] = 0;
        var note = new MapNote(
            NoteId.New(), "note", "**kept**", NoteAnchorKind.Object, objectId: text.Id);
        var baseDocument = new MapDocument(
            ProjectFixtures.FixedDocumentId,
            "full v1",
            ProjectFixtures.FixedCreatedUtc,
            new CanvasDefinition(2048, 2048, "px", "#FF10131A"),
            [
                new BaseColorLayer(ProjectFixtures.FixedBaseLayerId, "background", true, false, 1, "#FF10131A"),
                new ObjectLayer(
                    ProjectFixtures.FixedLayerId,
                    "objects",
                    true,
                    false,
                    0.9,
                    [stamp, line, shape, text, composition],
                    new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), maskPixels), true)),
                new BrushLayer(
                    LayerId.New(), "brush", true, false, 0.8,
                    TileMap.Empty(4).With(new TileKey(1, 2), brushPixels)),
                new LandLayer(
                    LayerId.New(), "land", true, false, 0.7,
                    TileMap.Empty(1).With(new TileKey(3, 4), landPixels), new LandEdgeStyle()),
                new GridLayer(
                    LayerId.New(), "grid", true, false, 1,
                    [new GridDefinition(GridId.New(), GridGeometry.HexFlat, 64)]),
                new FilterLayer(
                    LayerId.New(), "filter", true, false, 1,
                    new FilterSettings { Contrast = 0.2, TextureAssetSha256 = assetHash }),
            ],
            assets: [asset],
            notes: [note]);
        var checkpoint = new MapCheckpoint(
            CheckpointId.New(),
            "before migration",
            ProjectFixtures.FixedModifiedUtc,
            MapCheckpointSnapshot.Capture(baseDocument),
            ProjectFixtures.PreviewPng());
        return baseDocument.WithCheckpoints([checkpoint]);
    }

    private static void RewriteManifest(string path, Action<JsonObject> mutate) =>
        RewriteJsonEntry(path, MapProjectFormat.ManifestEntry, mutate);

    private static void RewriteJsonEntry(string path, string name, Action<JsonObject> mutate)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry(name)!;
        byte[] bytes;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        var root = JsonNode.Parse(bytes)!.AsObject();
        mutate(root);
        entry.Delete();
        Write(archive, name, JsonSerializer.SerializeToUtf8Bytes(root));

        if (name == MapProjectFormat.SceneEntry)
        {
            var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
            byte[] manifestBytes;
            using (var stream = manifestEntry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                manifestBytes = buffer.ToArray();
            }
            var manifest = JsonNode.Parse(manifestBytes)!.AsObject();
            manifest["sceneSha256"] = Sha256(JsonSerializer.SerializeToUtf8Bytes(root));
            manifestEntry.Delete();
            Write(archive, MapProjectFormat.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest));
        }
    }

    private static byte[] ReadEntry(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = MapProjectFormat.DeterministicEntryTime;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class NoOpStep(int source, int target) : IProjectMigrationStep
    {
        public int SourceVersion => source;
        public int TargetVersion => target;
        public void Migrate(ProjectMigrationPayload payload) => throw new NotSupportedException();
    }
}
