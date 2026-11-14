using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class DeltaAssetKindRowsTests
{
    private static readonly string A = new('a', 64), B = new('b', 64);
    private static SceneObject Stamp(string name, string? key = null, bool visible = true, bool locked = false) => new(ObjectId.New(), SceneObjectKind.Stamp,
        name, 20, 20, 30, 30, 0, "#FFFFFFFF", "#00000000", 0, visible, locked)
        { Stamp = key is null ? null : new StampStyle(key) };
    private static ObjectLayer Layer(params SceneObject[] items) => new(LayerId.New(), "objects", true, false, 1, items);
    private static MapDocument Doc(params ObjectLayer[] layers) => new(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), .. layers]);
    private static IReadOnlyList<ObjectTreeRow> Rows(MapDocument doc, params ObjectId[] selected) =>
        ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, selected);
    private static EditorViewModel Editor(MapDocument doc) => new(doc, null, false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());

    [Fact]
    public void Closed_rows_merge_counts_and_representatives_without_member_ids_and_expand_in_stack_order()
    {
        var bottom = Stamp("bottom", A); var high = Stamp("high", A) with { Sublayer = 4 };
        var other = Stamp("bridge", B); var upper = Stamp("upper · 2", A);
        var doc = Doc(Layer(bottom, high, other), Layer(upper));
        var rows = Rows(doc, bottom.Id, ObjectId.New());
        Assert.Equal(new[] { A, B }, rows.Select(r => r.AssetKey));
        Assert.All(rows, row => Assert.Null(row.MemberIds));
        Assert.Equal(3, rows[0].ChildCount);
        Assert.Same(upper, rows[0].Object);
        Assert.Equal("upper", rows[0].Title);
        Assert.True(rows[0].Selected); Assert.False(rows[1].Selected);
        Assert.Equal(new[] { upper.Id, high.Id, bottom.Id }, ObjectTreeRows.AssetMembers(doc, A).Select(o => o.Id));
        var opened = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, [high.Id], openAssetKey: A);
        Assert.Equal(ObjectTreeRowKind.AssetKindBreadcrumb, opened[0].Kind);
        Assert.Equal(3, opened[0].ChildCount);
        Assert.Equal(new[] { upper.Id, high.Id, bottom.Id }, opened.Skip(1).Select(r => r.Object!.Id));
        Assert.True(opened[2].Selected);
        Assert.Equal("library title", ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, [], assetTitle: _ => "library title")[0].Title);
    }

    [Fact]
    public void Kind_deltas_and_selection_are_correct_after_add_delete_rekey_undo_redo()
    {
        var a = Stamp("first", A); var b = Stamp("second", B);
        var layer = Layer(a, b); var doc = Doc(layer); var session = new EditingSession(doc);
        var original = Rows(doc);
        var added = Stamp("new", A);
        session.Execute(new AddObjectCommand(layer.Id, added));
        Assert.Equal(2, Rows(session.Document, added.Id).Single(r => r.AssetKey == A).ChildCount);
        Assert.Same(added, Rows(session.Document).Single(r => r.AssetKey == A).Object);
        Assert.True(session.Undo());
        Assert.Equal(original, Rows(session.Document));
        Assert.True(session.Redo());
        Assert.Equal(new[] { added.Id, a.Id }, ObjectTreeRows.AssetMembers(session.Document, A).Select(o => o.Id));
        var rekeyed = added with { Stamp = new StampStyle(B) };
        session.Execute(new ReplaceObjectsCommand([added], [rekeyed], "rekey"));
        Assert.True(Rows(session.Document, added.Id).Single(r => r.AssetKey == B).Selected);
        Assert.False(Rows(session.Document, added.Id).Single(r => r.AssetKey == A).Selected);
        Assert.True(session.Undo()); Assert.True(session.Redo());
        session.Execute(new DeleteObjectsCommand(session.Document, [b.Id, added.Id]));
        Assert.Equal(A, Assert.Single(Rows(session.Document)).AssetKey);
        Assert.True(session.Undo());
        Assert.Equal(2, Rows(session.Document).Single(r => r.AssetKey == B).ChildCount);
        Assert.True(session.Redo());
        Assert.Equal(2, original.Count);
        Assert.Equal(1, original[0].ChildCount);
        Assert.Equal(2, Rows(doc).Count);
    }

    [Fact]
    public void Select_all_keeps_closed_groups_atomic_and_obeys_availability_search_and_open_kind()
    {
        var group = Guid.NewGuid();
        var grouped = Stamp("oak group", A) with { GroupId = group };
        var text = Stamp("caption") with { Kind = SceneObjectKind.Text, GroupId = group, Text = new TextStyle() };
        var locked = Stamp("locked", A, locked: true);
        var hidden = Stamp("hidden", B, visible: false);
        var bridge = Stamp("bridge", B);
        var doc = Doc(Layer(grouped, text, locked, hidden, bridge));
        var editor = Editor(doc); editor.PanelSection = PanelSection.Assets;
        editor.SelectAllByFilter();
        Assert.Equal(new[] { grouped.Id, text.Id, bridge.Id }.OrderBy(id => id.Value), editor.Session.SelectedObjectIds.OrderBy(id => id.Value));
        editor.DeselectObject(); editor.OpenAssetKind(B); editor.SelectAllByFilter();
        Assert.Equal(bridge.Id, Assert.Single(editor.Session.SelectedObjectIds));
        editor.CloseAssetKind(); editor.ObjectSearchText = "bridge"; editor.SelectAllByFilter();
        Assert.Equal(bridge.Id, Assert.Single(editor.Session.SelectedObjectIds));
        editor.ObjectSearchText = ""; editor.SelectObject(grouped.Id); editor.RevealSelectionInAssetKinds();
        Assert.Equal(A, editor.OpenAssetKey);
        editor.SelectObject(bridge.Id, additive: true); editor.RevealSelectionInAssetKinds();
        Assert.Null(editor.OpenAssetKey);
    }

    [Fact]
    public void Name_only_kinds_and_missing_open_keys_preserve_search_and_selected_flags()
    {
        var named = Stamp("name only"); var duplicate = Stamp("name only");
        var free = Stamp("free") with { Kind = SceneObjectKind.Rectangle };
        var doc = Doc(Layer(named, duplicate, free));
        var row = Assert.Single(Rows(doc, free.Id, named.Id, ObjectId.New()));
        Assert.Equal("name:name only", row.AssetKey); Assert.Equal(2, row.ChildCount); Assert.True(row.Selected);
        Assert.Empty(ObjectTreeRows.SelectedAssetKeys(doc, [free.Id, ObjectId.New()]));
        var missing = ObjectTreeRows.Build(doc, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: "missing");
        Assert.Equal(ObjectTreeRowKind.AssetKind, Assert.Single(missing).Kind);
        var searched = ObjectTreeRows.Build(doc, "name only", ObjectTypeFilter.Stamps, null, [named.Id]);
        Assert.Equal(2, searched.Count); Assert.Single(searched.Where(r => r.Selected));
        Assert.All(searched, r => Assert.Equal(ObjectTreeRowKind.Object, r.Kind));
    }
}
