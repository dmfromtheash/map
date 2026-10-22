using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexStructurePersistenceTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public void Legacy_feature_migrates_to_single_poi_without_rewriting_source(int version)
    {
        using var workspace = new TempWorkspace();
        var doc = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 4, 4, "#FFFFFFFF");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        var hex = layer.Board.Layout.FromOffset(new HexOffset(1, 1));
        doc = doc.WithLayer(layer.Id, _ => layer with { Board = layer.Board.With([new(hex, HexCell.Of("starter/grass", feature: "starter/village", featureVariant: 1))]) });
        var path = workspace.File("legacy.mapproj");
        MapProjectStore.Save(doc, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        // Versions 12..16 all accept the v12 cell stride; only the container version changes.
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonObject manifest;
            using (var stream = entry.Open()) manifest = System.Text.Json.Nodes.JsonNode.Parse(stream)!.AsObject();
            manifest["schemaVersion"] = version;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(manifest.ToJsonString());
        }
        var before = File.ReadAllBytes(path);
        var actual = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single().Board[hex]!;
        Assert.Equal(new HexPoi("starter/village", variant:1), Assert.Single(actual.PointsOfInterest));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Poi_and_region_parent_roundtrip_in_v18()
    {
        using var workspace = new TempWorkspace();
        var doc = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 4, 4, "#FFFFFFFF");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        var hex = layer.Board.Layout.FromOffset(new HexOffset(1, 1));
        var cell = HexCell.Of("starter/grass").WithPointsOfInterest([new HexPoi("starter/ruins", "Секрет", false), new HexPoi("starter/village", "Город")]);
        layer = layer with { Board = layer.Board.With([new(hex, cell)]), Regions =
            [new HexRegion(1, "Королевство", "#FF123456"), new HexRegion(2, "Провинция", "#FF654321") { ParentRegion = 1 }] };
        doc = doc.WithLayer(layer.Id, _ => layer);
        var path = workspace.File("structure.mapproj");
        MapProjectStore.Save(doc, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(18, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var actual = MapProjectStore.Open(path).Document.Layers.OfType<HexLayer>().Single();
        Assert.Equal(cell, actual.Board[hex]);
        Assert.Equal(layer.Regions, actual.Regions);
    }
}
