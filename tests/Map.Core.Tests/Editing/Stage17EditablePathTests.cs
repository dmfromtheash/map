using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage17EditablePathTests
{
    private static readonly string Sha = new('a', 64);
    private static readonly string Sha2 = new('b', 64);

    [Fact]
    public void Nodes_regenerate_only_adjacent_segments_with_stable_ids_variants_and_exact_history()
    {
        var session = Session(out var layer);
        var path = Path();
        var create = new PlacementPathCommand(layer.Id, path.Id, path, "create"); session.Execute(create);
        var before = session.Document; var stored = Get(before);
        var unaffected = stored.Members.Where(m => m.Slot.NodeId == path.Nodes[2].Id).Select(m => DocumentEditor.FindObject(before, m.ObjectId)).ToArray();
        var moved = stored with { Nodes = stored.Nodes.SetItem(1, stored.Nodes[1] with { Y = 120 }) };
        var edit = new PlacementPathCommand(layer.Id, path.Id, moved, "node"); session.Execute(edit);
        Assert.Equal(2, edit.RegeneratedSegments);
        Assert.All(unaffected, item => Assert.Same(item, DocumentEditor.FindObject(session.Document, item!.Id)));
        foreach (var member in Get(session.Document).Members)
            if (DocumentEditor.FindObject(before, member.ObjectId) is { } old)
                Assert.Equal(old.Stamp, DocumentEditor.FindObject(session.Document, member.ObjectId)!.Stamp);
        var after = session.Document;
        Assert.True(session.Undo()); Assert.Same(before, session.Document);
        Assert.True(session.Redo()); Assert.Same(after, session.Document);
        Assert.True(session.Undo()); Assert.True(session.Undo()); Assert.Empty(GetPaths(session.Document));
        session.Redo(); Assert.Same(before, session.Document);
    }

    [Fact]
    public void Detach_exclusion_survives_shrink_grow_seed_and_bake_delete_are_reversible()
    {
        var session = Session(out var layer); var p = Path();
        session.Execute(new PlacementPathCommand(layer.Id, p.Id, p, "create"));
        var member = Get(session.Document).Members[2]; var stamp = DocumentEditor.FindObject(session.Document, member.ObjectId)!;
        var before = session.Document;
        session.Execute(new PlacementPathCommand(layer.Id, p.Id, Get(before), "detach", detach: member.ObjectId));
        var detached = session.Document;
        session.Undo(); Assert.Same(before, session.Document); session.Redo(); Assert.Same(detached, session.Document);
        var manual = SceneObjectClone.At(stamp, 20, 700);
        session.Execute(new ReplaceObjectsCommand([stamp], [manual], "manual detached edit"));
        Assert.Same(manual, DocumentEditor.FindObject(session.Document, member.ObjectId));
        stamp = manual;
        foreach (var spacing in new[] { 1000, 20, 50 })
        {
            var current = Get(session.Document);
            session.Execute(new PlacementPathCommand(layer.Id, p.Id, current with { Spacing = spacing, Seed = spacing }, "spacing"));
            Assert.Same(stamp, DocumentEditor.FindObject(session.Document, member.ObjectId));
            Assert.DoesNotContain(Get(session.Document).Members, m => m.Slot == member.Slot);
        }
        var beforeDelete = session.Document;
        session.Execute(new PlacementPathCommand(layer.Id, p.Id, null, "delete"));
        Assert.Same(stamp, DocumentEditor.FindObject(session.Document, member.ObjectId));
        Assert.Empty(GetPaths(session.Document));
        session.Undo(); Assert.Same(beforeDelete, session.Document);
        var ids = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects.Select(o => o.Id).ToArray();
        session.Execute(new PlacementPathCommand(layer.Id, p.Id, null, "bake", bake: true));
        Assert.Empty(GetPaths(session.Document));
        Assert.Equal(ids, ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects.Select(o => o.Id));
        session.Undo(); Assert.Same(beforeDelete, session.Document);
    }

    [Fact]
    public void Unfinished_and_refused_edits_leave_document_history_and_neighbours_intact()
    {
        var session = Session(out var layer); var path = Path();
        session.Execute(new PlacementPathCommand(layer.Id, path.Id, path, "create"));
        var before = session.Document; var stamp = DocumentEditor.FindObject(before, Get(before).Members[0].ObjectId)!;
        session.Execute(new ReplaceObjectsCommand([stamp], [SceneObjectClone.At(stamp, 999, 999)], "manual"));
        Assert.Same(before, session.Document); Assert.Equal(1, session.UndoDepth);
        Assert.Contains("Отделить", session.LastResourceError);
        session.Execute(new DeleteObjectsCommand(before, [stamp.Id]));
        Assert.Same(before, session.Document); Assert.Equal(1, session.UndoDepth);
        session.Execute(new DuplicateLayerCommand(layer.Id));
        Assert.Same(before, session.Document); Assert.Contains("обычные", session.LastResourceError);
        var preview = PlacementPathPlanner.Preview(Get(before), 8);
        Assert.Equal(8, preview.Count); Assert.Same(before, session.Document);
        foreach (var item in preview) Assert.Equal(item, DocumentEditor.FindObject(before, item.Id));
    }

    [Fact]
    public void A_long_row_keeps_a_local_delta_and_refuses_invalid_links_or_locked_layers()
    {
        var session = Session(out var layer);
        var path = Path();
        path = path with { Spacing = 1, Nodes = [.. path.Nodes.Select((n, i) => n with { X = 100 + i * 500 })] };
        session.Execute(new PlacementPathCommand(layer.Id, path.Id, path, "create"));
        var before = session.Document; var stored = Get(before);
        Assert.Throws<ArgumentException>(() => (stored with { Members = stored.Members.RemoveAt(0) }).ValidateMembers());
        var changed = new PlacementPathCommand(layer.Id, path.Id, stored with { Seed = 321 }, "seed");
        session.Execute(changed);
        var changedIndex = DocumentRevisionIndex.For(session.Document);
        Assert.True(changedIndex.OverlayObjectCount > 1024);
        Assert.Equal(stored.Members.Length, changedIndex.ObjectCount);
        var locked = session.Document.WithLayers(session.Document.Layers
            .Select(l => l.Id == layer.Id ? l with { Locked = true } : l).ToArray());
        var lockedSession = new EditingSession(locked);
        lockedSession.Execute(new PlacementPathCommand(layer.Id, path.Id, Get(locked) with { Spacing = 20 }, "locked"));
        Assert.Same(locked, lockedSession.Document); Assert.Equal(0, lockedSession.UndoDepth);
        Assert.Contains("заблокирован", lockedSession.LastResourceError);
    }

    [Fact]
    public void Long_preview_samples_both_ends_of_requested_segments_and_keeps_exclusions()
    {
        var path = Path() with { Spacing = 1 };
        path = path with { Exclusions = path.Exclusions.Union([
            new(path.Nodes[0].Id, 0), new(path.Nodes[2].Id, 198), new(path.Nodes[2].Id, 199)]) };
        var samples = PlacementPathPlanner.Preview(path, segments: new HashSet<int> { 0, 2 });
        Assert.Equal(597, PlacementPathPlanner.Count(path));
        Assert.Equal(64, samples.Count); Assert.Equal(64, samples.Select(o => o.Id).Distinct().Count());
        Assert.Equal(PlacementPathPlanner.Item(path, 0, 1), samples[0]);
        Assert.Equal(PlacementPathPlanner.Item(path, 2, 197), samples[^1]);
        Assert.DoesNotContain(samples, o => path.Exclusions.Any(slot => PlacementPathPlanner.ObjectIdFor(path.Id, slot) == o.Id));
        Assert.All(samples, o =>
        {
            var centre = o.X + o.Width / 2;
            Assert.Equal(100, o.Y + o.Height / 2);
            Assert.True(centre is >= 100 and < 300 or >= 500 and < 700);
        });
        var empty = path with { Nodes = [path.Nodes[0], path.Nodes[0] with { Id = Guid.NewGuid() }] };
        Assert.Empty(PlacementPathPlanner.Preview(empty));
    }

    private static EditingSession Session(out ObjectLayer layer)
    {
        layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, []);
        var assets = new[] { Sha, Sha2 }.Select(s => new MapAssetReference(s, "tree", "image/png", "png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "test", catalogId: s)).ToArray();
        return new(new MapDocument(DocumentId.New(), "Paths", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer], assets));
    }
    private static PlacementPath Path() => new()
    {
        Name = "Trees", Nodes = [new(Guid.NewGuid(), 100, 100), new(Guid.NewGuid(), 300, 100),
            new(Guid.NewGuid(), 500, 100), new(Guid.NewGuid(), 700, 100)],
        Variants = [new(Sha, 32, 32), new(Sha2, 64, 32)], Spacing = 50, Seed = 17,
        Request = new() { BaseWidth = 32, RandomizeVariant = true, RandomFlip = true, RandomAngleStep = StampRandomAngleStep.Degrees30 },
        Style = new(Sha),
    };
    private static IEnumerable<PlacementPath> GetPaths(MapDocument doc) => doc.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementPaths);
    private static PlacementPath Get(MapDocument doc) => GetPaths(doc).Single();
}
