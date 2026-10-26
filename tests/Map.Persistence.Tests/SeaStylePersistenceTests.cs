using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R12: the sea style of the page survives save and reopen in schema v32; a plain page writes nothing.</summary>
public sealed class SeaStylePersistenceTests
{
    [Fact]
    public void Sea_style_round_trips_in_v32_and_a_plain_page_keeps_its_schema()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Море");
        var page = (BaseColorLayer)document.Layers[0];
        var sea = SeaStyle.For(SeaManner.Isobaths) with { Strength = .7, Scale = 1.5, Ink = "#FF102030" };
        var styled = document.WithLayers([page with { Sea = sea }, .. document.Layers.Skip(1)]);
        var path = workspace.File("sea.mapproj");
        MapProjectStore.Save(styled, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        using (var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(32, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(sea, ((BaseColorLayer)MapProjectStore.Open(path).Document.Layers[0]).Sea);

        var plain = workspace.File("plain.mapproj");
        MapProjectStore.Save(document, plain, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var plainZip = ZipFile.OpenRead(plain);
        Assert.DoesNotContain("\"sea\"", new StreamReader(plainZip.GetEntry("scene.json")!.Open()).ReadToEnd(), StringComparison.Ordinal);
        Assert.Null(((BaseColorLayer)MapProjectStore.Open(plain).Document.Layers[0]).Sea);
    }
}
