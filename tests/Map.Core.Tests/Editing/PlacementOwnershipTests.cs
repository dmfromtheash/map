using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class PlacementOwnershipTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Point_edits_and_layer_metadata_share_membership_beyond_the_retained_delta_window()
    {
        var document = Fixture(out var layerId);
        var layer = (ObjectLayer)document.FindLayer(layerId)!;
        var ownership = PlacementOwnershipIndex.For(layer);
        var item = layer.Objects.First(o => o.Kind == SceneObjectKind.Rectangle);
        for (var step = 0; step < 140; step++)
        {
            item = MultiSelectionGeometry.Translate([item], 1, 0)[0];
            var after = DocumentEditor.ReplaceObject(document, item);
            Assert.Same(ownership, PlacementOwnershipIndex.For((ObjectLayer)after.FindLayer(layerId)!));
            Assert.True(PlacementAreaOwnership.Allows(document, after, out _));
            Assert.True(PlacementPathOwnership.Allows(document, after, out _));
            document = after;
        }
        document = document.WithLayer(layerId, l => l with { Name = "Renamed" });
        Assert.Same(ownership, PlacementOwnershipIndex.For((ObjectLayer)document.FindLayer(layerId)!));
        // A no-op command can produce the very same membership arrays and pre-shared cache.
        var missingArea = new PlacementArea { Variants = [new(Sha, 32, 32)] };
        var noOp = new PlacementAreaCommand(layerId, missingArea.Id, missingArea, "bake missing", bake: true).Apply(document);
        CheckOwners(noOp);
    }

    [Fact]
    public void Random_edits_match_full_guards_and_membership_across_history_and_unknown_revisions()
    {
        var document = Fixture(out var layerId);
        var random = new Random(47019);
        for (var step = 0; step < 600; step++)
        {
            var layer = (ObjectLayer)document.FindLayer(layerId)!;
            var item = layer.Objects[random.Next(layer.Objects.Count)];
            var replacement = MultiSelectionGeometry.Translate([item], 3, 0)[0] with { Name = "edit " + step };
            IEditCommand command = (step % 10) switch
            {
                0 => new ReplaceObjectsCommand([item], [replacement], "move"),
                1 => new DeleteObjectsCommand(document, [item.Id]),
                2 => new ManualAreaSelectionCommand(new ReplaceObjectsCommand([item], [replacement], "manual"), [item.Id]),
                3 => new ManualAreaSelectionCommand(new DeleteObjectsCommand(document, [item.Id]), [item.Id]),
                4 => new PlacementAreaCommand(layerId, layer.PlacementAreas[0].Id,
                    layer.PlacementAreas[0] with { Seed = step }, "seed"),
                5 => new PlacementPathCommand(layerId, layer.PlacementPaths[0].Id,
                    layer.PlacementPaths[0] with { Spacing = 20 + random.Next(10) }, "spacing"),
                6 => new MoveObjectsToLayerCommand(document, layerId, document.Layers.OfType<ObjectLayer>().Last().Id, [item.Id]),
                7 => new AddObjectsCommand(layerId, [Loose(step)]),
                8 => new CompositeCommand("round trip", [new ReplaceObjectsCommand([item], [replacement], "out"),
                    new ReplaceObjectsCommand([replacement], [item], "back")]),
                _ => new ReplaceObjectsCommand([item], [item with { }], "equal record"),
            };
            var candidate = command.Apply(document);
            Check(document, candidate);
            // An unlinked snapshot must use the safe full comparison, including cross-layer moves.
            if (step % 13 == 0)
            {
                var unlinkedLayer = layer with { Objects = layer.Objects.Select(o => o.Id == item.Id ? replacement : o).ToArray() };
                var unlinked = document.WithRevisedLayers(document.Layers.Select(l => l.Id == layerId ? unlinkedLayer : l).ToArray());
                Check(document, unlinked);
            }
            if (FullAllows(document, candidate, true) && FullAllows(document, candidate, false))
            {
                CheckOwners(candidate);
                var reverted = command.Revert(candidate);
                CheckOwners(reverted);
                // Some generic commands reconstruct a revision on redo rather than caching it.
                candidate = command.Apply(reverted);
                CheckOwners(candidate);
                document = candidate;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bake_delete_detach_and_regeneration_keep_both_owner_indexes(bool area)
    {
        var document = Fixture(out var layerId);
        var layer = (ObjectLayer)document.FindLayer(layerId)!;
        var member = area ? layer.PlacementAreas[0].Members.First().ObjectId : layer.PlacementPaths[0].Members[0].ObjectId;
        var id = area ? layer.PlacementAreas[0].Id : layer.PlacementPaths[0].Id;
        IEditCommand Detach() => area
            ? new PlacementAreaCommand(layerId, id, null, "detach", detach: member)
            : new PlacementPathCommand(layerId, id, null, "detach", detach: member);
        var detach = Detach();
        var detached = detach.Apply(document);
        CheckOwners(detached);
        Assert.Null(PlacementOwnership.Owner(detached, member));
        CheckOwners(detach.Revert(detached));
        foreach (var bake in new[] { true, false })
        {
            IEditCommand command = area ? new PlacementAreaCommand(layerId, id, null, "remove", bake: bake)
                : new PlacementPathCommand(layerId, id, null, "remove", bake: bake);
            var after = command.Apply(document);
            Check(document, after); CheckOwners(after);
            Assert.Null(PlacementOwnership.Owner(after, member));
            CheckOwners(command.Revert(after));
            CheckOwners(command.Apply(document));
        }
    }

    private static void Check(MapDocument before, MapDocument after)
    {
        Assert.Equal(FullAllows(before, after, true), PlacementAreaOwnership.Allows(before, after, out var areaReason));
        Assert.Equal(PlacementAreaOwnership.DetachFirst, areaReason);
        Assert.Equal(FullAllows(before, after, false), PlacementPathOwnership.Allows(before, after, out var pathReason));
        Assert.Equal(PlacementPathOwnership.DetachFirst, pathReason);
    }

    // Deliberately retains the previous complete member scan as an independent oracle.
    private static bool FullAllows(MapDocument before, MapDocument after, bool area)
    {
        foreach (var source in before.Layers.OfType<ObjectLayer>())
        {
            if (after.FindLayer(source.Id) is not ObjectLayer target || ReferenceEquals(source.Objects, target.Objects)) continue;
            var previous = DocumentRevisionIndex.For(before); var index = DocumentRevisionIndex.For(after);
            if (area)
            {
                foreach (var owner in source.PlacementAreas)
                    if (ReferenceEquals(owner, target.PlacementAreas.FirstOrDefault(a => a.Id == owner.Id)))
                        foreach (var member in owner.Members)
                            if (!Valid(member.ObjectId)) return false;
            }
            else
            {
                foreach (var owner in source.PlacementPaths)
                    if (ReferenceEquals(owner, target.PlacementPaths.FirstOrDefault(p => p.Id == owner.Id)))
                        foreach (var member in owner.Members)
                            if (!Valid(member.ObjectId)) return false;
            }
            bool Valid(ObjectId id) => index.TryFind(id, out var location) && location.LayerId == source.Id
                && location.Object == previous.FindObject(id);
        }
        return true;
    }

    private static void CheckOwners(MapDocument document)
    {
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
            foreach (var item in layer.Objects)
            {
                Assert.Same(layer.PlacementAreas.FirstOrDefault(a => a.Members.Any(m => m.ObjectId == item.Id)),
                    PlacementAreaOwnership.Owner(document, item.Id));
                Assert.Same(layer.PlacementPaths.FirstOrDefault(p => p.Members.Any(m => m.ObjectId == item.Id)),
                    PlacementPathOwnership.Owner(document, item.Id));
            }
    }

    private static MapDocument Fixture(out LayerId layerId)
    {
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [Loose(0)]);
        layerId = layer.Id;
        var document = new MapDocument(DocumentId.New(), "Ownership", DateTimeOffset.UnixEpoch,
            new(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer,
                new ObjectLayer(LayerId.New(), "Target", true, false, 1, [])],
            [new MapAssetReference(Sha, "tree", "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "test", "test", catalogId: Sha)]);
        for (var i = 0; i < 3; i++)
        {
            var x = i * 1024;
            var area = new PlacementArea { Density = 4, Seed = i, Variants = [new(Sha, 32, 32)],
                Nodes = [new(Guid.NewGuid(), x, 0), new(Guid.NewGuid(), x + 512, 0),
                    new(Guid.NewGuid(), x + 512, 512), new(Guid.NewGuid(), x, 512)] };
            document = new PlacementAreaCommand(layerId, area.Id, area, "create").Apply(document);
            var path = new PlacementPath { Spacing = 30, Variants = [new(Sha, 32, 32)],
                Nodes = [new(Guid.NewGuid(), x, 2048), new(Guid.NewGuid(), x + 200, 2048)] };
            document = new PlacementPathCommand(layerId, path.Id, path, "create").Apply(document);
        }
        return document;
    }

    private static SceneObject Loose(int i) => new(ObjectId.New(), SceneObjectKind.Rectangle,
        "loose " + i, i, 4096, 10, 10, 0, "#FFFFFFFF", "#00000000", 0, true, false);
}
