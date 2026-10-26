using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R14: the dim zone of a light survives save and reopen in v33; lights without it keep their schema.</summary>
public sealed class LightZonesPersistenceTests
{
    private static int Schema(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        return json.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    [Fact]
    public void Dim_zone_round_trips_in_v33_and_plain_lights_stay_older()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Свет");
        var torch = new MapLightSource(Guid.NewGuid(), 100, 120, "#ffff9a42", 200, 1.1, true);
        var plain = document with { Lighting = new MapLighting { Sources = [torch] } };
        var plainPath = workspace.File("plain.mapproj");
        MapProjectStore.Save(plain, plainPath, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(Schema(plainPath) < 33);

        var zoned = document with { Lighting = new MapLighting { Sources = [torch with { DimZone = 1.25 }] } };
        var path = workspace.File("zoned.mapproj");
        MapProjectStore.Save(zoned, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(33, Schema(path));
        var light = MapProjectStore.Open(path).Document.Lighting!.Sources.Single();
        Assert.Equal(1.25, light.DimZone, 6);
        Assert.Equal(450, light.DimRadius, 6);
    }
}
