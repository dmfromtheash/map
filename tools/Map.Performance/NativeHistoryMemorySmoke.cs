using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Performance;

/// <summary>One observation point of the history-memory scenario (R01).</summary>
internal sealed record HistoryMemoryPoint(
    string Label, int UndoDepth, int RedoDepth,
    int HistoryUniqueTiles, long HistoryUniqueBytes,
    int DocumentTiles, long DocumentBytes, int SharedTiles, long UnionBytes,
    long ManagedHeapBytes, long GcHeapSizeBytes, long PrivateBytes, long WorkingSetBytes,
    int TileCache, int ObjectCache, long ObjectCacheBytes, int TerrainImages, int StyledImages, int CarvedLand,
    bool RefinementRunning, string? ResourceError);

public sealed partial class Stage19Application
{
    private static readonly string[] HistoryMemoryRequired =
    [
        "strokes-recorded-each-cycle", "undo-returns-to-cycle-start", "redo-restores-each-cycle",
        "undo-redo-pixels-exact", "cleared-redo-releases-history", "closed-editor-collected", "resources-disposed",
    ];

    private List<HistoryMemoryPoint> HistoryPoints => _report.HistoryMemory;

    /// <summary>A 4096×3072 map covered by one land layer, the size of the regional start.</summary>
    private static MapDocument HistoryMemoryDocument()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 0; x < 16; x++) for (var y = 0; y < 12; y++) tiles = tiles.With(new TileKey(x, y), coverage);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, new LandEdgeStyle());
        return new MapDocument(DocumentId.New(), "История и память", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 3072, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"), land,
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
    }

    private async Task HistoryMemoryOnly()
    {
        var land = _editor.Session.Document.Layers.OfType<LandLayer>().Single();
        _editor.SelectLayer(land.Id);
        await Click("LandToolButton");
        _view.Canvas.FitToCanvas(); await Frame();
        await Observe("start");

        const int cycles = 3;
        var strokesOk = true; var undoOk = true; var redoOk = true; var pixelsOk = true;
        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var startDepth = _editor.Session.UndoDepth;
            var startLand = LandOf();

            // Eight material strokes and four land-shape strokes, the same every cycle, across
            // the map through the production canvas router.
            await Click("LandToolModePaint");
            await Click("LandPaintActionTexture");
            _editor.ChooseLandMaterial(cycle % 2 == 0 ? "regional-ochre" : "regional-forest");
            Find<Avalonia.Controls.Slider>("LandPaintSizeSlider").Value = 160;
            Find<Avalonia.Controls.Slider>("LandPaintSoftnessSlider").Value = 60;
            for (var i = 0; i < 8; i++)
            {
                var y = 300 + i * 320;
                Drag(300 + i * 60, y, 1800 + i * 60, y + 140); await Settle();
            }
            await Click("LandToolModeShape");
            for (var i = 0; i < 4; i++)
            {
                var y = 500 + i * 600;
                Drag(2400, y, 3700, y + 200); await Settle();
            }
            await Frame();
            var strokes = _editor.Session.UndoDepth - startDepth;
            strokesOk &= strokes == 12;
            var afterStrokes = LandOf();
            await Observe($"cycle{cycle}-after-12-strokes");

            for (var i = 0; i < strokes; i++) _editor.Undo();
            await Frame();
            undoOk &= _editor.Session.UndoDepth == startDepth && SameTiles(startLand, LandOf());
            await Observe($"cycle{cycle}-after-undo");

            for (var i = 0; i < strokes; i++) _editor.Redo();
            await Frame();
            redoOk &= _editor.Session.UndoDepth == startDepth + strokes && _editor.Session.RedoDepth == 0;
            pixelsOk &= SameTiles(afterStrokes, LandOf());
            await Observe($"cycle{cycle}-after-redo");
        }
        _report.Checks["strokes-recorded-each-cycle"] = strokesOk;
        _report.Checks["undo-returns-to-cycle-start"] = undoOk;
        _report.Checks["redo-restores-each-cycle"] = redoOk;
        _report.Checks["undo-redo-pixels-exact"] = pixelsOk;

        // Everything undone, then one new edit: the redo branch is discarded, so its tiles must
        // stop being retained by anything but a bounded, recomputable cache.
        while (_editor.Session.CanUndo) _editor.Undo();
        await Frame();
        await Observe("all-undone");
        await Click("LandToolModePaint");
        Drag(600, 600, 900, 700); await Settle(); await Frame();
        var afterNew = await Observe("new-edit-clears-redo");
        var allUndone = HistoryPoints.Single(p => p.Label == "all-undone");
        _report.Checks["cleared-redo-releases-history"] = afterNew.RedoDepth == 0 && afterNew.UndoDepth == 1
            && afterNew.HistoryUniqueBytes < allUndone.HistoryUniqueBytes / 10
            && afterNew.ManagedHeapBytes < allUndone.ManagedHeapBytes - allUndone.HistoryUniqueBytes / 2;
        // Diagnostic only: what the derived render caches alone still hold after that edit. The
        // same release the working-memory budget raises under pressure; nothing else is cleared.
        var release = typeof(Map.Core.Resources.WorkingMemoryBudget).GetField("ReleaseDerivedCaches", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Action;
        release?.Invoke();
        var afterRelease = await Observe("new-edit-after-derived-cache-release");
        Console.WriteLine($"retained by derived render caches after the redo branch was dropped: {(afterNew.ManagedHeapBytes - afterRelease.ManagedHeapBytes) / 1048576.0:0.0} MiB");

        // Close this editor the way switching documents does and check it can be collected.
        var weakEditor = new WeakReference(_editor); var weakView = new WeakReference(_view);
        var weakDocument = new WeakReference(_editor.Session.Document);
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
        _window.Content = null;
        OpenEditor(new MapDocument(DocumentId.New(), "Пусто", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFEDE3CD"), [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD")]));
        await Frame(); await Settle();
        await Observe("closed-and-reopened-empty");
        _report.Checks["closed-editor-collected"] = !weakEditor.IsAlive && !weakView.IsAlive && !weakDocument.IsAlive;
        Console.WriteLine($"closed editor alive: vm={weakEditor.IsAlive} view={weakView.IsAlive} document={weakDocument.IsAlive}");

        LandLayer LandOf() => (LandLayer)_editor.Session.Document.FindLayer(land.Id)!;
        static bool SameTiles(LandLayer a, LandLayer b) => Same(a.PaintTiles, b.PaintTiles) && Same(a.GeometryTiles, b.GeometryTiles);
        static bool Same(TileMap left, TileMap right) => left.TileCount == right.TileCount
            && left.Keys.All(k => right.TryGet(k) is { } bytes && left.TryGet(k)!.AsSpan().SequenceEqual(bytes));
    }

    private async Task<HistoryMemoryPoint> Observe(string label)
    {
        await Settle();
        for (var i = 0; i < 3; i++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var session = _editor.Session;
        var history = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var listName in new[] { "_undo", "_redo" })
            if (typeof(EditingSession).GetField(listName, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(session) is IList list)
                foreach (var entry in list)
                    if (entry?.GetType().GetProperty("Command")?.GetValue(entry) is { } command) Collect(command, history, 0);
        var document = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var layer in session.Document.Layers)
            foreach (var property in layer.GetType().GetProperties())
                if (property.PropertyType == typeof(TileMap) && property.GetValue(layer) is TileMap map)
                    foreach (var key in map.Keys) if (map.TryGet(key) is { } tile) document.Add(tile);
        var shared = history.Count(document.Contains);
        var union = new HashSet<byte[]>(history, ReferenceEqualityComparer.Instance); union.UnionWith(document);

        using var process = Process.GetCurrentProcess(); process.Refresh();
        var (terrain, styled, carved, running) = CanvasCaches();
        var point = new HistoryMemoryPoint(label, session.UndoDepth, session.RedoDepth,
            history.Count, history.Sum(b => (long)b.Length), document.Count, document.Sum(b => (long)b.Length),
            shared, union.Sum(b => (long)b.Length),
            GC.GetTotalMemory(false), GC.GetGCMemoryInfo().HeapSizeBytes, process.PrivateMemorySize64, process.WorkingSet64,
            _view.Canvas.TileCacheCount, _view.Canvas.ObjectCacheCount, _view.Canvas.ObjectCacheBytes, terrain, styled, carved, running,
            session.LastResourceError);
        HistoryPoints.Add(point);
        Console.WriteLine($"{label}: undo={point.UndoDepth} redo={point.RedoDepth} historyTiles={point.HistoryUniqueTiles} " +
            $"history={point.HistoryUniqueBytes / 1048576.0:0.0}MiB doc={point.DocumentBytes / 1048576.0:0.0}MiB union={point.UnionBytes / 1048576.0:0.0}MiB " +
            $"managed={point.ManagedHeapBytes / 1048576.0:0.0}MiB private={point.PrivateBytes / 1048576.0:0.0}MiB ws={point.WorkingSetBytes / 1048576.0:0.0}MiB " +
            $"tiles={point.TileCache} terrain={terrain} styled={styled} carved={carved} refining={running} error={point.ResourceError}");
        return point;
    }

    /// <summary>Every tile buffer a history command holds, through its fields, by reference.</summary>
    private static void Collect(object value, HashSet<byte[]> into, int depth)
    {
        if (depth > 6) return;
        switch (value)
        {
            case byte[] bytes: into.Add(bytes); return;
            case TileMap map: foreach (var key in map.Keys) if (map.TryGet(key) is { } tile) into.Add(tile); return;
            case string: return;
            case IDictionary dictionary: foreach (var item in dictionary.Values) if (item is not null) Collect(item, into, depth + 1); return;
            case IEnumerable sequence when value is not MapDocument:
                foreach (var item in sequence) if (item is not null) Collect(item, into, depth + 1);
                return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is MapDocument) return;
        if (type.IsValueType && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            if (type.GetProperty("Value")!.GetValue(value) is { } pair) Collect(pair, into, depth + 1);
            return;
        }
        if (type.Namespace?.StartsWith("Map.", StringComparison.Ordinal) != true) return;
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (field.GetValue(value) is { } child && !field.FieldType.IsPrimitive) Collect(child, into, depth + 1);
    }

    /// <summary>Entry counts of the canvas's terrain and land image caches, read for diagnostics only.</summary>
    private (int Terrain, int Styled, int Carved, bool Refining) CanvasCaches()
    {
        var resources = typeof(Map.App.Controls.MapCanvasControl).GetField("_renderResources", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(_view.Canvas);
        if (resources?.GetType().GetField("_contexts", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(resources) is not IDictionary contexts) return (-1, -1, -1, false);
        int terrain = 0, styled = 0, carved = 0; var refining = false;
        foreach (var context in contexts.Values)
        {
            if (context is null) continue;
            var fields = context.GetType().GetFields();
            object? Item(string name) => fields.FirstOrDefault(f => f.Name == name)?.GetValue(context);
            terrain += (int)(Item("Item5")?.GetType().GetProperty("Count")?.GetValue(Item("Item5")) ?? 0);
            styled += (int)(Item("Item4")?.GetType().GetProperty("Count")?.GetValue(Item("Item4")) ?? 0);
            carved += (int)(Item("Item3")?.GetType().GetProperty("Count")?.GetValue(Item("Item3")) ?? 0);
            refining |= Item("Item7")?.GetType().GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Item("Item7")) is not null;
        }
        return (terrain, styled, carved, refining);
    }
}
