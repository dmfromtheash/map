using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;

// No renderer, image assets or timing assertions. Run the same harness against both Core
// revisions. All cases include a million real stamp records in the SAME layer as the text.
var repetitions = args.Length == 0 ? 15 : int.Parse(args[0]);
Console.WriteLine($"LabelPriority; repetitions={repetitions}; stamps=1000000; view queries=100; runtime={Environment.Version}; arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("labels,phase,median_ms,min_ms,max_ms,median_alloc_bytes,checksum");
foreach (var count in new[] { 1000, 10000, 50000 }) Run(count);

void Run(int labelCount)
{
    const int stamps = 1_000_000;
    var style = new StampStyle(new string('a', 64));
    var items = new SceneObject[stamps + labelCount];
    for (var i = 0; i < stamps; i++)
        items[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "stamp", i % 1000 * 16, i / 1000 * 16,
            8, 8, 0, "#FF000000", "#FF000000", 0, true, false) { Stamp = style };
    for (var i = 0; i < labelCount; i++)
        items[stamps + i] = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label", i % 250 * 512, i / 250 * 256,
            100, 20, 0, "#FF000000", "#FF000000", 0, true, false)
        { Text = new TextStyle { Content = "label", Importance = 1 + i % 5, FontSize = 20 } };
    var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, items);
    var document = new MapDocument(DocumentId.New(), "labels", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(200000, 200000, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"), layer])
    { Labels = new LabelSettings { ByScale = true, ResolveOverlaps = true } };
    var ids = items.Skip(stamps).Take(100).Select(o => o.Id).ToArray();
    var queryLabels = items.Skip(stamps).Take(100).ToArray();
    int Query(MapDocument snapshot, double scale)
    {
#if LABEL_PRIORITY_PASS2
        var hidden = LabelPriority.ForFrame(snapshot, scale, () => { });
        var result = 0;
        foreach (var label in queryLabels) if (hidden.Contains(label)) result++;
#else
        var hidden = LabelPriority.Hidden(snapshot, scale);
        var result = 0;
        foreach (var id in ids) if (hidden.Contains(id)) result++;
#endif
        return result;
    }
    MapDocument Edit(int position)
    {
        var source = layer;
        var item = source.Objects[position];
        var replacement = SceneObjectClone.At(item, item.X + 1, item.Y);
        layer = source with { Objects = ((SceneObjectList)source.Objects).ReplaceAt(position, replacement) };
        ObjectLayerChanges.RegisterDerived(source, layer, [position], [position]);
        document = document with { Layers = [document.Layers[0], layer] };
        return document;
    }

#if LABEL_PRIORITY_PASS2
    // Same cold index on a worker, then the original first-frame workload on the caller.
    Task.Run(() => Measure(labelCount, "background_label_build", 1, () =>
    {
        LabelPriority.Prepare(document);
        return 0;
    })).GetAwaiter().GetResult();
#endif
    Measure(labelCount, "cold_view", 1, () => Query(document, 1));
#if LABEL_PRIORITY_PASS2
    // A separate, genuinely cold list: the frame races background preparation on open.
    var pendingLayer = layer with { Objects = SceneObjectList.From(items) };
    var pendingDocument = document with { Layers = [document.Layers[0], pendingLayer] };
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Measure(labelCount, "cold_pending_view", 1, () =>
    {
        var hidden = LabelPriority.ForFrame(pendingDocument, 1, () => ready.TrySetResult());
        return queryLabels.Count(hidden.Contains);
    });
    ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    // The editor's pre-existing full identity preparation is reported separately; the label
    // index no longer asks for it. Keep edits comparable with the editor's prepared storage.
    Task.Run(() => Measure(labelCount, "background_general_identity", 1, () => ((SceneObjectList)layer.Objects).IndexOfId(items[0].Id)))
        .GetAwaiter().GetResult();
#endif
    Measure(labelCount, "stamp_edit_view", repetitions, () => Query(Edit(0), 1));
    Measure(labelCount, "label_edit_view", repetitions, () => Query(Edit(stamps), 1));
    Measure(labelCount, "zoom_new_threshold_view", repetitions, () => Query(document, .1),
        () => { document = document with { Title = "zoom" }; _ = Query(document, 1); });
    Measure(labelCount, "zoom_cached_view", repetitions, () => Query(document, .11));
    Measure(labelCount, "zoom_new_threshold_full_set", repetitions, () => LabelPriority.Hidden(document, .1).Count,
        () => { document = document with { Title = "full" }; _ = Query(document, 1); });
}

static void Measure(int labels, string phase, int repetitions, Func<int> action, Action? prepare = null)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var elapsed = new double[repetitions];
    var allocated = new long[repetitions];
    var checksum = 0;
    for (var i = 0; i < repetitions; i++)
    {
        prepare?.Invoke();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        checksum += action();
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated[i] = GC.GetAllocatedBytesForCurrentThread() - before;
    }
    Array.Sort(elapsed);
    Array.Sort(allocated);
    Console.WriteLine(FormattableString.Invariant($"{labels},{phase},{elapsed[repetitions / 2]:F4},{elapsed[0]:F4},{elapsed[^1]:F4},{allocated[repetitions / 2]},{checksum}"));
}
