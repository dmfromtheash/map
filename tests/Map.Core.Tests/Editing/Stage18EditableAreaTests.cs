using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage18EditableAreaTests
{
    [Theory]
    [InlineData(18)]
    [InlineData(-184929685)]
    [InlineData(718)]
    public void Natural_groups_keep_spacing_scale_variation_and_exact_survivors_after_density_and_edge_edits(int seed)
    {
        var session = Session(out var layer);
        var area = Area() with { LayoutVersion = 3, Seed = seed, Density = 20, CompositionOrigin = new(860, 840),
            Variants = [new(Sha, 128, 128), new(Other, 128, 96)],
            Request = new() { BaseWidth = 112, RandomizeVariant = true, RandomAngleStep = StampRandomAngleStep.Degrees15 },
            Style = new(Sha) { Blur = 1, ShadowOpacity = .3, ShadowOffsetX = 4, ShadowOffsetY = 5 } };
        var preview = PlacementAreaPlanner.Preview(area);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "natural"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        var before = session.Document;
        var objects = ((ObjectLayer)before.FindLayer(layer.Id)!).Objects;
        Assert.True(objects.Count > 3, $"Sparse 20% fixture needs several neighbours, got {objects.Count}");
        Assert.False(HasOverlap(objects));
        Assert.All(objects, item => Assert.InRange(item.Width / area.Request.BaseWidth, .7, 1.3));
        Assert.True(objects.Max(o => o.Width) - objects.Min(o => o.Width) > area.Request.BaseWidth * .12);
        Assert.All(preview, item => Assert.Equal(item, DocumentEditor.FindObject(before, item.Id)));
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(before) with { Density = 60 }, "density"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.All(objects, item => Assert.Same(item, DocumentEditor.FindObject(session.Document, item.Id)));
        var dense = session.Document;
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(dense) with {
            Nodes = area.Nodes.SetItem(1, area.Nodes[1] with { X = 1780 }) }, "edge"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.Equal(area.CompositionOrigin, Get(session.Document).CompositionOrigin);
        Assert.All(Get(session.Document).Members.Where(m => DocumentEditor.FindObject(dense, m.ObjectId) is not null),
            m => Assert.Same(DocumentEditor.FindObject(dense, m.ObjectId), DocumentEditor.FindObject(session.Document, m.ObjectId)));
        Assert.False(HasOverlap(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects));
        var edited = session.Document; session.Undo(); Assert.Same(dense, session.Document);
        session.Redo(); Assert.Same(edited, session.Document);
        var member = Get(edited).Members.First();
        session.Execute(new ManualAreaSelectionCommand(new DeleteObjectsCommand(edited, [member.ObjectId]), [member.ObjectId]));
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(session.Document) with { Seed = seed + 99 }, "another"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.Null(DocumentEditor.FindObject(session.Document, member.ObjectId));
        Assert.False(HasOverlap(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects));
        Assert.True(PlacementAreaSampler.EstimateBytes(area, PlacementAreaGeometry.Coverage(area.Nodes)) < 32 * 1024 * 1024);
    }

    [Fact]
    public void Natural_density_grows_the_final_population_through_its_full_range_and_size_variation_can_be_disabled()
    {
        var session = Session(out var layer);
        var area = Area() with { LayoutVersion = 3, Density = 0, CompositionOrigin = new(900, 900),
            Variants = [new(Sha, 128, 128)], Request = new() { BaseWidth = 110 } };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "empty"));
        Assert.Empty(Get(session.Document).Members);
        var previous = session.Document;
        foreach (var density in new[] { 20, 50, 80, 100 })
        {
            session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(previous) with { Density = density }, "density"));
            Assert.Null(session.LastResourceError);
            Assert.True(Get(session.Document).MemberCount > Get(previous).MemberCount,
                $"Density {density}% must add objects: {Get(previous).MemberCount} -> {Get(session.Document).MemberCount}");
            Assert.All(Get(previous).Members, m => Assert.Same(DocumentEditor.FindObject(previous, m.ObjectId),
                DocumentEditor.FindObject(session.Document, m.ObjectId)));
            previous = session.Document;
        }
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(previous) with { SizeVariationPercent = 0 }, "equal sizes"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        var items = ((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects;
        Assert.All(items, o => Assert.Equal(110, o.Width)); Assert.False(HasOverlap(items));
    }

    [Fact]
    public void Composition_origin_is_inside_a_concave_polygon_even_when_its_centroid_is_outside()
    {
        PlacementPathNode[] nodes = [new(Guid.NewGuid(), 0, 0), new(Guid.NewGuid(), 900, 0), new(Guid.NewGuid(), 900, 100),
            new(Guid.NewGuid(), 100, 100), new(Guid.NewGuid(), 100, 900), new(Guid.NewGuid(), 0, 900)];
        var origin = PlacementAreaPlanner.CompositionOrigin(nodes);
        Assert.True(double.IsFinite(origin.X) && double.IsFinite(origin.Y));
        Assert.True(PlacementAreaGeometry.Contains(nodes, origin.X, origin.Y));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(-184929685)]
    public void Compact_layout_fills_more_real_spaces_without_overlap_or_moving_survivors(int seed)
    {
        var legacy = Area() with { Density = 12, Seed = seed, Request = new() { BaseWidth = 110 },
            Variants = [new(Sha, 128, 128)],
            Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 1100, 10), new(Guid.NewGuid(), 550, 660)] };
        var compact = legacy with { LayoutVersion = 2 };
        SceneObject[] Fill(PlacementArea area)
        {
            var sampler = new PlacementAreaSampler(area);
            return PlacementAreaGeometry.Coverage(area.Nodes).SelectMany(s => Enumerable.Range(s.MinX, s.MaxX - s.MinX + 1)
                .SelectMany(x => sampler.Slots(x, s.Y))).Select(sampler.Item).ToArray();
        }
        var oldObjects = Fill(legacy); var objects = Fill(compact);
        Assert.True(objects.Length > oldObjects.Length, $"Compact {objects.Length}, legacy {oldObjects.Length}");
        Assert.False(HasOverlap(objects));
        Assert.All(objects, item => Assert.True(PlacementAreaGeometry.Contains(compact.Nodes,
            item.X + item.Width / 2, item.Y + item.Height / 2)));

        var session = Session(out var layer);
        session.Execute(new PlacementAreaCommand(layer.Id, compact.Id, compact, "compact"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        var before = session.Document; var areaBefore = Get(before);
        session.Execute(new PlacementAreaCommand(layer.Id, compact.Id, areaBefore with { Density = 16 }, "density"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.All(areaBefore.Members, member => Assert.Same(DocumentEditor.FindObject(before, member.ObjectId),
            DocumentEditor.FindObject(session.Document, member.ObjectId)));
        var dense = session.Document;
        session.Execute(new PlacementAreaCommand(layer.Id, compact.Id, Get(dense) with {
            Nodes = compact.Nodes.SetItem(1, compact.Nodes[1] with { X = 1000 }) }, "edge"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.All(Get(session.Document).Members.Where(m => DocumentEditor.FindObject(dense, m.ObjectId) is not null),
            member => Assert.Same(DocumentEditor.FindObject(dense, member.ObjectId), DocumentEditor.FindObject(session.Document, member.ObjectId)));
        session.Undo(); Assert.Same(dense, session.Document);
    }

    [Fact]
    public void Manual_batch_detach_and_delete_is_atomic_and_exclusions_survive_regeneration()
    {
        var session = Session(out var layer); var area = Area() with { LayoutVersion = 2 };
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "area"));
        var original = session.Document; var stored = Get(original); var members = stored.Members.Take(5).ToArray();
        var ids = members.Select(m => m.ObjectId).ToArray();
        session.Execute(new ManualAreaSelectionCommand(new DeleteObjectsCommand(original, ids), ids));
        Assert.Null(session.LastResourceError); Assert.Equal(2, session.UndoDepth);
        var deleted = session.Document;
        Assert.Equal(stored.MemberCount - ids.Length, Get(deleted).MemberCount);
        Assert.All(ids, id => Assert.Null(DocumentEditor.FindObject(deleted, id)));
        Assert.All(members, m => Assert.Contains(m.Slot, Get(deleted).Exclusions));
        Assert.All(Get(deleted).Members, m => Assert.Same(DocumentEditor.FindObject(original, m.ObjectId), DocumentEditor.FindObject(deleted, m.ObjectId)));
        Get(deleted).ValidateMembers();
        session.Undo(); Assert.Same(original, session.Document); session.Redo(); Assert.Same(deleted, session.Document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, Get(deleted) with { Density = 8, Seed = 98 }, "regenerate"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.All(ids, id => Assert.Null(DocumentEditor.FindObject(session.Document, id)));
    }

    private static readonly string Sha = new('a', 64);
    private static readonly string Other = new('b', 64);
    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public void Large_mountains_preview_place_and_validate_without_reserving_the_dependency_halo(int density)
    {
        var session = Session(out var layer);
        var area = Area() with { Name = "Mountains", Density = density, Seed = -184929685,
            Nodes = [new(Guid.NewGuid(), 100, 100), new(Guid.NewGuid(), 900, 100), new(Guid.NewGuid(), 500, 670)],
            Variants = [new(Sha, 1254, 1254)], ScalePercent = 129, RotationDegrees = 359,
            Request = new() { BaseWidth = 256 * 1.29 }, Style = new(Sha) { FlipHorizontal = true } };
        var estimate = PlacementAreaSampler.EstimateBytes(area, PlacementAreaGeometry.Coverage(area.Nodes));
        Assert.InRange(estimate, 1, 32 * 1024 * 1024);
        var original = session.Document; var preview = PlacementAreaPlanner.Preview(area);
        Assert.NotEmpty(preview); Assert.InRange(preview.Count, 1, 64); Assert.Same(original, session.Document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "mountains"));
        Assert.Null(session.LastResourceError); Assert.Equal(1, session.UndoDepth);
        Assert.All(preview, o => Assert.Equal(o, DocumentEditor.FindObject(session.Document, o.Id)));
        Get(session.Document).ValidateMembers(); Assert.False(HasOverlap(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects));
        var placed = session.Document; session.Undo(); Assert.Same(original, session.Document);
        session.Redo(); Assert.Same(placed, session.Document);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Evicting_candidate_cache_keeps_seeded_slot_decisions_and_items_exact(int layoutVersion)
    {
        var area = Area() with { Density = 20, LayoutVersion = layoutVersion };
        var sampler = new PlacementAreaSampler(area);
        var slots = Enumerable.Range(0, 20).SelectMany(n => Enumerable.Range(0, 12).Select(x => new PlacementAreaSlot(x, 2, n))).ToArray();
        var included = slots.Select(sampler.Includes).ToArray(); var items = slots.Select(sampler.Item).ToArray();
        for (var x = 0; x <= PlacementAreaSampler.MaximumCachedCandidates; x++) sampler.Item(new(x + 10000, 0, 0));
        Assert.Equal(PlacementAreaSampler.MaximumCachedCandidates, sampler.CachedCandidateCount);
        Assert.Equal(included, slots.Select(sampler.Includes)); Assert.Equal(items, slots.Select(sampler.Item));
        Assert.InRange(sampler.CachedCandidateCount, 1, PlacementAreaSampler.MaximumCachedCandidates);
    }

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
            var sampler = new PlacementAreaSampler(next);
            var expected = PlacementAreaGeometry.Coverage(next.Nodes).SelectMany(s => Enumerable.Range(s.MinX, s.MaxX - s.MinX + 1)
                .SelectMany(x => sampler.Slots(x, s.Y))).ToHashSet();
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
        var area = Area() with { Density = 8 };
        var preview = PlacementAreaPlanner.Preview(area); Assert.Equal(64, preview.Count);
        Assert.True(preview.Max(o => o.X) > 1500); Assert.True(preview.Min(o => o.X) < 300);
        var next = area with { Nodes = area.Nodes.SetItem(1, area.Nodes[1] with { X = 1950 }) };
        var changed = PlacementAreaPlanner.Preview(next, area);
        Assert.NotEmpty(changed); Assert.True(changed.All(o => o.X + o.Width / 2 > 1600));
        Assert.Empty(area.Cells);
    }
    [Fact]
    public void Repack_removes_overlaps_without_moving_survivors_and_respects_other_stamps_after_seed_scale_density()
    {
        var session = Session(out var layer);
        var area = Area() with { LayoutVersion = 0, Density = 6,
            Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 650, 10),
                new(Guid.NewGuid(), 650, 650), new(Guid.NewGuid(), 10, 650)],
            Request = Area().Request with { BaseWidth = 65 } };
        var obstacle = PlacementAreaPlanner.Item(area, new(2, 2, 0)) with { Id = ObjectId.New(), Name = "Manual tree" };
        session.Execute(new AddObjectCommand(layer.Id, obstacle));
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area, "legacy"));
        var legacy = session.Document; area = Get(legacy);
        Assert.True(HasOverlap(((ObjectLayer)legacy.FindLayer(layer.Id)!).Objects));
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area with { LayoutVersion = 1 }, "repack"));
        Assert.Null(session.LastResourceError); var spaced = session.Document; var packed = Get(spaced);
        Assert.InRange(packed.MemberCount, 10, area.MemberCount - 1); packed.ValidateMembers();
        Assert.False(HasOverlap(((ObjectLayer)spaced.FindLayer(layer.Id)!).Objects));
        Assert.All(packed.Members, m => Assert.Same(DocumentEditor.FindObject(legacy, m.ObjectId), DocumentEditor.FindObject(spaced, m.ObjectId)));
        session.Undo(); Assert.Same(legacy, session.Document); session.Redo(); Assert.Same(spaced, session.Document);
        foreach (var density in new[] { 8, 2, 6 })
        {
            var previous = session.Document; var old = Get(previous);
            session.Execute(new PlacementAreaCommand(layer.Id, area.Id, old with { Density = density }, "density"));
            Assert.Null(session.LastResourceError);
            if (density >= old.Density) Assert.All(old.Members, m => Assert.Same(DocumentEditor.FindObject(previous, m.ObjectId), DocumentEditor.FindObject(session.Document, m.ObjectId)));
            Assert.False(HasOverlap(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects));
        }
        area = Get(session.Document);
        session.Execute(new PlacementAreaCommand(layer.Id, area.Id, area with { Seed = 17, Request = area.Request with { BaseWidth = 90 } }, "larger seed"));
        Assert.Null(session.LastResourceError); Get(session.Document).ValidateMembers();
        Assert.False(HasOverlap(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects));
        Assert.Same(obstacle, DocumentEditor.FindObject(session.Document, obstacle.Id));
    }
    private static bool HasOverlap(IReadOnlyList<SceneObject> objects)
    {
        for (var i = 0; i < objects.Count; i++)
            for (var j = i + 1; j < objects.Count; j++)
                if (PlacementAreaSampler.Overlaps(SceneGeometry.BoundsOf(objects[i]), SceneGeometry.BoundsOf(objects[j]))) return true;
        return false;
    }
    private static EditingSession Session(out ObjectLayer layer)
    {
        layer = new(LayerId.New(), "Objects", true, false, 1, []);
        var assets = new[] { Sha, Other }.Select(s => new MapAssetReference(s, "tree", "image/png", "png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "test", catalogId: s)).ToArray();
        return new(new MapDocument(DocumentId.New(), "Areas", DateTimeOffset.UnixEpoch, new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer], assets));
    }
    private static PlacementArea Area() => new() { Name = "Trees", LayoutVersion = 1, Nodes = [new(Guid.NewGuid(), 10, 10), new(Guid.NewGuid(), 1900, 10),
        new(Guid.NewGuid(), 1900, 1900), new(Guid.NewGuid(), 10, 1900)], Density = 4, Seed = 18,
        Variants = [new(Sha, 32, 32), new(Other, 64, 32)], Style = new(Sha),
        Request = new() { BaseWidth = 24, RandomizeVariant = true, RandomFlip = true, RandomAngleStep = StampRandomAngleStep.Degrees30 } };
    private static PlacementArea Get(MapDocument document) => document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
}
