using System.IO.Compression;
using Map.Core.Documents;
using Map.Persistence.Recovery;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class IncrementalSaveTests
{
    [Fact]
    public void Repeated_save_and_one_edit_serialize_only_changed_storage_chunks_and_keep_exact_bytes()
    {
        using var workspace = new TempWorkspace();
        var template = ProjectFixtures.Document();
        var source = template.Layers.OfType<ObjectLayer>().Single();
        var objects = Enumerable.Range(0, 17000).Select(i => source.Objects[0] with
        {
            Id = ObjectId.New(), Name = "object " + i,
        }).ToArray();
        var layer = source with { Objects = objects };
        var document = template.WithLayer(source.Id, _ => layer);
        var path = workspace.File("cached.mapproj");
        Assert.Equal(objects.Length, Save(document, path).SerializedObjects);
        var original = File.ReadAllBytes(path);
        Assert.Equal(0, Save(document, path).SerializedObjects);
        Assert.Equal(original, File.ReadAllBytes(path));

        var edited = layer.ObjectStorage.ReplaceAt(300, objects[300] with { Name = "edited" });
        var changed = document.WithLayer(layer.Id, _ => layer with { Objects = edited });
        Assert.Equal(SceneObjectList.ChunkSize, Save(changed, path).SerializedObjects);
        CompareWithCold(changed);
        Assert.Equal(0, Save(document, path).SerializedObjects); // undo reuses the old revision
        Assert.Equal(original, File.ReadAllBytes(path));

        var inserted = edited.InsertRange(125, [objects[0] with { Id = ObjectId.New(), Name = "inserted" }]);
        var shifted = changed.WithLayer(layer.Id, _ => layer with { Objects = inserted });
        Save(shifted, path);
        CompareWithCold(shifted);
        var removed = inserted.RemoveRange(9000, 17);
        var shorter = shifted.WithLayer(layer.Id, _ => layer with { Objects = removed });
        Save(shorter, path);
        CompareWithCold(shorter);

        void CompareWithCold(MapDocument expected)
        {
            var before = File.ReadAllBytes(path);
            var cold = expected.WithLayer(layer.Id, old => ((ObjectLayer)old) with
            {
                Objects = ((ObjectLayer)old).Objects.ToArray(),
            });
            var coldPath = workspace.File("cold.mapproj");
            Save(cold, coldPath);
            using (var warmZip = ZipFile.OpenRead(path))
            using (var coldZip = ZipFile.OpenRead(coldPath))
                foreach (var warmEntry in warmZip.Entries)
                {
                    using var warmBytes = new MemoryStream();
                    using var coldBytes = new MemoryStream();
                    using (var input = warmEntry.Open()) input.CopyTo(warmBytes);
                    using (var input = coldZip.GetEntry(warmEntry.FullName)!.Open()) input.CopyTo(coldBytes);
                    Assert.True(warmBytes.ToArray().AsSpan().SequenceEqual(coldBytes.ToArray()), warmEntry.FullName);
                }
            Assert.Equal(before, File.ReadAllBytes(coldPath));
            var reloaded = MapProjectStore.Open(path).Document;
            ProjectFixtures.AssertSemanticallyEqual(expected, reloaded);
            Save(reloaded, coldPath);
            Assert.Equal(before, File.ReadAllBytes(coldPath));
        }
    }

    [Fact]
    public void Byte_limited_chunks_and_large_objects_keep_exact_bytes_when_storage_boundaries_differ()
    {
        const int nameLength = 100000;
        using var workspace = new TempWorkspace();
        var random = new Random(17);
        var name = new string(Enumerable.Range(0, nameLength).Select(_ => (char)random.Next('a', 'z' + 1)).ToArray()) + "Ж\"\\";
        var template = ProjectFixtures.Document();
        var layer = template.Layers.OfType<ObjectLayer>().Single();
        var objects = Enumerable.Range(0, 100).Select(_ => layer.Objects[0] with
        {
            Id = ObjectId.New(), Name = name,
        }).ToArray();
        var storage = SceneObjectList.From(objects).InsertRange(37, [objects[0] with { Id = ObjectId.New() }]);
        var document = template.WithLayer(layer.Id, _ => layer with { Objects = storage });
        var path = workspace.File("large.mapproj");
        Save(document, path);
        var original = File.ReadAllBytes(path);
        Assert.Equal(0, Save(document, path).SerializedObjects);
        Assert.Equal(original, File.ReadAllBytes(path));
        using (var archive = ZipFile.OpenRead(path))
        {
            var entries = archive.Entries.Where(e => e.FullName.StartsWith(MapProjectFormat.ObjectPrefix, StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.All(entries, e => Assert.InRange(e.Length, 2, MapProjectFormat.ObjectChunkTargetBytes));
        }
        var cold = document.WithLayer(layer.Id, _ => layer with { Objects = storage.ToArray() });
        Save(cold, path);
        Assert.Equal(original, File.ReadAllBytes(path));
        var reopened = MapProjectStore.Open(path).Document;
        ProjectFixtures.AssertSemanticallyEqual(document, reopened);
        Save(reopened, path);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("short")]
    [InlineData("missing")]
    [InlineData("extra")]
    public void Damaged_durable_temp_never_replaces_the_previous_file(string damage)
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document();
        var path = workspace.File("atomic.mapproj");
        Save(document, path);
        var original = File.ReadAllBytes(path);
        var fault = new SaveFaultInjection { AfterFlushBeforeReadBack = temp =>
        {
            using var archive = ZipFile.Open(temp, ZipArchiveMode.Update);
            if (damage == "extra")
            {
                using var extra = archive.CreateEntry("extra.json").Open();
                extra.Write("[]"u8);
                return;
            }
            var entry = archive.Entries.First(e => e.FullName.StartsWith(MapProjectFormat.ObjectPrefix, StringComparison.Ordinal));
            using var buffer = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(buffer);
            var name = entry.FullName;
            entry.Delete();
            if (damage == "missing") return;
            var bytes = buffer.ToArray();
            if (damage == "changed") bytes[bytes.Length / 2] ^= 1;
            using var output = archive.CreateEntry(name).Open();
            output.Write(bytes.AsSpan(0, bytes.Length - (damage == "short" ? 1 : 0)));
        }};
        Assert.Throws<MapProjectSaveException>(() => MapProjectStore.Save(document, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), faults: fault));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_refuses_object_strings_that_the_reader_cannot_admit()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document();
        var path = workspace.File("limited.mapproj");
        Save(document, path);
        var before = File.ReadAllBytes(path);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var bad = document.WithLayer(layer.Id, _ => layer with
        {
            Objects = layer.ObjectStorage.ReplaceAt(0, layer.Objects[0] with
            {
                Name = new string('x', MapProjectJsonBudget.MaxStringUtf8Bytes + 1),
            }),
        });
        Assert.Throws<MapProjectResourceLimitException>(() => Save(bad, path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_changed_previous_autosave_is_revalidated_and_corruption_falls_back_to_older_generation()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(workspace.File("recovery"));
        var document = ProjectFixtures.Document();
        var first = AutosaveSnapshots.Save(journal, document, null, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var second = AutosaveSnapshots.Save(journal, document, null, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var directory = journal.DirectoryFor(document.Id.Value);
        var damaged = Path.Combine(directory, second.SnapshotFileName);
        var bytes = File.ReadAllBytes(damaged);
        bytes[0] ^= 1;
        File.WriteAllBytes(damaged, bytes);
        var third = AutosaveSnapshots.Save(journal, document, null, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(first.SnapshotFileName, third.PreviousSnapshotFileName);
        Assert.False(File.Exists(damaged));
    }

    private static SaveTimings Save(MapDocument document, string path)
    {
        var timings = new SaveTimings();
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), timings: timings);
        return timings;
    }
}
