using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class Stage13BLargeSceneRevisionTests
{
    [Fact]
    public void Stage13B_chunked_list_preserves_order_across_replace_insert_and_remove()
    {
        var source = Enumerable.Range(0, 600)
            .Select(index => Rectangle($"object-{index:D3}", index * 3, 0))
            .ToArray();
        var persistent = SceneObjectList.From(source);
        var replacement = Rectangle("replacement", 999, 10);

        var replaced = persistent.ReplaceAt(300, replacement);
        Assert.Same(source[299], replaced[299]);
        Assert.Same(replacement, replaced[300]);
        Assert.Same(source[301], replaced[301]);
        Assert.Same(source[300], persistent[300]);

        var inserted = replaced.InsertRange(256,
            [Rectangle("insert-a", 1, 1), Rectangle("insert-b", 2, 2)]);
        Assert.Equal(602, inserted.Count);
        Assert.Equal("insert-a", inserted[256].Name);
        Assert.Equal("insert-b", inserted[257].Name);
        Assert.Same(source[256], inserted[258]);

        var removed = inserted.RemoveRange(250, 20);
        var expected = inserted.Take(250).Concat(inserted.Skip(270)).Select(item => item.Id);
        Assert.Equal(expected, removed.Select(item => item.Id));
        Assert.Equal(582, removed.Count);
    }

    [Fact]
    public void Stage13B_replacement_revision_shares_indexes_and_undo_redo_are_exact_at_50k()
    {
        var objects = LargeObjects(50_000);
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        var document = DocumentWith(layer);
        var target = objects[^1];
        var moved = MultiSelectionGeometry.Translate(
            [target], 100 - target.X, 100 - target.Y)[0];

        _ = DocumentRevisionIndex.For(document);
        _ = ObjectSpatialIndex.For(layer);
        var session = new EditingSession(document) { SelectedObjectId = target.Id };
        session.Execute(new ReplaceObjectsCommand([target], [moved], "move last"));

        var revisedLayer = session.Document.Layers.OfType<ObjectLayer>().Single();
        var revisionIndex = DocumentRevisionIndex.For(session.Document);
        Assert.True(revisionIndex.TryFind(target.Id, out var location));
        Assert.Equal(49_999, location.ObjectIndex);
        Assert.Same(moved, location.Object);
        Assert.Equal(1, revisionIndex.OverlayObjectCount);
        Assert.Equal(1, ObjectSpatialIndex.For(revisedLayer).IncrementalOverrideCount);
        Assert.Equal(target.Id, SceneGeometry.HitTest(session.Document, 116, 116)!.Id);
        Assert.Null(SceneGeometry.HitTest(
            session.Document, target.X + (target.Width / 2), target.Y + (target.Height / 2)));
        Assert.Equal([target.Id], session.SelectedObjectIds);
        Assert.Equal(1, session.UndoDepth);

        Assert.True(session.Undo());
        Assert.Same(target, DocumentEditor.FindObject(session.Document, target.Id));
        Assert.Equal(0, session.UndoDepth);
        Assert.Equal(1, session.RedoDepth);

        Assert.True(session.Redo());
        Assert.Equal(moved, DocumentEditor.FindObject(session.Document, target.Id));
        Assert.Equal(1, session.UndoDepth);
        Assert.Equal(0, session.RedoDepth);
        Assert.Equal(objects.Select(item => item.Id),
            revisedLayer.Objects.Select(item => item.Id));
    }

    [Fact]
    public void Stage13B_group_index_tracks_replacement_overlay_in_document_order()
    {
        var groupId = Guid.NewGuid();
        var first = Rectangle("first", 0, 0) with { GroupId = groupId, GroupName = "old" };
        var middle = Rectangle("middle", 50, 0);
        var second = Rectangle("second", 100, 0) with { GroupId = groupId, GroupName = "old" };
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [first, middle, second]));
        _ = DocumentRevisionIndex.For(document);

        var renamed = SetGroupCommand.Rename(document, groupId, "new").Apply(document);
        var members = SceneGeometry.GroupMembers(renamed, groupId);

        Assert.Equal([first.Id, second.Id], members.Select(item => item.Id));
        Assert.All(members, item => Assert.Equal("new", item.GroupName));
        Assert.Equal(2, DocumentRevisionIndex.For(renamed).OverlayObjectCount);
    }

    [Fact]
    public void Stage13B_delta_validation_rejects_group_and_nested_identity_collisions()
    {
        var groupId = Guid.NewGuid();
        var first = Rectangle("first", 0, 0) with { GroupId = groupId, GroupName = "same" };
        var second = Rectangle("second", 50, 0) with { GroupId = groupId, GroupName = "same" };
        var owner = new SceneObject(
            ObjectId.New(), SceneObjectKind.Composition, "owner",
            100, 0, 64, 64, 0,
            "#00000000", "#00000000", 0, true, false)
        {
            Composition = new EditableComposition(
                CompositionId.New(), DocumentId.New(), "nested", 64, 64,
                [Rectangle("child", 5, 5)]),
        };
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [first, second, owner]));
        _ = DocumentRevisionIndex.For(document);

        var mismatchedGroupName = first with { GroupName = "different" };
        Assert.Throws<ArgumentException>(() =>
            new ReplaceObjectsCommand([first], [mismatchedGroupName], "invalid group").Apply(document));

        var collidingChild = Rectangle("collision", 5, 5) with { Id = second.Id };
        var collidingComposition = owner with
        {
            Composition = owner.Composition! with { Items = [collidingChild] },
        };
        Assert.Throws<ArgumentException>(() =>
            new ReplaceObjectsCommand([owner], [collidingComposition], "invalid composition")
                .Apply(document));
    }

    [Fact]
    public void Stage13B_renderer_draws_live_override_without_mutating_source_revision()
    {
        var original = Rectangle("outside", 5_000, 5_000);
        var moved = MultiSelectionGeometry.Translate([original], -4_950, -4_950)[0];
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [original]));
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var stats = new RenderStats();

        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            VisibleWorld = new WorldBounds(0, 0, 200, 200),
            ObjectOverrides = [moved],
            Stats = stats,
        });

        Assert.Equal(1, stats.ObjectCandidates);
        Assert.Equal(1, stats.ObjectsDrawn);
        Assert.Same(original, DocumentEditor.FindObject(document, original.Id));
    }

    [Fact]
    public void Stage13B_save_reopen_keeps_order_groups_and_composition_after_incremental_edit()
    {
        var groupId = Guid.NewGuid();
        var objects = LargeObjects(600).ToArray();
        objects[10] = objects[10] with { GroupId = groupId, GroupName = "pair" };
        objects[11] = objects[11] with { GroupId = groupId, GroupName = "pair" };
        var child = Rectangle("child", 5, 5);
        objects[300] = new SceneObject(
            objects[300].Id, SceneObjectKind.Composition, "composition",
            objects[300].X, objects[300].Y, 64, 64, 0,
            "#00000000", "#00000000", 0, true, false)
        {
            Composition = new EditableComposition(
                CompositionId.New(), DocumentId.New(), "nested", 64, 64, [child]),
        };
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects));
        _ = DocumentRevisionIndex.For(document);
        var before = objects[^1];
        var after = MultiSelectionGeometry.Translate([before], 7, 9)[0];
        var edited = new ReplaceObjectsCommand([before], [after], "round-trip edit").Apply(document);
        var directory = Path.Combine(
            Path.GetTempPath(), "Map-Stage13B-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "incremental.mapproj");
            MapProjectStore.Save(
                edited,
                path,
                new SaveMetadata("0.2.0", DateTimeOffset.UnixEpoch),
                ThumbnailRenderer.RenderPng(edited));
            var reopened = MapProjectStore.Open(path).Document;
            var reopenedObjects = reopened.Layers.OfType<ObjectLayer>().Single().Objects;

            Assert.Equal(objects.Select(item => item.Id), reopenedObjects.Select(item => item.Id));
            Assert.Equal(after, reopenedObjects[^1]);
            Assert.Equal([objects[10].Id, objects[11].Id],
                SceneGeometry.GroupMembers(reopened, groupId).Select(item => item.Id));
            Assert.Equal(child.Id, reopenedObjects[300].Composition!.Items.Single().Id);
            Assert.IsType<SceneObjectList>(reopenedObjects);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<SceneObject> LargeObjects(int count)
    {
        var objects = new SceneObject[count];
        for (var index = 0; index < count; index++)
        {
            objects[index] = Rectangle(
                $"object-{index:D6}",
                (index % 256) * 240 + 24,
                (index / 256) * 240 + 24);
        }

        return objects;
    }

    private static MapDocument DocumentWith(ObjectLayer layer) => new(
        DocumentId.New(),
        "stage13b",
        DateTimeOffset.UnixEpoch,
        new CanvasDefinition(65_536, 65_536, "px", "#FF202830"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF202830"),
            layer,
        ]);

    private static SceneObject Rectangle(string name, double x, double y) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, y, 32, 32, 0,
        "#FF557799", "#FF112233", 1, true, false);
}
