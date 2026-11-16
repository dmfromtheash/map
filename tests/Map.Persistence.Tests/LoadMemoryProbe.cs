using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>
/// Throw-away measurement (doc-mem worktree only). MAP_DOC_MEM_PHASE=save writes MAP_DOC_MEM_FILE with
/// MAP_DOC_MEM_COUNT stamps; MAP_DOC_MEM_PHASE=open opens it in a fresh process and reports time,
/// allocations, sampled peak GC heap, process peak working set, retained bytes and an allocation-type
/// histogram (GC AllocationTick, ~100 KB sampling) to MAP_DOC_MEM_PROBE.
/// </summary>
public sealed class LoadMemoryProbe
{
    private static long Retained()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(true);
    }

    private sealed class AllocationTypes : EventListener
    {
        public readonly Dictionary<string, long> Bytes = new(StringComparer.Ordinal);
        public volatile bool Recording;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!Recording || e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)) return;
            var type = e.Payload?[e.PayloadNames!.IndexOf("TypeName")] as string ?? "?";
            var amount = e.PayloadNames!.Contains("AllocationAmount64")
                ? Convert.ToInt64(e.Payload![e.PayloadNames.IndexOf("AllocationAmount64")])
                : Convert.ToInt64(e.Payload![e.PayloadNames.IndexOf("AllocationAmount")]);
            lock (Bytes) Bytes[type] = Bytes.GetValueOrDefault(type) + amount;
        }
    }

    [OptInFact("separate-process save/open memory report (phase save/open, input map and report path)",
        "MAP_DOC_MEM_PROBE", "MAP_DOC_MEM_PHASE", "MAP_DOC_MEM_FILE")]
    public void Measure()
    {
        var output = Environment.GetEnvironmentVariable("MAP_DOC_MEM_PROBE");
        var phase = Environment.GetEnvironmentVariable("MAP_DOC_MEM_PHASE");
        var path = Environment.GetEnvironmentVariable("MAP_DOC_MEM_FILE");
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (phase is not ("save" or "open")) throw new ArgumentException("MAP_DOC_MEM_PHASE must be save or open.");
        var n = int.Parse(Environment.GetEnvironmentVariable("MAP_DOC_MEM_COUNT") ?? "200000");
        var tag = Environment.GetEnvironmentVariable("MAP_DOC_MEM_TAG") ?? "";
        if (phase == "save") { Save(path, n); return; }

        using var listener = Environment.GetEnvironmentVariable("MAP_DOC_MEM_TYPES") == "1" ? new AllocationTypes() : null;
        var t0 = Retained();
        long peakHeap = 0;
        var sampling = true;
        var sampler = new Thread(() =>
        {
            while (Volatile.Read(ref sampling))
            {
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                Thread.Sleep(1);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Highest };
        sampler.Start();
        var before = GC.GetTotalAllocatedBytes(true);
        if (listener is not null) listener.Recording = true;
        var watch = Stopwatch.StartNew();
        var share = Environment.GetEnvironmentVariable("MAP_DOC_MEM_SHARE") ?? "all";
        SharedValues.BypassOnThisThread = share == "none";
        SharedValues.BypassStylesOnThisThread = share == "strings";
        var document = MapProjectStore.Open(path).Document;
        SharedValues.BypassOnThisThread = false;
        SharedValues.BypassStylesOnThisThread = false;
        watch.Stop();
        if (listener is not null) listener.Recording = false;
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Volatile.Write(ref sampling, false);
        sampler.Join();
        var retained = Retained() - t0;
        var loaded = ((ObjectLayer)document.Layers.Single(l => l is ObjectLayer)).Objects;
        int Distinct(Func<SceneObject, object?> pick) =>
            loaded.Select(pick).Where(s => s is not null).Distinct(ReferenceEqualityComparer.Instance).Count();
        using var process = Process.GetCurrentProcess();
        var report = new StringBuilder();
        report.AppendLine($"{tag} N={n:N0} Open {watch.Elapsed.TotalSeconds:F2} s | retained {retained / (double)n:F1} B/obj ({retained / 1048576.0:F1} MB)"
            + $" | allocated {allocated / (double)n:F0} B/obj ({allocated / 1048576.0:F0} MB)"
            + $" | peak GC heap above start {(peakHeap - t0) / 1048576.0:F0} MB | process peak WS {process.PeakWorkingSet64 / 1048576.0:F0} MB"
            + $" | distinct fill {Distinct(o => o.FillColor)}, sha {Distinct(o => o.Stamp!.AssetSha256)}, blend {Distinct(o => o.Stamp!.Blend)},"
            + $" StampStyle {Distinct(o => o.Stamp)}");
        if (listener is not null) lock (listener.Bytes)
            foreach (var (type, bytes) in listener.Bytes.OrderByDescending(p => p.Value).Take(14))
                report.AppendLine($"      {bytes / (double)n,7:F0} B/obj  {type}");
        File.AppendAllText(output, report.ToString());
        GC.KeepAlive(document);
    }

    private static void Save(string path, int n)
    {
        var hashes = Enumerable.Range(0, 16).Select(i => new string("0123456789abcdef"[i], 64)).ToArray();
        var assets = hashes.Select(h => new MapAssetReference(h, "Р“РѕСЂР°", "image/webp", "webp", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "user-imported", "unknown", catalogId: h, relativePath: $"assets/{h[..8]}.webp",
            fallbackThumbnailPng: ProjectFixtures.PreviewPng())).ToArray();
        var random = new Random(42);
        var objects = new SceneObject[n];
        for (var i = 0; i < n; i++)
        {
            var width = 64 * (0.5 + random.NextDouble() * 1.5);
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Р“РѕСЂР° " + (i + 1),
                random.NextDouble() * 131000, random.NextDouble() * 131000, width, width * 0.8,
                random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(hashes[i % 16]) { ReferenceWidth = 128, ReferenceHeight = 96 } };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MapProjectStore.Save(new MapDocument(DocumentId.New(), "probe", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Р¤РѕРЅ", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "РћР±СЉРµРєС‚С‹", true, false, 1, objects)], assets),
            path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
    }
}
