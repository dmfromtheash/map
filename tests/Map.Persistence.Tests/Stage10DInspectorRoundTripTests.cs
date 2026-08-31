using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// Stage 10D: every inspector-visible appearance value survives save/reopen, an old file without
/// the new fields opens with neutral defaults, and non-finite/out-of-range/unknown values are
/// corruption, not data.
/// </summary>
public partial class Stage10DInspectorRoundTripTests
{
    [System.Text.RegularExpressions.GeneratedRegex("\"sceneSha256\"\\s*:\\s*\"[0-9a-f]+\"")]
    private static partial System.Text.RegularExpressions.Regex SceneShaRegex();

    private const string StampSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // ------------------------------------------------------------ save / reopen equality

    [Fact]
    public void All_stage10d_appearance_values_survive_save_and_reopen()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("appearance.mapproj");
        var document = AppearanceDocument();

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        ProjectFixtures.AssertSemanticallyEqual(document, reopened);

        var objects = ((ObjectLayer)reopened.Layers[1]).Objects;
        var stamp = objects[0].Stamp!;
        Assert.Equal(96, stamp.ReferenceWidth);
        Assert.Equal(72, stamp.ReferenceHeight);
        var line = objects[1].Line!;
        Assert.Equal(StrokePattern.Dashed, line.Stroke.Pattern);
        Assert.Equal(2.5, line.Stroke.Blur);
    }

    [Fact]
    public void A_second_reopen_produces_the_same_document_without_drift()
    {
        using var workspace = new TempWorkspace();
        var first = workspace.File("first.mapproj");
        var second = workspace.File("second.mapproj");

        MapProjectStore.Save(
            AppearanceDocument(), first, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var opened = MapProjectStore.Open(first).Document;
        MapProjectStore.Save(opened, second, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(second).Document;

        ProjectFixtures.AssertSemanticallyEqual(opened, reopened);
    }

    [Fact]
    public void Optional_appearance_asset_hashes_are_validated_and_normalized_on_open()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("uppercase-appearance-hashes.mapproj");
        MapProjectStore.Save(
            AppearanceDocument(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        RewriteScene(path, json => json.Replace(
            StampSha, StampSha.ToUpperInvariant(), StringComparison.Ordinal));

        var reopened = MapProjectStore.Open(path).Document;
        var objects = reopened.Layers.OfType<ObjectLayer>().Single().Objects;
        Assert.Equal(StampSha, objects.Single(item => item.Line is not null).Line!.Stroke.AssetSha256);
        Assert.Equal(
            StampSha,
            objects.Single(item => item.Shape is not null).Shape!.Fill.TextureAssetSha256);
        Assert.Equal(
            StampSha,
            reopened.Layers.OfType<FilterLayer>().Single().Settings.TextureAssetSha256);
    }

    // ------------------------------------------------------------- old files stay neutral

    [Fact]
    public void An_old_scene_without_the_new_fields_opens_with_neutral_defaults()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("old.mapproj");
        MapProjectStore.Save(
            AppearanceDocument(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        // Simulate a pre-10D writer: strip every new field from the stored JSON. "blur" existed
        // on stamps and text before this slice, but stripping it everywhere only makes the
        // neutral-default assertions stricter.
        RewriteScene(path, sceneJson =>
        {
            using var parsed = JsonDocument.Parse(sceneJson);
            var scene = StripProperties(
                parsed.RootElement, ["referenceWidth", "referenceHeight", "pattern", "blur"]);
            return scene;
        });

        var reopened = MapProjectStore.Open(path).Document;
        var objects = ((ObjectLayer)reopened.Layers[1]).Objects;
        Assert.Null(objects[0].Stamp!.ReferenceWidth);
        Assert.Null(objects[0].Stamp!.ReferenceHeight);
        Assert.Equal(StrokePattern.Solid, objects[1].Line!.Stroke.Pattern);
        Assert.Equal(0, objects[1].Line!.Stroke.Blur);
        Assert.Equal(StrokePattern.Solid, objects[2].Shape!.Stroke.Pattern);
    }

    // --------------------------------------------------------- invalid values are refused

    [Theory]
    [InlineData("\"referenceWidth\": 96", "\"referenceWidth\": -5")]
    [InlineData("\"referenceWidth\": 96", "\"referenceWidth\": \"NaN\"")]
    [InlineData("\"blur\": 2.5", "\"blur\": -1")]
    [InlineData("\"blur\": 2.5", "\"blur\": 32.0001")]
    [InlineData("\"blur\": 2.5", "\"blur\": \"Infinity\"")]
    [InlineData("\"pattern\": \"Dashed\"", "\"pattern\": \"Zigzag\"")]
    [InlineData("\"blend\": \"Multiply\"", "\"blend\": \"Glow\"")]
    [InlineData("\"blur\": 1.5", "\"blur\": \"NaN\"")]
    [InlineData(
        "\"textureAssetSha256\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"",
        "\"textureAssetSha256\": \"not-a-sha\"")]
    public void An_invalid_appearance_value_is_corruption_not_data(string needle, string poison)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("poisoned.mapproj");
        MapProjectStore.Save(
            AppearanceDocument(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        RewriteScene(path, sceneJson =>
        {
            Assert.True(
                JsonTestMutations.ContainsFragment(sceneJson, needle),
                $"scene.json does not contain «{needle}»");
            return JsonTestMutations.ReplaceFirst(sceneJson, needle, poison);
        });

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void Maximum_supported_blur_round_trips_for_every_persisted_inspector_style()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("maximum-blur.mapproj");
        var document = AppearanceDocument();
        var layer = (ObjectLayer)document.Layers[1];
        var objects = layer.Objects.ToArray();
        objects[0] = objects[0] with
        {
            Stamp = objects[0].Stamp! with { Blur = AppearanceLimits.MaxBlur },
        };
        objects[1] = objects[1] with
        {
            Line = objects[1].Line! with
            {
                Stroke = objects[1].Line!.Stroke with { Blur = AppearanceLimits.MaxBlur },
            },
        };
        objects[3] = objects[3] with
        {
            Text = objects[3].Text! with { Blur = AppearanceLimits.MaxBlur },
        };
        document = document with
        {
            Layers = [document.Layers[0], layer with { Objects = objects }, document.Layers[2]],
        };

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<ObjectLayer>().Single().Objects;

        Assert.Equal(AppearanceLimits.MaxBlur, reopened[0].Stamp!.Blur);
        Assert.Equal(AppearanceLimits.MaxBlur, reopened[1].Line!.Stroke.Blur);
        Assert.Equal(AppearanceLimits.MaxBlur, reopened[3].Text!.Blur);
    }

    [Theory]
    [InlineData("\"blur\": 1.5", "stamp blur")]
    [InlineData("\"blur\": 2.5", "vector stroke blur")]
    [InlineData("\"blur\": 0.5", "text blur")]
    public void Invalid_blur_reports_the_rejected_style_boundary(string needle, string expected)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("named-blur-error.mapproj");
        MapProjectStore.Save(
            AppearanceDocument(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        RewriteScene(path, sceneJson => JsonTestMutations.ReplaceAll(
            sceneJson, needle, "\"blur\": 32.0001"));

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("scene.json", corruption.Message, StringComparison.Ordinal);
        Assert.Contains("out-of-range", corruption.Message, StringComparison.Ordinal);
        Assert.Contains(expected, corruption.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------- fixture

    /// <summary>One document exercising every Stage 10D appearance field on all four kinds.</summary>
    internal static MapDocument AppearanceDocument()
    {
        var stamp = new SceneObject(
            ObjectId.Parse("00000001-0001-0001-0001-000000000001"), SceneObjectKind.Stamp, "штамп",
            100, 100, 120, 90, 15, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Sublayer = 3,
            ObjectOpacity = 0.8,
            Stamp = new StampStyle(StampSha)
            {
                ReferenceWidth = 96,
                ReferenceHeight = 72,
                FlipHorizontal = true,
                FlipVertical = true,
                Blend = "Multiply",
                Blur = 1.5,
                ShadowOffsetX = 5,
                ShadowOffsetY = -3,
                ShadowBlur = 7,
                ShadowOpacity = 0.4,
                Hue = 35,
                Saturation = 0.2,
                Brightness = -0.3,
                Contrast = 0.6,
            },
        };

        var line = VectorGeometry.CreateLine(
            ObjectId.Parse("00000002-0002-0002-0002-000000000002"), "линия",
            [new VectorPoint(300, 300), new VectorPoint(500, 380), new VectorPoint(650, 350)],
            new VectorStrokeStyle
            {
                Color = "#FF884422",
                Width = 12,
                Opacity = 0.7,
                Blend = "Darken",
                Blur = 2.5,
                Pattern = StrokePattern.Dashed,
                Offset = 4,
                EndCap = VectorCap.Square,
                NodeCap = VectorCap.Butt,
                ShadowOffsetX = 3,
                ShadowOffsetY = 3,
                ShadowBlur = 5,
                ShadowOpacity = 0.35,
                AssetSha256 = StampSha,
            });

        var shape = VectorGeometry.CreateShape(
            ObjectId.Parse("00000003-0003-0003-0003-000000000003"), "фигура",
            ShapePrimitiveKind.Rectangle, new WorldBounds(700, 200, 950, 420),
            new ShapeFillStyle
            {
                Color = "#AA507A5B",
                Opacity = 0.65,
                TextureAssetSha256 = StampSha,
            },
            new VectorStrokeStyle
            {
                Color = "#FF223344",
                Width = 5,
                Opacity = 0.9,
                Blend = "Screen",
                Blur = 1.25,
                Pattern = StrokePattern.Dotted,
            }) with
        {
            ObjectOpacity = 0.75,
        };

        var text = new SceneObject(
            ObjectId.Parse("00000004-0004-0004-0004-000000000004"), SceneObjectKind.Text, "подпись",
            200, 600, 400, 160, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Первая строка\nвторая строка",
                FontWeight = 700,
                FontSize = 44,
                FillColor = "#FF112233",
                OutlineColor = "#FFEEDDCC",
                OutlineWidth = 2,
                ShadowOffsetX = 4,
                ShadowOffsetY = 4,
                ShadowBlur = 6,
                ShadowOpacity = 0.55,
                Curve = 18,
                LetterSpacing = 2.5,
                LineHeight = 1.4,
                Alignment = TextAlignment.Center,
                FlipHorizontal = true,
                Blur = 0.5,
                Blend = "Overlay",
            },
        };

        return new MapDocument(
            ProjectFixtures.FixedDocumentId, "appearance", ProjectFixtures.FixedCreatedUtc,
            new CanvasDefinition(1920, 1080, "px", "#FF10131A"),
            [
                new BaseColorLayer(
                    ProjectFixtures.FixedBaseLayerId, "background", true, false, 1.0, "#FF10131A"),
                new ObjectLayer(
                    ProjectFixtures.FixedLayerId, "ground", true, false, 1.0,
                    [stamp, line, shape, text]),
                new FilterLayer(
                    LayerId.Parse("00000005-0005-0005-0005-000000000005"),
                    "texture filter", true, false, 1.0,
                    new FilterSettings { TextureAssetSha256 = StampSha }),
            ]);
    }

    /// <summary>Re-serializes the scene JSON without the named properties, wherever they occur.</summary>
    private static string StripProperties(JsonElement element, IReadOnlyList<string> names)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteWithout(element, names, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteWithout(
        JsonElement element, IReadOnlyList<string> names, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (names.Contains(property.Name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteWithout(property.Value, names, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteWithout(item, names, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>Patches scene.json in place and fixes the recorded checksum.</summary>
    private static void RewriteScene(string path, Func<string, string> rewrite)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);

        string sceneJson;
        var sceneEntry = archive.GetEntry(MapProjectFormat.SceneEntry)!;
        using (var reader = new StreamReader(sceneEntry.Open(), Encoding.UTF8, leaveOpen: false))
        {
            sceneJson = reader.ReadToEnd();
        }

        var sceneBytes = Encoding.UTF8.GetBytes(rewrite(sceneJson));
        using (var stream = sceneEntry.Open())
        {
            stream.SetLength(0);
            stream.Write(sceneBytes);
        }

        using (var parsed = JsonDocument.Parse(sceneBytes))
        {
            _ = parsed.RootElement;
        }

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        string manifestText;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestText = reader.ReadToEnd();
        }

        var newHash = MapProjectRoundTripTests.Sha256Hex(sceneBytes);
        manifestText = SceneShaRegex().Replace(manifestText, $"\"sceneSha256\": \"{newHash}\"");

        using var writer = new StreamWriter(manifestEntry.Open());
        writer.BaseStream.SetLength(0);
        writer.Write(manifestText);
    }
}
