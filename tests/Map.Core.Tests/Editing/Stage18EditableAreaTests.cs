using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage18EditableAreaTests
{
    private static readonly string Sha = new('a', 64);
    private static readonly string Other = new('b', 64);
    [Fact]
    public void Boundary_delta_matches_full_fill_preserves_unchanged_cells_objects_order_and_exact_history()
    {
        var session = Session(out var layer); var area = Area();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        Assert.Null(session.LastResourceError);
        var before = session.Document; area = Get(before);
        var next = area with { Nodes = area.Nodes.SetItem(1, area.Nodes[1] with { X = 1850 }) };
        var edit = new PlacementAreaCommand(layer.Id, area.Id, next, "edge"); session.Execute(edit);
        Assert.Null(session.LastResourceError); Assert.InRange(edit.VisitedCells, 1, 80);
        Assert.True(edit.ChangedObjects > 0);
        var after = session.Document; var changed = Get(after); changed.ValidateMembers();
        Assert.All(changed.Members.Where(m => DocumentEditor.FindObject(before, m.ObjectId) is not null),
            m => Assert.Same(DocumentEditor.FindObject(before, m.ObjectId), DocumentEditor.FindObject(after, m.ObjectId)));
        var remaining = changed.Members.Select(m => m.ObjectId).ToHashSet();
        Assert.Equal(((ObjectLayer)before.FindLayer(layer.Id)!).Objects.Where(o => remaining.Contains(o.Id)).Select(o => o.Id),
            ((ObjectLayer)after.FindLayer(layer.Id)!).Objects.Where(o => DocumentEditor.FindObject(before, o.Id) is not null).Select(o => o.Id));
        Assert.True(area.Cells[0].Equals(changed.Cells[0]));
        session.Undo(); Assert.Same(before, session.Document); session.Redo(); Assert.Same(after, session.Document);
        Assert.All(PlacementAreaPlanner.Preview(next, area), o => Assert.Equal(o, DocumentEditor.FindObject(after, o.Id)));
    }
    [Fact]
    public void Density_retains_exact_instances_and_detach_is_permanent_across_shrink_return_density_seed_delete()
    {
        var session = Session(out var layer); var area = Area();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create")); area = Get(session.Document);
        var before = session.Document;
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area with { Density = 8 }, "density"));
        Assert.All(area.Members, m => Assert.Same(DocumentEditor.FindObject(before, m.ObjectId), DocumentEditor.FindObject(session.Document, m.ObjectId)));
        area = Get(session.Document); var member = area.Members.First();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "detach", detach: member.ObjectId));
        var stamp = DocumentEditor.FindObject(session.Document, member.ObjectId)!;
        var manual = SceneObjectClone.At(stamp, 11, 31);
        session.Execute(new ReplaceObjectsCommand([stamp], [manual], "manual"));
        foreach (var density in new[] { 0d, 2.5, 8, 4 })
        {
            var current = Get(session.Document);
            session.Execute(new PlacementAreaCommand(layer.Id, area.Id, current with { Density = density, Seed = (int)density }, "parameters"));
            Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
            Assert.Same(manual, DocumentEditor.FindObject(session.Document, member.ObjectId));
            Assert.Contains(member.Slot, Get(session.Document).Exclusions);
        }
        var nodes = Get(session.Document).Nodes;
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with {
            Nodes = [new(Guid.NewGuid(), 700, 700), new(Guid.NewGuid(), 800, 700), new(Guid.NewGuid(), 800, 800)] }, "shrink"));
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { Nodes = nodes }, "return"));
        Assert.Same(manual, DocumentEditor.FindObject(session.Document, member.ObjectId));
        var full = session.Document;
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, null, "bake", bake: true));
        Assert.Empty(((ObjectLayer)session.Document.FindLayer(layer.Id)!).PlacementAreas);
        Assert.Equal(((ObjectLayer)full.FindLayer(layer.Id)!).Objects, ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects);
        session.Undo(); Assert.Same(full, session.Document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, null, "delete"));
        Assert.Same(manual, Assert.Single(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects));
    }
    [Fact]
    public void Concave_insert_remove_and_local_movement_match_fresh_generation()
    {
        var session = Session(out var layer); var area = Area() with { Nodes = [new(Guid.NewGuid(), 10, 10),
            new(Guid.NewGuid(), 700, 10), new(Guid.NewGuid(), 700, 700), new(Guid.NewGuid(), 350, 400), new(Guid.NewGuid(), 10, 700)] };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "create"));
        for (var i = 0; i < 12; i++)
        {
            var old = Get(session.Document); var next = old with { Nodes = old.Nodes.SetItem(3, old.Nodes[3] with { X = 200 + i * 25, Y = 500 - i * 20 }) };
            session.Execute(new PlacementAreaCommand(layer.Id, area.Id, next, "move")); Assert.Null(session.LastResourceError);
            Get(session.Document).ValidateMembers();
            var expected = PlacementAreaGeometry.Coverage(next.Nodes).SelectMany(s => Enumerable.Range(s.MinX, s.MaxX - s.MinX + 1)
                .SelectMany(x => PlacementAreaPlanner.Slots(next, x, s.Y))).ToHashSet();
            Assert.True(expected.SetEquals(Get(session.Document).Members.Select(m => m.Slot)));
        }
        area = Get(session.Document);
        var inserted = area with { Nodes = area.Nodes.Insert(1, new(Guid.NewGuid(), 350, 20)) };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, inserted, "insert")); Get(session.Document).ValidateMembers();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { Nodes = area.Nodes }, "remove"));
        Get(session.Document).ValidateMembers();
    }
    [Fact]
    public void Invalid_geometry_and_unsupported_member_edits_refuse_atomically_and_paths_are_preserved()
    {
        var session = Session(out var layer); var area = Area();
        var path = new PlacementPath { Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 100, 100)],
            Variants = area.Variants, Style = area.Style, Spacing = 20 };
        session.Execute(new PlacementPathCommand(layer.Id, path.Id, path, "path"));
        var pathObjects = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects.ToArray();
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "area")); var before = session.Document;
        area = Get(before); var item = DocumentEditor.FindObject(before, area.Members.First().ObjectId)!;
        session.Execute(new ReplaceObjectsCommand([item], [SceneObjectClone.At(item, 5, 5)], "bad"));
        Assert.Same(before, session.Document); Assert.Contains("Отделить", session.LastResourceError);
        session.Execute(new DeleteObjectsCommand(before, [item.Id])); Assert.Same(before, session.Document);
        session.Execute(new DuplicateLayerCommand(layer.Id)); Assert.Same(before, session.Document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area with { Density = int.MaxValue - 1d }, "resource refusal"));
        Assert.Same(before, session.Document); Assert.Equal(2, session.UndoDepth);
        Assert.Contains("памят", session.LastResourceError);
        var crossed = area with { Nodes = [area.Nodes[0], area.Nodes[2], area.Nodes[1], area.Nodes[3]] };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, crossed, "bad polygon"));
        Assert.Same(before, session.Document); Assert.Equal(2, session.UndoDepth);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area with { Seed = 828 }, "seed"));
        Assert.All(pathObjects, o => Assert.Same(o, DocumentEditor.FindObject(session.Document, o.Id)));
    }
    [Fact]
    public void Preview_is_bounded_samples_changed_far_edge_and_does_not_mutate_saved_document()
    {
        var area = Area() with { Density = 400 };
        var preview = PlacementAreaPlanner.Preview(area); Assert.Equal(64, preview.Count);
        Assert.True(preview.Max(o => o.X) > 1500); Assert.True(preview.Min(o => o.X) < 300);
        var next = area with { Nodes = area.Nodes.SetItem(1, area.Nodes[1] with { X = 1950 }) };
        var changed = PlacementAreaPlanner.Preview(next, area);
        Assert.NotEmpty(changed); Assert.True(changed.All(o => o.X + o.Width / 2 > 1600));
        Assert.Empty(area.Cells);
    }
    private static EditingSession Session(out ObjectLayer layer)
    {
        layer = new(LayerId.New(), "Objects", true, false, 1, []);
        var assets = new[] { Sha, Other }.Select(s => new MapAssetReference(s, "tree", "image/png", "png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "test", catalogId: s)).ToArray();
        return new(new MapDocument(DocumentId.New(), "Areas", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer], assets));
    }
    private static PlacementArea Area() => new() { Name = "Trees", Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 1900, 10),
        new(Guid.NewGuid(), 1900, 1900), new(Guid.NewGuid(), 10, 1900)], Density = 4, Seed = 18,
        Variants = [new(Sha, 32, 32), new(Other, 64, 32)], Style = new(Sha),
        Request = new() { BaseWidth = 24, RandomizeVariant = true, RandomFlip = true, RandomAngleStep = StampRandomAngleStep.Degrees30 } };
    private static PlacementArea Get(MapDocument document) => document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
}
