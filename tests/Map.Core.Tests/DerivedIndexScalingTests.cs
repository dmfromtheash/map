using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

[CollectionDefinition(nameof(DerivedIndexCounters), DisableParallelization = true)]
public sealed class DerivedIndexCounters;

/// <summary>
/// A click, a drag, a placement and their undo on a prepared map never rebuild a lookup over
/// every object: each new snapshot inherits identity slots, revision index, spatial grid and
/// name occupancy from its delta. The global build counters run in a non-parallel collection.
/// </summary>
[Collection(nameof(DerivedIndexCounters))]
public sealed class DerivedIndexScalingTests
{
    private static readonly string Hash = new('a', 64);

    private static SceneObject Stamp(int i, double x, double y, ObjectId? id = null, double? rotation = null, string? name = null) => new(id ?? ObjectId.New(), SceneObjectKind.Stamp,
        name ?? "Ассет " + i, x, y, 32, 24, rotation ?? i % 360 - 180, "#FFFFFFFF", "#00000000", 0, true, false)
    { Stamp = new StampStyle(Hash) };

    private static MapDocument Map(int count)
    {
        var random = new Random(12345);
        var objects = Enumerable.Range(0, count)
            .Select(i => Stamp(i, random.NextDouble() * 60000, random.NextDouble() * 60000)).ToArray();
        objects[^1] = Stamp(count - 1, 30000, 30000, objects[^1].Id, 0);
        var reference = new MapAssetReference(Hash, "Гора", "image/png", ".png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "user-owned", catalogId: Hash);
        return new MapDocument(DocumentId.New(), "Scaling", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65536, 65536, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)]).WithAssets([reference]);
    }

    private static (long Identity, long Names) Builds() => (SceneObjectList.IdentityRootBuilds, ObjectNameIndex.RootBuilds);

    [Theory]
    [InlineData(2_000)]
    [InlineData(40_000)]
    public void Prepared_select_drag_place_and_undo_build_no_full_lookup(int count)
    {
        var document = Map(count);
        DocumentDerivedIndexes.Prepare(document);
        Assert.True(DocumentDerivedIndexes.IsPrepared(document));
        var session = new EditingSession(document);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var anchor = layer.Objects[^1];
        var before = Builds();

        // Select: hit test and lookup of the clicked object.
        Assert.Equal(anchor.Id, SceneGeometry.HitTest(session.Document, 30016, 30012)?.Id);
        Assert.Equal(anchor, DocumentEditor.FindObject(session.Document, anchor.Id));
        // Drag.
        session.Execute(new ReplaceObjectsCommand([anchor], [Stamp(count - 1, anchor.X + 40, anchor.Y, anchor.Id, 0)], "Move"));
        // Place ten stamps, each named from the occupancy index.
        for (var k = 0; k < 10; k++)
        {
            var name = EntityNameGenerator.Objects(session.Document, SceneObjectKind.Stamp, 1, "Гора")[0];
            var placed = Stamp(count + k, 1000 + k * 64, 1000) with { Name = name };
            session.Execute(new PlaceAssetObjectsCommand(layer.Id, session.Document.Assets[0], [placed]));
            Assert.Equal(placed.Id, SceneGeometry.HitTest(session.Document, placed.X + 16, placed.Y + 12)?.Id);
        }
        Assert.True(DocumentDerivedIndexes.IsPrepared(session.Document));
        for (var k = 0; k < 11; k++) Assert.True(session.Undo());
        Assert.Equal(anchor, DocumentEditor.FindObject(session.Document, anchor.Id));
        Assert.Equal(anchor.Id, SceneGeometry.HitTest(session.Document, 30016, 30012)?.Id);

        Assert.Equal(before, Builds());
    }

    [Fact]
    public void Placed_names_follow_occupancy_exactly_as_a_fresh_index()
    {
        var document = Map(500);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var session = new EditingSession(document);
        var names = new List<string>();
        for (var k = 0; k < 5; k++)
        {
            var name = EntityNameGenerator.Objects(session.Document, SceneObjectKind.Stamp, 1, "Гора")[0];
            names.Add(name);
            session.Execute(new PlaceAssetObjectsCommand(layer.Id, session.Document.Assets[0],
                [Stamp(900 + k, 10 * k, 0) with { Name = name }]));
        }
        Assert.Equal(["Гора", "Гора 1", "Гора 2", "Гора 3", "Гора 4"], names);
        // Delete «Гора 2»: the next free number is that hole, in the derived and a fresh index alike.
        var hole = session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Single(o => o.Name == "Гора 2");
        session.Execute(new RemoveObjectsCommand(session.Document, layer.Id, [hole.Id]));
        var derived = EntityNameGenerator.Objects(session.Document, SceneObjectKind.Stamp, 2, "Гора");
        var fresh = session.Document;
        var rebuilt = new MapDocument(DocumentId.New(), "copy", DateTimeOffset.UnixEpoch, fresh.Canvas,
            [.. fresh.Layers.Select(l => l is ObjectLayer o ? new ObjectLayer(o.Id, o.Name, o.Visible, o.Locked, o.Opacity, [.. o.Objects]) : l)]);
        Assert.Equal(EntityNameGenerator.Objects(rebuilt, SceneObjectKind.Stamp, 2, "Гора"), derived);
        Assert.Equal("Гора 2", derived[0]);
    }

    [Fact]
    public void A_numbered_family_finds_its_first_hole_without_trying_every_number()
    {
        var random = new Random(3);
        for (var round = 0; round < 6; round++)
        {
            var taken = Enumerable.Range(1, 3000).Where(_ => random.Next(400) != 0).ToList();
            var names = taken.Select(n => (round % 2 == 0 ? "Гора " : "гора ") + n)
                .Append("Гора").Append("Гора 007").Append("Гора -3").Append("Гора x").ToArray();
            var document = new MapDocument(DocumentId.New(), "Names", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.New(), "Объекты", true, false, 1,
                    [.. names.Select((name, i) => Stamp(i, i, 0, name: name))])]);
            var occupied = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expected = Enumerable.Range(1, 4000).First(n => !occupied.Contains("Гора " + n));
            var twin = new MapDocument(DocumentId.New(), "Twin", DateTimeOffset.UnixEpoch, document.Canvas, document.Layers);
            Assert.Equal("Гора " + expected, EntityNameGenerator.Objects(twin, SceneObjectKind.Stamp, 1, "Гора")[0]);
            _ = EntityNameGenerator.Objects(document, SceneObjectKind.Stamp, 1, "Дуб"); // root built, «Гора» untouched

            // A removal below the root hole of a family the session never allocated from reopens
            // that number in the derived index (Register), as a fresh index of the result would.
            var layer = document.Layers.OfType<ObjectLayer>().Single();
            var lowNumber = taken.First(n => n >= 2 && n < expected);
            var low = layer.Objects.Single(o => o.Name.Equals("Гора " + lowNumber, StringComparison.OrdinalIgnoreCase));
            var other = new MapDocument(DocumentId.New(), "Names", DateTimeOffset.UnixEpoch, document.Canvas, document.Layers);
            var removed = new RemoveObjectsCommand(document, layer.Id, [low.Id]).Apply(other);
            ObjectNameIndex.Register(document, removed, [low], []);
            Assert.Equal("Гора " + lowNumber, EntityNameGenerator.Objects(removed, SceneObjectKind.Stamp, 1, "Гора")[0]);
        }
    }

    [Fact]
    public void Identity_slots_stay_exact_through_random_structural_edits()
    {
        var random = new Random(7);
        var list = SceneObjectList.From([.. Enumerable.Range(0, 3000).Select(i => Stamp(i, i, i))]);
        _ = list.IndexOfId(list[0].Id); // root table
        var gone = new HashSet<ObjectId>();
        for (var step = 0; step < 200; step++)
        {
            var at = random.Next(list.Count);
            switch (random.Next(5))
            {
                case 0: list = list.ReplaceAt(at, Stamp(step, -step, 0, list[at].Id)); break;
                case 1: list = list.InsertRange(at, [.. Enumerable.Range(0, random.Next(1, 600)).Select(i => Stamp(i, step, i))]); break;
                case 2:
                    var n = Math.Min(random.Next(1, 400), list.Count - at);
                    for (var i = 0; i < n; i++) gone.Add(list[at + i].Id);
                    list = list.RemoveRange(at, n); break;
                case 3: list = list.AppendRange([Stamp(step, 0, 0)]); break;
                default:
                    var many = new Dictionary<int, SceneObject>();
                    for (var i = 0; i < 20; i++) { var j = random.Next(list.Count); many[j] = Stamp(i, 0, -i, list[j].Id); }
                    list = list.ReplaceMany(many); break;
            }
            Assert.True(list.HasIdentityTable);
            if (step % 20 != 0) continue;
            for (var i = 0; i < list.Count; i++) Assert.Equal(i, list.IndexOfId(list[i].Id));
            Assert.All(gone.Where(id => !list.Any(o => o.Id == id)), id => Assert.Equal(-1, list.IndexOfId(id)));
        }
    }
}
