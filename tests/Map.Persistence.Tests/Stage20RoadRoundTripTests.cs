using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage20RoadRoundTripTests
{
    [Fact]
    public void V7_stays_unchanged_and_v8_keeps_nodes_branches_styles_order_and_checkpoints()
    {
        using var workspace = new TempWorkspace();
        var oldFile = workspace.File("rivers-v7.mapproj"); var newFile = workspace.File("roads-v8.mapproj");
        var document = ProjectFixtures.Document("Roads");
        var layer = document.Layers.OfType<ObjectLayer>().First();
        var river = new River { Name = "Река", Channels = [RiverEditor.NewChannel([new VectorPoint(500, 20), new VectorPoint(520, 400)], 16)] };
        var session = new EditingSession(document);
        session.Execute(new RiverCommand(layer.Id, river.Id, river, "river"));
        MapProjectStore.Save(session.Document, oldFile, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var oldBytes = File.ReadAllBytes(oldFile);
        using (var zip = ZipFile.OpenRead(oldFile))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(MapProjectFormat.RiversSchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());

        var road = RoadEditor.NewRoad([new(40, 60), new(200, 120), new(420, 90)], 14, RoadStyle.Country() with { TextureId = "sand", EdgeRoughness = 0.45 }, "Тракт", seed: 4242);
        var main = road.Branches[0].Id;
        var geometry = RoadGeometry.Evaluate(road).Branches[main];
        road = RoadEditor.InsertVertex(road, main, 0, 0.4, RoadGeometry.PointAt(geometry, 0, 0.4), out var shared);
        road = RoadEditor.AddBranch(road, shared, [new(150, 250), new(120, 330)], null, 6, out var path, "Тропа", RoadStyle.PenRuts());
        road = RoadEditor.SetVertexSharp(road, main, 2, true);
        road = RoadEditor.SetVertexTension(road, main, 1, 1.6);
        road = RoadEditor.SetClosed(RoadEditor.AddBranch(road, null, [new(600, 300), new(700, 300), new(700, 400), new(600, 400)], null, 8, out var loop, "Квартал", closed: false), loop, true);
        road = road with { Level = 2, Locked = true, WidthScale = 1.25, Style = road.Style with { StartCap = RoadEndCap.Taper, TaperLength = 30 } };
        road.Validate();
        session.Execute(new RoadCommand(layer.Id, road.Id, road, "road"));
        Assert.Null(session.LastResourceError);
        session.Execute(new ReorderSurfaceCommand(layer.Id, [river.Id, road.Id], "road over water"));
        Assert.Null(session.LastResourceError);
        Assert.Equal([river.Id, road.Id], ((ObjectLayer)session.Document.FindLayer(layer.Id)!).SurfaceOrder.ToArray());
        var expected = session.Document.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Road version",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(session.Document), ProjectFixtures.PreviewPng())]);
        MapProjectStore.Save(expected, newFile, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(newFile))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(MapProjectFormat.RoadsSchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProjectFileStatus.Ready, MapProjectStore.Inspect(newFile).Status);

        var opened = MapProjectStore.Open(newFile).Document;
        var actualLayer = (ObjectLayer)opened.FindLayer(layer.Id)!;
        var actual = actualLayer.Roads.Single();
        AssertRoadEqual(road, actual);
        Assert.Equal([river.Id, road.Id], actualLayer.SurfaceOrder.ToArray());
        Assert.Equal([new SurfaceEntry(SurfaceKind.Water, river.Id), new SurfaceEntry(SurfaceKind.Road, road.Id)], LayerSurfaceOrder.Resolve(actualLayer));
        var snapshotLayer = opened.Checkpoints.Single().Snapshot.Layers.OfType<ObjectLayer>().First();
        AssertRoadEqual(road, snapshotLayer.Roads.Single());
        Assert.Equal([river.Id, road.Id], snapshotLayer.SurfaceOrder.ToArray());
        Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));

        // Editing continues on the reopened network: the shared node still moves both branches.
        var moved = RoadEditor.MoveNode(actual with { Locked = false }, shared, 180, 140);
        var edited = new RoadCommand(layer.Id, actual.Id, moved, "move").Apply(opened);
        var set = RoadGeometry.Evaluate(((ObjectLayer)edited.FindLayer(layer.Id)!).Roads.Single());
        Assert.Equal((180.0, 140.0), (set.Branches[path].Start.X, set.Branches[path].Start.Y));
        Assert.NotNull(set.Branches[path].StartJoin);
    }

    private static void AssertRoadEqual(Road expected, Road actual)
    {
        Assert.Equal((expected.Id, expected.Name, expected.Style, expected.WidthScale, expected.Visible, expected.Locked, expected.Level, expected.Seed),
            (actual.Id, actual.Name, actual.Style, actual.WidthScale, actual.Visible, actual.Locked, actual.Level, actual.Seed));
        Assert.Equal(expected.Nodes.ToArray(), actual.Nodes.ToArray());
        Assert.Equal(expected.Branches.Length, actual.Branches.Length);
        for (var i = 0; i < expected.Branches.Length; i++)
        {
            Assert.Equal((expected.Branches[i].Id, expected.Branches[i].Name, expected.Branches[i].Closed, expected.Branches[i].Style),
                (actual.Branches[i].Id, actual.Branches[i].Name, actual.Branches[i].Closed, actual.Branches[i].Style));
            Assert.Equal(expected.Branches[i].Vertices.ToArray(), actual.Branches[i].Vertices.ToArray());
        }
    }

    [Fact]
    public void A_map_without_roads_is_still_written_as_before_and_a_road_without_order_needs_no_order_entry()
    {
        using var workspace = new TempWorkspace();
        var plain = workspace.File("plain.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Plain"), plain, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(plain))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(MapProjectFormat.SchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(MapProjectFormat.IsExtendedV4Schema(MapProjectFormat.RoadsSchemaVersion));
        Assert.False(MapProjectFormat.IsExtendedV4Schema(MapProjectFormat.MaxSupportedSchemaVersion + 1));

        var withRoad = workspace.File("road.mapproj");
        var document = ProjectFixtures.Document("Road");
        var layer = document.Layers.OfType<ObjectLayer>().First();
        var road = RoadEditor.NewRoad([new(10, 10), new(200, 40)], 10);
        var session = new EditingSession(document);
        session.Execute(new RoadCommand(layer.Id, road.Id, road, "road"));
        MapProjectStore.Save(session.Document, withRoad, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(withRoad))
        using (var scene = JsonDocument.Parse(zip.GetEntry("scene.json")!.Open()))
        {
            var layerJson = scene.RootElement.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("id").GetString() == layer.Id.ToString());
            Assert.True(layerJson.TryGetProperty("roads", out _));
            Assert.False(layerJson.TryGetProperty("surfaceOrder", out _));
        }
        var reopened = MapProjectStore.Open(withRoad).Document;
        Assert.Single(((ObjectLayer)reopened.FindLayer(layer.Id)!).Roads);
    }
}
