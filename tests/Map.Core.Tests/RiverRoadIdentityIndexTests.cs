using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

public sealed class RiverRoadIdentityIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Membership_follows_replace_insert_remove_absorb_undo_and_redo(bool roads)
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(10, 20), new(80, 20)], 12)] };
        var road = RoadEditor.NewRoad([new(10, 60), new(80, 60)], 12, seed: 7);
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
            { Rivers = [river], Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "index", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), layer]);
        DocumentDerivedIndexes.Prepare(document);
        var originalIndex = Index(layer);
        var id = roads ? road.Id : river.Id;
        var movedRiver = RiverEditor.Translate(river, 5, 0);
        var movedRoad = RoadEditor.Translate(road, 5, 0);
        IEditCommand replacement = roads ? new RoadCommand(layer.Id, id, movedRoad, "replace")
            : new RiverCommand(layer.Id, id, movedRiver, "replace");
        var replaced = replacement.Apply(document);
        var after = (ObjectLayer)replaced.Layers[1];
        Assert.Same(originalIndex, Index(after));
        Assert.Equal(originalIndex.Version, Index(after).Version);
        Assert.Same(roads ? (object)layer.Rivers[0] : layer.Roads[0], roads ? (object)after.Rivers[0] : after.Roads[0]);
        Assert.True(DocumentDerivedIndexes.IsPrepared(replaced));
        Assert.Same(document, replacement.Revert(replaced));
        Assert.Same(replaced, replacement.Apply(document));

        var extraRiver = movedRiver with { Id = Guid.NewGuid() };
        var extraRoad = movedRoad with { Id = Guid.NewGuid() };
        var extraId = roads ? extraRoad.Id : extraRiver.Id;
        IEditCommand insertion = roads ? new RoadCommand(layer.Id, extraId, extraRoad, "insert")
            : new RiverCommand(layer.Id, extraId, extraRiver, "insert");
        var inserted = insertion.Apply(replaced);
        var insertedLayer = (ObjectLayer)inserted.Layers[1];
        Assert.True(Contains(insertedLayer, id)); Assert.True(Contains(insertedLayer, extraId));
        Assert.NotEqual(originalIndex.Version, Index(insertedLayer).Version);
        Assert.True(DocumentDerivedIndexes.IsPrepared(inserted));
        Assert.False(Contains(after, extraId));

        IEditCommand absorption = roads ? new RoadCommand(layer.Id, id, movedRoad, "absorb", extraId)
            : new RiverCommand(layer.Id, id, movedRiver, "absorb", extraId);
        var absorbed = absorption.Apply(inserted);
        Assert.False(Contains((ObjectLayer)absorbed.Layers[1], extraId));
        Assert.Same(inserted, absorption.Revert(absorbed));

        IEditCommand deletion = roads ? new RoadCommand(layer.Id, id, null, "delete")
            : new RiverCommand(layer.Id, id, null, "delete");
        var deleted = deletion.Apply(absorbed);
        Assert.False(Contains((ObjectLayer)deleted.Layers[1], id));
        Assert.True(Contains(layer, id));

        FeatureIdentityIndex Index(ObjectLayer value) => roads ? value.RoadIds : value.RiverIds;
        bool Contains(ObjectLayer value, Guid key) => roads ? value.ContainsRoad(key) : value.ContainsRiver(key);
    }

    [Fact]
    public void Metadata_copies_share_content_but_new_arrays_start_a_new_membership_version()
    {
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
            { Rivers = [new River()], Roads = [new Road()] };
        var clone = layer with { Name = "renamed" };
        Assert.Same(layer.RiverIds, clone.RiverIds); Assert.Same(layer.RoadIds, clone.RoadIds);
        var assigned = clone with { Rivers = [new River()], Roads = [new Road()] };
        Assert.NotEqual(layer.RiverIds.Version, assigned.RiverIds.Version);
        Assert.NotEqual(layer.RoadIds.Version, assigned.RoadIds.Version);
        Assert.False(assigned.ContainsRiver(layer.Rivers[0].Id));
        Assert.False(assigned.ContainsRoad(layer.Roads[0].Id));
        // Same serialized content, independently assigned: index ownership is not record equality.
        var equal = layer with { Rivers = [], Roads = [] };
        var equal2 = equal with { Rivers = layer.Rivers, Roads = layer.Roads };
        var hash = layer.GetHashCode();
        Assert.Equal(layer, equal2); Assert.Equal(hash, equal2.GetHashCode());
        layer.ContainsRiver(layer.Rivers[0].Id); layer.ContainsRoad(layer.Roads[0].Id);
        Assert.Equal(hash, layer.GetHashCode()); Assert.Equal(layer, equal2);
    }

    [Fact]
    public void Incomplete_known_deltas_fail_loudly()
    {
        var source = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
            { Rivers = [new River()], Roads = [new Road()] };
        var target = source with { Rivers = [], Roads = [] };
        Assert.Contains("version", Assert.Throws<InvalidOperationException>(() => target.RegisterRiverDelta(source, [], [])).Message);
        Assert.Contains("version", Assert.Throws<InvalidOperationException>(() => target.RegisterRoadDelta(source, [], [])).Message);
    }
}
