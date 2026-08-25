using System.IO.Compression;
using Map.Persistence.Recovery;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public class RecoveryJournalTests
{
    [Fact]
    public void A_successful_save_walks_the_journal_and_clears_it_leaving_a_bak_of_the_old_version()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(Path.Combine(workspace.Root, "journal"));
        var path = workspace.File("doc" + MapProjectFormat.Extension);

        var first = ProjectFixtures.Document("first");
        MapProjectStore.Save(first, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal);
        var firstBytes = File.ReadAllBytes(path);

        Assert.Null(journal.TryRead(ProjectFixtures.FixedDocumentId.Value)); // cleared after success
        Assert.False(File.Exists(path + ".bak")); // first save had nothing to back up

        var second = ProjectFixtures.Document("second");
        MapProjectStore.Save(second, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal);

        // Bounded backup: exactly the previous correct version survives as .bak.
        Assert.True(File.Exists(path + ".bak"));
        Assert.Equal(firstBytes, File.ReadAllBytes(path + ".bak"));
        Assert.Equal("second", MapProjectStore.Open(path).Document.Title);
        Assert.Equal("first", MapProjectStore.Open(path + ".bak").Document.Title);

        Assert.Null(journal.TryRead(ProjectFixtures.FixedDocumentId.Value));
        Assert.Empty(RecoveryCandidates.Find(journal));
    }

    [Fact]
    public void A_fault_after_the_durable_temp_leaves_a_findable_valid_candidate_and_an_intact_target()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(Path.Combine(workspace.Root, "journal"));
        var path = workspace.File("doc" + MapProjectFormat.Extension);

        var first = ProjectFixtures.Document("first");
        MapProjectStore.Save(first, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal);
        var firstBytes = File.ReadAllBytes(path);

        var second = ProjectFixtures.Document("second");
        var faults = new SaveFaultInjection
        {
            AfterDurableTempBeforeReplace = () => throw new IOException("Injected before replace."),
        };

        Assert.Throws<MapProjectSaveException>(() =>
            MapProjectStore.Save(
                second, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal, faults));

        // Target is untouched.
        Assert.Equal(firstBytes, File.ReadAllBytes(path));

        // The journal records TempDurable and the candidate validates as a full container.
        var record = journal.TryRead(ProjectFixtures.FixedDocumentId.Value);
        Assert.NotNull(record);
        Assert.Equal(RecoveryState.TempDurable, record!.StateValue);
        Assert.True(record.HasRecoverableWork);

        var candidate = Assert.Single(RecoveryCandidates.Find(journal));
        Assert.Equal("second", candidate.Title);
        Assert.Equal(Path.GetFullPath(path), candidate.TargetPath);

        // Restore reads the new version without touching the old file on disk.
        var restored = RecoveryCandidates.Restore(candidate);
        Assert.Equal("second", restored.Document.Title);
        Assert.Equal(firstBytes, File.ReadAllBytes(path));

        // Discard removes exactly the recorded temp and the journal - and the target stays.
        RecoveryCandidates.Discard(journal, candidate);
        Assert.False(File.Exists(candidate.CandidatePath));
        Assert.Null(journal.TryRead(ProjectFixtures.FixedDocumentId.Value));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("\"FutureVersionDurable\"")]
    [InlineData("\"999\"")]
    [InlineData("\"tempdurable\"")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void Unknown_journal_states_never_throw_and_are_never_offered(string stateJsonFragment)
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(Path.Combine(workspace.Root, "journal"));
        var documentId = Guid.NewGuid();

        var tempPath = workspace.File("candidate.tmp");
        File.WriteAllText(tempPath, "durable bytes");

        Directory.CreateDirectory(journal.DirectoryFor(documentId));
        File.WriteAllText(
            journal.PathFor(documentId),
            "{\"documentId\":\"" + documentId.ToString("D") + "\"," +
            "\"tempPath\":" + System.Text.Json.JsonSerializer.Serialize(tempPath) + "," +
            "\"state\":" + stateJsonFragment + "}");

        var record = journal.TryRead(documentId);
        Assert.NotNull(record);
        Assert.False(record!.HasRecoverableWork);
        Assert.Null(record.StateValue);
        Assert.Empty(RecoveryCandidates.Find(journal));
        Assert.True(File.Exists(tempPath)); // raw state kept, nothing deleted on a guess
    }

    [Fact]
    public void A_TempDurable_journal_whose_candidate_does_not_validate_is_not_offered()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(Path.Combine(workspace.Root, "journal"));
        var documentId = Guid.NewGuid();

        var tempPath = workspace.File("broken.tmp");
        File.WriteAllText(tempPath, "not a zip at all");

        Directory.CreateDirectory(journal.DirectoryFor(documentId));
        File.WriteAllText(
            journal.PathFor(documentId),
            "{\"documentId\":\"" + documentId.ToString("D") + "\"," +
            "\"tempPath\":" + System.Text.Json.JsonSerializer.Serialize(tempPath) + "," +
            "\"state\":\"TempDurable\"}");

        Assert.True(journal.TryRead(documentId)!.HasRecoverableWork); // journal says durable...
        Assert.Empty(RecoveryCandidates.Find(journal));               // ...but validation gates the offer
    }
}

public class ContainerHardeningTests
{
    private static string SavedProject(TempWorkspace workspace, string name = "doc")
    {
        var path = workspace.File(name + MapProjectFormat.Extension);
        MapProjectStore.Save(
            ProjectFixtures.Document(), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        return path;
    }

    [Fact]
    public void A_duplicate_required_entry_is_reported_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var duplicate = archive.CreateEntry(MapProjectFormat.SceneEntry);
            using var stream = duplicate.Open();
            stream.Write("{}"u8);
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("more than once", corruption.Message);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/absolute.json")]
    [InlineData("nested/../escape.json")]
    [InlineData(@"back\slash.json")]
    [InlineData("C:/drive.json")]
    public void Non_canonical_entry_paths_are_reported_as_corruption(string entryName)
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write("x"u8);
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("not canonical", corruption.Message);
    }

    [Fact]
    public void Too_many_entries_are_rejected_before_reading_content()
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            for (var i = 0; i <= MapProjectFormat.MaxEntries; i++)
            {
                var entry = archive.CreateEntry($"filler-{i:D3}.bin");
                using var stream = entry.Open();
                stream.Write("x"u8);
            }
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("entries", corruption.Message);
    }

    [Theory]
    [InlineData("{\"documentId\":null,\"layers\":[]}", "document id")]
    [InlineData("{\"documentId\":\"11111111-2222-3333-4444-555555555555\",\"layers\":null}", "null layers")]
    [InlineData("{\"documentId\":\"11111111-2222-3333-4444-555555555555\",\"layers\":[null]}", "null layers")]
    public void Null_required_json_shapes_are_reported_as_corruption_not_an_NRE(
        string sceneJson, string expectedFragment)
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);
        ReplaceSceneJson(path, sceneJson);

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains(expectedFragment, corruption.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Hexagon")]
    [InlineData("999")]
    [InlineData("rectangle")]
    public void An_unknown_object_kind_is_reported_as_corruption_naming_the_kind(string kind)
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);

        var sceneJson =
            "{\"documentId\":\"11111111-2222-3333-4444-555555555555\",\"layers\":[" +
            "{\"id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"name\":\"l\",\"visible\":true," +
            "\"locked\":false,\"opacity\":1,\"objects\":[" +
            "{\"id\":\"01234567-89ab-cdef-0123-456789abcdef\",\"kind\":\"" + kind + "\"," +
            "\"name\":\"o\",\"x\":0,\"y\":0,\"width\":10,\"height\":10,\"rotationDegrees\":0," +
            "\"fillColor\":\"#FF000000\",\"strokeColor\":\"#FF000000\",\"strokeWidth\":0," +
            "\"visible\":true,\"locked\":false}]}]}";

        ReplaceSceneJson(path, sceneJson);

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains(kind, corruption.Message);
    }

    [Fact]
    public void A_preview_that_is_not_a_png_is_reported_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(MapProjectFormat.PreviewEntry)!;
            using var stream = entry.Open();
            stream.SetLength(0);
            stream.Write("JFIF pretending"u8);
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("not a PNG", corruption.Message);
    }

    [Fact]
    public void A_preview_with_absurd_dimensions_is_reported_as_corruption()
    {
        using var workspace = new TempWorkspace();
        var path = SavedProject(workspace);

        var huge = ProjectFixtures.PreviewPng();
        // Overwrite IHDR width (bytes 16..19, big-endian) with 100 000.
        huge[16] = 0x00;
        huge[17] = 0x01;
        huge[18] = 0x86;
        huge[19] = 0xA0;

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(MapProjectFormat.PreviewEntry)!;
            using var stream = entry.Open();
            stream.SetLength(0);
            stream.Write(huge);
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("outside the supported preview size", corruption.Message);
    }

    /// <summary>Swaps scene.json content and fixes the recorded checksum, isolating the shape check.</summary>
    private static void ReplaceSceneJson(string path, string sceneJson)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);

        var sceneBytes = System.Text.Encoding.UTF8.GetBytes(sceneJson);
        var sceneEntry = archive.GetEntry(MapProjectFormat.SceneEntry)!;
        using (var stream = sceneEntry.Open())
        {
            stream.SetLength(0);
            stream.Write(sceneBytes);
        }

        var manifestEntry = archive.GetEntry(MapProjectFormat.ManifestEntry)!;
        string manifestText;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestText = reader.ReadToEnd();
        }

        var newHash = MapProjectRoundTripTests.Sha256Hex(sceneBytes);
        manifestText = System.Text.RegularExpressions.Regex.Replace(
            manifestText,
            "\"sceneSha256\": \"[0-9a-f]+\"",
            $"\"sceneSha256\": \"{newHash}\"");

        using var writer = new StreamWriter(manifestEntry.Open());
        writer.BaseStream.SetLength(0);
        writer.Write(manifestText);
    }
}
