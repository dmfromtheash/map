using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.Tests;

/// <summary>Opt-in editor save worker, including the real CPU thumbnail and recovery journal.
/// Invokes the worker after admission: the Windows named heavy-operation gate is unavailable on macOS.</summary>
public sealed class SaveWorkflowCostProbeTests
{
    [Fact]
    public async Task Measure_save_and_autosave_with_owner_art()
    {
        var countText = Environment.GetEnvironmentVariable("MAP_SAVE_WORKFLOW_COUNT");
        if (countText is null) return;
        var count = int.Parse(countText);
        var root = Directory.CreateTempSubdirectory("map-save-workflow-").FullName;
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
            var art = File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
            var sha = Convert.ToHexStringLower(SHA256.HashData(art));
            var random = new Random(42);
            var style = new StampStyle(sha);
            var objects = new SceneObject[count];
            for (var i = 0; i < count; i++)
            {
                var bytes = new byte[16];
                random.NextBytes(bytes);
                objects[i] = new SceneObject(new ObjectId(new Guid(bytes)), SceneObjectKind.Stamp, "stamp",
                    i % 1000 * 128, i / 1000 % 1000 * 128, 4096, 3072, 0,
                    "#FFFFFFFF", "#00000000", 0, true, false)
                    { Stamp = style };
            }
            var document = new MapDocument(DocumentId.Parse("11111111-2222-3333-4444-555555555555"), "workflow probe",
                DateTimeOffset.UnixEpoch, new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"), "base", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "objects", true, false, 1, objects)],
                [new MapAssetReference(sha, "mountain", "image/png", "png", ProjectAssetStorage.Embedded,
                    DateTimeOffset.UnixEpoch, "owner", "owner", embeddedContent: art)]);
            var path = Path.Combine(root, "probe.mapproj");
            var journal = new RecoveryJournal(Path.Combine(root, "recovery"));
            var store = new ProjectStore(journal);
            var metadata = new Map.Persistence.SaveMetadata("probe", DateTimeOffset.UnixEpoch);
            await Measure("save-cold", () => Task.Run(() =>
            {
                using var images = new ExportStampImageSource(document, path, null);
                store.Save(document, path, metadata, ThumbnailRenderer.RenderPng(document, stamps: images));
                return true;
            }));
            var layer = document.Layers.OfType<ObjectLayer>().Single();
            var changed = document.WithLayer(layer.Id, _ => layer with
            {
                Objects = layer.ObjectStorage.ReplaceAt(count / 2, objects[count / 2] with { ObjectOpacity = 0.8 }),
            });
            await Measure("autosave-one-edit", () => Task.Run(() =>
            {
                using var images = new ExportStampImageSource(changed, path, null);
                store.SaveAutosave(changed, path, metadata, ThumbnailRenderer.RenderPng(changed, stamps: images));
                return true;
            }));
            var record = AutosaveSnapshots.TryRead(journal, document.Id.Value)!;
            var autosavePath = Path.Combine(journal.DirectoryFor(document.Id.Value), record.SnapshotFileName);
            foreach (var file in new[] { path, autosavePath })
            {
                using var zip = ZipFile.OpenRead(file);
                using var preview = zip.GetEntry("preview.png")!.Open();
                Console.WriteLine($"PREVIEW count={count} op={(file == path ? "save" : "autosave")} sha256={Convert.ToHexStringLower(SHA256.HashData(preview))}");
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task Measure(string operation, Func<Task<bool>> action)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long peakHeap = GC.GetTotalMemory(false), peakRss = 0;
            var sampling = true;
            using var process = Process.GetCurrentProcess();
            var sampler = new Thread(() =>
            {
                while (Volatile.Read(ref sampling))
                {
                    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                    process.Refresh();
                    peakRss = Math.Max(peakRss, process.WorkingSet64);
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            sampler.Start();
            var before = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            var result = false;
            double callerMilliseconds = 0;
            try
            {
                var pending = action();
                callerMilliseconds = timer.Elapsed.TotalMilliseconds;
                result = await pending;
            }
            finally
            {
                timer.Stop();
                var allocated = GC.GetTotalAllocatedBytes(true) - before;
                Volatile.Write(ref sampling, false);
                sampler.Join();
                Console.WriteLine($"WORKFLOW count={count} op={operation} ms={timer.Elapsed.TotalMilliseconds:F1} callerMs={callerMilliseconds:F1} "
                    + $"allocated={allocated} peakHeap={peakHeap} peakRss={peakRss} success={result}");
            }
            Assert.True(result);
        }
    }
}
