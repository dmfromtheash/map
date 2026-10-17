using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class LandMaterialPersistenceTests
{
    [Theory]
    [InlineData("forest-moss")]
    [InlineData(MapFillTextures.SoftMeadow)]
    [InlineData(MapFillTextures.Foothill)]
    public void Painted_land_and_checkpoint_round_trip_with_exact_bytes_in_v9(string material)
    {
        using var workspace = new TempWorkspace();
        var doc = ProjectFixtures.Document("Painted land");
        var rgba = new byte[256 * 256 * 4]; rgba[0] = 75; rgba[1] = 50; rgba[2] = 25; rgba[3] = 128;
        var coverage = Enumerable.Repeat((byte)255, 256 * 256).ToArray();
        var land = LandLayer.CreateEmpty(LayerId.New(), "Луг") with
        {
            GeometryTiles = TileMap.Empty(1).With(new(0, 0), coverage),
            PaintTiles = TileMap.Empty(4).With(new(-1, 2), rgba),
            EdgeStyle = new LandEdgeStyle { FillTextureId = material, FillTextureOpacity = .7 },
        };
        doc = doc.WithLayers([.. doc.Layers, land]);
        doc = doc.WithCheckpoints([new MapCheckpoint(CheckpointId.New(), "Раскраска", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(doc), ProjectFixtures.PreviewPng())]);
        var path = workspace.File("paint.mapproj");
        MapProjectStore.Save(doc, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
        {
            Assert.Equal(9, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Contains(archive.Entries, e => e.FullName.Contains("rasters/land-paint/"));
        }
        var opened = MapProjectStore.Open(path).Document;
        var actual = (LandLayer)opened.FindLayer(land.Id)!;
        Assert.Equal(rgba, actual.PaintTiles.TryGet(new(-1, 2)));
        Assert.Equal(coverage, actual.GeometryTiles.TryGet(new(0, 0)));
        Assert.Equal(material, actual.EdgeStyle.FillTextureId);
        var savedCheckpoint = opened.Checkpoints[0].Snapshot.Layers.OfType<LandLayer>().Last();
        Assert.Equal(rgba, savedCheckpoint.PaintTiles.TryGet(new(-1, 2)));
    }

    [Fact]
    public void A_map_without_painted_land_keeps_its_previous_schema()
    {
        using var workspace = new TempWorkspace();
        var doc = ProjectFixtures.Document("Original");
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        doc = doc.WithLayers([.. doc.Layers, land]);
        var path = workspace.File("plain.mapproj");
        MapProjectStore.Save(doc, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var archive = ZipFile.OpenRead(path);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal(4, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(archive.Entries.Where(e => e.FullName.Contains("land-paint")));
        Assert.Equal(0, MapProjectStore.Open(path).Document.Layers.OfType<LandLayer>().Last().PaintTiles.TileCount);
    }
}
