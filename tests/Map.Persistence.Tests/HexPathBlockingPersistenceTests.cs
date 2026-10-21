using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexPathBlockingPersistenceTests
{
    [Theory]
    [InlineData(false, false, false, 14)]
    [InlineData(true, false, false, 13)]
    [InlineData(false, true, false, 14)]
    [InlineData(false, false, true, 14)]
    [InlineData(true, true, true, 14)]
    public void Schema_is_minimal_and_save_reopen_preserves_all_flags(bool sight, bool move, bool light, int schema)
    {
        using var workspace = new TempWorkspace();
        var document = BorderMap(sight, move, light);
        var path = workspace.File("flags.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(schema, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());
        var before = document.Layers.OfType<HexLayer>().Single().Paths.Single();
        var reopened = MapProjectStore.Open(path).Document;
        var actual = reopened.Layers.OfType<HexLayer>().Single().Paths.Single();
        Assert.True(before.SameAs(actual));
        Assert.Equal(sight ? 1 : 0, VttGeometry.Collect(new ExportScene(reopened, 0)).Walls.Count);
        if (schema == 13)
        {
            var bytes = File.ReadAllBytes(path);
            // A real v13 fixture has no blocking fields; reading migrates the old Border+Edges rule in memory.
            Assert.DoesNotContain("blocksSight", ReadJson(path, "scene.json").ToJsonString());
            Assert.True(actual.BlocksSight);
            Assert.False(actual.BlocksMove);
            Assert.False(actual.BlocksLight);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Blocking_only_in_a_named_version_also_requires_v14_and_reopens()
    {
        using var workspace = new TempWorkspace();
        var snapshot = BorderMap(false, true, true);
        var document = snapshot.WithLayer(snapshot.Layers.OfType<HexLayer>().Single().Id,
            l => ((HexLayer)l) with { Paths = [] });
        document = document.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Стена",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(snapshot), ProjectFixtures.PreviewPng())]);
        var path = workspace.File("version.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(14, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());
        var reopened = MapProjectStore.Open(path).Document;
        var actual = reopened.Checkpoints.Single().Snapshot.Layers.OfType<HexLayer>().Single().Paths.Single();
        Assert.False(actual.BlocksSight);
        Assert.True(actual.BlocksMove);
        Assert.True(actual.BlocksLight);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void Compatible_v4_payload_is_readable_under_each_supported_legacy_schema(int schema)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("legacy.mapproj");
        var document = ProjectFixtures.Document();
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteSchema(path, schema);
        var bytes = File.ReadAllBytes(path);
        ProjectFixtures.AssertSemanticallyEqual(document, MapProjectStore.Open(path).Document);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Blocking_fields_in_a_container_claiming_v13_are_rejected()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("damaged.mapproj");
        MapProjectStore.Save(BorderMap(false, false, false), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteSchema(path, 13);
        var error = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("v14", error.Message);
    }

    private static MapDocument BorderMap(bool sight, bool move, bool light)
    {
        var document = HexMapFactory.Create("Граница", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Flat, 32, 8, 6, "#FFFFFFFF");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        var hex = layout.FromOffset(new HexOffset(2, 2));
        var path = new HexPath(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
            [layout.Canonical(new(hex, 0)), layout.Canonical(new(hex, 1))])
            { BlocksSight = sight, BlocksMove = move, BlocksLight = light };
        return document.WithLayer(layer.Id, _ => layer with { Paths = [path] });
    }

    private static JsonNode ReadJson(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(entry)!.Open();
        return JsonNode.Parse(stream)!;
    }

    private static void RewriteSchema(string path, int schema)
    {
        var manifest = ReadJson(path, "manifest.json");
        manifest["schemaVersion"] = schema;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        zip.GetEntry("manifest.json")!.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
