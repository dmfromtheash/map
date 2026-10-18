using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class MapCategoryPersistenceTests
{
    [Fact]
    public void A_standard_map_keeps_its_bytes_and_schema_and_opens_as_standard()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Standard map");
        Assert.True(document.Category.IsStandard);
        var path = workspace.File("standard.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var manifest = Manifest(path))
        {
            Assert.Equal(MapProjectFormat.SchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(manifest.RootElement.TryGetProperty("category", out _));
        }

        Assert.Equal(MapCategoryId.Standard, MapProjectStore.Open(path).Document.Category);
    }

    [Fact]
    public void Another_category_round_trips_in_v11_and_its_versions_inherit_it()
    {
        using var workspace = new TempWorkspace();
        var source = ProjectFixtures.Document("Other workshop");
        var category = new MapCategoryId("other-workshop");
        var document = new MapDocument(
            source.Id, source.Title, source.CreatedUtc, source.Canvas, source.Layers, source.Assets, source.Notes,
            checkpoints: [], source.StampForeground, category);
        document = document.WithCheckpoints([
            new MapCheckpoint(CheckpointId.New(), "Версия", DateTimeOffset.UnixEpoch,
                MapCheckpointSnapshot.Capture(document), ProjectFixtures.PreviewPng()),
        ]);
        var path = workspace.File("other.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var manifest = Manifest(path))
        {
            Assert.Equal(MapProjectFormat.CategoriesSchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("other-workshop", manifest.RootElement.GetProperty("category").GetString());
        }

        var opened = MapProjectStore.Open(path).Document;
        Assert.Equal(category, opened.Category);
        Assert.Equal(category, Assert.Single(opened.Checkpoints).Snapshot.Category);
    }

    [Fact]
    public void A_category_in_an_older_schema_is_a_damaged_file_not_a_standard_map()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Tampered");
        var path = workspace.File("tampered.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            json = json.Replace("\"preset\":\"blank\"", "\"preset\":\"blank\",\"category\":\"other-workshop\"");
            entry.Delete();
            var replaced = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(replaced.Open());
            writer.Write(json);
        }

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    private static JsonDocument Manifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream);
    }
}
