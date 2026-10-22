using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexTileSetPersistenceTests
{
    [Fact]
    public void Hex_TileSet_reference_only_in_named_checkpoint_selects_v24_and_reopens()
    {
        using var w = new TempWorkspace();
        var d = HexMapFactory.Create("Версия", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
        var layer = d.Layers.OfType<HexLayer>().Single(); var at = new HexCoord(1, 1);
        var snapshot = d.WithLayer(layer.Id, _ => layer with { Board = layer.Board.With([new(at, HexCell.Of("user/old-set/tile"))]) });
        d = d.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "С комплектом", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(snapshot), ProjectFixtures.PreviewPng())]);
        var path = w.File("версия.mapproj"); MapProjectStore.Save(d, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path)) Assert.Equal(24, JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!["schemaVersion"]!.GetValue<int>());
        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(0, opened.Layers.OfType<HexLayer>().Single().Board.CellCount);
        Assert.Equal("user/old-set/tile", opened.Checkpoints.Single().Snapshot.Layers.OfType<HexLayer>().Single().Board[at]!.Terrain);
    }

    [Fact]
    public void Hex_TileSet_v24_reopens_without_any_installed_set_and_keeps_all_POI_references()
    {
        using var w = new TempWorkspace();
        var d = HexMapFactory.Create("Комплект", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
        var l = d.Layers.OfType<HexLayer>().Single(); var at = new HexCoord(1, 1);
        var cell = HexCell.Of("user/terrain/grass").WithPointsOfInterest([new("user/towns/tower"), new("user/terrain/camp", "Лагерь")]);
        l = l with { Board = l.Board.With([new(at, cell)]) }; d = d.WithLayer(l.Id, _ => l);
        var path = w.File("комплект.mapproj"); MapProjectStore.Save(d, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal(24, JsonNode.Parse(zip.GetEntry("manifest.json")!.Open())!["schemaVersion"]!.GetValue<int>());
            Assert.Contains("tileSets", new StreamReader(zip.GetEntry("scene.json")!.Open()).ReadToEnd());
        }
        var opened = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single();
        Assert.Equal(cell.Terrain, opened.Board[at]!.Terrain); Assert.Equal(cell.PointsOfInterest, opened.Board[at]!.PointsOfInterest);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    [InlineData(20)] [InlineData(21)] [InlineData(22)] [InlineData(23)]
    public void Persistence_TileSet_reader_preserves_old_container_versions(int version)
    {
        using var w = new TempWorkspace(); var path = w.File("старая.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; JsonNode json;
            using (var stream = entry.Open()) json = JsonNode.Parse(stream)!;
            json["schemaVersion"] = version; entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(json.ToJsonString());
        }
        var before = File.ReadAllBytes(path); Assert.NotNull(MapProjectStore.Open(path).Document); Assert.Equal(before, File.ReadAllBytes(path));
    }
}
