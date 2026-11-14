using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

/// <summary>
/// Prepared-map guards: N unrelated stamps, a fixed small edit, fresh command/session on EVERY
/// run (cached Apply/Redo must not hide the first execution). Setup, assertions and GC are outside
/// the measurements. Seven interleaved samples, two warmups; no absolute machine-speed threshold.
/// Bounded cases request a 64 MiB no-GC region around sampling (256 MiB for 1M); preparation of
/// that region is untimed. This avoids charging a previous test's collection to one map size.
/// Failure to acquire the region is reported; neither timing nor byte thresholds are relaxed.
/// The catalogue test forces new public command types to receive an explicit scenario.
/// MAP_ON_GUARDS_AUDIT=1 also measures the documented skipped cases, without enforcing their ratios.
/// MAP_ON_GUARDS_HUGE=1 adds 1M stamps. Both expensive variants are absent from ordinary execution.
/// </summary>
[Collection(nameof(DerivedIndexCounters))]
public sealed class EditCostGuardTests(EditCostGuardTests.Maps maps) : IClassFixture<EditCostGuardTests.Maps>
{
    private const int Runs = 7;
    private const double Limit = 3;
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);
    private sealed record Case(string Key, Type Type, Func<Fixture, IEditCommand> Create, string? Linear = null, string State = "ready");

    private static readonly HashSet<string> ChunkDirectory =
    [
        "AddObjectCommand", "TransformObjectCommand", "PasteCompositionCommand", "DeleteObjectsCommand/singleton",
        "RegionLabelFollowCommand", "DecorationRefreshCommand", "SetMapScaleCommand",
    ];

    private static Case C<T>(Func<Fixture, T> create, string? suffix = null, string? linear = null, string state = "ready") where T : IEditCommand
    {
        var key = typeof(T).Name + (suffix is null ? "" : "/" + suffix);
        linear ??= ChunkDirectory.Contains(key)
            ? "SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3"
            : null;
        return new(key, typeof(T), f => create(f), linear, state);
    }

    // 'linear' means a whole-STAMP-map scan, not merely linear in the changed payload.
    private static readonly Case[] Cases =
    [
        C(f => new AddObjectCommand(f.Layer.Id, f.Added[0])),
        C(f => new AddObjectsCommand(f.Layer.Id, f.Added)),
        C(f => new AddObjectsCommand(f.Layer.Id, f.Added), "legend",
            linear: "EditingSession.cs:245 -> MapDecorations.cs:84: legend refresh scans every stamp on every Execute", state: "legend"),
        C(f => new AddObjectsCommand(f.Layer.Id, f.Added), "append-limit",
            linear: "DocumentRevisionIndex.cs:479: after 256 appends the next lookup rebuilds the full root", state: "append-limit"),
        C(f => new PlaceAssetObjectsCommand(f.Layer.Id, Asset(OtherHash), f.Added)),
        C(f => new PasteObjectsCommand(f.Layer.Id, f.Added, [Asset(OtherHash)]), linear: "Commands.cs:328 filters every object on Undo"),
        C(f => new ReplaceStampAssetCommand(f.Selected[0], Asset(OtherHash))),
        C(f => new UpdateStampStyleWithAssetCommand(f.Selected[0], f.Selected[0].Stamp! with
            { MaskAssetSha256 = OtherHash, ClipMaskEnabled = true }, Asset(OtherHash))),
        C(f => new ReplaceObjectsCommand(f.Selected, f.Moved, "move 10"), "move"),
        C(f => new ReplaceObjectsCommand(f.Selected, f.Moved, "move 10"), "overlay-limit",
            linear: "DocumentRevisionIndex.cs:384-387: after 1024 overrides on a map without owners the full index is rebuilt", state: "overlay-limit"),
        C(f => new ReplaceObjectsCommand(f.Selected, [.. f.Selected.Select(o => o with { Name = o.Name + " renamed" })], "rename 10"), "rename"),
        C(f => new ReplaceObjectsCommand(f.Selected, [.. f.Selected.Select(o => o with { ObjectOpacity = .5 })], "style 10"), "style"),
        C(f => new TransformObjectCommand(f.Selected[0], f.Moved[0])),
        C(f => new RemoveObjectsCommand(f.Document, f.Layer.Id, f.Ids)),
        C(f => new DeleteObjectsCommand(f.Document, f.Ids)),
        C(f => new DeleteObjectsCommand(f.Document, [f.Grouped[0].Id]), "singleton"),
        C(f => new MoveObjectsToLayerCommand(f.Document, f.Layer.Id, f.Target.Id, f.Ids)),
        C(f => SetGroupCommand.Group(f.Document, f.Ids, "new group"), "group"),
        C(f => SetGroupCommand.Ungroup(f.Document, [.. f.Grouped.Select(o => o.Id)]), "ungroup"),
        C(f => SetGroupCommand.Rename(f.Document, f.Grouped[0].GroupId!.Value, "renamed group"), "rename"),
        C(f => new ReorderObjectsCommand(f.Layer.Id, f.Layer.Objects, SwapTwo(f.Layer.Objects), "swap two"),
            linear: "Commands.cs:553,573 captures and rebuilds the entire layer for a two-object swap"),
        C(f => new AddLayerCommand(new ObjectLayer(LayerId.New(), "ten", true, false, 1, f.Added))),
        C(f => new DeleteLayerCommand(f.Document, f.Target.Id)),
        C(f => new DuplicateLayerCommand(f.Target.Id)),
        C(f => new ReorderLayerCommand(f.Target.Id, true)),
        C(f => UpdateLayerCommand.Rename(f.Layer, "renamed"), "rename"),
        C(f => UpdateLayerCommand.SetVisible(f.Layer, false), "visibility"),
        C(f => UpdateLayerCommand.SetLocked(f.Layer, true), "lock"),
        C(f => UpdateLayerCommand.SetOpacity(f.Layer, .5), "opacity"),
        C(f => UpdateLayerCommand.SetBlend(f.Layer, "Multiply"), "blend"),
        C(f => UpdateLayerCommand.Replace(f.Layer, f.Layer with { Name = "generation metadata" }, "apply"), "replace"),
        C(f => (UpdateLayerCommand)ControlledAreaGeneration.Create(f.Document, f.Layer.Id, f.Area.Id, CancellationToken.None),
            "controlled-area", linear: "ControlledAreaGeneration.cs:23 -> UpdateLayerCommand.Replace -> WithLayers: scans/copies all stamps for one area"),
        C(f => new BrushStrokeCommand(f.Brush.Id, f.EmptyTiles, f.RgbaTiles, "stroke")),
        C(f => new MaskStrokeCommand(f.Layer.Id, f.EmptyTiles, f.MaskTiles)),
        C(f => UpdateMaskCommand.SetEnabled(f.Layer, false)),
        C(f => new UpdateLandStyleCommand(f.Land, f.Land.EdgeStyle with { FillTextureOpacity = .5 })),
        C(f => new LandPaintCommand(f.Land.Id, f.EmptyTiles, f.RgbaTiles, "paint")),
        C(f => new LandCliffMaskCommand(f.Land.Id, f.EmptyTiles, f.MaskTiles, f.EmptyTiles, f.MaskTiles, "cliff")),
        C(f => new LandReliefCommand(f.Land.Id, f.EmptyTiles, f.MaskTiles, "relief")),
        C(f => new UpdateStampForegroundCommand(f.Document.StampForeground, null, "clear foreground")),
        C(f => new StampForegroundStrokeCommand(f.EmptyTiles, f.RgbaTiles, "foreground")),
        C(f => new VectorAssetStyleCommand(f.Shapes[0], f.Shapes[0] with
            { Shape = f.Shapes[0].Shape! with { Fill = f.Shapes[0].Shape!.Fill with { TextureAssetSha256 = OtherHash } } }, Asset(OtherHash))),
        C(f => new ReplaceObjectWithManyCommand(f.Document, f.Layer.Id, f.Shapes[0].Id,
            [VectorGeometry.WithNewIds(f.Shapes[0]), VectorGeometry.WithNewIds(f.Shapes[0])]),
            linear: "VectorEditing.cs:22,35 copies and filters the whole layer"),
        C(f => new ShapeBooleanCommand(f.Document, f.Layer.Id, f.Shapes[0].Id, f.Shapes[1].Id, ShapeBooleanOperation.Add),
            linear: "VectorEditing.cs:307,315 filters the whole layer"),
        C(f => new ReplaceObjectsWithCompositionCommand(f.Document, f.Layer.Id, f.Ids, f.NewComposition),
            linear: "Compositions.cs:163-168 scans/copies the whole layer"),
        C(f => new PasteCompositionCommand(f.Layer.Id, f.NewComposition, [])),
        C(f => new FlattenCompositionCommand(f.Composition, f.Composition with
            { Kind = SceneObjectKind.Stamp, Composition = null, Stamp = new StampStyle(OtherHash) }, Asset(OtherHash))),
        C(f => new UpdateFilterCommand(f.Filter, f.Filter with { Settings = f.Filter.Settings with { Brightness = .2 } })),
        C(f => new UpdateFilterWithAssetCommand(f.Filter, f.Filter with
            { Settings = f.Filter.Settings with { TextureAssetSha256 = OtherHash } }, Asset(OtherHash))),
        C(f => new FillTextureEmbedCommand(new AddObjectsCommand(f.Layer.Id, f.Added), [Asset(OtherHash)])),
        C(f => new AddNoteCommand(new MapNote(NoteId.New(), "added", "text", NoteAnchorKind.World))),
        C(f => new ReplaceNoteCommand(f.Note, f.Note with { Title = "renamed" })),
        C(f => new DeleteNoteCommand(f.Document, f.Note.Id)),
        C(f => new AddCheckpointCommand(new MapCheckpoint(CheckpointId.New(), "added", DateTimeOffset.UnixEpoch,
            MapCheckpointSnapshot.Capture(f.Document), [1]))),
        C(f => DeleteCheckpointCommand.Create(f.Document, f.Checkpoint.Id)),
        C(f => RenameCheckpointCommand.Create(f.Document, f.Checkpoint.Id, "renamed")),
        C(f => RestoreCheckpointCommand.Create(f.Document, f.Checkpoint.Id)),
        C(f => new ReplaceGridLayerCommand(f.Grid, f.Grid with { Name = "changed grid" })),
        C(f => HexPaintCommand.Create("ten hexes", f.Hex, f.HexChanges)!),
        C(f => new UpdateHexGridCommand(f.Hex.Id, f.Hex.Grid, f.Hex.Grid with { Visible = false })),
        C(f => new HexLayerEditCommand("hex metadata", f.Hex, f.Hex with { Name = "hex edit", Grid = f.Hex.Grid with { Visible = false } })),
        C(f => HexBoardShapeCommand.Create("reshape", f.Document, f.Hex,
            h => h with { Board = h.Board.Translated(10, 0) })!),
        C(f => new HexImportCommand(f.Document, f.Hex with { Board = f.Hex.Board.With(f.HexChanges) })),
        C(f => new SetHexSystemsCommand(f.Hex.Id, f.Hex.Systems,
            f.Hex.Systems.SetItem(new(0, 0), new StarSystem { Name = "system" }), "system")),
        C(f => new MakeStarSectorCommand(f.Hex.Id)),
        C(f => new PlacementPathCommand(f.Layer.Id, f.Path.Id, f.Path with { Nodes = f.Path.Nodes.SetItem(1,
            f.Path.Nodes[1] with { X = f.Path.Nodes[1].X + 10 }) }, "path node")),
        C(f => new PlacementAreaCommand(f.Layer.Id, f.Area.Id, f.Area with { Seed = f.Area.Seed + 1 }, "area regenerate")),
        C(f => new PlacementAreaCommand(f.Layer.Id, f.Area.Id, f.Area with
            { LayoutVersion = PlacementArea.LatestLayoutVersion, Density = 4 }, "natural area"), "natural"),
        C(f => new ManualAreaSelectionCommand(new TransformObjectCommand(f.AreaMember,
            SceneObjectClone.At(f.AreaMember, f.AreaMember.X + 3, f.AreaMember.Y)), [f.AreaMember.Id])),
        C(f => new RiverCommand(f.Layer.Id, f.River.Id, RiverEditor.MoveNode(f.River,
            f.River.Channels[0].Id, 0, 200, 200), "river node")),
        C(f => new RoadCommand(f.Layer.Id, f.Road.Id, RoadEditor.MoveNode(f.Road,
            f.Road.Nodes[0].Id, 250, 250), "road node")),
        C(f => new ReorderRiversCommand(f.Layer.Id, [f.SecondRiver.Id, f.River.Id], "water order")),
        C(f => new ReorderSurfaceCommand(f.Layer.Id, [f.River.Id, f.Road.Id, f.SecondRiver.Id], "surface order")),
        C(f => Follow(f, 0) as TextPathFollowCommand ?? throw new InvalidOperationException()),
        C(f => Follow(f, 1) as RegionLabelFollowCommand ?? throw new InvalidOperationException()),
        C(f => Follow(f, 2) as DecorationRefreshCommand ?? throw new InvalidOperationException()),
        C(f => new SetLabelSettingsCommand(f.Document.Labels, f.Document.Labels with { ByScale = true })),
        C(f => new SetMapStyleCommand("style", f.Document.Style, new MapStyle())),
        C(f => new SetLightingCommand("lighting", f.Document.Lighting, new MapLighting())),
        C(f => new SetFogCommand(f.Document.Fog, f.Document.Fog with { Enabled = true }, "fog")),
        C(f => new SetEntitiesCommand(f.Document.Entities, f.Document.Entities.Add(new MapEntity { Name = "place" }), "entity")),
        C(f => new SetTransitionsCommand(f.Document.Transitions, f.Document.Transitions.Add(new MapTransition()), "transition")),
        C(f => new SetTimelineCommand(f.Document.Timeline, f.Document.Timeline with { Year = 1200 }, "timeline")),
        C(f => new SetPointcrawlCommand(f.Document.Pointcrawl, f.Document.Pointcrawl with
            { Nodes = [new(Guid.NewGuid(), "place", 10, 20)] }, "pointcrawl")),
        C(f => new SetPlanetCommand(f.Document.Planet, f.Document.Planet with { Enabled = true })),
        C(f => new SetIsometricCommand(f.Document.Isometric, f.Document.Isometric with { HeightScale = 2 })),
        C(f => new SetMapHandoutCommand(f.Document.Handout, MapHandout.Found)),
        C(f => new SetMapScaleCommand(f.Document.Canvas.Scale, new MapScale(1, "km"))),
        C(f => new ApplyStylePackCommand("pack", f.Document, f.Document with { Style = new MapStyle() })),
        C(f => new ApplyChangePackageCommand(f.Document, f.SmallChange),
            linear: "ChangePackages.cs:222 -> WithLayers: the big layer arrives as another storage; its 200k objects exceed the layer-delta bound (MapDocument.MaxDeltaObjects) and take the whole-document validation"),
        C(f => new MapVariantCommand("variant", f.Document, f.Document with { Handout = MapHandout.Found, Style = new MapStyle() })),
        C(f => new ResizeCanvasCommand(f.Document, f.SmallChange)),
        C(f => new ReplaceCityCommand(f.Target, f.Target with { Objects = f.Added }, f.Document.Notes, f.Document.Notes)),
        C(f => new CompositeCommand("ten moves", [.. f.Selected.Zip(f.Moved, (a, b) => new TransformObjectCommand(a, b))])),
    ];

    public static IEnumerable<object[]> BoundedCases => Cases.Where(c => c.Linear is null).Select(c => new object[] { c.Key });

    [Fact]
    public void Catalogue_covers_every_public_editor_command()
    {
        var actual = typeof(IEditCommand).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == typeof(IEditCommand).Namespace && t.IsClass && !t.IsAbstract && typeof(IEditCommand).IsAssignableFrom(t));
        Assert.Equal(actual.OrderBy(t => t.Name), Cases.Select(c => c.Type).Distinct().OrderBy(t => t.Name));
        Assert.Equal(Cases.Length, Cases.Select(c => c.Key).Distinct().Count());
        Assert.Equal(Cases.Where(c => c.Linear is not null).Select(c => "Known_" + c.Key.Replace('/', '_').Replace('-', '_')).Order(),
            typeof(EditCostGuardTests).GetMethods().Where(m => m.Name.StartsWith("Known_", StringComparison.Ordinal)).Select(m => m.Name).Order());
        foreach (var c in Cases) Console.WriteLine($"EDIT_CASE|{c.Key}|{c.Type.Name}|{c.Linear ?? "bounded"}");
    }

    [Theory]
    [MemberData(nameof(BoundedCases))]
    public void Fixed_edit_does_not_scale_with_unrelated_stamps(string key) => Compare(Find(key), maps.Small, maps.Large, enforce: true);

    [Theory]
    [MemberData(nameof(BoundedCases))]
    public void Prepared_indexes_follow_execute_undo_redo_without_full_rebuild(string key)
    {
        var f = maps.Large;
        var session = new EditingSession(f.Document);
        session.SetSelection(f.Ids);
        var roots = Roots();
        var identities = SceneObjectList.IdentityRootObjects;
        var enumerated = SceneObjectList.EnumeratedOnThisThread;
        session.Execute(Find(key).Create(f));
        PrepareAfter(f.Document, session.Document);
        var after = session.Document;
        Assert.True(session.Undo());
        PrepareAfter(after, session.Document);
        var undone = session.Document;
        Assert.True(session.Redo());
        PrepareAfter(undone, session.Document);
        Assert.Equal(roots, Roots());
        // Identity slots count the objects they visit, not the tables: a genuinely new layer (add,
        // duplicate, a rebuilt city) needs its own small table, which is work on the changed
        // payload. A table over the unrelated stamps visits far more than this bound.
        Assert.True(SceneObjectList.IdentityRootObjects - identities < 10_000, key + ": identity table over the whole map");
        // A separate deterministic guard: even a fast allocation-free foreach over the whole map
        // must fail. Bounded chunks and the fixed area/path payload may still be inspected.
        Assert.True(SceneObjectList.EnumeratedOnThisThread - enumerated < 10_000, key + ": full object enumeration");
        Assert.Null(session.LastResourceError);
    }

    private static (long Revision, long Names) Roots() =>
        (DocumentRevisionIndex.RootBuilds, ObjectNameIndex.RootBuilds);

    private static void PrepareAfter(MapDocument before, MapDocument after)
    {
        foreach (var layer in after.Layers.OfType<ObjectLayer>())
        {
            var previous = before.FindLayer(layer.Id) as ObjectLayer;
            if (previous is not null && !ReferenceEquals(previous.Objects, layer.Objects))
                Assert.True(ObjectLayerChanges.TryGet(layer, out _), "Changed storage lost ObjectLayerChanges");
            var spatial = ObjectSpatialIndex.For(layer);
            if (previous is not null && ReferenceEquals(previous.Objects, layer.Objects))
                Assert.Same(ObjectSpatialIndex.For(previous), spatial);
            _ = AssetUsageIndex.For(layer);
            _ = BoundObjectIndex.For(layer);
        }
        DocumentDerivedIndexes.Prepare(after);
        Assert.True(DocumentDerivedIndexes.IsPrepared(after));
    }

    [Fact]
    public void Harness_detects_a_deliberate_full_layer_copy()
    {
        var linear = new Case("positive-control/full-copy", typeof(AddObjectsCommand), f =>
        {
            GC.KeepAlive(f.Layer.Objects.ToArray());
            return new AddObjectsCommand(f.Layer.Id, f.Added);
        });
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Compare(linear, maps.Small, maps.Large, enforce: true));
    }

    [Fact]
    public void Background_generation_apply_is_bounded()
    {
        CompareSamples("BackgroundGeneration.TryApply", maps.Small, maps.Large, RunBackground, enforce: true);
    }

    private static Sample RunBackground(Fixture f)
    {
        var session = new EditingSession(f.Document);
        session.SetSelection(f.Ids);
        using var generation = new BackgroundGeneration();
        // Calculation belongs to the worker, not to apply; never mix worker allocations with the
        // UI thread's allocation counter. Every sample has its own completed, fresh preview.
        Task.Run(() => generation.CalculateAsync(session, (_, _) =>
            new ReplaceObjectsCommand(f.Selected, f.Moved, "generated ten"))).GetAwaiter().GetResult();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var applied = generation.TryApply(session);
        var after = session.Document;
        var undone = session.Undo();
        var before = session.Document;
        var redone = session.Redo();
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(GenerationApplyResult.Applied, applied);
        Assert.True(undone && redone);
        VerifySnapshot(f.Document, before);
        VerifySnapshot(after, session.Document);
        foreach (var moved in f.Moved) Assert.Equal(moved, FindInLayers(session.Document, moved.Id));
        return new(elapsed, bytes);
    }

    // Each documented violation has its own skipped Fact below, so xUnit discovery lists ALL
    // commands even though a skipped Theory would never enumerate its MemberData.
    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_AddObjectCommand() => Compare(Find("AddObjectCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): EditingSession.cs:245 -> MapDecorations.cs:84: legend refresh scans every stamp on every Execute")]
    public void Known_AddObjectsCommand_legend() => Compare(Find("AddObjectsCommand/legend"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): Commands.cs:328 filters every object on Undo")]
    public void Known_PasteObjectsCommand() => Compare(Find("PasteObjectsCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_TransformObjectCommand() => Compare(Find("TransformObjectCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_DeleteObjectsCommand_singleton() => Compare(Find("DeleteObjectsCommand/singleton"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): Commands.cs:553,573 captures and rebuilds the entire layer for a two-object swap")]
    public void Known_ReorderObjectsCommand() => Compare(Find("ReorderObjectsCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): ControlledAreaGeneration.cs:23 -> UpdateLayerCommand.Replace -> WithLayers: scans/copies all stamps for one area")]
    public void Known_UpdateLayerCommand_controlled_area() => Compare(Find("UpdateLayerCommand/controlled-area"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): VectorEditing.cs:22,35 copies and filters the whole layer")]
    public void Known_ReplaceObjectWithManyCommand() => Compare(Find("ReplaceObjectWithManyCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): VectorEditing.cs:307,315 filters the whole layer")]
    public void Known_ShapeBooleanCommand() => Compare(Find("ShapeBooleanCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): Compositions.cs:163-168 scans/copies the whole layer")]
    public void Known_ReplaceObjectsWithCompositionCommand() => Compare(Find("ReplaceObjectsWithCompositionCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_PasteCompositionCommand() => Compare(Find("PasteCompositionCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_RegionLabelFollowCommand() => Compare(Find("RegionLabelFollowCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_DecorationRefreshCommand() => Compare(Find("DecorationRefreshCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): SceneObjectList.cs:90-97,466: copies chunk directories (O(N/256)); allocated-byte ratio exceeds 3")]
    public void Known_SetMapScaleCommand() => Compare(Find("SetMapScaleCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): ChangePackages.cs:222 -> WithLayers: the big layer arrives as another storage; its 200k objects exceed the layer-delta bound (MapDocument.MaxDeltaObjects) and take the whole-document validation")]
    public void Known_ApplyChangePackageCommand() => Compare(Find("ApplyChangePackageCommand"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): DocumentRevisionIndex.cs:479: after 256 appends the next lookup rebuilds the full root")]
    public void Known_AddObjectsCommand_append_limit() => Compare(Find("AddObjectsCommand/append-limit"), maps.Small, maps.Large, enforce: true);

    [Fact(Skip = "O(N): DocumentRevisionIndex.cs:384-387: after 1024 overrides on a map without owners the full index is rebuilt")]
    public void Known_ReplaceObjectsCommand_overlay_limit() => Compare(Find("ReplaceObjectsCommand/overlay-limit"), maps.Small, maps.Large, enforce: true);


    [EnvironmentFact("MAP_ON_GUARDS_AUDIT")]
    public void Measure_documented_linear_cases()
    {
        foreach (var c in Cases.Where(c => c.Linear is not null)) Compare(c, maps.Small, maps.Large, enforce: false);
    }

    [EnvironmentFact("MAP_ON_GUARDS_HUGE")]
    public void Million_stamp_variant()
    {
        var huge = new Fixture(1_000_000);
        foreach (var c in Cases) Compare(c, maps.Large, huge, enforce: c.Linear is null);
    }

    private static Case Find(string key) => Cases.Single(c => c.Key == key);

    private static void Compare(Case c, Fixture small, Fixture large, bool enforce)
        => CompareSamples(c.Key, small, large, f => Run(c, f), enforce);

    private static void CompareSamples(string key, Fixture small, Fixture large, Func<Fixture, Sample> run, bool enforce)
    {
        for (var i = 0; i < 2; i++) { run(small); run(large); }
        var a = new Sample[Runs]; var b = new Sample[Runs];
        var noGc = false;
        if (enforce) noGc = GC.TryStartNoGCRegion((large.N >= 1_000_000 ? 256L : 64L) * 1024 * 1024);
        var collections = GC.CollectionCount(0);
        try
        {
            for (var i = 0; i < Runs; i++)
            {
                // Interleaving and reversed pairs avoid measuring all small cases in a cooler process.
                if ((i & 1) == 0) { a[i] = run(small); b[i] = run(large); }
                else { b[i] = run(large); a[i] = run(small); }
            }
        }
        finally
        {
            collections = GC.CollectionCount(0) - collections;
            if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
            if (enforce) Console.WriteLine($"EDIT_GC|{key}|noGC={noGc}|collectionsDuringSamples={collections}");
        }
        var first = Median(a); var second = Median(b);
        var time = second.Microseconds / first.Microseconds;
        var bytes = (double)second.Bytes / first.Bytes;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"EDIT_COST|{key}|{small.N}|{large.N}|{first.Microseconds:F3}|{second.Microseconds:F3}|{first.Bytes}|{second.Bytes}|{time:F3}|{bytes:F3}|{(enforce ? "GUARD" : "O(N)")}"));
        if (!enforce) return;
        Assert.True(time <= Limit, $"{key}: median time ratio {time:F3} > {Limit}; {first.Microseconds:F3} -> {second.Microseconds:F3} us");
        Assert.True(bytes <= Limit, $"{key}: allocated bytes ratio {bytes:F3} > {Limit}; {first.Bytes} -> {second.Bytes}");
    }

    private readonly record struct Sample(double Microseconds, long Bytes);
    private static Sample Median(Sample[] values) => new(values.Select(s => s.Microseconds).Order().ElementAt(Runs / 2),
        values.Select(s => s.Bytes).Order().ElementAt(Runs / 2));

    private static Sample Run(Case c, Fixture f)
    {
        // Session allocation and a selected set of ten are fixed; include factory cost (not fixture
        // construction) so scans in constructors, e.g. reorder/composition, remain observable.
        var original = c.State switch
        {
            "legend" => f.LegendDocument,
            "append-limit" => f.AppendLimitDocument,
            "overlay-limit" => f.OverlayLimitDocument,
            _ => f.Document,
        };
        var session = new EditingSession(original);
        session.SetSelection(f.Ids);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        session.Execute(c.Create(f));
        var after = session.Document;
        var undone = session.Undo();
        var before = session.Document;
        var redone = session.Redo();
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Null(session.LastResourceError);
        Assert.True(undone && redone);
        Assert.Equal(3, session.Revision);
        Assert.Equal(1, session.UndoDepth);
        Assert.Equal(0, session.RedoDepth);
        Assert.NotSame(original, after); // rejected/no-op commands must not give a cheap green guard
        VerifySnapshot(original, before);
        VerifySnapshot(after, session.Document);
        foreach (var id in f.WitnessIds)
        {
            Assert.Equal(FindInLayers(original, id), FindInLayers(before, id));
            Assert.Equal(FindInLayers(after, id), FindInLayers(session.Document, id));
        }
        return new(elapsed, bytes);
    }

    private static SceneObject? FindInLayers(MapDocument document, ObjectId id)
    {
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            var at = layer.ObjectStorage.IndexOfId(id);
            if (at >= 0) return layer.Objects[at];
        }
        return null;
    }

    // Constant-size witnesses of the actual edit and its inverse, outside the measurement. Never
    // enumerate the large object layer, stringify it or compare two whole document snapshots.
    private static void VerifySnapshot(MapDocument expected, MapDocument actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Canvas, actual.Canvas);
        Assert.Equal(expected.Layers.Count, actual.Layers.Count);
        for (var i = 0; i < expected.Layers.Count; i++)
        {
            var a = expected.Layers[i]; var b = actual.Layers[i];
            Assert.Equal(a.Id, b.Id); Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Visible, b.Visible); Assert.Equal(a.Locked, b.Locked);
            Assert.Equal(a.Opacity, b.Opacity); Assert.Equal(a.Blend, b.Blend);
            Assert.Equal(a.Mask?.Enabled, b.Mask?.Enabled);
            if (a.Mask is { } am && b.Mask is { } bm) VerifyTiles(am.Tiles, bm.Tiles);
            if (a is not ObjectLayer x || b is not ObjectLayer y) continue;
            Assert.Equal(x.Objects.Count, y.Objects.Count);
            foreach (var at in new[] { 0, x.Objects.Count / 2, x.Objects.Count - 1 }.Where(at => at >= 0 && at < x.Objects.Count))
                Assert.Equal(x.Objects[at], y.Objects[at]);
            Assert.True(x.Rivers.SequenceEqual(y.Rivers)); Assert.True(x.Roads.SequenceEqual(y.Roads));
            Assert.True(x.PlacementPaths.SequenceEqual(y.PlacementPaths)); Assert.True(x.PlacementAreas.SequenceEqual(y.PlacementAreas));
        }
        Assert.Equal(expected.Assets, actual.Assets); Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.Checkpoints.Select(c => (c.Id, c.Name)), actual.Checkpoints.Select(c => (c.Id, c.Name)));
        Assert.Equal(expected.Style, actual.Style); Assert.Equal(expected.Lighting, actual.Lighting);
        Assert.Equal(expected.Entities, actual.Entities); Assert.Equal(expected.Labels, actual.Labels);
        Assert.Equal(expected.Fog, actual.Fog); Assert.Equal(expected.Transitions, actual.Transitions);
        Assert.Equal(expected.Timeline, actual.Timeline); Assert.Equal(expected.Pointcrawl, actual.Pointcrawl);
        Assert.Equal(expected.Planet, actual.Planet); Assert.Equal(expected.Isometric, actual.Isometric);
        Assert.Equal(expected.Handout, actual.Handout);
        Assert.Equal(expected.StampForeground is null, actual.StampForeground is null);
        if (expected.StampForeground is { } ef && actual.StampForeground is { } af) VerifyTiles(ef.Tiles, af.Tiles);
    }

    private static void VerifyTiles(TileMap expected, TileMap actual)
    {
        Assert.Equal(expected.TileCount, actual.TileCount);
        foreach (var key in expected.Keys) Assert.Equal(expected.TryGet(key), actual.TryGet(key));
    }

    private static SceneObject[] SwapTwo(IReadOnlyList<SceneObject> source)
    {
        var result = source.ToArray();
        (result[0], result[^1]) = (result[^1], result[0]);
        return result;
    }

    private static IEditCommand Follow(Fixture f, int kind)
    {
        var inner = new TransformObjectCommand(f.Selected[0], f.Moved[0]);
        var edited = inner.Apply(f.Document);
        var source = kind == 0 ? f.PathLabel : kind == 1 ? f.RegionLabel : f.Decoration;
        var followed = DocumentEditor.ReplaceObject(edited, SceneObjectClone.At(source, source.X + 1, source.Y));
        return kind switch
        {
            0 => new TextPathFollowCommand(inner, edited, followed),
            1 => new RegionLabelFollowCommand(inner, edited, followed),
            _ => new DecorationRefreshCommand(inner, edited, followed),
        };
    }

    private static MapAssetReference Asset(string hash) => new(hash, "stamp", "image/png", ".png",
        ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "guard", "user-owned", catalogId: hash);
    private static SceneObject Stamp(int i, string hash) => new(ObjectId.New(), SceneObjectKind.Stamp, "Stamp " + i,
        i % 1000 * 60, i / 1000 * 60, 24, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(hash) };

    public sealed class Maps
    {
        private readonly Lazy<Fixture> _small = new(() => new Fixture(10_000));
        private readonly Lazy<Fixture> _large = new(() => new Fixture(200_000));
        internal Fixture Small => _small.Value;
        internal Fixture Large => _large.Value;
    }

    internal sealed class Fixture
    {
        public int N { get; }
        public MapDocument Document { get; }
        public MapDocument SmallChange { get; }
        private MapDocument? _legend;
        public MapDocument LegendDocument => _legend ??= PrepareLegend();
        private MapDocument? _appendLimit, _overlayLimit;
        public MapDocument AppendLimitDocument => _appendLimit ??= PrepareAppendLimit();
        public MapDocument OverlayLimitDocument => _overlayLimit ??= PrepareOverlayLimit();
        public ObjectLayer Layer { get; }
        public ObjectLayer Target { get; }
        public SceneObject[] Selected { get; }
        public SceneObject[] Moved { get; }
        public ObjectId[] Ids { get; }
        public ObjectId[] WitnessIds { get; }
        public SceneObject[] Added { get; } = [.. Enumerable.Range(0, 10).Select(i => Stamp(i, OtherHash))];
        public SceneObject[] Shapes { get; }
        public SceneObject[] Grouped { get; }
        public SceneObject Composition { get; }
        public SceneObject NewComposition { get; }
        public SceneObject PathLabel { get; }
        public SceneObject RegionLabel { get; }
        public SceneObject Decoration { get; }
        public BrushLayer Brush { get; } = BrushLayer.CreateEmpty(LayerId.New(), "brush");
        public LandLayer Land { get; } = LandLayer.CreateEmpty(LayerId.New(), "land");
        public FilterLayer Filter { get; } = new(LayerId.New(), "filter", true, false, 1, new FilterSettings());
        public GridLayer Grid { get; } = new(LayerId.New(), "grid", true, false, 1, [new(GridId.New(), GridGeometry.Square, 100)]);
        public HexLayer Hex { get; } = new(LayerId.New(), "hex", true, false, 1, HexBoard.Create(HexOrientation.Pointy, 10, 20, 20));
        public MapNote Note { get; } = new(NoteId.New(), "note", "text", NoteAnchorKind.World);
        public MapCheckpoint Checkpoint { get; }
        public River River { get; }
        public River SecondRiver { get; }
        public Road Road { get; }
        public PlacementArea Area { get; }
        public PlacementPath Path { get; }
        public SceneObject AreaMember { get; }
        public Dictionary<TileKey, byte[]?> EmptyTiles { get; } = new() { [new(0, 0)] = null };
        public Dictionary<TileKey, byte[]?> RgbaTiles { get; } = new() { [new(0, 0)] = new byte[TileMap.TileSize * TileMap.TileSize * 4] };
        public Dictionary<TileKey, byte[]?> MaskTiles { get; } = new() { [new(0, 0)] = new byte[TileMap.TileSize * TileMap.TileSize] };
        public Dictionary<HexCoord, HexCell?> HexChanges { get; } = Enumerable.Range(0, 10)
            .ToDictionary(i => new HexCoord(i, 0), _ => (HexCell?)new HexCell("plains"));

        public Fixture(int n)
        {
            N = n;
            var stamps = Enumerable.Range(0, n).Select(i => Stamp(i, Hash)).ToArray();
            Selected = [.. Enumerable.Range(1, 10).Select(i => stamps[i * (n / 11)])];
            Moved = [.. Selected.Select(o => SceneObjectClone.At(o, o.X + 3, o.Y + 5))];
            Ids = [.. Selected.Select(o => o.Id)];
            var group = Guid.NewGuid();
            Grouped = [stamps[0] with { GroupId = group, GroupName = "pair" }, stamps[1] with { GroupId = group, GroupName = "pair" }];
            stamps[0] = Grouped[0]; stamps[1] = Grouped[1];
            Shapes = [.. Enumerable.Range(0, 2).Select(i => VectorGeometry.CreateFreeShape(ObjectId.New(), "shape",
                [new(100 + i * 30, 100), new(300 + i * 30, 100), new(300 + i * 30, 300), new(100 + i * 30, 300)]))];
            River = new River { Channels = [new RiverChannel { Nodes = [new(Guid.NewGuid(), 100, 100, 10), new(Guid.NewGuid(), 400, 400, 20)] }] };
            SecondRiver = River with { Id = Guid.NewGuid(), Channels = [River.Channels[0] with { Id = Guid.NewGuid() }] };
            Road = RoadEditor.NewRoad([new(100, 500), new(600, 500)], 10, seed: 7);
            PathLabel = Stamp(-1, Hash) with { Kind = SceneObjectKind.Text, Stamp = null,
                Text = new TextStyle { Content = "path", Path = new TextPathBinding { TargetId = River.Id } } };
            RegionLabel = Stamp(-2, Hash) with { Kind = SceneObjectKind.Text, Stamp = null,
                Text = new TextStyle { Content = "region", Region = new TextRegionBinding { TargetId = Shapes[0].Id.Value } } };
            Decoration = Stamp(-3, Hash) with { Kind = SceneObjectKind.Decoration, Stamp = null,
                Decoration = new DecorationStyle { Kind = DecorationKind.ScaleBar } };
            Composition = Stamp(-4, Hash) with { Kind = SceneObjectKind.Composition, Stamp = null,
                Composition = new EditableComposition(CompositionId.New(), DocumentId.New(), "composition", 100, 100, [Stamp(1, Hash)]) };
            NewComposition = VectorGeometry.WithNewIds(Composition);
            var layer = new ObjectLayer(LayerId.New(), "stamps", true, false, 1,
                [.. stamps, .. Shapes, PathLabel, RegionLabel, Decoration, Composition])
                { Mask = LayerMask.CreateEmpty(), Rivers = [River, SecondRiver], Roads = [Road] };
            Target = new ObjectLayer(LayerId.New(), "small target", true, false, 1, [Stamp(-5, Hash)]);
            var doc = new MapDocument(DocumentId.New(), "guard", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(65_536, 32_768, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), Land, Brush, layer, Target, Grid, Hex, Filter],
                [Asset(Hash)], [Note], stampForeground: StampForegroundSurface.CreateEmpty());
            var area = new PlacementArea { Density = 4, Seed = 17, Variants = [new(Hash, 32, 32)],
                Nodes = [new(Guid.NewGuid(), 3000, 2000), new(Guid.NewGuid(), 3512, 2000),
                    new(Guid.NewGuid(), 3512, 2512), new(Guid.NewGuid(), 3000, 2512)] };
            doc = new PlacementAreaCommand(layer.Id, area.Id, area, "setup area").Apply(doc);
            Area = ((ObjectLayer)doc.FindLayer(layer.Id)!).PlacementAreas.Single();
            var path = new PlacementPath { Spacing = 30, Variants = [new(Hash, 32, 32)],
                Nodes = [new(Guid.NewGuid(), 4000, 2000), new(Guid.NewGuid(), 4270, 2000)] };
            doc = new PlacementPathCommand(layer.Id, path.Id, path, "setup path").Apply(doc);
            Path = ((ObjectLayer)doc.FindLayer(layer.Id)!).PlacementPaths.Single();
            doc = TextOnPath.Follow(doc, doc);
            doc = RegionLabels.Follow(doc, doc);
            doc = MapDecorations.Refresh(doc);
            // A checkpoint is captured from the live, prepared document (AddCheckpointCommand); its
            // snapshot shares that document's layer-keyed indexes. A never-prepared snapshot (one
            // read from disk) restores like an opened file: its indexes are built once, off input.
            var versioned = DocumentEditor.ReplaceObjects(doc, Moved);
            DocumentDerivedIndexes.Prepare(versioned);
            Checkpoint = new MapCheckpoint(CheckpointId.New(), "checkpoint", DateTimeOffset.UnixEpoch,
                MapCheckpointSnapshot.Capture(versioned), [1]);
            Document = doc.WithCheckpoints([Checkpoint]);
            Layer = (ObjectLayer)Document.FindLayer(layer.Id)!;
            PathLabel = DocumentEditor.FindObject(Document, PathLabel.Id)!;
            RegionLabel = DocumentEditor.FindObject(Document, RegionLabel.Id)!;
            Decoration = DocumentEditor.FindObject(Document, Decoration.Id)!;
            AreaMember = DocumentEditor.FindObject(Document, Area.Members.First().ObjectId)!;
            WitnessIds = [.. Ids, .. Added.Select(o => o.Id), .. Grouped.Select(o => o.Id), .. Shapes.Select(o => o.Id),
                Composition.Id, NewComposition.Id, PathLabel.Id, RegionLabel.Id, Decoration.Id, AreaMember.Id];
            SmallChange = DocumentEditor.ReplaceObjects(Document, Moved);
            DocumentDerivedIndexes.Prepare(Document);
            DocumentDerivedIndexes.Prepare(SmallChange);
            foreach (var objects in Document.Layers.OfType<ObjectLayer>()) _ = AssetUsageIndex.For(objects);
            Assert.True(DocumentDerivedIndexes.IsPrepared(Document));
            Console.WriteLine($"EDIT_FIXTURE|stamps={N}|area={Area.MemberCount}|path={Path.Members.Length}|selected={Ids.Length}");
        }

        private MapDocument PrepareLegend()
        {
            var document = DocumentEditor.ReplaceObject(Document, Decoration with
                { Decoration = new DecorationStyle { Kind = DecorationKind.Legend } });
            document = MapDecorations.Refresh(document);
            DocumentDerivedIndexes.Prepare(document);
            return document;
        }

        private MapDocument PrepareAppendLimit()
        {
            // Exactly at the depth bound: the fixture's own setup edits may already have started
            // the append chain that Document shares (indexes follow the layer list).
            var document = Document;
            for (var i = 0; DocumentRevisionIndex.For(document).AppendDepth < 256; i++)
                document = DocumentEditor.AppendObjects(document, Layer.Id, [Stamp(-1000 - i, Hash)]);
            DocumentDerivedIndexes.Prepare(document);
            return document;
        }

        private MapDocument PrepareOverlayLimit()
        {
            var document = Document.WithLayer(Layer.Id, l => ((ObjectLayer)l) with { PlacementPaths = [], PlacementAreas = [] });
            var ids = Ids.ToHashSet();
            var replacements = Layer.Objects.Skip(10).Take(2048).Where(o => !ids.Contains(o.Id)).Take(1024)
                .Select(o => SceneObjectClone.At(o, o.X + 1, o.Y)).ToArray();
            document = DocumentEditor.ReplaceObjects(document, replacements);
            Assert.Equal(1024, DocumentRevisionIndex.For(document).OverlayObjectCount);
            DocumentDerivedIndexes.Prepare(document);
            return document;
        }
    }

    public sealed class EnvironmentFactAttribute : FactAttribute
    {
        public EnvironmentFactAttribute(string variable)
        {
            if (Environment.GetEnvironmentVariable(variable) != "1") Skip = "Set " + variable + "=1 to run the slow variant";
        }
    }
}
