using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>A road in the owner's painted look takes schema v23 and reopens with its strokes; a plain road keeps the older schema.</summary>
public sealed class PaintedRoadPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_painted_road_reopens_in_v23(bool painted)
    {
        using var workspace = new TempWorkspace();
        var road = RoadEditor.NewRoad([new(50, 200), new(350, 220)], 20, painted ? RoadStyle.Painted() : RoadStyle.Country(), "Тракт", seed: 3);
        var document = new MapDocument(DocumentId.New(), "roads", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Roads = [road] }]);
        var path = workspace.File("road.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
        {
            var schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
            if (painted) Assert.Equal(23, schema); else Assert.True(schema < 22);
        }

        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Single();
        Assert.Equal(road.Style, reopened.Style);
    }
}
