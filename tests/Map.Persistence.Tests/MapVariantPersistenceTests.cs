using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R09e (schema v50): the variants of a map and the chosen one live in its file - no copy of the file.</summary>
public sealed class MapVariantPersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-variants-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private static MapDocument Map(LayerId names)
    {
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray());
        return new MapDocument(DocumentId.New(), "Варианты", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2"),
             new LandLayer(LayerId.New(), "Суша", true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()),
             new ObjectLayer(names, "Подписи", true, false, 1, [])])
        {
            Style = MapStyle.For(MapStylePreset.InkWetBrush) with { Enabled = true },
            Lighting = new MapLighting { Enabled = true, Sources = [new MapLightSource(Guid.NewGuid(), 50, 60, "#FFFFC070", 120, 1, true)] },
        };
    }

    [Fact]
    public void The_variants_and_the_chosen_one_survive_save_and_reopen()
    {
        var names = LayerId.New();
        var id = 0;
        var document = MapVariantEditing.AddStarters(Map(names), () => new Guid(++id, 0, 0, new byte[8]));
        var night = document.Variants.Items.Single(v => v.Name == "Ночь").Id;
        document = MapVariantEditing.Switch(document, night);
        // An edit made while «Ночь» is chosen: the names hidden. It is saved as part of «Ночь».
        document = document.WithLayer(names, l => l with { Visible = false });
        var path = Path.Combine(_root, "variants.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(MapVariantEditing.Current(document), opened.Variants);
        Assert.Equal(night, opened.Variants.Active);
        Assert.Equal(names, Assert.Single(opened.Variants.Find(night)!.Hidden));
        Assert.Equal(document.Style, opened.Style);
        Assert.Equal(document.Lighting! with { Sources = [] }, opened.Lighting! with { Sources = [] });
        Assert.Equal(document.Lighting.Sources.ToArray(), opened.Lighting.Sources.ToArray());

        var day = MapVariantEditing.Switch(opened, opened.Variants.Items.Single(v => v.Name == "День").Id);
        Assert.True(day.FindLayer(names)!.Visible);
        Assert.Equal(MapVariantEditing.DayTime, day.Lighting!.TimeOfDay);
        Assert.Single(day.Lighting.Sources);
        var political = MapVariantEditing.Switch(day, opened.Variants.Items.Single(v => v.Name == "Политическая").Id);
        Assert.True(political.Timeline.PoliticalMode);
    }

    [Fact]
    public void A_map_with_variants_is_saved_in_schema_50_and_one_without_stays_older()
    {
        var names = LayerId.New();
        var plain = Path.Combine(_root, "plain.mapproj");
        MapProjectStore.Save(Map(names), plain, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(MapProjectStore.Open(plain).Document.Variants.IsDefault);
        Assert.True(SchemaOf(plain) < MapProjectFormat.VariantsSchemaVersion);

        var with = Path.Combine(_root, "with.mapproj");
        MapProjectStore.Save(MapVariantEditing.Add(Map(names), "Осада", Guid.NewGuid()), with, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(MapProjectFormat.VariantsSchemaVersion, SchemaOf(with));
    }

    [Fact]
    public void A_named_version_keeps_its_variants()
    {
        var names = LayerId.New();
        var document = MapVariantEditing.Add(Map(names), "Осада", Guid.NewGuid());
        var checkpoint = new MapCheckpoint(CheckpointId.New(), "До осады", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(document), ProjectFixtures.PreviewPng());
        var path = Path.Combine(_root, "version.mapproj");
        MapProjectStore.Save(document.WithCheckpoints([checkpoint]), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal("Осада", opened.Checkpoints.Single().Snapshot.Variants.Items.Single().Name);
    }

    private static int SchemaOf(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open();
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("schemaVersion").GetInt32();
    }
}
