using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R06: a chosen crossing kind takes schema v15 and survives save → reopen; plain roads stay in v8.</summary>
public sealed class RoadCrossingsPersistenceTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 15)]
    public void Schema_is_minimal_and_the_chosen_kind_reopens(bool ford, int schema)
    {
        using var workspace = new TempWorkspace();
        var document = Crossing(ford);
        var path = workspace.File("crossing.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(schema, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());
        var reopened = MapProjectStore.Open(path).Document;
        var layer = reopened.Layers.OfType<ObjectLayer>().Single();
        var road = layer.Roads.Single();
        if (ford)
        {
            var item = Assert.Single(road.CrossingOverrides);
            Assert.Equal(RoadCrossingKind.Ford, item.Kind);
            Assert.Equal(road.Branches[0].Id, item.BranchId);
            Assert.Equal(layer.Rivers.Single().Channels[0].Id, item.ChannelId);
            Assert.Equal(RoadCrossingKind.Ford, Assert.Single(RoadCrossings.Detect([road], layer.Rivers)).Kind);
        }
        else
        {
            Assert.Empty(road.CrossingOverrides);
            Assert.DoesNotContain("crossings", ReadJson(path, "scene.json").ToJsonString());
            Assert.Equal(RoadCrossingKind.Bridge, Assert.Single(RoadCrossings.Detect([road], layer.Rivers)).Kind);
        }
    }

    [Fact]
    public void A_kind_only_in_a_named_version_also_requires_v15()
    {
        using var workspace = new TempWorkspace();
        var snapshot = Crossing(true);
        var document = Crossing(false).WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Брод",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(snapshot), ProjectFixtures.PreviewPng())]);
        var path = workspace.File("version.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(15, ReadJson(path, "manifest.json")["schemaVersion"]!.GetValue<int>());
        var reopened = MapProjectStore.Open(path).Document;
        var road = reopened.Checkpoints.Single().Snapshot.Layers.OfType<ObjectLayer>().Single().Roads.Single();
        Assert.Equal(RoadCrossingKind.Ford, Assert.Single(road.CrossingOverrides).Kind);
    }

    private static MapDocument Crossing(bool ford)
    {
        var river = new River { Name = "Река", Style = RiverStyle.Illustrated(), Channels = [RiverEditor.NewChannel([new(200, 0), new(200, 400)], 30)] };
        var road = RoadEditor.NewRoad([new(0, 200), new(400, 200)], 20);
        if (ford) road = RoadEditor.SetCrossingKind(road, road.Branches[0].Id, river.Channels[0].Id, 0, RoadCrossingKind.Ford);
        var layer = new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Rivers = [river], Roads = [road] };
        return new MapDocument(DocumentId.New(), "crossing", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
    }

    private static JsonNode ReadJson(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(entry)!.Open();
        return JsonNode.Parse(stream)!;
    }
}
