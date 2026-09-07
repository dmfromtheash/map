using System.IO.Compression;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public class MapProjectFailureTests
{
    [Fact]
    public void A_fault_after_the_durable_temp_but_before_replace_leaves_the_last_good_file_openable()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("atomic" + MapProjectFormat.Extension);

        // First save establishes a known-good version on disk.
        var firstVersion = ProjectFixtures.Document("first version");
        MapProjectStore.Save(firstVersion, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var firstBytes = File.ReadAllBytes(path);

        // Second save carries different content and dies at the one controlled boundary:
        // temp fully written, flushed and validated - target not yet replaced.
        var secondVersion = ProjectFixtures.Document("second version");
        var faults = new SaveFaultInjection
        {
            AfterDurableTempBeforeReplace = () => throw new IOException("Injected failure before replace."),
        };

        var failure = Assert.Throws<MapProjectSaveException>(() =>
            MapProjectStore.Save(
                secondVersion, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(),
                journal: null, faults));

        // The failure is reported, never masked as success, and it names the paths involved.
        Assert.Equal(Path.GetFullPath(path), failure.TargetPath);
        Assert.IsType<IOException>(failure.InnerException);

        // The target is byte-for-byte the first version and still opens semantically intact.
        Assert.Equal(firstBytes, File.ReadAllBytes(path));
        var reopened = MapProjectStore.Open(path);
        ProjectFixtures.AssertSemanticallyEqual(firstVersion, reopened.Document);
        Assert.Equal("first version", reopened.Document.Title);
    }

    [Fact]
    public void Tampered_scene_content_is_reported_as_a_corrupted_project_not_a_raw_zip_or_json_error()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("tampered" + MapProjectFormat.Extension);
        MapProjectStore.Save(
            ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        // Modify scene.json without updating the recorded checksum.
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(MapProjectFormat.SceneEntry)!;
            using var stream = entry.Open();
            stream.Seek(0, SeekOrigin.End);
            stream.Write(" "u8);
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains(MapProjectFormat.SceneEntry, corruption.Message);
    }

    [Theory]
    [InlineData(MapProjectFormat.ManifestEntry)]
    [InlineData(MapProjectFormat.SceneEntry)]
    [InlineData(MapProjectFormat.AssetManifestEntry)]
    [InlineData(MapProjectFormat.PreviewEntry)]
    public void A_missing_required_entry_is_reported_as_corruption_naming_the_entry(string missing)
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("missing" + MapProjectFormat.Extension);
        MapProjectStore.Save(
            ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry(missing)!.Delete();
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains(missing, corruption.Message);
    }

    [Fact]
    public void Something_that_is_not_a_zip_at_all_is_reported_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("not-a-zip" + MapProjectFormat.Extension);
        File.WriteAllText(path, "this is a text file wearing the wrong extension");

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void Unparseable_manifest_json_is_reported_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("bad-json" + MapProjectFormat.Extension);
        MapProjectStore.Save(
            ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
            using var stream = entry.Open();
            stream.SetLength(stream.Length / 2); // torn JSON
        }

        Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
    }

    [Fact]
    public void A_future_schema_version_is_refused_and_the_file_is_left_untouched()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("future" + MapProjectFormat.Extension);
        MapProjectStore.Save(
            ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        // Rewrite the manifest to a future schema, keeping its checksum contract out of the way:
        // schema is checked before checksums, per the open order in the store.
        var futureSchema = MapProjectFormat.MaxSupportedSchemaVersion + 1;
        RewriteManifestSchemaVersion(path, futureSchema);
        var bytesBefore = File.ReadAllBytes(path);

        var refusal = Assert.Throws<UnsupportedSchemaVersionException>(() => MapProjectStore.Open(path));
        Assert.Equal(futureSchema, refusal.SchemaVersion);
        Assert.Contains(futureSchema.ToString(), refusal.Message);

        // Open never migrates and never rewrites the source (docs/05).
        Assert.Equal(bytesBefore, File.ReadAllBytes(path));
    }

    private static void RewriteManifestSchemaVersion(string path, int version)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;

        string text;
        using (var reader = new StreamReader(entry.Open()))
        {
            text = reader.ReadToEnd();
        }

        text = JsonTestMutations.ReplaceFirst(
            text,
            $"\"schemaVersion\": {MapProjectFormat.SchemaVersion}",
            $"\"schemaVersion\": {version}");

        using var writer = new StreamWriter(entry.Open());
        writer.BaseStream.SetLength(0);
        writer.Write(text);
    }
}
