using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// Delete and move-to-layer, with their Undo and Redo, cost what they change: on a prepared map
/// no lookup over every object is rebuilt and the objects visited by enumeration do not grow
/// with the map. The results equal the old whole-list algorithm: order, instances, positions,
/// names, groups, spatial queries and asset usage after every step.
/// </summary>
[Collection(nameof(DerivedIndexCounters))]
public sealed class DeleteMoveScalingTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);

    private static SceneObject Stamp(int i, double x, double y, string? hash = null) => new(ObjectId.New(), SceneObjectKind.Stamp,
        "Гора " + i, x, y, 32, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false)
    { Stamp = new StampStyle(hash ?? Hash) };

    private static MapDocument Map(int count)
    {
        var random = new Random(12345);
        var objects = Enumerable.Range(0, count)
            .Select(i => Stamp(i, random.NextDouble() * 60000, random.NextDouble() * 60000, i % 7 == 0 ? OtherHash : null)).ToArray();
        return new MapDocument(DocumentId.New(), "Scaling", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65536, 65536, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects),
             new ObjectLayer(LayerId.New(), "Второй", true, false, 1, [])]).WithAssets(
            [Reference(Hash), Reference(OtherHash)]);
    }

    private static MapAssetReference Reference(string hash) => new(hash, "Гора", "image/png", ".png", ProjectAssetStorage.Linked,
        DateTimeOffset.UnixEpoch, "test", "user-owned", catalogId: hash);

    private static (long Identity, long Names, long Revisions) Builds() =>
        (SceneObjectList.IdentityRootBuilds, ObjectNameIndex.RootBuilds, DocumentRevisionIndex.RootBuilds);

    /// <summary>Runs every action on a prepared map; returns the full builds and objects enumerated.</summary>
    private static ((long, long, long) Builds, long Visited, long NameProbes) Run(int count)
    {
        var document = Map(count);
        DocumentDerivedIndexes.Prepare(document);
        var session = new EditingSession(document);
        var layers = document.Layers.OfType<ObjectLayer>().ToArray();
        var source = layers[0];
        var single = new[] { source.Objects[^1].Id };
        var spread = Enumerable.Range(0, 100).Select(k => source.Objects[k * count / 100].Id).ToArray();
        var probe = source.Objects[count / 2];

        void Look()
        {
            // The lookups the editor makes right after an edit: click, outline, names, panels.
            _ = DocumentEditor.FindObject(session.Document, probe.Id);
            _ = SceneGeometry.HitTest(session.Document, probe.X + 16, probe.Y + 12);
            _ = EntityNameGenerator.Objects(session.Document, SceneObjectKind.Stamp, 1, "Гора");
            foreach (var layer in session.Document.Layers.OfType<ObjectLayer>())
            {
                _ = AssetUsageIndex.For(layer)[Hash];
                _ = ObjectSpatialIndex.For(layer).Query(new WorldBounds(probe.X - 500, probe.Y - 500, probe.X + 500, probe.Y + 500));
                _ = BoundObjectIndex.For(layer);
            }
        }

        Look();
        var before = Builds();
        var visited = SceneObjectList.EnumeratedOnThisThread;
        var probes = ObjectNameIndex.ProbesOnThisThread;
        foreach (var ids in new[] { single, spread })
        {
            session.Execute(new DeleteObjectsCommand(session.Document, ids)); Look();
            Assert.True(session.Undo()); Look();
            Assert.True(session.Redo()); Look();
            Assert.True(session.Undo()); Look();
            session.Execute(new MoveObjectsToLayerCommand(session.Document, source.Id, layers[1].Id, ids)); Look();
            Assert.True(session.Undo()); Look();
            Assert.True(session.Redo()); Look();
            Assert.True(session.Undo()); Look();
        }

        var result = (Subtract(Builds(), before), SceneObjectList.EnumeratedOnThisThread - visited,
            ObjectNameIndex.ProbesOnThisThread - probes);
        Assert.True(DocumentDerivedIndexes.IsPrepared(session.Document));
        Assert.Equal(source.Objects, session.Document.Layers.OfType<ObjectLayer>().First().Objects);
        return result;
    }

    private static (long, long, long) Subtract((long, long, long) a, (long, long, long) b) =>
        (a.Item1 - b.Item1, a.Item2 - b.Item2, a.Item3 - b.Item3);

    [Fact]
    public void Delete_and_move_with_undo_redo_visit_the_same_objects_on_a_small_and_a_twenty_times_larger_map()
    {
        var small = Run(2_000);
        var large = Run(40_000);
        Assert.Equal((0L, 0L, 0L), small.Builds);
        Assert.Equal((0L, 0L, 0L), large.Builds);
        Assert.Equal(small.Visited, large.Visited);
        // Names «Гора 0…N-1» form one dense family: Undo of a delete must not walk it.
        Assert.Equal(small.NameProbes, large.NameProbes);
    }

    // ------------------------------------------------------------ equivalence with the old algorithm

    private static MapDocument Mixed(Random random, out LayerId[] layerIds, out Guid[] groups)
    {
        groups = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var lists = new List<SceneObject>[] { [], [], [] };
        for (var i = 0; i < 1500; i++)
        {
            var x = random.NextDouble() * 20000;
            var y = random.NextDouble() * 20000;
            SceneObject item = random.Next(10) switch
            {
                // Vector shapes and compositions own nested identities that a delete must release.
                0 => VectorGeometry.CreateFreeShape(ObjectId.New(), "Контур " + i,
                    [new(x, y), new(x + 300, y), new(x + 300, y + 200), new(x, y + 200)]),
                1 => Stamp(i, x, y) with
                {
                    Kind = SceneObjectKind.Composition, Stamp = null, Name = "Набор " + i,
                    Composition = new EditableComposition(CompositionId.New(), DocumentId.New(), "nested", 64, 64,
                        [Stamp(i, 0, 0) with { Name = "внутри" }]),
                },
                _ => Stamp(i % 300, x, y, random.Next(3) == 0 ? OtherHash : null),
            };
            if (random.Next(8) == 0)
            {
                var g = random.Next(groups.Length);
                item = item with { GroupId = groups[g], GroupName = "Группа " + g };
            }

            lists[random.Next(i < 900 ? 1 : 3)].Add(item);
        }

        layerIds = [LayerId.New(), LayerId.New(), LayerId.New()];
        return new MapDocument(DocumentId.New(), "Mixed", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(layerIds[0], "A", true, false, 1, lists[0]),
             new ObjectLayer(layerIds[1], "B", true, false, 1, lists[1]),
             new ObjectLayer(layerIds[2], "C", true, false, 1, lists[2])]).WithAssets([Reference(Hash), Reference(OtherHash)]);
    }

    private static SceneObject[][] Snapshot(MapDocument document) =>
        [.. document.Layers.OfType<ObjectLayer>().Select(layer => layer.Objects.ToArray())];

    /// <summary>The old algorithm on plain arrays: filter out, then append to the target.</summary>
    private static SceneObject[][] OldDeleteOrMove(SceneObject[][] layers, IReadOnlySet<ObjectId> ids, int? target)
    {
        var moved = new List<SceneObject>();
        var result = layers.Select((objects, li) =>
        {
            if (li == target) return objects;
            moved.AddRange(objects.Where(o => ids.Contains(o.Id)));
            return objects.Where(o => !ids.Contains(o.Id)).ToArray();
        }).ToArray();
        if (target is { } t) result[t] = [.. result[t], .. moved];
        return result;
    }

    private static void AssertSameOrder(SceneObject[][] expected, MapDocument document)
    {
        var actual = Snapshot(document);
        Assert.Equal(expected.Length, actual.Length);
        for (var li = 0; li < expected.Length; li++)
        {
            Assert.Equal(expected[li].Length, actual[li].Length);
            for (var i = 0; i < expected[li].Length; i++) Assert.Same(expected[li][i], actual[li][i]);
        }
    }

    /// <summary>Every derived lookup of the edited snapshot equals the one of a fresh rebuild.</summary>
    private static void AssertDerivedLikeFresh(MapDocument document, IReadOnlyCollection<ObjectId> everId, Guid[] groups, Random random)
    {
        var fresh = new MapDocument(DocumentId.New(), "fresh", DateTimeOffset.UnixEpoch, document.Canvas,
            [.. document.Layers.Select(l => l is ObjectLayer o ? o with { Objects = SceneObjectList.From([.. o.Objects]) } : l)])
            .WithAssets(document.Assets);
        var index = DocumentRevisionIndex.For(document);
        var freshIndex = DocumentRevisionIndex.For(fresh);
        Assert.Equal(freshIndex.ObjectCount, index.ObjectCount);
        foreach (var id in everId)
        {
            Assert.Equal(freshIndex.TryFind(id, out var expected), index.TryFind(id, out var actual));
            Assert.Same(expected.Object, actual.Object);
            Assert.Equal(expected.LayerId, actual.LayerId);
            Assert.Equal(expected.ObjectIndex, actual.ObjectIndex);
            Assert.Equal(DocumentEditor.FindLayerOf(fresh, id)?.Id, DocumentEditor.FindLayerOf(document, id)?.Id);
        }

        foreach (var group in groups)
            Assert.Equal(freshIndex.GroupMembers(group).Select(o => o.Id), index.GroupMembers(group).Select(o => o.Id));
        Assert.Equal(EntityNameGenerator.Objects(fresh, SceneObjectKind.Stamp, 3, "Гора"),
            EntityNameGenerator.Objects(document, SceneObjectKind.Stamp, 3, "Гора"));
        for (var li = 0; li < document.Layers.Count; li++)
        {
            if (document.Layers[li] is not ObjectLayer layer) continue;
            var freshLayer = (ObjectLayer)fresh.Layers[li];
            Assert.Equal(AssetUsageIndex.For(freshLayer)[Hash], AssetUsageIndex.For(layer)[Hash]);
            Assert.Equal(AssetUsageIndex.For(freshLayer)[OtherHash], AssetUsageIndex.For(layer)[OtherHash]);
            Assert.Equal(AssetUsageIndex.For(freshLayer).StampCount, AssetUsageIndex.For(layer).StampCount);
            for (var i = 0; i < layer.Objects.Count; i++) Assert.Equal(i, layer.ObjectStorage.IndexOfId(layer.Objects[i].Id));
            for (var q = 0; q < 4; q++)
            {
                var x = random.NextDouble() * 20000;
                var y = random.NextDouble() * 20000;
                var view = new WorldBounds(x, y, x + 3000, y + 3000);
                Assert.Equal(ObjectSpatialIndex.For(freshLayer).Query(view).Select(o => o.Id),
                    ObjectSpatialIndex.For(layer).Query(view).Select(o => o.Id));
                var hit = layer.Objects.Count > 0 ? layer.Objects[random.Next(layer.Objects.Count)] : null;
                if (hit is null) continue;
                Assert.Equal(ObjectSpatialIndex.For(freshLayer).QueryPoint(hit.X + 1, hit.Y + 1).Select(o => o.Id),
                    ObjectSpatialIndex.For(layer).QueryPoint(hit.X + 1, hit.Y + 1).Select(o => o.Id));
            }
            Assert.Equal(BoundObjectIndex.For(freshLayer).Decorations.Count, BoundObjectIndex.For(layer).Decorations.Count);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_deletes_moves_drags_and_history_equal_the_old_algorithm_and_fresh_lookups(int seed)
    {
        var random = new Random(seed);
        var document = Mixed(random, out var layerIds, out var groups);
        DocumentDerivedIndexes.Prepare(document);
        var session = new EditingSession(document);
        var everId = document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => o.Id).ToHashSet();
        var history = new Stack<SceneObject[][]>();
        history.Push(Snapshot(document));
        var redo = new Stack<SceneObject[][]>();

        for (var step = 0; step < 60; step++)
        {
            var all = session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
            var choice = random.Next(10);
            if (choice < 2 && session.CanUndo)
            {
                Assert.True(session.Undo());
                redo.Push(history.Pop());
                AssertSameOrder(history.Peek(), session.Document);
            }
            else if (choice < 3 && session.CanRedo)
            {
                Assert.True(session.Redo());
                history.Push(redo.Pop());
                AssertSameOrder(history.Peek(), session.Document);
            }
            else if (choice < 5 && all.Length > 0)
            {
                // Drag: a replacement between the structural edits.
                var item = all[random.Next(all.Length)];
                session.Execute(new ReplaceObjectsCommand([item], [SceneObjectClone.At(item, item.X + 40, item.Y - 10)], "drag"));
                history.Push(Snapshot(session.Document));
                redo.Clear();
            }
            else if (all.Length > 0)
            {
                var ids = Enumerable.Range(0, random.Next(1, 40)).Select(_ => all[random.Next(all.Length)].Id).ToHashSet();
                var expectedBefore = Snapshot(session.Document);
                var move = random.Next(2) == 0;
                SceneObject[][] expected;
                if (move)
                {
                    // The editor's composite: one move per source layer, each appending in turn.
                    var target = random.Next(3);
                    var bySource = ids.Select(id => (Id: id, Layer: DocumentEditor.FindLayerOf(session.Document, id)!.Id))
                        .Where(pair => pair.Layer != layerIds[target]).GroupBy(pair => pair.Layer).ToList();
                    if (bySource.Count == 0) continue;
                    var steps = bySource.Select(g => (IEditCommand)new MoveObjectsToLayerCommand(
                        session.Document, g.Key, layerIds[target], [.. g.Select(pair => pair.Id)])).ToList();
                    expected = expectedBefore;
                    foreach (var g in bySource) expected = OldDeleteOrMove(expected, g.Select(pair => pair.Id).ToHashSet(), target);
                    session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("move", steps));
                }
                else
                {
                    expected = OldDeleteOrMove(expectedBefore, ids, null);
                    var deleted = new DeleteObjectsCommand(session.Document, ids);
                    session.Execute(deleted);
                    // A group left with one member loses its identity inside the same entry.
                    expected = [.. expected.Select(l => l.Select(o => DocumentEditor.FindObject(session.Document, o.Id)!).ToArray())];
                }

                AssertSameOrder(expected, session.Document);
                history.Push(Snapshot(session.Document));
                redo.Clear();
            }

            foreach (var item in session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)) everId.Add(item.Id);
            AssertDerivedLikeFresh(session.Document, everId, groups, random);
        }

        while (session.Undo()) { history.Pop(); }
        AssertSameOrder(history.Peek(), session.Document);
        AssertDerivedLikeFresh(session.Document, everId, groups, random);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Identity_slots_follow_every_storage_edit_by_chunk_and_stay_exact(int seed)
    {
        // Chunk-level forwarding: after any mix of edits each id names its position, and every
        // id that left the list (or was replaced by another id at its place) names nothing.
        var random = new Random(seed);
        var list = SceneObjectList.From([.. Enumerable.Range(0, 3000).Select(i => Stamp(i, i, i))]);
        _ = list.IndexOfId(list[0].Id);
        var gone = new HashSet<ObjectId>();
        var history = new List<SceneObjectList> { list };
        for (var step = 0; step < 300; step++)
        {
            var at = random.Next(Math.Max(1, list.Count));
            switch (random.Next(8))
            {
                case 0 when list.Count > 0:
                    var positions = Enumerable.Range(0, random.Next(1, 60)).Select(_ => random.Next(list.Count)).Distinct().Order().ToArray();
                    foreach (var p in positions) gone.Add(list[p].Id);
                    list = list.RemovePositions(positions);
                    break;
                case 1:
                    var batches = new Dictionary<int, IReadOnlyList<SceneObject>>();
                    for (var b = random.Next(1, 30); b > 0; b--)
                        batches[random.Next(list.Count + 1)] = [.. Enumerable.Range(0, random.Next(1, 4)).Select(i => Stamp(i, step, b))];
                    list = list.InsertMany(batches);
                    break;
                case 2 when list.Count > 0:
                    var many = new Dictionary<int, SceneObject>();
                    for (var i = 0; i < 25; i++)
                    {
                        var j = random.Next(list.Count);
                        // Every third replacement brings a new identity to the place.
                        if (i % 3 == 0) { if (!many.ContainsKey(j)) gone.Add(list[j].Id); many[j] = Stamp(i, 0, 0); }
                        else if (!many.ContainsKey(j)) many[j] = Stamp(i, 0, -i) with { Id = list[j].Id };
                    }
                    list = list.ReplaceMany(many);
                    break;
                case 3 when list.Count > 0:
                    var n = Math.Min(random.Next(1, 700), list.Count - at);
                    for (var i = 0; i < n; i++) gone.Add(list[at + i].Id);
                    list = list.RemoveRange(at, n);
                    break;
                case 4: list = list.InsertRange(Math.Min(at, list.Count), [.. Enumerable.Range(0, random.Next(1, 600)).Select(i => Stamp(i, step, i))]); break;
                case 5: list = list.AppendRange([Stamp(step, 0, 0)]); break;
                case 6 when list.Count > 0: list = list.ReplaceAt(at, Stamp(step, -step, 0) with { Id = list[at].Id }); break;
                default:
                    // Branch from an older version, as Undo and a new edit do.
                    list = history[random.Next(history.Count)];
                    gone.Clear();
                    break;
            }

            history.Add(list);
            if (list.Count > 0) Assert.True(list.HasIdentityTable);
            if (step % 15 != 0) continue;
            var present = new HashSet<ObjectId>();
            for (var i = 0; i < list.Count; i++) { Assert.Equal(i, list.IndexOfId(list[i].Id)); present.Add(list[i].Id); }
            foreach (var id in gone) if (!present.Contains(id)) Assert.Equal(-1, list.IndexOfId(id));
        }

        // Every version kept its own exact answers.
        foreach (var version in history.Where((_, i) => i % 37 == 0))
            for (var i = 0; i < version.Count; i++) Assert.Equal(i, version.IndexOfId(version[i].Id));
    }

    [Fact]
    public void Undo_of_deleting_a_vector_shape_and_a_composition_restores_their_nested_identities()
    {
        var shape = VectorGeometry.CreateFreeShape(ObjectId.New(), "Контур", [new(0, 0), new(300, 0), new(300, 200)]);
        var composition = Stamp(1, 500, 500) with
        {
            Kind = SceneObjectKind.Composition, Stamp = null,
            Composition = new EditableComposition(CompositionId.New(), DocumentId.New(), "nested", 64, 64, [Stamp(2, 0, 0)]),
        };
        var filler = Enumerable.Range(0, 600).Select(i => Stamp(i, i * 10, 900)).ToArray();
        var layerId = LayerId.New();
        var other = LayerId.New();
        var document = new MapDocument(DocumentId.New(), "Nested", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(layerId, "A", true, false, 1, [.. filler.Take(300), shape, .. filler.Skip(300), composition]),
             new ObjectLayer(other, "B", true, false, 1, [])]).WithAssets([Reference(Hash)]);
        DocumentDerivedIndexes.Prepare(document);
        var session = new EditingSession(document);
        for (var round = 0; round < 3; round++)
        {
            session.Execute(new DeleteObjectsCommand(session.Document, [shape.Id, composition.Id]));
            Assert.Null(DocumentEditor.FindObject(session.Document, shape.Id));
            Assert.True(session.Undo());
            Assert.Same(shape, DocumentEditor.FindObject(session.Document, shape.Id));
            Assert.Equal(300, DocumentRevisionIndex.For(session.Document).TryFind(shape.Id, out var at) ? at.ObjectIndex : -1);
            session.Execute(new MoveObjectsToLayerCommand(session.Document, layerId, other, [shape.Id, composition.Id]));
            Assert.Equal(other, DocumentEditor.FindLayerOf(session.Document, composition.Id)!.Id);
            Assert.True(session.Undo());
            Assert.True(session.Redo());
            Assert.True(session.Undo());
        }

        // A copy that reuses the composition's nested ids is still refused while the original lives.
        var clash = composition with { Id = ObjectId.New() };
        Assert.Throws<ArgumentException>(() => new AddObjectCommand(layerId, clash).Apply(session.Document));
        AssertSameOrder(Snapshot(document), session.Document);
    }
}
