using Map.Core.Documents;

namespace Map.Core.Tests;

/// <summary>
/// A new record of an object layer that keeps its object list (mask, opacity, name changed) shares
/// the spatial index of the record before it; a changed object list gets an index of its own.
/// </summary>
public sealed class ObjectSpatialIndexSharingTests
{
    [Fact]
    public void A_layer_record_with_the_same_objects_shares_the_index()
    {
        SceneObject Stamp(int i) => new(ObjectId.New(), SceneObjectKind.Stamp, "Гора " + i, i * 40, i * 30, 32, 32, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(new string('a', 64)) };
        var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. Enumerable.Range(0, 50).Select(Stamp)]);
        var index = ObjectSpatialIndex.For(layer);

        var faded = layer with { Opacity = .5, Name = "Горы вдали" };
        Assert.Same(index, ObjectSpatialIndex.For(faded));

        var fewer = layer with { Objects = [.. layer.Objects.Skip(1)] };
        var other = ObjectSpatialIndex.For(fewer);
        Assert.NotSame(index, other);
        Assert.Equal(49, other.ObjectCount);
        Assert.DoesNotContain(layer.Objects[0], other.Query(new WorldBounds(-10, -10, 2000, 2000)));
    }
}
