using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

/// <summary>The array-cell grid answers exactly like a brute-force scan, cold and after every delta.</summary>
public sealed class ObjectSpatialIndexEquivalenceTests
{
    [Fact]
    public void Queries_match_a_full_scan_across_moves_appends_undo_and_old_snapshots()
    {
        var random = new Random(7);
        SceneObject Random(string name)
        {
            var size = random.Next(10) == 0 ? random.Next(9000, 20000) : random.Next(4, 900);
            return new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, name,
                random.Next(-500, 30000), random.Next(-500, 30000), size, size * 0.7, random.Next(-180, 181),
                "#FFFFFFFF", "#FF000000", 1, true, false);
        }
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1,
            Enumerable.Range(0, 1500).Select(i => Random("o" + i)).ToArray());
        var document = new MapDocument(DocumentId.New(), "Test", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer]);
        var snapshots = new List<MapDocument> { document };
        AssertMatches(document, random);
        for (var step = 0; step < 30; step++)
        {
            var current = document.Layers.OfType<ObjectLayer>().Single();
            if (step % 3 == 2)
            {
                var command = new AddObjectsCommand(current.Id, [Random("a" + step), Random("b" + step)]);
                var added = command.Apply(document);
                AssertMatches(added, random);
                document = step % 2 == 0 ? command.Revert(added) : added;
            }
            else
            {
                var victim = current.Objects[random.Next(current.Objects.Count)];
                document = DocumentEditor.ReplaceObject(document,
                    MultiSelectionGeometry.Translate([victim], random.Next(-6000, 6000), random.Next(-6000, 6000))[0]);
            }
            AssertMatches(document, random);
            snapshots.Add(document);
        }
        foreach (var snapshot in snapshots) AssertMatches(snapshot, random);
    }

    private static void AssertMatches(MapDocument document, Random random)
    {
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var index = ObjectSpatialIndex.For(layer);
        for (var probe = 0; probe < 12; probe++)
        {
            var left = random.Next(-2000, 32000);
            var top = random.Next(-2000, 32000);
            var view = new WorldBounds(left, top, left + random.Next(1, 9000), top + random.Next(1, 9000));
            var expected = layer.Objects.Where(o => SceneGeometry.BoundsOf(o).Intersects(view)).Select(o => o.Id).ToArray();
            Assert.Equal(expected, index.Query(view).Select(o => o.Id).ToArray());
            var x = random.Next(0, 30000) + 0.5;
            var y = random.Next(0, 30000) + 0.5;
            var atPoint = layer.Objects.Where(o =>
            {
                var b = SceneGeometry.BoundsOf(o);
                return b.Left <= x && b.Right >= x && b.Top <= y && b.Bottom >= y;
            }).Select(o => o.Id).ToArray();
            Assert.Equal(atPoint, index.QueryPoint(x, y).Select(o => o.Id).ToArray());
            Assert.True(index.MayContainAtLeast(view, expected.Length));
        }
    }
}
