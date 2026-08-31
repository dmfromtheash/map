using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class Stage13ALargeSceneInteractionTests
{
    [Fact]
    public void Stage13A_spatial_hit_preserves_layer_sublayer_and_exact_geometry()
    {
        var lower = Rectangle("lower", 100, 100, 100, 100);
        var highSublayer = Rectangle("high-sublayer", 100, 100, 100, 100) with { Sublayer = 3 };
        var laterLowSublayer = Rectangle("later-low", 100, 100, 100, 100) with { Sublayer = -2 };
        var ellipse = new SceneObject(
            ObjectId.New(), SceneObjectKind.Ellipse, "ellipse",
            100, 100, 100, 100, 0,
            "#FFFFFFFF", "#FF000000", 0, visible: true, locked: false);
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "lower", true, false, 1, [lower]),
            new ObjectLayer(LayerId.New(), "upper", true, false, 1,
                [highSublayer, laterLowSublayer, ellipse]));

        // The ellipse's broad-phase bounds contain the corner but exact geometry rejects it.
        // Inside the remaining upper-layer hits, sublayer wins over later list position.
        Assert.Equal(highSublayer.Id, SceneGeometry.HitTest(document, 105, 105)!.Id);
        Assert.Equal(highSublayer.Id, SceneGeometry.HitTest(document, 150, 150)!.Id);
    }

    [Fact]
    public void Stage13A_point_query_keeps_rotated_line_and_text_exact_checks()
    {
        var rotated = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "rotated",
            150, 190, 100, 20, 90,
            "#FFFFFFFF", "#FF000000", 0, visible: true, locked: false);
        var curvedLine = new SceneObject(
            ObjectId.New(), SceneObjectKind.Line, "curve",
            300, 100, 200, 120, 0,
            "#00000000", "#FFFFFFFF", 0, visible: true, locked: false)
        {
            Line = new LineStyle(
            [
                new VectorNode(VectorNodeId.New(), 0, 0.5, VectorNodeKind.Curve,
                    controlOutX: 0.25, controlOutY: -0.5),
                new VectorNode(VectorNodeId.New(), 1, 0.5, VectorNodeKind.Curve,
                    controlInX: -0.25, controlInY: -0.5),
            ],
            new VectorStrokeStyle { Width = 12 }),
        };
        var text = new SceneObject(
            ObjectId.New(), SceneObjectKind.Text, "text",
            550, 100, 120, 40, 0,
            "#FFFFFFFF", "#00000000", 0, visible: true, locked: false);
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [rotated, curvedLine, text]));

        Assert.Equal(rotated.Id, SceneGeometry.HitTest(document, 200, 240)!.Id);
        Assert.Null(SceneGeometry.HitTest(document, 240, 200));
        Assert.Equal(curvedLine.Id, SceneGeometry.HitTest(document, 400, 115)!.Id);
        Assert.Equal(text.Id, SceneGeometry.HitTest(document, 600, 120)!.Id);
    }

    [Fact]
    public void Stage13A_marquee_uses_spatial_candidates_but_keeps_document_order_and_rules()
    {
        var first = Rectangle("first", 10, 10, 20, 20);
        var outside = Rectangle("outside", 5_000, 5_000, 20, 20);
        var second = Rectangle("second", 40, 10, 20, 20);
        var hidden = Rectangle("hidden", 70, 10, 20, 20, visible: false);
        var locked = Rectangle("locked", 100, 10, 20, 20, locked: true);
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1,
                [first, outside, second, hidden, locked]));

        var result = MultiSelectionGeometry.ObjectsInMarquee(
            document, new WorldBounds(0, 0, 150, 100));

        Assert.Equal([first.Id, second.Id], result.Select(sceneObject => sceneObject.Id));
    }

    [Fact]
    public void Stage13A_virtual_object_rows_keep_group_search_and_selection_semantics()
    {
        var groupId = Guid.NewGuid();
        var groupedA = Rectangle("oak", 0, 0, 20, 20) with
        {
            GroupId = groupId,
            GroupName = "forest",
        };
        var groupedB = Rectangle("pine", 30, 0, 20, 20) with
        {
            GroupId = groupId,
            GroupName = "forest",
        };
        var loose = Rectangle("tower", 60, 0, 20, 20);
        var document = DocumentWith(
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, [groupedA, groupedB, loose]));

        var ordinary = ObjectTreeRows.Build(
            document, "", ObjectTypeFilter.All, null, [groupedA.Id, groupedB.Id]);
        Assert.Equal(2, ordinary.Count);
        Assert.Equal(ObjectTreeRowKind.Group, ordinary[0].Kind);
        Assert.True(ordinary[0].Selected);
        Assert.Equal([groupedA.Id, groupedB.Id], ordinary[0].MemberIds);
        Assert.Equal(loose.Id, ordinary[1].Object!.Id);

        var filtered = ObjectTreeRows.Build(
            document, "tower", ObjectTypeFilter.All, null, Array.Empty<ObjectId>());
        Assert.Single(filtered);
        Assert.Equal(loose.Id, filtered[0].Object!.Id);

        var opened = ObjectTreeRows.Build(
            document, "", ObjectTypeFilter.All, groupId, [groupedB.Id]);
        Assert.Equal(ObjectTreeRowKind.Breadcrumb, opened[0].Kind);
        Assert.Equal([groupedA.Id, groupedB.Id], opened.Skip(1).Select(row => row.Object!.Id));
        Assert.False(opened[1].Selected);
        Assert.True(opened[2].Selected);
    }

    [Fact]
    public void Stage13A_drag_preview_publishes_canvas_only_events()
    {
        var sceneObject = Rectangle("drag", 100, 100, 100, 60);
        var editor = EditorWith(sceneObject);
        Assert.Equal(CanvasGesture.Drag,
            editor.BeginCanvasInteraction(130, 130, viewScale: 1, additive: false));

        var legacyEvents = 0;
        var fineEvents = new List<EditorChangeReason>();
        editor.SceneChanged += (_, _) => legacyEvents++;
        editor.SceneInvalidated += (_, e) => fineEvents.Add(e.Reasons);

        editor.UpdateDragTo(150, 150);
        editor.UpdateDragTo(160, 160);

        Assert.Equal(0, legacyEvents);
        Assert.Equal(2, fineEvents.Count);
        Assert.All(fineEvents, reason => Assert.Equal(EditorChangeReason.CanvasInvalidation, reason));
        Assert.Equal(0, editor.Session.UndoDepth);

        editor.EndDrag();
        Assert.Equal(1, legacyEvents);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    private static EditorViewModel EditorWith(params SceneObject[] objects) => new(
        DocumentWith(new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects)),
        documentPath: null,
        startDirty: false,
        new FakeProjectStore(),
        new FakeRecentMaps(),
        new FakeFilePicker(),
        new FakePrompts(),
        new FakeLog());

    private static MapDocument DocumentWith(params ObjectLayer[] layers) => new(
        DocumentId.New(),
        "stage13a",
        DateTimeOffset.UnixEpoch,
        new CanvasDefinition(65_536, 65_536, "px", "#FF202830"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF202830"),
            .. layers,
        ]);

    private static SceneObject Rectangle(
        string name, double x, double y, double width, double height,
        bool visible = true, bool locked = false) => new(
            ObjectId.New(), SceneObjectKind.Rectangle, name,
            x, y, width, height, 0,
            "#FF557799", "#FF112233", 1,
            visible, locked);
}
