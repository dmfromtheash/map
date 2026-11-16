using System.Diagnostics;
using System.Text;
using Map.Core.Documents;

namespace Map.Core.Tests;

/// <summary>
/// Throw-away measurement (doc-mem worktree only): retained bytes per object of the document model
/// and of each derived per-object index. Runs only with MAP_DOC_MEM_PROBE=&lt;output file&gt;.
/// </summary>
public sealed class DocMemoryProbe
{
    private static readonly string[] Hashes = Enumerable.Range(0, 16)
        .Select(i => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([(byte)i])).ToLowerInvariant())
        .ToArray();

    private static long Retained()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return GC.GetTotalMemory(true);
    }

    /// <param name="loadLike">true: every object gets its own copy of every string, as JSON load does.</param>
    internal static SceneObject[] Build(int count, bool loadLike, bool uniqueNames = true, int seed = 42)
    {
        var random = new Random(seed);
        const double w = 131072;
        var objects = new SceneObject[count];
        for (var i = 0; i < count; i++)
        {
            var width = 64 * (0.5 + random.NextDouble() * 1.5);
            var name = uniqueNames ? "Ассет " + i : "Гора";
            var sha = Hashes[i % Hashes.Length];
            var fill = "#FFFFFFFF";
            var stroke = "#00000000";
            string blend = "Normal", shadow = AppearanceColor.OpaqueBlack;
            if (loadLike)
            {
                name = new string(name.AsSpan()); sha = new string(sha.AsSpan());
                fill = new string(fill.AsSpan()); stroke = new string(stroke.AsSpan());
                blend = new string(blend.AsSpan()); shadow = new string(shadow.AsSpan());
            }
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, name,
                random.NextDouble() * (w - width), random.NextDouble() * (w - width),
                width, width * 0.8, random.Next(-180, 181), fill, stroke, 0, true, false)
            { Stamp = new StampStyle(sha) { Blend = blend, ShadowColor = shadow } };
        }
        return objects;
    }

    internal static MapDocument Document(IReadOnlyList<SceneObject> objects) =>
        new(DocumentId.New(), "probe", DateTimeOffset.UnixEpoch, new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)]);

    [OptInFact("100k/1M document/index memory report (no CI memory budget)", "MAP_DOC_MEM_PROBE")]
    public void Measure()
    {
        var output = Environment.GetEnvironmentVariable("MAP_DOC_MEM_PROBE");
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        var report = new StringBuilder();
        foreach (var n in new[] { 100_000, 1_000_000 }) MeasureOne(n, report);
        File.AppendAllText(output, report.ToString());
    }

    private static void MeasureOne(int n, StringBuilder report)
    {
        void Line(string what, long bytes) =>
            report.AppendLine($"{n,9} | {what,-58} | {bytes / (double)n,8:F1} B/obj | {bytes / 1048576.0,8:F1} MB");
        report.AppendLine($"--- N = {n:N0} ---");

        // Pieces alone (array slot of 8 bytes removed).
        var t0 = Retained();
        var names = new string[n];
        for (var i = 0; i < n; i++) names[i] = "Ассет " + i;
        Line("name string 'Ассет i' (unique)", Retained() - t0 - 8L * n);
        GC.KeepAlive(names); names = null!;

        t0 = Retained();
        var bare = new SceneObject[n];
        for (var i = 0; i < n; i++)
            bare[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", i, i, 10, 10, 0, "#FFFFFFFF", "#00000000", 0, true, false);
        Line("SceneObject record alone (shared strings)", Retained() - t0 - 8L * n);
        GC.KeepAlive(bare); bare = null!;

        t0 = Retained();
        var stamps = new StampStyle[n];
        for (var i = 0; i < n; i++) stamps[i] = new StampStyle(Hashes[i % 16]);
        Line("StampStyle record alone (shared hash)", Retained() - t0 - 8L * n);
        GC.KeepAlive(stamps); stamps = null!;

        t0 = Retained();
        var dupes = new string[n * 6];
        for (var i = 0; i < n; i++)
        {
            dupes[i * 6] = new string(Hashes[i % 16].AsSpan()); dupes[i * 6 + 1] = new string("#FFFFFFFF".AsSpan());
            dupes[i * 6 + 2] = new string("#00000000".AsSpan()); dupes[i * 6 + 3] = new string("Normal".AsSpan());
            dupes[i * 6 + 4] = new string(AppearanceColor.OpaqueBlack.AsSpan()); dupes[i * 6 + 5] = new string("Гора".AsSpan());
        }
        Line("load-like duplicate strings: sha+fill+stroke+blend+shadow+'Гора'", Retained() - t0 - 48L * n);
        GC.KeepAlive(dupes); dupes = null!;

        // Fixture-like chain: everything kept alive, deltas are the next structure only.
        var modes = (Environment.GetEnvironmentVariable("MAP_DOC_MEM_MODES") ?? "fixture-none,load-none,load-strings,load-all").Split(',');
        foreach (var mode in modes)
        {
            var loadLike = mode.StartsWith("load", StringComparison.Ordinal);
            var tag = $"[{mode}]";
            t0 = Retained();
            SharedValues.BypassOnThisThread = mode.EndsWith("-none", StringComparison.Ordinal);
            SharedValues.BypassStylesOnThisThread = mode.EndsWith("-strings", StringComparison.Ordinal);
            var objects = Build(n, loadLike);
            SharedValues.BypassOnThisThread = false;
            SharedValues.BypassStylesOnThisThread = false;
            var tObjects = Retained();
            Line($"{tag} SceneObject[] + StampStyle + strings", tObjects - t0);
            var list = SceneObjectList.From(objects);
            var tList = Retained();
            GC.KeepAlive(objects);
            Line($"{tag} SceneObjectList chunks (+ array kept)", tList - tObjects);
            objects = null!;
            var tDropArray = Retained();
            Line($"{tag}   after dropping caller array (list only)", tDropArray - tObjects);
            var allocBefore = GC.GetTotalAllocatedBytes(true);
            var peakWatch = Stopwatch.StartNew();
            var document = Document(list);
            var tDoc = Retained();
            Line($"{tag} MapDocument ctor retained (layer, AssetUsageIndex)", tDoc - tDropArray);
            Line($"{tag}   MapDocument ctor allocated (transient sets)", GC.GetTotalAllocatedBytes(true) - allocBefore);
            var layer = (ObjectLayer)document.Layers[1];
            var storage = (SceneObjectList)layer.Objects;
            _ = storage.IndexOfId(storage[0].Id);
            var tIdentity = Retained();
            Line($"{tag} SceneObjectList identity slots (ImmutableDictionary)", tIdentity - tDoc);
            _ = DocumentRevisionIndex.For(document);
            var tRevision = Retained();
            Line($"{tag} DocumentRevisionIndex (positions + locations)", tRevision - tIdentity);
            allocBefore = GC.GetTotalAllocatedBytes(true);
            var spatial = ObjectSpatialIndex.For(layer);
            var tSpatial = Retained();
            Line($"{tag} ObjectSpatialIndex (cells={spatial.CellCount})", tSpatial - tRevision);
            Line($"{tag}   ObjectSpatialIndex allocated while building", GC.GetTotalAllocatedBytes(true) - allocBefore);
            _ = ObjectNameIndex.For(document);
            var tNames = Retained();
            Line($"{tag} ObjectNameIndex (unique names)", tNames - tSpatial);
            Line($"{tag} TOTAL Core retained", tNames - t0);
            report.AppendLine($"{tag} process private bytes {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F0} MB, GC heap {GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0:F0} MB");

            // One point edit: what an undo snapshot costs with structural sharing.
            var tEdit0 = Retained();
            var edited = storage.ReplaceAt(n / 2, storage[n / 2] with { Name = "edited" });
            _ = edited.IndexOfId(edited[0].Id);
            Line($"{tag} one ReplaceAt snapshot (list + identity path copy)", Retained() - tEdit0);
            GC.KeepAlive(edited);
            GC.KeepAlive(document);
            GC.KeepAlive(spatial);
            list = null!; document = null!; layer = null!; storage = null!; spatial = null!; edited = null!;
        }
        Retained();
    }
}
