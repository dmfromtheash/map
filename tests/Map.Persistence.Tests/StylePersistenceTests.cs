using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>The style of a map in the file (schema v29, DEC-059): saved, reopened, undone.</summary>
public sealed class StylePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-style-" + Guid.NewGuid().ToString("N"));
    public StylePersistenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(MapStylePreset.InkWetBrush)]
    [InlineData(MapStylePreset.InkPenHatching)]
    [InlineData(MapStylePreset.InkPenStipple)]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.EngravingAquatint)]
    public void Style_v29_roundtrip_keeps_the_preset_strength_and_every_effect(MapStylePreset preset)
    {
        var style = MapStyle.For(preset) with
        {
            Strength = 0.7, Strokes = new(false, 0.3), Bleed = new(true, 0.45), Mist = new(true, 0.2), Contour = new(false, 0.6),
        };
        foreach (var kind in MapStyle.EffectsOf(preset))
            style = style.WithEffect(kind, new StyleEffect((int)kind % 2 == 0, 0.21 + (int)kind * 0.03));
        var document = ProjectFixtures.Document("Стиль") with { Style = style, Lighting = new MapLighting { TimeOfDay = 0.5 } };
        var path = Path.Combine(_root, "style.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(style, opened.Style);
        Assert.Equal(0.5, opened.Lighting!.TimeOfDay);
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        Assert.Equal(29, JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_map_without_style_keeps_its_older_schema()
    {
        var path = Path.Combine(_root, "plain.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Без стиля") with { Lighting = new MapLighting() }, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        Assert.Equal(28, JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>());
        Assert.Null(MapProjectStore.Open(path).Document.Style);
    }

    [Fact]
    public void A_named_version_keeps_its_style_through_save_and_reopen()
    {
        var pen = MapStyle.For(MapStylePreset.InkPenStipple);
        var plain = ProjectFixtures.Document("Версии");
        var version = new MapCheckpoint(CheckpointId.New(), "Пуантилизм", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(plain with { Style = pen }), ProjectFixtures.PreviewPng());
        var path = Path.Combine(_root, "versions.mapproj");
        MapProjectStore.Save(plain.WithCheckpoints([version]), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Null(opened.Style);
        Assert.Equal(pen, Assert.Single(opened.Checkpoints).Snapshot.Style);
        Assert.Equal(pen, Assert.Single(opened.Checkpoints).Snapshot.Materialize(opened.Id).Style);
    }

    [Fact]
    public void Applying_a_style_is_one_undo_step()
    {
        var document = ProjectFixtures.Document("Отмена");
        var before = MapStyle.For(MapStylePreset.InkWetBrush);
        var command = new SetMapStyleCommand("Стиль: перо", before, MapStyle.For(MapStylePreset.InkPenHatching));
        var styled = command.Apply(document with { Style = before });
        Assert.Equal(MapStylePreset.InkPenHatching, styled.Style!.Preset);
        Assert.Equal(before, command.Revert(styled).Style);
        Assert.Null(new SetMapStyleCommand("Стиль", null, before).Revert(styled).Style);
    }

    [Fact]
    public void Out_of_range_numbers_and_unknown_presets_are_brought_into_range()
    {
        var style = new MapStyle { Preset = (MapStylePreset)99, Strength = 4, Bleed = new(true, -2) }.Normalized();
        Assert.Equal(MapStylePreset.InkWetBrush, style.Preset);
        Assert.Equal(1, style.Strength);
        Assert.Equal(0, style.Bleed.Strength);
    }

    [Fact]
    public void A_file_with_an_unknown_style_is_reported_as_damaged()
    {
        var path = Path.Combine(_root, "unknown.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Чужой стиль") with { Style = new MapStyle() }, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteManifest(path, m => m["style"]!["preset"] = "marble");
        Assert.ThrowsAny<Exception>(() => MapProjectStore.Open(path));
    }

    [Theory]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.EngravingAquatint)]
    public void Missing_new_effects_use_the_approved_preset_defaults(MapStylePreset preset)
    {
        var path = Path.Combine(_root, "defaults.mapproj");
        var style = MapStyle.For(preset);
        MapProjectStore.Save(ProjectFixtures.Document("Умолчания") with { Style = style }, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        RewriteManifest(path, m =>
        {
            foreach (var key in new[] { "sea", "coast", "colourWash", "aquatint", "plate", "border" })
                m["style"]!.AsObject().Remove(key);
        });
        Assert.Equal(style, MapProjectStore.Open(path).Document.Style);
    }

    [Fact]
    public void The_panel_shows_only_the_effects_a_preset_uses()
    {
        Assert.Contains(StyleEffectKind.Bleed, MapStyle.EffectsOf(MapStylePreset.InkWetBrush));
        Assert.DoesNotContain(StyleEffectKind.Pen, MapStyle.EffectsOf(MapStylePreset.InkWetBrush));
        Assert.Equal([StyleEffectKind.Pen, StyleEffectKind.Paper, StyleEffectKind.Contour], MapStyle.EffectsOf(MapStylePreset.InkPenStipple));
        // Every effect a preset shows is switched on as it comes.
        foreach (var preset in Enum.GetValues<MapStylePreset>())
            Assert.All(MapStyle.EffectsOf(preset), kind => Assert.True(MapStyle.For(preset).Effect(kind).Enabled));
    }

    private static void RewriteManifest(string path, Action<JsonNode> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("manifest.json")!;
        JsonNode node;
        using (var read = entry.Open()) node = JsonNode.Parse(read)!;
        change(node);
        entry.Delete();
        using var write = zip.CreateEntry("manifest.json").Open();
        using var writer = new StreamWriter(write);
        writer.Write(node.ToJsonString());
    }
}
