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
        var sea = SeaStyle.For(SeaManner.DepthBands) with { Strength = .7, Scale = 1.5, Ink = "#FF102030" };
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

    [Fact]
    public void A_map_saved_with_the_removed_isobaths_opens_with_the_sea_of_new_maps()
    {
        // DEC-091: «Изолинии» are no longer a sea; an old map with them is not refused.
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Море");
        var sea = SeaStyle.For(SeaManner.DepthBands) with { Strength = .7, Scale = 1.5 };
        var path = workspace.File("old-sea.mapproj");
        MapProjectStore.Save(document.WithLayers([(BaseColorLayer)document.Layers[0] with { Sea = sea }, .. document.Layers.Skip(1)]),
            path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var rewritten = 0;
        ProjectJsonTestMutations.RewriteScene(path, scene =>
        {
            foreach (var layer in scene["layers"]!.AsArray())
                if (layer?["sea"] is { } saved) { saved["manner"] = "isobaths"; rewritten++; }
        });
        Assert.Equal(1, rewritten);
        var opened = ((BaseColorLayer)MapProjectStore.Open(path).Document.Layers[0]).Sea;
        Assert.Equal(SeaStyle.NewMapDefault with { Strength = .7, Scale = 1.5 }, opened);
    }

    [Theory]
    [InlineData(SeaManner.ShoreHatch)]
    [InlineData(SeaManner.WatercolorHalo)]
    [InlineData(SeaManner.Surf)]
    [InlineData(SeaManner.Ice)]
    [InlineData(SeaManner.Shoals)]
    public void The_manners_of_2026_11_round_trip_in_v48_and_an_older_schema_may_not_carry_them(SeaManner manner)
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Море");
        var sea = SeaStyle.For(manner) with { Strength = .8, Scale = 2 };
        var styled = document.WithLayers([(BaseColorLayer)document.Layers[0] with { Sea = sea }, .. document.Layers.Skip(1)]);
        var path = workspace.File("sea.mapproj");
        MapProjectStore.Save(styled, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.OpenRead(path))
        using (var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Assert.Equal(48, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(sea, ((BaseColorLayer)MapProjectStore.Open(path).Document.Layers[0]).Sea);

        // The same file relabelled as v47: an older schema with a newer sea is refused, not drawn as something else.
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(text.Replace("\"schemaVersion\":48", "\"schemaVersion\":47", StringComparison.Ordinal)
                .Replace("\"schemaVersion\": 48", "\"schemaVersion\": 47", StringComparison.Ordinal));
        }
        Assert.ThrowsAny<Exception>(() => MapProjectStore.Open(path));
    }
}
