using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexTravelPersistenceTests
{
    private static MapDocument Document() => HexMapFactory.Create("Путешествие", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
    [Fact]
    public void Travel_v21_roundtrip_keeps_four_edited_profiles_encounters_endpoints_and_calendar()
    {
        using var w = new TempWorkspace(); var d = Document(); var l = d.Layers.OfType<HexLayer>().Single();
        l = l with { CampaignDay = 275, Journey = new HexTravelSettings { Mode = TravelMode.Ride, Seed = -48, From = new(1, 1), To = new(5, 1), ShowReach = true,
            Profiles = [.. Enum.GetValues<TravelMode>().Select(m => new TravelProfile(m, 20 + (int)m, ImmutableDictionary<string, double>.Empty.Add("starter/forest", 1.75)))],
            Encounters = [new("starter/forest", 0, ["Караван", "Пустая стоянка"])] } };
        var path = w.File("путь.mapproj"); MapProjectStore.Save(d.WithLayer(l.Id, _ => l), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path)) Assert.Equal(21, JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!["schemaVersion"]!.GetValue<int>());
        var opened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single();
        Assert.Equal(275, opened.CampaignDay); Assert.Equal(l.Journey.From, opened.Journey.From); Assert.Equal(l.Journey.To, opened.Journey.To);
        Assert.Equal(TravelMode.Ride, opened.Journey.Mode); Assert.Equal(-48, opened.Journey.Seed); Assert.True(opened.Journey.ShowReach);
        Assert.Equal(4, opened.Journey.Profiles.Count);
        foreach (var p in opened.Journey.Profiles) { Assert.Equal(20 + (int)p.Mode, p.MilesPerDay); Assert.Equal(1.75, p.Costs["starter/forest"]); }
        Assert.Equal(new[] { "Караван", "Пустая стоянка" }, opened.Journey.Encounters.Single().Entries);
    }
    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)]
    public void Old_containers_v4_to_v20_remain_readable_and_are_not_rewritten(int version)
    {
        using var w = new TempWorkspace(); var path = w.File("старая.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var e = zip.GetEntry("manifest.json")!; JsonNode json; using (var s = e.Open()) json = JsonNode.Parse(s)!;
            json["schemaVersion"] = version; e.Delete(); using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(json.ToJsonString());
        }
        var bytes = File.ReadAllBytes(path); MapProjectStore.Open(path); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(4, Document().Layers.OfType<HexLayer>().Single().Journey.Profiles.Count);
    }
    [Fact]
    public void Named_checkpoint_alone_selects_v21()
    {
        using var w = new TempWorkspace(); var d = Document(); var l = d.Layers.OfType<HexLayer>().Single();
        var snapshot = d.WithLayer(l.Id, _ => l with { Journey = l.Journey with { Mode = TravelMode.Boat } });
        d = d.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Речной путь", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(snapshot), ProjectFixtures.PreviewPng())]);
        var path = w.File("версия.mapproj"); MapProjectStore.Save(d, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var opened = MapProjectStore.Open(path).Document;
        Assert.True(opened.Layers.OfType<HexLayer>().Single().Journey.IsDefault);
        Assert.Equal(TravelMode.Boat, opened.Checkpoints.Single().Snapshot.Layers.OfType<HexLayer>().Single().Journey.Mode);
    }
}
