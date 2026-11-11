using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R09f (schema v51): the handout chosen for a map lives in its file and in its named versions.</summary>
public sealed class MapHandoutPersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-handout-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private static MapDocument Map() =>
        new(DocumentId.New(), "Хендаут", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FFF2E9DC"),
            [new BaseColorLayer(LayerId.New(), "Бумага", true, false, 1, "#FFF2E9DC"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);

    [Theory]
    [InlineData(MapHandout.Found)]
    [InlineData(MapHandout.Burnt)]
    [InlineData(MapHandout.ScrollInk)]
    [InlineData(MapHandout.ScrollBone)]
    [InlineData(MapHandout.PortolanSkin)]
    public void The_handout_survives_save_and_reopen_in_schema_51(MapHandout handout)
    {
        var path = Path.Combine(_root, MapHandouts.Id(handout) + ".mapproj");
        MapProjectStore.Save(Map() with { Handout = handout }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(handout, MapProjectStore.Open(path).Document.Handout);
        Assert.Equal(MapProjectFormat.HandoutSchemaVersion, SchemaOf(path));
    }

    [Fact]
    public void A_map_without_a_handout_stays_in_an_older_schema()
    {
        var path = Path.Combine(_root, "plain.mapproj");
        MapProjectStore.Save(Map(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(MapHandout.None, MapProjectStore.Open(path).Document.Handout);
        Assert.True(SchemaOf(path) < MapProjectFormat.HandoutSchemaVersion);
    }

    [Fact]
    public void A_named_version_keeps_its_handout()
    {
        var document = Map() with { Handout = MapHandout.PortolanSkin };
        var checkpoint = new MapCheckpoint(CheckpointId.New(), "Портолан", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(document), ProjectFixtures.PreviewPng());
        var path = Path.Combine(_root, "version.mapproj");
        MapProjectStore.Save((document with { Handout = MapHandout.None }).WithCheckpoints([checkpoint]), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(MapHandout.None, opened.Handout);
        Assert.Equal(MapHandout.PortolanSkin, opened.Checkpoints.Single().Snapshot.Handout);
    }

    private static int SchemaOf(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open();
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("schemaVersion").GetInt32();
    }
}
