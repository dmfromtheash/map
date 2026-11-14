using System.Globalization;
using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

/// <summary>
/// DEC-111: live managed bytes, not allocated bytes or process RSS. Each sample owns a fresh
/// prepared map and empty history. Command construction and Execute are inside the GC delta;
/// fixture construction and Undo/Redo checks are outside it. The input snapshot is anchored
/// across the first two GCs: releasing its old derived indexes must not cancel new history
/// allocations. A third GC drops that anchor and also reports the ordinary net session delta.
/// Both metrics keep the session/history alive. Snapshot references sharing existing storage
/// are not charged as copies; copied arrays and persistent-list directories are charged.
/// Five samples follow two warmups; the 10k/200k samples are interleaved. No byte floor or ratio allowance.
/// Full collections require a nonparallel collection, including under the standard xUnit runner.
/// MAP_UNDO_MEM_AUDIT=1 measures skipped violations without asserting their ratios.
/// MAP_UNDO_MEM_HUGE=1 adds the same cases at 1M objects; ordinary runs never build that map.
/// </summary>
[Collection(nameof(DerivedIndexCounters))]
public sealed class UndoMemoryGuardTests
{
    private const int Samples = 5;
    private const double RatioLimit = 3;
    private static readonly string Hash = new('a', 64);

    public enum Edit
    {
        Place10, Move10, Delete10, Style10, Area, Path,
        AddLayer10, DeleteLayer10, DuplicateLayer10, ReorderLayer,
        RenameLayer, LayerVisible, LayerLocked, LayerOpacity, LayerBlend,
        MapStyle, Lighting, Fog, Labels, MapScale,
        CheckpointAdd, CheckpointDelete, CheckpointRename, CheckpointRestore,
        ReorderTwoObjects,
    }

    private const string PathSnapshots = "PlacementPathEditing.cs:111,225 retains before/after documents. Persistent chunk directories are shared; owner/member deltas instead of document snapshots remain out of scope.";

    private static bool IsViolation(Edit edit) => false;
    private static bool IsHugeViolation(Edit edit) => edit is Edit.Path;

    public static IEnumerable<object[]> BoundedCases() => Enum.GetValues<Edit>()
        .Where(edit => !IsViolation(edit)).Select(edit => new object[] { edit });

    [Theory]
    [MemberData(nameof(BoundedCases))]
    public void One_entry_retained_bytes_do_not_scale_with_map(Edit edit) => Compare(edit, enforce: true);

    public static IEnumerable<object[]> HugeBoundedCases() => Enum.GetValues<Edit>()
        .Where(edit => !IsViolation(edit) && !IsHugeViolation(edit)).Select(edit => new object[] { edit });

    public sealed class HugeTheoryAttribute : TheoryAttribute
    {
        public HugeTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("MAP_UNDO_MEM_HUGE") != "1")
                Skip = "Set MAP_UNDO_MEM_HUGE=1 to build and measure 1M objects.";
        }
    }

    [HugeTheory]
    [MemberData(nameof(HugeBoundedCases))]
    [InlineData(Edit.Path, Skip = PathSnapshots)]
    public void One_entry_retained_bytes_at_one_million_objects(Edit edit) => CompareHuge(edit, enforce: true);

    [Fact]
    public void Audit_skipped_entries_when_requested()
    {
        if (Environment.GetEnvironmentVariable("MAP_UNDO_MEM_AUDIT") != "1") return;
        foreach (var edit in Enum.GetValues<Edit>().Where(IsViolation)) Compare(edit, enforce: false);
        if (Environment.GetEnvironmentVariable("MAP_UNDO_MEM_HUGE") == "1")
            foreach (var edit in Enum.GetValues<Edit>().Where(edit => IsViolation(edit) || IsHugeViolation(edit)))
                CompareHuge(edit, enforce: false);
    }

    [Theory]
    [InlineData(Edit.Place10, true)]
    [InlineData(Edit.Move10, true)]
    [InlineData(Edit.Delete10, true)]
    [InlineData(Edit.Style10, true)]
    [InlineData(Edit.Area, true)]
    [InlineData(Edit.Path, true)]
    [InlineData(Edit.Move10, false)]
    [InlineData(Edit.Style10, false)]
    public void Two_hundred_small_edits_keep_the_whole_history_alive(Edit edit, bool withOwners)
    {
        Warm(edit, withOwners);
        var samples = Enumerable.Range(0, Samples).Select(_ => Measure(200_000, edit, 200, withOwners)).ToArray();
        var bytes = Median(samples.Select(s => s.Added).ToArray());
        var net = Median(samples.Select(s => s.Net).ToArray());
        Write($"UNDO_HISTORY|edit={edit}|owners={withOwners}|objects=200000|entries=200|median_bytes={bytes}|net_bytes={net}|bytes_per_entry={bytes / 200.0:F2}");
        // This is a total-heap delta, not a graph profiler: cache compaction during a long
        // sequence may also release memory. Preserve signed results; Measure verifies that
        // all 200 entries really execute, undo and redo, even when the net delta is negative.
    }

    private static void Compare(Edit edit, bool enforce)
    {
        Warm(edit);
        var small = new Measurement[Samples];
        var large = new Measurement[Samples];
        for (var i = 0; i < Samples; i++)
        {
            // Alternate order so process-wide initialization cannot always penalize one size.
            if (i % 2 == 0) { small[i] = Measure(10_000, edit, 1); large[i] = Measure(200_000, edit, 1); }
            else { large[i] = Measure(200_000, edit, 1); small[i] = Measure(10_000, edit, 1); }
        }
        var a = Median(small.Select(s => s.Added).ToArray());
        var b = Median(large.Select(s => s.Added).ToArray());
        var ratio = Ratio(a, b);
        Write($"UNDO_MEMORY|edit={edit}|entries=1|small_objects=10000|large_objects=200000|small_bytes={a}|large_bytes={b}|ratio={ratio:F3}|enforced={enforce}|small_net={Median(small.Select(s => s.Net).ToArray())}|large_net={Median(large.Select(s => s.Net).ToArray())}|small_samples={string.Join(',', small.Select(s => s.Added))}|large_samples={string.Join(',', large.Select(s => s.Added))}");
        if (enforce) Assert.True(a > 0 && ratio <= RatioLimit,
            $"{edit}: retained {a} -> {b} bytes; ratio {ratio:F3} > {RatioLimit}, or nonpositive baseline.");
    }

    private static void CompareHuge(Edit edit, bool enforce)
    {
        Warm(edit);
        var smallSamples = Enumerable.Range(0, Samples).Select(_ => Measure(10_000, edit, 1)).ToArray();
        var hugeSamples = Enumerable.Range(0, Samples).Select(_ => Measure(1_000_000, edit, 1)).ToArray();
        var small = Median(smallSamples.Select(s => s.Added).ToArray());
        var huge = Median(hugeSamples.Select(s => s.Added).ToArray());
        var hugeRatio = Ratio(small, huge);
        Write($"UNDO_HUGE|edit={edit}|objects=1000000|small_bytes={small}|median_bytes={huge}|net_bytes={Median(hugeSamples.Select(s => s.Net).ToArray())}|ratio_vs_10000={hugeRatio:F3}|enforced={enforce}|small_samples={string.Join(',', smallSamples.Select(s => s.Added))}|huge_samples={string.Join(',', hugeSamples.Select(s => s.Added))}");
        if (enforce) Assert.True(small > 0 && hugeRatio <= RatioLimit,
            $"{edit}: retained {small} -> {huge} bytes at 1M; ratio {hugeRatio:F3} > {RatioLimit}.");
    }

    private static double Ratio(long small, long large) => small > 0 ? (double)large / small : double.PositiveInfinity;
    private static long Median(long[] values) { Array.Sort(values); return values[values.Length / 2]; }
    private static void Write(FormattableString text) => Console.WriteLine(text.ToString(CultureInfo.InvariantCulture));

    private static void Warm(Edit edit, bool withOwners = true)
    {
        for (var i = 0; i < 2; i++) _ = Measure(1_000, edit, 1, withOwners);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement Measure(int objects, Edit edit, int entries, bool withOwners = true)
    {
        _ = Retained(); // Drain the preceding sample before mutating global weak-index tables.
        var fixture = Create(objects, edit, withOwners);
        var beforeState = Fingerprint(fixture);
        var before = Retained();
        Execute(fixture, edit, entries);
        var after = Retained();
        GC.KeepAlive(fixture); // Session/history AND the anchor MUST survive the second collection.
        var retained = after - before;
        ReleaseAnchor(fixture);
        var net = Retained() - before;
        GC.KeepAlive(fixture.Session);

        Assert.Null(fixture.Session.LastResourceError);
        Assert.Equal(entries, fixture.Session.UndoDepth);
        var afterState = Fingerprint(fixture);
        if (entries == 1) Assert.NotEqual(beforeState, afterState); // A single no-op cannot pass.
        for (var i = 0; i < entries; i++) Assert.True(fixture.Session.Undo());
        Assert.False(fixture.Session.CanUndo);
        Assert.Equal(beforeState, Fingerprint(fixture));
        for (var i = 0; i < entries; i++) Assert.True(fixture.Session.Redo());
        Assert.Equal(entries, fixture.Session.UndoDepth);
        Assert.Equal(afterState, Fingerprint(fixture));
        return new(retained, net);
    }

    private readonly record struct Measurement(long Added, long Net);
    private sealed record Fixture(EditingSession Session, LayerId Main, LayerId Small)
    {
        public MapDocument? Anchor { get; set; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseAnchor(Fixture fixture) => fixture.Anchor = null;

    private static long Retained()
    {
        // ConditionalWeakTable values from discarded setup revisions can survive the first
        // collection. Drain finalizers and collect again before asking for the live-byte count.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Fixture Create(int count, Edit edit, bool withOwners)
    {
        var main = new ObjectLayer(LayerId.New(), "main", true, false, 1, []);
        var small = new ObjectLayer(LayerId.New(), "small", true, false, 1,
            Enumerable.Range(0, 10).Select(i => Stamp(i, "small")).ToArray());
        var background = new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF");
        var canvas = new CanvasDefinition(65_536, 32_768, "px", "#FFFFFFFF");
        var asset = new MapAssetReference(Hash, "stamp", "image/png", ".png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "user-owned", catalogId: Hash);
        var document = new MapDocument(DocumentId.New(), "memory", DateTimeOffset.UnixEpoch,
            canvas, [background, main, small], [asset]);
        if (withOwners)
        {
            var area = new PlacementArea { Density = 4, Seed = 17, Variants = [new(Hash, 32, 32)],
                Nodes = [new(Guid.NewGuid(), 3000, 2000), new(Guid.NewGuid(), 3512, 2000),
                    new(Guid.NewGuid(), 3512, 2512), new(Guid.NewGuid(), 3000, 2512)] };
            document = new PlacementAreaCommand(main.Id, area.Id, area, "setup area").Apply(document);
            var path = new PlacementPath { Spacing = 30, Variants = [new(Hash, 32, 32)],
                Nodes = [new(Guid.NewGuid(), 4000, 2000), new(Guid.NewGuid(), 4270, 2000)] };
            document = new PlacementPathCommand(main.Id, path.Id, path, "setup path").Apply(document);
        }
        main = (ObjectLayer)document.FindLayer(main.Id)!;
        var filler = Enumerable.Range(0, count - 10 - main.Objects.Count).Select(i => Stamp(i, "stamp")).ToArray();
        main = main with { Objects = SceneObjectList.From([.. filler, .. main.Objects]) };
        // Start from an independent document root, with no setup command/snapshot chain alive.
        document = new MapDocument(document.Id, document.Title, document.CreatedUtc, canvas,
            [background, main, small], [asset]);
        if (edit is Edit.CheckpointDelete or Edit.CheckpointRename or Edit.CheckpointRestore)
        {
            var checkpoint = new MapCheckpoint(CheckpointId.New(), "before", DateTimeOffset.UnixEpoch,
                MapCheckpointSnapshot.Capture(document), [1]);
            document = document.WithCheckpoints([checkpoint]);
            if (edit == Edit.CheckpointRestore)
                document = DocumentEditor.ReplaceObjects(document,
                    [SceneObjectClone.At(main.Objects[0], main.Objects[0].X + 7, main.Objects[0].Y)]);
        }
        DocumentDerivedIndexes.Prepare(document);
        foreach (var layer in document.Layers.OfType<ObjectLayer>()) _ = AssetUsageIndex.For(layer);
        Assert.Equal(count, DocumentDerivedIndexes.ObjectCount(document));
        Assert.True(DocumentDerivedIndexes.IsPrepared(document));
        return new(new EditingSession(document), main.Id, small.Id) { Anchor = document };
    }

    private static SceneObject Stamp(int i, string prefix) => new(ObjectId.New(), SceneObjectKind.Stamp,
        prefix + " " + i, (i % 1000) * 48, (i / 1000) * 48, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
    { Stamp = new StampStyle(Hash) };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Execute(Fixture f, Edit edit, int entries)
    {
        for (var step = 0; step < entries; step++)
        {
            var document = f.Session.Document;
            var layer = (ObjectLayer)document.FindLayer(f.Main)!;
            var target = (ObjectLayer)document.FindLayer(f.Small)!;
            // Spread ten edits across the layer: a contiguous ten must not hide chunk-directory costs.
            var unowned = layer.Objects.Count - layer.PlacementAreas.Sum(a => a.MemberCount)
                - layer.PlacementPaths.Sum(p => p.Members.Length);
            // Rotate history edits onto distinct stamps, reaching the revision-index overlay
            // limit on stamp-only maps instead of overwriting the same ten ids for 200 entries.
            var selected = Enumerable.Range(1, 10).Select(i => layer.Objects[(i * (unowned / 11) + step) % unowned]).ToArray();
            IEditCommand command = edit switch
            {
                Edit.Place10 => new AddObjectsCommand(layer.Id,
                    Enumerable.Range(0, 10).Select(i => Stamp(step * 10 + i, "added")).ToArray()),
                Edit.Move10 => new ReplaceObjectsCommand(selected,
                    selected.Select(o => SceneObjectClone.At(o, o.X + 3, o.Y + 5)).ToArray(), "move ten"),
                Edit.Style10 => new ReplaceObjectsCommand(selected,
                    selected.Select(o => o with { ObjectOpacity = o.ObjectOpacity == 1 ? .5 : 1 }).ToArray(), "style ten"),
                Edit.Delete10 => new DeleteObjectsCommand(document, selected.Select(o => o.Id).ToArray()),
                Edit.Area => AreaEdit(layer),
                Edit.Path => PathEdit(layer),
                Edit.AddLayer10 => new AddLayerCommand(new ObjectLayer(LayerId.New(), "added layer", true, false, 1,
                    Enumerable.Range(0, 10).Select(i => Stamp(i, "layer")).ToArray())),
                Edit.DeleteLayer10 => new DeleteLayerCommand(document, target.Id),
                Edit.DuplicateLayer10 => new DuplicateLayerCommand(target.Id),
                Edit.ReorderLayer => new ReorderLayerCommand(target.Id, false),
                Edit.RenameLayer => UpdateLayerCommand.Rename(layer, "renamed"),
                Edit.LayerVisible => UpdateLayerCommand.SetVisible(layer, false),
                Edit.LayerLocked => UpdateLayerCommand.SetLocked(layer, true),
                Edit.LayerOpacity => UpdateLayerCommand.SetOpacity(layer, .5),
                Edit.LayerBlend => UpdateLayerCommand.SetBlend(layer, "Multiply"),
                Edit.MapStyle => new SetMapStyleCommand("style", document.Style, new MapStyle()),
                Edit.Lighting => new SetLightingCommand("light", document.Lighting, new MapLighting()),
                Edit.Fog => new SetFogCommand(document.Fog, document.Fog with { Enabled = true }, "fog"),
                Edit.Labels => new SetLabelSettingsCommand(document.Labels, document.Labels with { ByScale = true }),
                Edit.MapScale => new SetMapScaleCommand(document.Canvas.Scale, new MapScale(2, "km")),
                Edit.CheckpointAdd => new AddCheckpointCommand(new MapCheckpoint(CheckpointId.New(), "added",
                    DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(document), [1])),
                Edit.CheckpointDelete => DeleteCheckpointCommand.Create(document, document.Checkpoints[0].Id),
                Edit.CheckpointRename => RenameCheckpointCommand.Create(document, document.Checkpoints[0].Id, "renamed"),
                Edit.CheckpointRestore => RestoreCheckpointCommand.Create(document, document.Checkpoints[0].Id),
                Edit.ReorderTwoObjects => ReorderTwo(document, layer),
                _ => throw new ArgumentOutOfRangeException(nameof(edit)),
            };
            f.Session.Execute(command);
        }
    }

    private static PlacementAreaCommand AreaEdit(ObjectLayer layer)
    {
        var area = layer.PlacementAreas.Single();
        return new(layer.Id, area.Id, area with { Seed = area.Seed + 1 }, "area seed");
    }

    private static PlacementPathCommand PathEdit(ObjectLayer layer)
    {
        var path = layer.PlacementPaths.Single();
        return new(layer.Id, path.Id, path with { Nodes = path.Nodes.SetItem(1,
            path.Nodes[1] with { Y = path.Nodes[1].Y == 2000 ? 2003 : 2000 }) }, "path node");
    }

    // The current command (ca390da / 027892d) takes moved ids and their final positions.
    private static ReorderObjectsCommand ReorderTwo(MapDocument document, ObjectLayer layer) =>
        new(document, layer.Id, [layer.Objects[1].Id, layer.Objects[0].Id], [0, 1], "swap two");

    // Verify semantic Undo/Redo outside the measurement, without pinning an old document or
    // traversing/preparing all derived indexes after Execute inside the retained delta.
    private static string Fingerprint(Fixture f)
    {
        var d = f.Session.Document;
        var hash = new HashCode();
        foreach (var layer in d.Layers)
        {
            hash.Add(layer.Id); hash.Add(layer.Name); hash.Add(layer.Visible); hash.Add(layer.Locked);
            hash.Add(layer.Opacity); hash.Add(layer.Blend);
            if (layer is not ObjectLayer objects) continue;
            foreach (var o in objects.Objects) hash.Add(o);
            foreach (var area in objects.PlacementAreas) hash.Add(area);
            foreach (var path in objects.PlacementPaths) hash.Add(path);
        }
        foreach (var c in d.Checkpoints) { hash.Add(c.Id); hash.Add(c.Name); }
        hash.Add(d.Style); hash.Add(d.Lighting); hash.Add(d.Fog); hash.Add(d.Labels); hash.Add(d.Canvas);
        return hash.ToHashCode().ToString(CultureInfo.InvariantCulture);
    }
}
