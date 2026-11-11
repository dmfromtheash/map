using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>R09 «Зима» (DEC-097, schema v52): the three winter styles and the «Зима» variant live in the map's file.</summary>
public sealed class WinterStylePersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-winter-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private static MapDocument Map() =>
        new(DocumentId.New(), "Зима", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF9DB7C2"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9DB7C2"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);

    [Theory]
    [InlineData(MapStylePreset.WinterEngraving)]
    [InlineData(MapStylePreset.WinterBlizzard)]
    [InlineData(MapStylePreset.WinterWoodblock)]
    public void A_winter_style_survives_save_and_reopen_in_schema_52(MapStylePreset preset)
    {
        var style = MapStyle.For(preset) with { Strength = .8 };
        var path = Path.Combine(_root, preset + ".mapproj");
        MapProjectStore.Save(Map() with { Style = style }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(style, MapProjectStore.Open(path).Document.Style);
        Assert.Equal(MapProjectFormat.WinterSchemaVersion, SchemaOf(path));
    }

    [Theory]
    [InlineData(MapStylePreset.PixelFantasy16)]
    [InlineData(MapStylePreset.PixelHandheld)]
    public void A_pixel_style_survives_save_and_reopen_in_schema_53(MapStylePreset preset)
    {
        var style = MapStyle.For(preset) with { Strength = .9 };
        var path = Path.Combine(_root, preset + ".mapproj");
        MapProjectStore.Save(Map() with { Style = style }, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(style, MapProjectStore.Open(path).Document.Style);
        Assert.Equal(MapProjectFormat.PixelSchemaVersion, SchemaOf(path));
    }

    [Fact]
    public void The_winter_variant_alone_raises_the_schema_and_comes_back()
    {
        var id = 0;
        var document = MapVariantEditing.AddStarters(Map(), () => new Guid(++id, 0, 0, new byte[8]));
        Assert.Null(document.Style);
        var path = Path.Combine(_root, "starters.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(MapProjectFormat.WinterSchemaVersion, SchemaOf(path));
        var winter = MapProjectStore.Open(path).Document.Variants.Items.Single(v => v.Name == "Зима");
        Assert.Equal(MapStylePreset.WinterEngraving, winter.Style!.Preset);
    }

    private static int SchemaOf(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open();
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("schemaVersion").GetInt32();
    }
}
