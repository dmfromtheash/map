using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public partial class Stage15StampAppearanceRoundTripTests
{
    private static readonly byte[] AssetBytes = ProjectFixtures.PreviewPng();
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(AssetBytes));

    [System.Text.RegularExpressions.GeneratedRegex("\"sceneSha256\"\\s*:\\s*\"[0-9a-f]+\"")]
    private static partial System.Text.RegularExpressions.Regex SceneShaRegex();

    [Fact]
    public void Stage15_stamp_fields_round_trip_without_a_schema_bump()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage15-appearance.mapproj");
        var document = Document();

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var archive = ZipFile.OpenRead(path))
        using (var manifest = JsonDocument.Parse(
                   MapProjectRoundTripTests.ReadEntry(archive, MapProjectFormat.ManifestEntry)))
        {
            Assert.Equal(MapProjectFormat.SchemaVersion,
                manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        var reopened = MapProjectStore.Open(path);
        var stamp = reopened.Document.Layers.OfType<ObjectLayer>().Single().Objects.Single().Stamp!;

        Assert.Equal("Luminosity", stamp.Blend);
        Assert.Equal(StampShadowPlacement.Layer, stamp.ShadowPlacement);
        Assert.Equal("#8012AB34", stamp.ShadowColor);
        Assert.Equal(48, stamp.ShadowOffsetX);
        Assert.Equal(63, stamp.ShadowBlur);
    }

    [Fact]
    public void Missing_stage15_shadow_fields_open_with_legacy_neutral_defaults()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage15-old.mapproj");
        MapProjectStore.Save(Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteStamp(path, stamp =>
        {
            stamp.Remove("shadowPlacement");
            stamp.Remove("shadowColor");
        });

        var stamp = MapProjectStore.Open(path).Document.Layers
            .OfType<ObjectLayer>().Single().Objects.Single().Stamp!;
        Assert.Equal(StampShadowPlacement.Object, stamp.ShadowPlacement);
        Assert.Equal(AppearanceColor.OpaqueBlack, stamp.ShadowColor);
    }

    [Fact]
    public void Out_of_range_or_invalid_stage15_values_are_rejected_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("stage15-invalid.mapproj");
        MapProjectStore.Save(Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteStamp(path, stamp =>
        {
            stamp["shadowBlur"] = 65;
            stamp["shadowColor"] = "not-a-colour";
        });

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    private static MapDocument Document()
    {
        var source = ProjectFixtures.Document("Stage 15");
        var layer = (ObjectLayer)source.Layers[1];
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "stamp", 200, 200, 120, 80, 25,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Sublayer = 7,
            ObjectOpacity = 0.65,
            Stamp = new StampStyle(Sha)
            {
                ReferenceWidth = 100,
                ReferenceHeight = 66.666,
                Blend = "Luminosity",
                Blur = 31,
                ShadowOffsetX = 48,
                ShadowOffsetY = -32,
                ShadowBlur = 63,
                ShadowOpacity = 0.6,
                ShadowPlacement = StampShadowPlacement.Layer,
                ShadowColor = "#8012AB34",
                Hue = -170,
                Saturation = 0.8,
                Brightness = -0.7,
                Contrast = 0.4,
            },
        };
        var asset = new MapAssetReference(
            Sha, "stamp", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "test", "test", AssetBytes);
        return source.WithAssets([asset]).WithLayer(layer.Id,
            current => ((ObjectLayer)current) with { Objects = [stamp] });
    }

    private static void RewriteStamp(string path, Action<JsonObject> change)
    {
        ProjectJsonTestMutations.RewriteFirstObjectChunk(path, objects =>
        {
            var stamp = objects[0]!["stamp"]!.AsObject();
            change(stamp);
        });
    }
}
