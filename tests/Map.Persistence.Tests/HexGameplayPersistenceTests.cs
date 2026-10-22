using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexGameplayPersistenceTests
{
    [Fact]
    public void Gameplay_inside_named_checkpoint_selects_v20_and_reopens()
    {
        using var w = new TempWorkspace();
        var d = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 5, 5, "#FFFFFFFF");
        var l = d.Layers.OfType<HexLayer>().Single(); var h = l.Board.Layout.FromOffset(new(1, 1));
        var snapshot = d.WithLayer(l.Id, _ => HexGameplay.MoveParty(l, h));
        d = d.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Первый день", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(snapshot), ProjectFixtures.PreviewPng())]);
        var path = w.File("версия.mapproj");
        MapProjectStore.Save(d, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var opened = MapProjectStore.Open(path).Document;
        Assert.Null(opened.Layers.OfType<HexLayer>().Single().Party.Position);
        Assert.Equal(h, opened.Checkpoints.Single().Snapshot.Layers.OfType<HexLayer>().Single().Party.Position);
    }

    [Fact]
    public void Gameplay_v20_roundtrip_preserves_party_visits_conditions_and_rumors()
    {
        using var w = new TempWorkspace();
        var d = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 5, 5, "#FFFFFFFF");
        var l = d.Layers.OfType<HexLayer>().Single(); var h = l.Board.Layout.FromOffset(new(1, 1));
        l = l with { Board = l.Board.With([new(h, HexCell.Of("starter/grass").WithPointsOfInterest(
            [new HexPoi("starter/ruins", "Истина") { Rumor = "Слух", ApproximateLabel = "Примерно" }]))]), CampaignDay = 12,
            Regions = [new HexRegion(1, "Край", "#FFFFFFFF") { Rumor = "Слух о крае", ApproximateLabel = "Дальний край" }],
            Notes = l.Notes.Add(h, new HexNote("Тайна", playerText: "Правда") { RevealCondition = RevealCondition.FromDate,
                RevealDay = 10, ManuallyRevealed = true, Rumor = "Слух о тайне", ApproximateLabel = "Примерно тайна" }) };
        l = HexGameplay.MoveParty(l, h);
        l = l with { Party = l.Party with { History = [] } };
        var path = w.File("игра.mapproj");
        MapProjectStore.Save(d.WithLayer(l.Id, _ => l), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var z = ZipFile.OpenRead(path))
            Assert.Equal(20, JsonNode.Parse(z.GetEntry("manifest.json")!.Open())!["schemaVersion"]!.GetValue<int>());
        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single();
        Assert.Equal(l.Notes[h], reopened.Notes[h]); Assert.Equal(l.Regions, reopened.Regions);
        Assert.Equal(l.Board[h], reopened.Board[h]); Assert.Equal(12, reopened.CampaignDay);
        Assert.Equal(h, reopened.Party.Position); Assert.Empty(reopened.Party.History); Assert.Contains(h, reopened.Party.Visited);
        Assert.Equal(l.Party.SightRadius, reopened.Party.SightRadius);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)]
    public void Older_containers_open_unchanged_with_default_gameplay(int version)
    {
        using var w = new TempWorkspace();
        var path = w.File("старая.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var z = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var e = z.GetEntry("manifest.json")!; JsonNode n;
            using (var s = e.Open()) n = JsonNode.Parse(s)!;
            n["schemaVersion"] = version; e.Delete();
            using var writer = new StreamWriter(z.CreateEntry("manifest.json").Open()); writer.Write(n.ToJsonString());
        }
        var before = File.ReadAllBytes(path);
        MapProjectStore.Open(path);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
