using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

[CollectionDefinition(nameof(DerivedIndexPreparationCounters), DisableParallelization = true)]
public sealed class DerivedIndexPreparationCounters;

/// <summary>
/// A large map prepares the lookups of the first click and placement away from the input
/// thread; after that a click, a drag and its undo build no lookup over every object, and the
/// side list derived through unobserved revisions equals one built from scratch.
/// </summary>
[Collection(nameof(DerivedIndexPreparationCounters))]
public sealed class DerivedIndexPreparationTests
{
    private static readonly string[] Hashes = [.. Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 64))];

    private static SceneObject Stamp(int i, double x, double y, ObjectId? id = null) => new(id ?? ObjectId.New(),
        SceneObjectKind.Stamp, "Ассет " + i, x, y, 32, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false)
    { Stamp = new StampStyle(Hashes[i % Hashes.Length]) };

    private static MapDocument Map(int count)
    {
        var random = new Random(5);
        var objects = Enumerable.Range(0, count).Select(i => Stamp(i, random.NextDouble() * 60000, random.NextDouble() * 60000)).ToArray();
        objects[^1] = Stamp(count - 1, 30000, 30000, objects[^1].Id);
        return new MapDocument(DocumentId.New(), "Prepared", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65536, 65536, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)]);
    }

    private static EditorViewModel Editor(MapDocument document) => new(document, null, false, new FakeProjectStore(),
        new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);

    [Fact]
    public async Task Opening_a_large_map_prepares_label_priority_before_the_first_settled_frame()
    {
        var document = Map(EditorViewModel.DerivedIndexPreparationMinimumObjects + 1);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var label = layer.Objects[0] with
        {
            Kind = SceneObjectKind.Text, Stamp = null,
            Text = new TextStyle { Content = "label", Importance = 1 },
        };
        layer = layer with { Objects = layer.ObjectStorage.ReplaceAt(0, label) };
        document = document with { Layers = [document.Layers[0], layer], Labels = new LabelSettings { ByScale = true } };
        var editor = Editor(document);
        await editor.DerivedIndexPreparation;
        Assert.True(editor.DerivedIndexesPrepared);
        Assert.True(LabelPriority.IsPrepared(document));
        var enumerated = SceneObjectList.EnumeratedOnThisThread;
        Assert.True(LabelPriority.ForFrame(document, .1, () => throw new InvalidOperationException()).Contains(label));
        Assert.Equal(enumerated, SceneObjectList.EnumeratedOnThisThread);
    }

    [Fact]
    public async Task A_large_map_is_prepared_at_open_and_a_click_drag_and_undo_rebuild_nothing()
    {
        var document = Map(EditorViewModel.DerivedIndexPreparationMinimumObjects + 5_000);
        var editor = Editor(document);
        await editor.DerivedIndexPreparation;
        Assert.True(editor.DerivedIndexesPrepared);
        Assert.Same(document, editor.Session.Document);
        var identity = SceneObjectList.IdentityRootBuilds;
        var names = ObjectNameIndex.RootBuilds;

        var router = new CanvasInputRouter(editor);
        editor.Session.Viewport.Set(0, 0, 1);
        editor.ActiveTool = EditorTool.Select;
        var anchor = document.Layers.OfType<ObjectLayer>().Single().Objects[^1];
        router.Press(CanvasPointerButton.Left, 30010, 30010);
        router.Release(30010, 30010);
        Assert.Equal(anchor.Id, editor.Session.SelectedObjectId);
        router.Press(CanvasPointerButton.Left, 30010, 30010);
        router.Move(30050, 30030);
        router.Release(30050, 30030);
        Assert.Equal(1, editor.Session.UndoDepth);
        await editor.DerivedIndexPreparation;
        Assert.True(editor.DerivedIndexesPrepared);
        editor.Undo();
        await editor.DerivedIndexPreparation;
        Assert.True(editor.DerivedIndexesPrepared);
        Assert.Equal(anchor, DocumentEditor.FindObject(editor.Session.Document, anchor.Id));

        Assert.Equal(identity, SceneObjectList.IdentityRootBuilds);
        Assert.Equal(names, ObjectNameIndex.RootBuilds);
    }

    [Fact]
    public void A_small_map_keeps_its_lookups_on_demand()
    {
        var editor = Editor(Map(200));
        Assert.True(editor.DerivedIndexPreparation.IsCompleted);
        Assert.False(editor.DerivedIndexesPrepared);
        editor.SelectObject(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects[^1].Id);
        Assert.Equal(1, editor.SelectionCount);
    }

    [Fact]
    public void Asset_kind_rows_derived_through_unobserved_revisions_equal_rows_listed_at_every_revision()
    {
        var source = Map(3_000);
        var objects = source.Layers.OfType<ObjectLayer>().Single();
        // Two equal maps with separate layer records, so each keeps its own derived kinds.
        MapDocument Twin() => new(DocumentId.New(), "Twin", DateTimeOffset.UnixEpoch, source.Canvas,
            [source.Layers[0], new ObjectLayer(objects.Id, objects.Name, true, false, 1, [.. objects.Objects])]);
        var watched = new EditingSession(Twin());
        var unwatched = new EditingSession(Twin());
        IReadOnlyList<ObjectTreeRow> Rows(EditingSession s) => ObjectTreeRows.Build(s.Document, "", ObjectTypeFilter.Stamps, null, []);
        _ = Rows(watched); _ = Rows(unwatched);
        var added = Stamp(5, 100, 100) with { Stamp = new StampStyle(new string('z', 64)) };
        var moved = objects.Objects[20];
        var extra = Stamp(2, 300, 300);
        // Edits with a delta (append, move, undo of an append), so both sides derive.
        Action<EditingSession>[] edits =
        [
            s => s.Execute(new AddObjectCommand(objects.Id, added)),
            s => s.Execute(new ReplaceObjectsCommand([moved], [Stamp(3, 7, 7, moved.Id) with { Stamp = moved.Stamp }], "Move")),
            s => s.Execute(new AddObjectCommand(objects.Id, extra)),
            s => Assert.True(s.Undo()),
        ];
        // The watched side list is asked at every revision; the unwatched one only at the end.
        foreach (var edit in edits) { edit(watched); _ = Rows(watched); edit(unwatched); }

        var expected = Rows(watched);
        var actual = Rows(unwatched);
        Assert.Equal(expected.Select(r => (r.AssetKey, r.ChildCount, r.Title)), actual.Select(r => (r.AssetKey, r.ChildCount, r.Title)));
        Assert.Contains(actual, r => r.AssetKey == added.Stamp!.AssetSha256);
        // Derived, not regrouped: the moved copy leads its kind (a full regroup would put the
        // top-most copy first).
        Assert.Equal(moved.Id, actual.Single(r => r.AssetKey == moved.Stamp!.AssetSha256).Object!.Id);
        foreach (var row in expected)
            Assert.Equal(ObjectTreeRows.AssetMembers(watched.Document, row.AssetKey!).Select(o => o.Id),
                ObjectTreeRows.AssetMembers(unwatched.Document, row.AssetKey!).Select(o => o.Id));
    }
}
