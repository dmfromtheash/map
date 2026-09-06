using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage17EditablePathRoundTripTests
{
    [Fact]
    public void V4_opens_unchanged_and_v5_retains_editable_inputs_members_exclusions_and_checkpoints()
    {
        using var workspace = new TempWorkspace();
        var oldFile = workspace.File("old-v4.mapproj"); var newFile = workspace.File("path-v5.mapproj");
        var document = ProjectFixtures.Document("Paths");
        MapProjectStore.Save(document, oldFile, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var oldBytes = File.ReadAllBytes(oldFile);
        var reopenedOld = MapProjectStore.Open(oldFile).Document;
        var bytes = ProjectFixtures.PreviewPng(); var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var asset = new MapAssetReference(sha, "Tree", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "test fixture", "test", bytes);
        var layer = reopenedOld.Layers.OfType<ObjectLayer>().First();
        var path = new PlacementPath { Name = "Trees", Spacing = 50, Seed = 112,
            AssetSetId = "frozen-set", VariantId = "original-variant", Variants = [new(sha, 32, 32)],
            Nodes = [new(Guid.NewGuid(), 100, 100), new(Guid.NewGuid(), 350, 200), new(Guid.NewGuid(), 600, 100)],
            Request = new() { BaseWidth = 32, RandomFlip = true, RandomAngleStep = StampRandomAngleStep.Degrees60 }, Style = new(sha) { Blur = 1 } };
        var session = new EditingSession(reopenedOld);
        session.Execute(new PlacementPathCommand(layer.Id, path.Id, path, "create", [asset]));
        var stored = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementPaths.Single();
        var member = stored.Members[1];
        session.Execute(new PlacementPathCommand(layer.Id, path.Id, stored, "detach", detach: member.ObjectId));
        var expected = session.Document.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Path version",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(session.Document), ProjectFixtures.PreviewPng())]);
        MapProjectStore.Save(expected, newFile, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(newFile))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(5, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProjectFileStatus.Ready, MapProjectStore.Inspect(newFile).Status);
        var opened = MapProjectStore.Open(newFile).Document;
        var actual = ((ObjectLayer)opened.FindLayer(layer.Id)!).PlacementPaths.Single();
        Assert.Single(opened.Checkpoints.Single().Snapshot.Layers.OfType<ObjectLayer>().First().PlacementPaths);
        Assert.Equal(stored.Nodes.ToArray(), actual.Nodes.ToArray()); Assert.Equal(stored.Variants.ToArray(), actual.Variants.ToArray());
        Assert.Equal(stored.Request, actual.Request); Assert.Equal(stored.Style, actual.Style);
        Assert.Contains(member.Slot, actual.Exclusions); Assert.DoesNotContain(actual.Members, m => m.ObjectId == member.ObjectId);
        Assert.Equal(((ObjectLayer)expected.FindLayer(layer.Id)!).Objects, ((ObjectLayer)opened.FindLayer(layer.Id)!).Objects);
        var edit = new PlacementPathCommand(layer.Id, actual.Id, actual with { Spacing = 31, Seed = 777 }, "edit reopened");
        var edited = edit.Apply(opened);
        Assert.Equal(DocumentEditor.FindObject(opened, member.ObjectId), DocumentEditor.FindObject(edited, member.ObjectId));
        Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));
    }
}
