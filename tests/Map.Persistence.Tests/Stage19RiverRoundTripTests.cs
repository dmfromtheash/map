using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage19RiverRoundTripTests
{
    [Fact]
    public void V6_stays_unchanged_and_v7_keeps_channels_widths_junctions_style_and_checkpoints()
    {
        using var workspace = new TempWorkspace();
        var oldFile = workspace.File("old-v4.mapproj"); var newFile = workspace.File("rivers-v7.mapproj");
        var document = ProjectFixtures.Document("Rivers");
        MapProjectStore.Save(document, oldFile, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var oldBytes = File.ReadAllBytes(oldFile);
        var reopenedOld = MapProjectStore.Open(oldFile).Document;
        var layer = reopenedOld.Layers.OfType<ObjectLayer>().First();
        var main = new RiverChannel
        {
            Name = "Главное русло",
            Nodes =
            [
                new(Guid.NewGuid(), 40, 60, 5, Sharp: true),
                new(Guid.NewGuid(), 200, 120, 14, Tension: 1.75),
                new(Guid.NewGuid(), 420, 90, 26, Tension: 0.4),
            ],
        };
        var tributary = new RiverChannel
        {
            Name = "Приток",
            Nodes = [new(Guid.NewGuid(), 230, 20, 4), new(Guid.NewGuid(), 250, 70, 7)],
            Mouth = new RiverJunction(main.Id, main.Nodes[1].Id, 0.375),
        };
        var lake = new RiverChannel { Name = "Озеро", Closed = true, Nodes = [new(Guid.NewGuid(), 500, 300, 1), new(Guid.NewGuid(), 620, 280, 1), new(Guid.NewGuid(), 640, 400, 1), new(Guid.NewGuid(), 520, 420, 1)] };
        var branch = new RiverChannel { Name = "Рукав", Nodes = [new(Guid.NewGuid(), 470, 150, 6), new(Guid.NewGuid(), 520, 230, 5)],
            Source = new RiverJunction(main.Id, main.Nodes[1].Id, 0.6), Mouth = new RiverJunction(lake.Id, lake.Nodes[0].Id, 0.3) };
        var river = new River
        {
            Name = "Серебрянка", Channels = [main, tributary, lake, branch], WidthScale = 1.25,
            Visible = true, Locked = true,
            Style = new RiverStyle { WaterColor = "#FF3B7A99", BankWidth = 2, HighlightOpacity = 0.4, MouthFade = 30, TaperSource = false, EdgeRoughness = 0.35, CutsLand = false, ClipToLand = true },
        };
        var session = new EditingSession(reopenedOld);
        session.Execute(new RiverCommand(layer.Id, river.Id, river, "create"));
        Assert.Null(session.LastResourceError);
        var expected = session.Document.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "River version",
            DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(session.Document), ProjectFixtures.PreviewPng())]);
        MapProjectStore.Save(expected, newFile, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(newFile))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(7, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var inspection = MapProjectStore.Inspect(newFile);
        Assert.Equal(ProjectFileStatus.Ready, inspection.Status);
        var opened = MapProjectStore.Open(newFile).Document;
        var actual = ((ObjectLayer)opened.FindLayer(layer.Id)!).Rivers.Single();
        AssertRiverEqual(river, actual);
        AssertRiverEqual(river, opened.Checkpoints.Single().Snapshot.Layers.OfType<ObjectLayer>().First().Rivers.Single());
        Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));

        // Editing continues on the reopened river: the junction still resolves on the parent.
        var moved = RiverEditor.MoveNode(actual, main.Id, 2, 440, 140);
        var edited = new RiverCommand(layer.Id, actual.Id, moved, "move").Apply(opened);
        var set = RiverGeometry.Evaluate(((ObjectLayer)edited.FindLayer(layer.Id)!).Rivers.Single());
        Assert.True(set.Channels[tributary.Id].JoinsParent);

        // The curvature handles and the per-river switches survive save and reopen unchanged.
        Assert.Equal(1.75, actual.FindChannel(main.Id)!.Nodes[1].Tension);
        Assert.Equal(0.4, actual.FindChannel(main.Id)!.Nodes[2].Tension);
        Assert.True(actual.Locked);
        Assert.True(actual.Visible);
        Assert.Equal(1, actual.FindChannel(tributary.Id)!.Nodes[0].Tension);
    }

    /// <summary>Record equality of ImmutableArray members is by reference; compare the content.</summary>
    private static void AssertRiverEqual(River expected, River actual)
    {
        Assert.Equal((expected.Id, expected.Name, expected.Style, expected.WidthScale, expected.Visible, expected.Locked),
            (actual.Id, actual.Name, actual.Style, actual.WidthScale, actual.Visible, actual.Locked));
        Assert.Equal(expected.Channels.Length, actual.Channels.Length);
        for (var i = 0; i < expected.Channels.Length; i++)
        {
            Assert.Equal((expected.Channels[i].Id, expected.Channels[i].Name, expected.Channels[i].Mouth, expected.Channels[i].Source, expected.Channels[i].Closed),
                (actual.Channels[i].Id, actual.Channels[i].Name, actual.Channels[i].Mouth, actual.Channels[i].Source, actual.Channels[i].Closed));
            Assert.Equal(expected.Channels[i].Nodes.ToArray(), actual.Channels[i].Nodes.ToArray());
        }
    }

    [Fact]
    public void V7_file_without_rivers_is_not_written_and_v7_container_is_rejected_by_older_expectations_only_above_max()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.File("plain.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Plain"), file, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var zip = ZipFile.OpenRead(file);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Equal(MapProjectFormat.SchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(MapProjectFormat.IsExtendedV4Schema(MapProjectFormat.RiversSchemaVersion));
        Assert.False(MapProjectFormat.IsExtendedV4Schema(MapProjectFormat.RiversSchemaVersion + 1));
    }
}
