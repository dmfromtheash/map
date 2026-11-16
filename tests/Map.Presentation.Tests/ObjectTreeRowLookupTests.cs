using System.Collections;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class ObjectTreeRowLookupTests
{
    [Fact]
    public void Hints_preserve_equality_and_follow_reorder_delete_undo_and_redo()
    {
        var key = new string('a', 64);
        var items = Enumerable.Range(0, 8).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "mountain " + i, i * 64, 0, 64, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(key) }).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, items);
        var document = new MapDocument(DocumentId.New(), "rows", DateTimeOffset.UnixEpoch,
            new(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), layer]);
        var original = Rows(document);
        var wanted = original[^1];
        Assert.Equal(8, ((IList)original).IndexOf(wanted));
        // A copied/external row has no hint and must retain IList value equality.
        Assert.Equal(8, ((IList)original).IndexOf(wanted with { }));
        Assert.Equal(-1, ((IList)original).IndexOf(wanted with { Title = "different" }));
        Assert.Equal(-1, ((IList)original).IndexOf(null));
        Assert.Equal(0, ((IList)original).IndexOf(original[0]));
        var selected = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Stamps, null, [items[0].Id], openAssetKey: key);
        Assert.Equal(-1, ((IList)selected).IndexOf(wanted));
        Assert.Equal(8, ((IList)selected).IndexOf(selected[^1]));

        var session = new EditingSession(document);
        session.SetSelection([items[0].Id]);
        var extra = items[0] with { Id = ObjectId.New(), Name = "extra" };
        session.Execute(new AddObjectsCommand(layer.Id, [extra]));
        Assert.Equal(9, ((IList)Rows(session.Document)).IndexOf(wanted));
        session.Undo();
        Assert.Equal(8, ((IList)Rows(session.Document)).IndexOf(wanted));
        session.Redo();
        Assert.Equal(9, ((IList)Rows(session.Document)).IndexOf(wanted));
        session.Execute(new DeleteObjectsCommand(session.Document, [items[0].Id]));
        Assert.Equal(-1, ((IList)Rows(session.Document)).IndexOf(wanted));
        session.Undo();
        // The existing kind index puts restored members at the front.
        Assert.Equal(1, ((IList)Rows(session.Document)).IndexOf(wanted));
        Assert.Equal(9, original.Count);
        Assert.Equal(wanted, original[^1]);

        IReadOnlyList<ObjectTreeRow> Rows(MapDocument source) =>
            ObjectTreeRows.Build(source, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: key);
    }
}
