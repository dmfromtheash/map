using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage18EditableAreaRoundTripTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void V6_roundtrip_retains_area_exclusions_frozen_images_and_v5_path_versions_without_changing_original(int layoutVersion)
    {
        using var workspace = new TempWorkspace();
        var source = workspace.File("path-v5.mapproj"); var target = workspace.File("areas-v6.mapproj");
        var bytes = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Tree", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "own fixture", "test", bytes);
        var session = new EditingSession(ProjectFixtures.Document("Areas"));
        var layer = session.Document.Layers.OfType<ObjectLayer>().First();
        var path = new PlacementPath { Nodes = [new(Guid.NewGuid(), 20, 20), new(Guid.NewGuid(), 200, 50)],
            Variants = [new(sha, 32, 32)], Style = new(sha) };
        session.Execute(new PlacementPathCommand(layer.Id, path.Id, path, "path", [asset]));
        MapProjectStore.Save(session.Document, source, ProjectFixtures.Metadata(), bytes);
        var sourceBytes = File.ReadAllBytes(source);
        var loaded = MapProjectStore.Open(source).Document;
        var oldVersion = new MapCheckpoint(CheckpointId.New(), "Path version", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(loaded), bytes);
        session = new EditingSession(loaded.WithCheckpoints([oldVersion]));
        var area = new PlacementArea { AssetSetId = "frozen", VariantId = "tree", Density = layoutVersion >= 3 ? 65 : 5, Seed = 18, LayoutVersion = layoutVersion,
            CompositionOrigin = new(315, 228),
            Pattern = PlacementAreaPattern.Ridge, CompositionLength = 550, CompositionAngleDegrees = 12,
            JoinEdges = layoutVersion >= 3, SizeVariationPercent = 25,
            ContentBounds = System.Collections.Immutable.ImmutableDictionary<string, StampContentBounds>.Empty.Add(sha, new(.1, .1, .9, .9)),
            ContentMasks = layoutVersion >= 4 ? System.Collections.Immutable.ImmutableDictionary<string, StampContentMask>.Empty
                .Add(sha, new([.. Enumerable.Repeat(uint.MaxValue, 32)])) : System.Collections.Immutable.ImmutableDictionary<string, StampContentMask>.Empty,
            Nodes = [new(Guid.NewGuid(), 50, 50), new(Guid.NewGuid(), 600, 50), new(Guid.NewGuid(), 600, 400), new(Guid.NewGuid(), 50, 400)],
            Variants = [new(sha, 32, 32)], Style = new(sha) { Blur = 1, ShadowOpacity = .2 },
            Request = new() { BaseWidth = 32, RandomFlip = true, RandomAngleStep = StampRandomAngleStep.Degrees60 } };
        var obstacle = PlacementAreaPlanner.Preview(area).First() with { Id = ObjectId.New(), Name = "Manual obstacle" };
        session.Execute(new AddObjectCommand(layer.Id, obstacle));
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "area")); Assert.Null(session.LastResourceError);
        area = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementAreas.Single();
        Assert.NotEmpty(area.OccupiedSlots);
        var member = area.Members.First();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "detach", detach: member.ObjectId));
        var expected = session.Document.WithCheckpoints([oldVersion, new MapCheckpoint(CheckpointId.New(), "Area version",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(session.Document), bytes)]);
        MapProjectStore.Save(expected, target, ProjectFixtures.Metadata(), bytes);
        using (var archive = ZipFile.OpenRead(target))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
            Assert.Equal(6, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProjectFileStatus.Ready, MapProjectStore.Inspect(target).Status);
        var reopened = MapProjectStore.Open(target).Document; var objectLayer = (ObjectLayer)reopened.FindLayer(layer.Id)!;
        var actual = objectLayer.PlacementAreas.Single(); actual.ValidateMembers();
        Assert.Equal(area.Nodes.ToArray(), actual.Nodes.ToArray()); Assert.Equal(area.Variants.ToArray(), actual.Variants.ToArray());
        Assert.Equal(area.Request, actual.Request); Assert.Equal(area.Style, actual.Style);
        Assert.Equal(area.AssetSetId, actual.AssetSetId); Assert.Equal(area.VariantId, actual.VariantId);
        Assert.Equal(layoutVersion, actual.LayoutVersion); Assert.True(area.OccupiedSlots.SetEquals(actual.OccupiedSlots));
        Assert.Equal(area.CompositionOrigin, actual.CompositionOrigin);
        Assert.Equal(area.SizeVariationPercent, actual.SizeVariationPercent);
        Assert.Equal(area.Pattern, actual.Pattern); Assert.Equal(area.JoinEdges, actual.JoinEdges);
        Assert.Equal(area.CompositionLength, actual.CompositionLength); Assert.Equal(area.CompositionAngleDegrees, actual.CompositionAngleDegrees);
        Assert.Equal(area.ContentBounds[sha], actual.ContentBounds[sha]);
        Assert.Equal(area.ContentMasks.Count, actual.ContentMasks.Count);
        if (layoutVersion >= 4) Assert.Equal(area.ContentMasks[sha].Rows.ToArray(), actual.ContentMasks[sha].Rows.ToArray());
        Assert.Contains(member.Slot, actual.Exclusions); Assert.Equal(2, reopened.Checkpoints.Count);
        Assert.Empty(reopened.Checkpoints[0].Snapshot.Layers.OfType<ObjectLayer>().First().PlacementAreas);
        Assert.Single(reopened.Checkpoints[1].Snapshot.Layers.OfType<ObjectLayer>().First().PlacementAreas);
        Assert.Equal(((ObjectLayer)expected.FindLayer(layer.Id)!).Objects, objectLayer.Objects);
        Assert.Equal(path.Id, objectLayer.PlacementPaths.Single().Id);
        var edited = new PlacementAreaCommand(layer.Id, area.Id, actual with { Density = 2, Seed = 718 }, "reopen edit").Apply(reopened);
        Assert.Same(DocumentEditor.FindObject(reopened, member.ObjectId), DocumentEditor.FindObject(edited, member.ObjectId));
        MapProjectStore.Save(edited, target, ProjectFixtures.Metadata(), bytes);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        // A reader must reject a container which falsely declares the old schema.
        using (var archive = ZipFile.Open(target, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!; JsonNode manifest;
            using (var stream = entry.Open()) manifest = JsonNode.Parse(stream)!;
            manifest["schemaVersion"] = 5; entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()); writer.Write(manifest.ToJsonString());
        }
        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(target));
    }
}
