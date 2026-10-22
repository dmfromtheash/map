using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>A «living» river takes schema v25 and reopens with its bank manner; a plain river keeps the older schema.</summary>
public sealed class NaturalRiverPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_living_river_reopens_in_v25(bool natural, bool atlas)
    {
        using var workspace = new TempWorkspace();
        var river = new River
        {
            Name = "Серебрянка", Style = natural ? RiverStyle.Natural() : RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new(50, 200), new(200, 260), new(350, 220)], 20)],
        };
        var document = new MapDocument(DocumentId.New(), "rivers", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] }]);
        if (atlas) document = document with { Atlas = new(Guid.NewGuid(), "rivers.mapatlas") };
        var path = workspace.File("river.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
        {
            var schema = JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
            if (atlas) Assert.Equal(26, schema);
            else if (natural) Assert.Equal(25, schema); else Assert.True(schema < 22);
        }

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(document.Atlas, opened.Atlas);
        var reopened = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Single();
        Assert.Equal(river.Style, reopened.Style);
    }
}
