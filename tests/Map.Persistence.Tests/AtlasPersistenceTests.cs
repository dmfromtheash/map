using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class AtlasPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-atlas-" + Guid.NewGuid().ToString("N"));
    public AtlasPersistenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string FilePath(string name) => Path.Combine(_root, name);
    private static MapDocument Document(string title) => ProjectFixtures.Document(title) with { Id = DocumentId.New() };
    private static AtlasTransform Transform => new(100, 200, 300, 400, 600, 800);
    private AtlasSave Save(MapDocument document, string name) => new(document, FilePath(name), ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
    private AtlasLinkedResult Pair(MapDocument? parent = null, MapDocument? child = null, Action<int>? fault = null) =>
        AtlasStore.SaveLinked(Save(parent ?? Document("Мир"), "parent.mapproj"), Save(child ?? Document("Регион"), "child.mapproj"), Transform, "region", fault);

    [Fact]
    public void Atlas_v26_roundtrip_save_as_and_file_copy_keep_one_identity()
    {
        var pair = Pair(); var path = FilePath("parent.mapproj");
        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(pair.Parent.Id, opened.Id); Assert.Equal(pair.Parent.Atlas, opened.Atlas);
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("manifest.json")!.Open())
            Assert.Equal(26, JsonNode.Parse(stream)!["schemaVersion"]!.GetValue<int>());
        File.Copy(path, FilePath("copy.mapproj"));
        Assert.Equal(opened.Id, MapProjectStore.Open(FilePath("copy.mapproj")).Document.Id);
        AtlasStore.SaveMap(Save(opened, "renamed.mapproj"));
        var atlas = AtlasStore.Read(pair.ManifestPath);
        Assert.Equal(2, atlas.Maps.Length); Assert.Single(atlas.Maps, m => m.Id == opened.Id.Value);
        Assert.Equal("renamed.mapproj", atlas.Maps.Single(m => m.Id == opened.Id.Value).File);
        Assert.Equal(opened.Id, MapProjectStore.Open(FilePath("renamed.mapproj")).Document.Id);
    }

    [Fact]
    public void Atlas_region_has_exact_bidirectional_transform_and_parent_chain()
    {
        var parent = Document("Мир");
        var child = CanvasTransformService.ExtractRegion(parent, new(100, 200, 300, 400), "Регион", DocumentId.New(), DateTimeOffset.UtcNow);
        var pair = Pair(parent, child); var atlas = AtlasStore.Read(pair.ManifestPath);
        var link = Assert.Single(atlas.Links); Assert.Equal(parent.Id.Value, link.Parent); Assert.Equal(child.Id.Value, link.Child);
        var point = link.Transform.ToChild(250, 400); Assert.Equal((300d, 400d), point);
        Assert.Equal((250d, 400d), link.Transform.ToParent(point.X, point.Y));
        Assert.Equal(new[] { parent.Id.Value, child.Id.Value }, atlas.Breadcrumbs(child.Id.Value).Select(m => m.Id));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Atlas_recovery_completes_every_interrupted_publish_and_is_idempotent(int failAfter)
    {
        var parent = Document("Мир"); var child = Document("Регион");
        MapProjectStore.Save(parent, FilePath("parent.mapproj"), ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var manifest = AtlasStore.ManifestPath(parent, FilePath("parent.mapproj"));
        Assert.Throws<IOException>(() => Pair(parent, child, number => { if (number == failAfter) throw new IOException("Обрыв питания"); }));
        Assert.True(File.Exists(manifest + ".pending"));
        // Opening either map performs recovery, even when its old snapshot had no locator.
        var opened = MapProjectStore.Open(FilePath("parent.mapproj"));
        Assert.NotNull(opened.Document.Atlas); Assert.Equal(child.Id, MapProjectStore.Open(FilePath("child.mapproj")).Document.Id);
        Assert.Equal(2, AtlasStore.Read(manifest).Maps.Length);
        AtlasStore.Recover(manifest); Assert.False(File.Exists(manifest + ".pending"));
        Assert.Empty(Directory.GetDirectories(_root, ".atlas-txn-*"));
    }

    [Fact]
    public void Atlas_corrupt_staged_payload_never_replaces_remaining_files()
    {
        var parent = Document("Мир"); var child = Document("Регион");
        var manifest = AtlasStore.ManifestPath(parent, FilePath("parent.mapproj"));
        Assert.Throws<IOException>(() => Pair(parent, child, n => { if (n == 1) throw new IOException("Обрыв"); }));
        var parentBytes = File.ReadAllBytes(FilePath("parent.mapproj"));
        var stage = Assert.Single(Directory.GetDirectories(_root, ".atlas-txn-*"));
        File.WriteAllBytes(Path.Combine(stage, "1.mapproj"), [0]);
        Assert.Throws<InvalidDataException>(() => AtlasStore.Recover(manifest));
        Assert.Equal(parentBytes, File.ReadAllBytes(FilePath("parent.mapproj")));
        Assert.False(File.Exists(FilePath("child.mapproj"))); Assert.True(File.Exists(manifest + ".pending"));
    }

    [Fact]
    public void Atlas_recovery_opens_a_child_whose_nested_folder_was_not_published_yet()
    {
        var parent = Document("Мир"); var child = Document("Регион");
        var childPath = FilePath("nested/child.mapproj");
        Assert.Throws<IOException>(() => AtlasStore.SaveLinked(Save(parent, "parent.mapproj"), Save(child, "nested/child.mapproj"), Transform, "region",
            number => { if (number == 1) throw new IOException("Обрыв перед созданием подпапки"); }));
        Assert.False(Directory.Exists(FilePath("nested")));
        Assert.Equal(child.Id, MapProjectStore.Open(childPath).Document.Id);
        Assert.Empty(AtlasStore.Inspect(AtlasStore.ManifestPath(parent, FilePath("parent.mapproj"))));
    }

    [Fact]
    public void Atlas_rejects_duplicate_id_at_another_path_without_writing()
    {
        var pair = Pair();
        var before = File.ReadAllBytes(pair.ManifestPath);
        Assert.Throws<InvalidDataException>(() => AtlasStore.SaveLinked(Save(pair.Parent, "copy.mapproj"), Save(Document("Другой"), "other.mapproj"), Transform, "region"));
        Assert.Equal(before, File.ReadAllBytes(pair.ManifestPath)); Assert.False(File.Exists(FilePath("copy.mapproj")));
    }

    [Fact]
    public void Atlas_inspection_finds_missing_child_and_does_not_create_it()
    {
        var pair = Pair(); File.Delete(FilePath("child.mapproj"));
        var issue = Assert.Single(AtlasStore.Inspect(pair.ManifestPath));
        Assert.Equal("missing-map", issue.Code); Assert.Contains("Не найдена подкарта", issue.Message); Assert.NotEmpty(issue.Repair);
        Assert.False(File.Exists(FilePath("child.mapproj")));
    }

    [Fact]
    public void Atlas_inspection_finds_missing_linked_asset_and_wrong_hash()
    {
        var content = ProjectFixtures.PreviewPng();
        var asset = new MapAssetReference(Convert.ToHexString(SHA256.HashData(content)), "Утраченный рисунок", "image/png", ".png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "Свой", "Свой", relativePath: "art.png");
        var pair = Pair(Document("Мир").WithAssets([asset]));
        Assert.Contains(AtlasStore.Inspect(pair.ManifestPath), p => p.Code == "missing-asset");
        File.WriteAllBytes(FilePath("art.png"), [1]);
        Assert.Contains(AtlasStore.Inspect(pair.ManifestPath), p => p.Code == "missing-asset");
        File.WriteAllBytes(FilePath("art.png"), content); Assert.Empty(AtlasStore.Inspect(pair.ManifestPath));
    }

    [Fact]
    public void Atlas_inspection_rejects_external_map_and_hex_link_without_opening_them()
    {
        var parent = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 8, 8, "#FFFFFFFF");
        var layer = parent.Layers.OfType<HexLayer>().Single();
        parent = parent.WithLayer(layer.Id, _ => layer with { Notes = layer.Notes.SetItem(new(0, 0), new HexNote("Выход") { ChildMap = "../outside.mapproj" }) });
        var pair = Pair(parent); var atlas = AtlasStore.Read(pair.ManifestPath);
        Assert.Contains(AtlasStore.Inspect(pair.ManifestPath), p => p.Code == "external-link");
        AtlasStore.Save(atlas with { Maps = [.. atlas.Maps.Select(m => m.Id == pair.Child.Id.Value ? m with { File = "../outside.mapproj" } : m)] }, pair.ManifestPath);
        Assert.Contains(AtlasStore.Inspect(pair.ManifestPath), p => p.Map == pair.Child.Id.Value && p.Message.Contains("пределы"));
    }

    [Fact]
    public void Atlas_validates_cycles_multiple_parents_invalid_transform_and_format()
    {
        var pair = Pair(); var atlas = AtlasStore.Read(pair.ManifestPath); var link = atlas.Links[0];
        Assert.Throws<InvalidOperationException>(() => (atlas with { Links = [link, link] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (atlas with { Links = [link, new(link.Child, link.Parent, Transform, "hex")] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (atlas with { Links = [link with { Transform = Transform with { Width = 0 } }] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (atlas with { Version = 2 }).Validate());
    }

    [Fact]
    public void Atlas_save_with_child_outside_root_leaves_original_files_intact()
    {
        var parent = Document("Мир"); var child = Document("Регион");
        Assert.Throws<InvalidDataException>(() => AtlasStore.SaveLinked(Save(parent, "parent.mapproj"), Save(child, "../outside.mapproj"), Transform, "region"));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Atlas_moved_directory_keeps_relative_links_and_ids()
    {
        var pair = Pair(); var moved = _root + "-moved";
        Directory.Move(_root, moved);
        try
        {
            var path = Path.Combine(moved, Path.GetFileName(pair.ManifestPath));
            var atlas = AtlasStore.Read(path); Assert.Empty(AtlasStore.Inspect(path));
            Assert.Equal(pair.Child.Id, MapProjectStore.Open(AtlasStore.ResolveMap(path, atlas.Maps.Single(m => m.Id == pair.Child.Id.Value))).Document.Id);
        }
        finally { Directory.Move(moved, _root); }
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)] [InlineData(21)]
    [InlineData(22)] [InlineData(23)] [InlineData(24)] [InlineData(25)]
    public void Atlas_reader_still_opens_every_previous_schema_without_atlas(int schema)
    {
        var document = Document("Старая карта"); var path = FilePath("old.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; JsonNode manifest;
            using (var stream = entry.Open()) manifest = JsonNode.Parse(stream)!;
            manifest["schemaVersion"] = schema; entry.Delete();
            using var output = zip.CreateEntry("manifest.json").Open(); System.Text.Json.JsonSerializer.Serialize(output, manifest);
        }
        var opened = MapProjectStore.Open(path).Document; Assert.Equal(document.Id, opened.Id); Assert.Null(opened.Atlas);
    }

    [Fact]
    public void Atlas_v26_parent_keeps_named_versions_and_player_export_omits_locator()
    {
        var document = Document("Мир");
        var checkpoint = new MapCheckpoint(CheckpointId.New(), "До региона", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(document), ProjectFixtures.PreviewPng());
        var pair = Pair(document.WithCheckpoints([checkpoint]));
        var opened = MapProjectStore.Open(FilePath("parent.mapproj")).Document;
        Assert.Single(opened.Checkpoints); Assert.Equal("Мир", opened.Checkpoints[0].Snapshot.Title);
        Assert.Null(new ExportScene(AssetFree( pair.Parent), recipient: ExportRecipient.Players).Document.Atlas);
    }
    private static MapDocument AssetFree(MapDocument d) => d.WithAssets([]);
}
