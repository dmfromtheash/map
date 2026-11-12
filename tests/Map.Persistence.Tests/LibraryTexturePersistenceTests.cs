using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// 2026-11-12: a library texture on land, a river, a road and the sea is saved in schema v55 with its bytes embedded,
/// and reopens without the library; a map without one keeps its older schema; a v54 file naming one is refused.
/// </summary>
public sealed class LibraryTexturePersistenceTests
{
    [Fact]
    public void Library_textures_round_trip_in_v55_with_their_bytes_embedded()
    {
        using var workspace = new TempWorkspace();
        var (document, id, png) = TexturedMap();
        var path = workspace.File("textured.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(MapProjectFormat.LibraryTextureSchemaVersion, Schema(path));

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(id, opened.BaseColor.TextureId);
        Assert.Equal(id, opened.Layers.OfType<LandLayer>().Single().EdgeStyle.FillTextureId);
        var objects = opened.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(id, objects.Rivers.Single().Style.TextureId);
        Assert.Equal(id, objects.Roads.Single().Style.TextureId);
        var asset = Assert.Single(opened.Assets);
        Assert.Equal(MapFillTextures.AssetSha256(id), asset.Sha256);
        Assert.Equal(ProjectAssetStorage.Embedded, asset.Storage);
        Assert.Equal(png, asset.EmbeddedContent);
        Assert.Empty(FillTextureAssets.MissingHashes(opened));
    }

    [Fact]
    public void A_map_with_built_in_textures_only_keeps_an_older_schema()
    {
        using var workspace = new TempWorkspace();
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        var document = new MapDocument(DocumentId.New(), "Снег", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land with { EdgeStyle = land.EdgeStyle with { FillTextureId = MapFillTextures.OwnerSnow } }]);
        var path = workspace.File("snow.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.True(Schema(path) < MapProjectFormat.LibraryTextureSchemaVersion);
    }

    [Fact]
    public void A_file_claiming_v54_that_names_a_library_texture_is_refused()
    {
        using var workspace = new TempWorkspace();
        var (document, _, _) = TexturedMap();
        var path = workspace.File("claims-v54.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteSchema(path, MapProjectFormat.AreaRangeSchemaVersion);
        var error = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("v55", error.Message);
    }

    private static (MapDocument Document, string Id, byte[] Png) TexturedMap()
    {
        var png = ProjectFixtures.PreviewPng();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png));
        var id = MapFillTextures.AssetId(sha);
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        var river = new River { Name = "Река", Style = new RiverStyle { TextureId = id }, Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 50)], 8)] };
        var road = RoadEditor.NewRoad([new(20, 300), new(400, 320)], 12, RoadStyle.Country() with { TextureId = id }, "Дорога", seed: 7);
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) with { Rivers = [river], Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "Текстуры", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF") { TextureId = id },
             land with { EdgeStyle = land.EdgeStyle with { FillTextureId = id } }, objects],
            [new MapAssetReference(sha, "Камень", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "Test", "Test", png)]);
        return (document, id, png);
    }

    private static int Schema(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>();
    }

    private static void RewriteSchema(string path, int schema)
    {
        JsonNode manifest;
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            manifest = JsonNode.Parse(stream)!;
        manifest["schemaVersion"] = schema;
        using var update = ZipFile.Open(path, ZipArchiveMode.Update);
        update.GetEntry("manifest.json")!.Delete();
        using var writer = new StreamWriter(update.CreateEntry("manifest.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
