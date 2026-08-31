using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// Stage 10C: the group name travels with the .mapproj container. Old files without the field
/// keep opening, the field is omitted for ungrouped and unnamed content (old documents stay
/// byte-identical), and a file whose members disagree on the name is corruption, not data.
/// </summary>
public partial class Stage10CGroupRoundTripTests
{
    [System.Text.RegularExpressions.GeneratedRegex("\"groupName\":\\s*\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial System.Text.RegularExpressions.Regex GroupNameFieldRegex();

    [System.Text.RegularExpressions.GeneratedRegex("\"sceneSha256\"\\s*:\\s*\"[0-9a-f]+\"")]
    private static partial System.Text.RegularExpressions.Regex SceneShaRegex();

    private static readonly Guid FixedGroupId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static MapDocument GroupedDocument(string? groupName)
    {
        var a = new SceneObject(
            ProjectFixtures.FixedRectangleId, SceneObjectKind.Rectangle, "A",
            10, 20, 100, 50, 0, "#FF3E5C76", "#FF1B263B", 2, visible: true, locked: false)
        {
            GroupId = FixedGroupId,
            GroupName = groupName,
        };
        var b = new SceneObject(
            ProjectFixtures.FixedEllipseId, SceneObjectKind.Ellipse, "B",
            300, 200, 64, 48, 0, "#80FFD60A", "#FF000000", 0, visible: true, locked: false)
        {
            GroupId = FixedGroupId,
            GroupName = groupName,
        };

        return new MapDocument(
            ProjectFixtures.FixedDocumentId, "grouped", ProjectFixtures.FixedCreatedUtc,
            new CanvasDefinition(1920, 1080, "px", "#FF10131A"),
            [
                new BaseColorLayer(ProjectFixtures.FixedBaseLayerId, "background", true, false, 1.0, "#FF10131A"),
                new ObjectLayer(ProjectFixtures.FixedLayerId, "ground", true, false, 1.0, [a, b]),
            ]);
    }

    [Fact]
    public void Group_name_and_id_survive_save_and_reopen()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("named-group.mapproj");
        MapProjectStore.Save(GroupedDocument("Тестовая группа"), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var reopened = MapProjectStore.Open(path).Document;

        var objects = ((ObjectLayer)reopened.Layers[1]).Objects;
        Assert.Equal(FixedGroupId, objects[0].GroupId);
        Assert.Equal("Тестовая группа", objects[0].GroupName);
        Assert.Equal("Тестовая группа", objects[1].GroupName);
    }

    [Fact]
    public void A_group_without_a_name_saves_without_the_field_and_reopens()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("old-style-group.mapproj");
        MapProjectStore.Save(GroupedDocument(groupName: null), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(archive.GetEntry(MapProjectFormat.SceneEntry)!.Open());
            var sceneJson = reader.ReadToEnd();
            Assert.DoesNotContain("groupName", sceneJson, StringComparison.Ordinal);
            Assert.Contains("groupId", sceneJson, StringComparison.Ordinal);
        }

        var reopened = MapProjectStore.Open(path).Document;
        var objects = ((ObjectLayer)reopened.Layers[1]).Objects;
        Assert.Equal(FixedGroupId, objects[0].GroupId);
        Assert.Null(objects[0].GroupName);
    }

    [Fact]
    public void An_ungrouped_document_scene_carries_no_group_fields_at_all()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("plain.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document(), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry(MapProjectFormat.SceneEntry)!.Open());
        var sceneJson = reader.ReadToEnd();
        Assert.DoesNotContain("groupName", sceneJson, StringComparison.Ordinal);
        Assert.DoesNotContain("groupId", sceneJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Members_disagreeing_on_the_group_name_are_corruption_not_data()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("disagreeing.mapproj");
        MapProjectStore.Save(GroupedDocument("Тестовая группа"), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        RewriteScene(path, sceneJson =>
        {
            // The writer may escape non-ASCII, so the patch targets the field, not the value.
            Assert.Matches(GroupNameFieldRegex(), sceneJson);
            return GroupNameFieldRegex().Replace(sceneJson, "\"groupName\": \"other-name\"", count: 1);
        });

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void A_name_without_a_group_id_is_corruption_not_data()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("orphan-name.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document(), path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        RewriteScene(path, sceneJson =>
        {
            return JsonTestMutations.InsertAfter(
                sceneJson, "\"name\": \"keep\",", " \"groupName\": \"Сирота\",");
        });

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    /// <summary>Patches scene.json in place and fixes the recorded checksum, isolating the model check.</summary>
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

        // Sanity: the patched scene must still be valid JSON, or the test proves nothing.
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
