using System.IO.Compression;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>The Свет section in the file (schema v28, DEC-057): saved, reopened, undone.</summary>
public sealed class LightingPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-light-" + Guid.NewGuid().ToString("N"));
    public LightingPersistenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Lighting_v28_roundtrip_keeps_every_switch_strength_and_light()
    {
        var torch = new MapLightSource(Guid.NewGuid(), 120.5, 340, "#ff9a42", 280, 1.1, true);
        var lighting = new MapLighting
        {
            Enabled = true, TimeOfDay = 0.62,
            Relief = new(true, 0.4), Shadows = new(false, 0.9), Fog = new(true, 0.25), Lights = new(true, 0.7), Color = new(false, 0.5),
            Sources = [torch],
        };
        var document = ProjectFixtures.Document("Свет") with { Lighting = lighting };
        var path = Path.Combine(_root, "light.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document.Lighting!;
        Assert.True(opened.Enabled);
        Assert.Equal(0.62, opened.TimeOfDay, 6);
        Assert.Equal(new LightingEffect(true, 0.4), opened.Relief);
        Assert.Equal(new LightingEffect(false, 0.9), opened.Shadows);
        Assert.Equal(new LightingEffect(true, 0.25), opened.Fog);
        Assert.Equal(new LightingEffect(true, 0.7), opened.Lights);
        Assert.Equal(new LightingEffect(false, 0.5), opened.Color);
        Assert.Equal(torch, Assert.Single(opened.Sources));
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        Assert.Equal(28, JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_map_without_light_keeps_its_older_schema()
    {
        var path = Path.Combine(_root, "plain.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Без света"), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        Assert.True(JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>() < 28);
        Assert.Null(MapProjectStore.Open(path).Document.Lighting);
    }

    [Fact]
    public void A_named_version_keeps_its_light_through_save_and_reopen()
    {
        var night = new MapLighting { TimeOfDay = 1, Sources = [new MapLightSource(Guid.NewGuid(), 40, 50, "#ff8a3c", 380, 1.3, true)] };
        var plain = ProjectFixtures.Document("Версии");
        var version = new MapCheckpoint(CheckpointId.New(), "Ночь", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(plain with { Lighting = night }), ProjectFixtures.PreviewPng());
        // The map itself is unlit now: the version alone carries the light (and so the schema v28).
        var document = plain.WithCheckpoints([version]);
        var path = Path.Combine(_root, "versions.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(path).Document;
        Assert.Null(opened.Lighting);
        var kept = Assert.Single(opened.Checkpoints).Snapshot.Lighting!;
        Assert.Equal(1, kept.TimeOfDay);
        Assert.Equal(night.Sources[0], Assert.Single(kept.Sources));
        Assert.Equal(1, Assert.Single(opened.Checkpoints).Snapshot.Materialize(opened.Id).Lighting!.TimeOfDay);
    }

    [Fact]
    public void A_light_change_is_one_undo_step()
    {
        var document = ProjectFixtures.Document("Отмена");
        var command = new SetLightingCommand("Время суток", null, new MapLighting { TimeOfDay = 1 });
        var lit = command.Apply(document);
        Assert.Equal(1, lit.Lighting!.TimeOfDay);
        Assert.Null(command.Revert(lit).Lighting);
    }

    [Fact]
    public void Out_of_range_numbers_from_a_file_are_clamped()
    {
        var lighting = new MapLighting
        {
            TimeOfDay = 7, Relief = new(true, -3), Sources = [new MapLightSource(Guid.NewGuid(), 1, 2, "#ffffff", 1e9, 99, false)],
        }.Normalized();
        Assert.Equal(1, lighting.TimeOfDay);
        Assert.Equal(0, lighting.Relief.Strength);
        Assert.Equal(MapLightSource.MaxRadius, lighting.Sources[0].Radius);
        Assert.Equal(2, lighting.Sources[0].Intensity);
    }

    /// <summary>
    /// Live-check map for Claude: MAP_LIGHT_MAP_OUT (target .mapproj) and MAP_LIGHT_ASSETS (folder of
    /// mountain PNGs named by SHA-256, a COPY of the owner's originals). Skipped otherwise.
    /// </summary>
    [Fact]
    public void Write_the_live_check_map_when_asked()
    {
        var target = Environment.GetEnvironmentVariable("MAP_LIGHT_MAP_OUT");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        var placements = new[] { (560.0, 880.0, 0.76), (1300.0, 760.0, 0.80), (2040.0, 900.0, 0.74) };
        var references = new List<MapAssetReference>();
        var objects = new List<SceneObject>();
        var i = 0;
        foreach (var file in Directory.GetFiles(assets, "*.png").Take(3))
        {
            var sha = Path.GetFileNameWithoutExtension(file);
            var bytes = File.ReadAllBytes(file);
            references.Add(new MapAssetReference(sha, "Гора " + (i + 1), "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "owner-drawing (live-check copy)", "owned", bytes, fallbackThumbnailPng: ProjectFixtures.PreviewPng()));
            // PNG header: width and height, big-endian, at bytes 16 and 20.
            int pw = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            int ph = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            var (cx, baseY, scale) = placements[i++ % placements.Length];
            double w = 1024 * scale, h = 1024.0 * ph / pw * scale;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", cx - w / 2, baseY - h, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        var document = new MapDocument(DocumentId.New(), "Проверка света", DateTimeOffset.UtcNow,
            new CanvasDefinition(2560, 1440, "px", "#FFB6AE86"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFB6AE86"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects.OrderBy(o => o.Y + o.Height)])],
            references);
        MapProjectStore.Save(document, target, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
    }
}
