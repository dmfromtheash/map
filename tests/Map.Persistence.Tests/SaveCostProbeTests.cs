using System.Diagnostics;
using Map.Core.Documents;
using Map.Persistence.Recovery;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>Opt-in headless persistence probe; preview rendering is measured separately.</summary>
public sealed class SaveCostProbeTests
{
    [Fact]
    public void Cross_build_round_trip()
    {
        var input = Environment.GetEnvironmentVariable("MAP_SAVE_COMPAT_INPUT");
        if (input is null) return;
        var output = Environment.GetEnvironmentVariable("MAP_SAVE_COMPAT_OUTPUT")!;
        var opened = MapProjectStore.Open(input);
        MapProjectStore.Save(opened.Document, output,
            new SaveMetadata(opened.AppVersion, opened.ModifiedUtc), opened.PreviewPng);
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
        Console.WriteLine($"COMPAT {Path.GetFileName(input)} -> {Path.GetFileName(output)}: byte-identical");
    }

    [Fact]
    public void Save_and_autosave_cost()
    {
        var countText = Environment.GetEnvironmentVariable("MAP_SAVE_PROBE_COUNT");
        if (countText is null) return;
        var count = int.Parse(countText);
        var rich = Environment.GetEnvironmentVariable("MAP_SAVE_PROBE_RICH") == "1";
        using var workspace = new TempWorkspace();
        var objects = new SceneObject[count];
        var random = new Random(42);
        var style = new StampStyle(new string('a', 64));
        if (rich) style = style with { ReferenceWidth = 128, ReferenceHeight = 96 };
        for (var i = 0; i < count; i++)
        {
            var idBytes = new byte[16];
            random.NextBytes(idBytes);
            var width = rich ? 64 * (0.5 + random.NextDouble() * 1.5) : 32;
            objects[i] = new SceneObject(new ObjectId(new Guid(idBytes)), SceneObjectKind.Stamp,
                rich ? "mountain " + i : "stamp", rich ? random.NextDouble() * 131000 : i % 1000 * 32,
                rich ? random.NextDouble() * 131000 : i / 1000 * 32, width, rich ? width * 0.8 : 24,
                rich ? random.Next(-180, 181) : 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style };
        }
        var layer = new ObjectLayer(ProjectFixtures.FixedLayerId, "objects", true, false, 1, objects);
        var background = new BaseColorLayer(ProjectFixtures.FixedBaseLayerId, "base", true, false, 1, "#FFFFFFFF");
        var document = new MapDocument(ProjectFixtures.FixedDocumentId, "save probe",
            ProjectFixtures.FixedCreatedUtc, new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
            [background, layer]);
        var path = workspace.File("probe.mapproj");
        var journal = new RecoveryJournal(workspace.File("recovery"));
        if (Environment.GetEnvironmentVariable("MAP_SAVE_PROBE_OP") == "autosave-only")
        {
            Measure("autosave-cold", () => AutosaveSnapshots.Save(journal, document, path,
                ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng()));
            return;
        }
        Measure("save-cold", () => MapProjectStore.Save(document, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal));
        if (!File.Exists(path))
        {
            Measure("autosave-limit", () => AutosaveSnapshots.Save(journal, document, path,
                ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng()));
            return;
        }
        Measure("save-unchanged", () => MapProjectStore.Save(document, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal));
        var changed = document.WithLayers([background, layer with { Objects = layer.ObjectStorage.ReplaceAt(count / 2,
            new SceneObject(layer.Objects[count / 2].Id, SceneObjectKind.Stamp, "edited",
                13, 17, 32, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style }) }]);
        Measure("autosave-one-edit", () => AutosaveSnapshots.Save(journal, changed, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng()));
        Measure("autosave-next", () => AutosaveSnapshots.Save(journal, changed, path,
            ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng()));
        var export = Environment.GetEnvironmentVariable("MAP_SAVE_PROBE_EXPORT");
        if (export is not null) File.Copy(path, export, true);
        GC.KeepAlive(document);

        void Measure(string operation, Action action)
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
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            var result = "OK";
            try { action(); }
            catch (MapProjectResourceLimitException ex) { result = "LIMIT " + ex.Message; }
            finally
            {
                timer.Stop();
                allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                Volatile.Write(ref sampling, false);
                sampler.Join();
                Console.WriteLine($"PROBE count={count} op={operation} ms={timer.Elapsed.TotalMilliseconds:F1} "
                    + $"allocated={allocated} peakHeap={peakHeap} peakRss={peakRss} result={result}");
            }
        }
    }
}
