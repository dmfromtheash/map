using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>G1 (2026-11-12): an area of layout 5 or «Горный массив» is saved in schema v54 and reopens unchanged.</summary>
public sealed class AreaRangePersistenceTests
{
    [Theory]
    [InlineData(PlacementAreaPattern.Peaks)]
    [InlineData(PlacementAreaPattern.Ridge)]
    public void A_layout_five_area_round_trips_in_v54(PlacementAreaPattern pattern)
    {
        using var workspace = new TempWorkspace();
        var png = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "G1", DateTimeOffset.UnixEpoch, new(1600, 900, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer],
            [new MapAssetReference(sha, "Гора", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "Test", "Test", png)]);
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea { LayoutVersion = 5, Density = 80, Seed = 11, Pattern = pattern, JoinEdges = true,
            Nodes = [new(Guid.NewGuid(), 20, 20), new(Guid.NewGuid(), 1580, 20), new(Guid.NewGuid(), 1580, 880), new(Guid.NewGuid(), 20, 880)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 120 } });
        document = new PlacementAreaCommand(layer.Id, area.Id, area, "Create").Apply(document);
        var path = workspace.File("range.mapproj"); MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), png);
        using (var zip = ZipFile.OpenRead(path)) using (var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(MapProjectFormat.AreaRangeSchemaVersion, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var saved = document.Layers.OfType<ObjectLayer>().Single();
        var actual = MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(pattern, actual.PlacementAreas.Single().Pattern);
        Assert.Equal(5, actual.PlacementAreas.Single().LayoutVersion);
        Assert.NotEmpty(saved.Objects);
        Assert.Equal(saved.Objects, actual.Objects);
    }

    [Fact]
    public void A_layout_four_area_keeps_an_older_schema()
    {
        using var workspace = new TempWorkspace();
        var png = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "G1", DateTimeOffset.UnixEpoch, new(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer],
            [new MapAssetReference(sha, "Гора", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "Test", "Test", png)]);
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea { LayoutVersion = 4, Density = 70, Seed = 5,
            Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 500, 10), new(Guid.NewGuid(), 500, 500), new(Guid.NewGuid(), 10, 500)],
            Variants = [new(sha, 32, 32)], Style = new(sha), Request = new() { BaseWidth = 40 } });
        document = new PlacementAreaCommand(layer.Id, area.Id, area, "Create").Apply(document);
        var path = workspace.File("old.mapproj"); MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), png);
        using var zip = ZipFile.OpenRead(path); using var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.True(json.RootElement.GetProperty("schemaVersion").GetInt32() < MapProjectFormat.AreaRangeSchemaVersion);
    }
}
