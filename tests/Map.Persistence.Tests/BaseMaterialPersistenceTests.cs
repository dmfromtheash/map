using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class BaseMaterialPersistenceTests
{
    [Fact]
    public void A_sea_material_round_trips_in_v10_and_a_plain_page_keeps_its_older_schema()
    {
        using var workspace = new TempWorkspace();
        var plain = ProjectFixtures.Document("Plain sea");
        var plainPath = workspace.File("plain.mapproj");
        MapProjectStore.Save(plain, plainPath, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(SchemaOf(plainPath) < MapProjectFormat.BaseMaterialSchemaVersion);
        Assert.Null(MapProjectStore.Open(plainPath).Document.BaseColor.TextureId);

        var sea = plain.BaseColor with { TextureId = "regional-water", TextureOpacity = .45, TextureScale = 300 };
        var textured = plain.WithLayers([sea, .. plain.Layers.Skip(1)]);
        var path = workspace.File("sea.mapproj");
        MapProjectStore.Save(textured, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        Assert.Equal(MapProjectFormat.BaseMaterialSchemaVersion, SchemaOf(path));
        var opened = MapProjectStore.Open(path).Document.BaseColor;
        Assert.Equal("regional-water", opened.TextureId);
        Assert.Equal(.45, opened.TextureOpacity);
        Assert.Equal(300, opened.TextureScale);
        Assert.Equal(plain.BaseColor.Color, opened.Color);
    }

    private static int SchemaOf(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        return manifest.RootElement.GetProperty("schemaVersion").GetInt32();
    }
}
