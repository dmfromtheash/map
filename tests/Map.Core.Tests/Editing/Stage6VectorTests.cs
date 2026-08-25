using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage6VectorTests
{
    [Fact]
    public void Node_and_portal_edits_are_stable_and_one_command_is_reversible()
    {
        var line = VectorGeometry.CreateLine(ObjectId.New(), "road",
            [new VectorPoint(100, 100), new VectorPoint(300, 120), new VectorPoint(500, 300)]);
        var firstNode = line.Line!.Nodes[0].Id;
        var edited = VectorObjectEditor.MoveNode(line, firstNode, 0.1, 0.2);
        edited = VectorObjectEditor.SetNodeCurve(edited, firstNode, curved: true);
        edited = VectorObjectEditor.AddPortal(edited, 0.5, 48);
        var portalId = edited.Line!.Portals.Single().Id;
        edited = VectorObjectEditor.MovePortal(edited, portalId, 0.65);

        var document = Document(line);
        var session = new EditingSession(document);
        session.Execute(new ReplaceObjectsCommand([line], [edited], "Edit line gesture"));

        var applied = session.SelectedObject ?? DocumentEditor.FindObject(session.Document, line.Id)!;
        Assert.Equal(firstNode, applied.Line!.Nodes[0].Id);
        Assert.Equal(portalId, applied.Line.Portals.Single().Id);
        Assert.True(session.Undo());
        Assert.Empty(DocumentEditor.FindObject(session.Document, line.Id)!.Line!.Portals);
        Assert.True(session.Redo());
        Assert.Equal(0.65, DocumentEditor.FindObject(session.Document, line.Id)!.Line!.Portals.Single().Position);
    }

    public static IEnumerable<object[]> BooleanFixtures()
    {
        yield return [new WorldBounds(100, 100, 220, 220), new WorldBounds(300, 100, 420, 220), ShapeBooleanOperation.Add];
        yield return [new WorldBounds(100, 100, 220, 220), new WorldBounds(220, 100, 340, 220), ShapeBooleanOperation.Merge];
        yield return [new WorldBounds(100, 100, 260, 260), new WorldBounds(180, 180, 340, 340), ShapeBooleanOperation.Add];
        yield return [new WorldBounds(100, 100, 400, 400), new WorldBounds(180, 180, 320, 320), ShapeBooleanOperation.Subtract];
    }

    [Theory]
    [MemberData(nameof(BooleanFixtures))]
    public void Boolean_expression_handles_disjoint_touching_overlap_and_hole(
        WorldBounds aBounds, WorldBounds bBounds, ShapeBooleanOperation operation)
    {
        var first = VectorGeometry.CreateShape(ObjectId.New(), "a", ShapePrimitiveKind.Rectangle, aBounds);
        var second = VectorGeometry.CreateShape(ObjectId.New(), "b", ShapePrimitiveKind.Rectangle, bBounds);
        var document = Document(first, second);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var command = new ShapeBooleanCommand(document, layer.Id, first.Id, second.Id, operation);

        var applied = command.Apply(document);
        var result = Assert.Single(applied.Layers.OfType<ObjectLayer>().Single().Objects);
        ShapeBooleanCommand.Validate(result.Shape!.Regions);
        Assert.All(result.Shape.Regions.SelectMany(region => region.Nodes), node =>
        {
            Assert.True(double.IsFinite(node.X));
            Assert.True(double.IsFinite(node.Y));
        });
        var reverted = command.Revert(applied);
        Assert.Equal([first.Id, second.Id], reverted.Layers.OfType<ObjectLayer>().Single().Objects.Select(o => o.Id));
    }

    [Fact]
    public void Reversed_winding_and_near_collinear_regions_remain_valid()
    {
        var first = VectorGeometry.CreateFreeShape(ObjectId.New(), "reverse",
            [new VectorPoint(300, 300), new VectorPoint(300, 100), new VectorPoint(100, 100), new VectorPoint(100, 300)]);
        var second = VectorGeometry.CreateFreeShape(ObjectId.New(), "near",
            [new VectorPoint(150, 150), new VectorPoint(250, 150.000001), new VectorPoint(250, 250), new VectorPoint(150, 250)]);
        var document = Document(first, second);
        var layer = document.Layers.OfType<ObjectLayer>().Single();

        var result = new ShapeBooleanCommand(document, layer.Id, first.Id, second.Id, ShapeBooleanOperation.Subtract)
            .Apply(document).Layers.OfType<ObjectLayer>().Single().Objects.Single();

        ShapeBooleanCommand.Validate(result.Shape!.Regions);
        Assert.True(VectorGeometry.ContainsShape(result, 120, 120));
        Assert.False(VectorGeometry.ContainsShape(result, 200, 200));
    }

    [Fact]
    public void Spatial_bounds_include_line_offset_width_and_shadow()
    {
        var line = VectorGeometry.CreateLine(ObjectId.New(), "wide",
            [new VectorPoint(100, 100), new VectorPoint(200, 100)],
            new VectorStrokeStyle { Width = 40, Offset = 30, ShadowOpacity = 1, ShadowBlur = 10, ShadowOffsetX = 12 });
        var bounds = SceneGeometry.BoundsOf(line);
        Assert.True(bounds.Left < 40);
        Assert.True(bounds.Right > 250);
    }

    private static MapDocument Document(params SceneObject[] objects) => new(
        DocumentId.New(), "vectors", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects),
        ]);
}
