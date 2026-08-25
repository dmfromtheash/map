using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class VectorRoundTripTests
{
    [Fact]
    public void Line_nodes_portals_asset_stroke_and_boolean_shape_survive_reopen()
    {
        using var workspace = new TempWorkspace();
        var line = VectorGeometry.CreateLine(ObjectId.New(), "river",
            [new VectorPoint(20, 40), new VectorPoint(300, 80), new VectorPoint(500, 300)],
            new VectorStrokeStyle
            {
                Width = 18, Offset = 4, EndCap = VectorCap.Square, NodeCap = VectorCap.Round,
                AssetSha256 = new string('a', 64), AssetSpacing = 1.5, Hue = 35, Saturation = 0.2,
                ShadowOpacity = 0.4,
            });
        line = VectorObjectEditor.AddPortal(line, 0.42, 32);
        line = VectorObjectEditor.SetNodeCurve(line, line.Line!.Nodes[1].Id, curved: true);
        var outer = VectorGeometry.CreateShape(ObjectId.New(), "outer", ShapePrimitiveKind.Rectangle,
            new WorldBounds(100, 100, 600, 600));
        var inner = VectorGeometry.CreateShape(ObjectId.New(), "inner", ShapePrimitiveKind.Ellipse,
            new WorldBounds(220, 220, 480, 480));
        var baseDocument = Document(line, outer, inner);
        var layer = baseDocument.Layers.OfType<ObjectLayer>().Single();
        var boolean = new ShapeBooleanCommand(baseDocument, layer.Id, outer.Id, inner.Id, ShapeBooleanOperation.Subtract);
        var document = boolean.Apply(baseDocument);
        var expectedLine = DocumentEditor.FindObject(document, line.Id)!;
        var expectedShape = DocumentEditor.FindObject(document, boolean.ResultId)!;
        var path = workspace.File("vectors.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        Assert.Equivalent(expectedLine.Line, DocumentEditor.FindObject(reopened, line.Id)!.Line, strict: true);
        Assert.Equivalent(expectedShape.Shape, DocumentEditor.FindObject(reopened, boolean.ResultId)!.Shape, strict: true);
    }

    private static MapDocument Document(params SceneObject[] objects) => new(
        DocumentId.New(), "vectors", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects),
        ]);
}
