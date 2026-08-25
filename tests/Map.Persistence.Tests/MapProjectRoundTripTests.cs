using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public class MapProjectRoundTripTests
{
    [Fact]
    public void Save_then_open_preserves_every_id_order_geometry_and_property()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document();
        var path = workspace.File("roundtrip" + MapProjectFormat.Extension);

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var opened = MapProjectStore.Open(path);

        ProjectFixtures.AssertSemanticallyEqual(document, opened.Document);

        Assert.Equal("0.1.0-test", opened.AppVersion);
        Assert.Equal(ProjectFixtures.FixedModifiedUtc, opened.ModifiedUtc);
        Assert.Equal(ProjectFixtures.PreviewPng(), opened.PreviewPng);
    }

    [Fact]
    public void The_container_holds_exactly_the_required_entries_with_matching_checksums()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("container" + MapProjectFormat.Extension);

        MapProjectStore.Save(
            ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using var archive = ZipFile.OpenRead(path);

        // Exactly the four required entries - no extras, nothing missing.
        Assert.Equal(
            MapProjectFormat.RequiredEntries.Order().ToArray(),
            archive.Entries.Select(e => e.FullName).Order().ToArray());

        var manifestBytes = ReadEntry(archive, MapProjectFormat.ManifestEntry);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;

        Assert.Equal(MapProjectFormat.FormatId, root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        // Both recorded checksums must match the actual stored bytes.
        var sceneBytes = ReadEntry(archive, MapProjectFormat.SceneEntry);
        var assetManifestBytes = ReadEntry(archive, MapProjectFormat.AssetManifestEntry);

        Assert.Equal(Sha256Hex(sceneBytes), root.GetProperty("sceneSha256").GetString());
        Assert.Equal(Sha256Hex(assetManifestBytes), root.GetProperty("assetManifestSha256").GetString());

        // Canvas metadata follows the docs/05 manifest baseline.
        var canvas = root.GetProperty("canvas");
        Assert.Equal(1920, canvas.GetProperty("width").GetInt32());
        Assert.Equal(1080, canvas.GetProperty("height").GetInt32());
        Assert.Equal("px", canvas.GetProperty("unit").GetString());
        Assert.Equal("#FF10131A", canvas.GetProperty("background").GetString());

        // The empty asset catalog is a real, well-formed document.
        using var assetManifest = JsonDocument.Parse(assetManifestBytes);
        Assert.Equal(0, assetManifest.RootElement.GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public void Saving_the_same_snapshot_with_the_same_metadata_yields_identical_json_bytes()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document();

        var first = workspace.File("a" + MapProjectFormat.Extension);
        var second = workspace.File("b" + MapProjectFormat.Extension);

        MapProjectStore.Save(document, first, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        MapProjectStore.Save(document, second, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        foreach (var entry in new[]
                 {
                     MapProjectFormat.ManifestEntry,
                     MapProjectFormat.SceneEntry,
                     MapProjectFormat.AssetManifestEntry,
                 })
        {
            using var archiveA = ZipFile.OpenRead(first);
            using var archiveB = ZipFile.OpenRead(second);
            Assert.Equal(ReadEntry(archiveA, entry), ReadEntry(archiveB, entry));
        }
    }

    [Fact]
    public void Save_rejects_missing_preview_bytes_before_touching_the_disk()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("no-preview" + MapProjectFormat.Extension);

        Assert.ThrowsAny<ArgumentException>(() =>
            MapProjectStore.Save(ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), []));

        Assert.False(File.Exists(path));
    }

    internal static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);

        using var stream = entry!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
