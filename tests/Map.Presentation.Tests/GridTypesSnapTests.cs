using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>Isometric and vertical-hex grids from the editor: placement, object and node snap land on grid nodes.</summary>
public sealed class GridTypesSnapTests
{
    [Theory]
    [InlineData(GridGeometry.IsometricSquares)]
    [InlineData(GridGeometry.HexFlat)]
    [InlineData(GridGeometry.HexPointy)]
    public void Placement_object_and_node_snap_land_on_nodes_of_the_chosen_grid(GridGeometry geometry)
    {
        var (editor, grid, line) = Editor(geometry);

        // Placement: the armed shape follows the pointer onto the nearest grid node.
        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        editor.UpdatePlacementPreview(203.7, 141.2);
        var expected = GridGeometryContract.Snap(grid, new VectorPoint(203.7, 141.2));
        Assert.Equal(expected.X, editor.PendingPlacement!.WorldX, 6);
        Assert.Equal(expected.Y, editor.PendingPlacement.WorldY, 6);
        editor.CancelPlacement();

        // «Привязать объект»: the object's anchor is a grid node afterwards.
        editor.SelectObject(line.Id);
        editor.SnapSelectedObjectToGrid();
        var snapped = Objects(editor).Single();
        Assert.Equal(GridSnapService.SnapObject(line, grid), snapped);
        Assert.NotEqual(line, snapped);

        // «Привязать узел»: the selected node lands on a node in world space.
        var node = snapped.Line!.Nodes[1];
        editor.SelectVectorNode(node.Id);
        editor.SnapSelectedNodeToGrid();
        var after = Objects(editor).Single();
        var world = new VectorPoint(after.X + (after.Line!.Nodes[1].X * after.Width), after.Y + (after.Line.Nodes[1].Y * after.Height));
        var onGrid = GridGeometryContract.Snap(grid, world);
        Assert.Equal(onGrid.X, world.X, 4);
        Assert.Equal(onGrid.Y, world.Y, 4);
        Assert.Equal(2, editor.Session.UndoDepth);
    }

    [Fact]
    public void Vertical_hex_columns_stack_straight_down()
    {
        var grid = new GridDefinition(GridId.New(), GridGeometry.HexFlat, 60);
        var basis = GridGeometryContract.LocalBasis(grid);
        Assert.Equal(0, basis.V.X, 9);
        Assert.True(basis.V.Y > 0);
        Assert.Equal("Гексы: вертикальные столбцы (плоский верх)", EditorLabels.GridGeometry(GridGeometry.HexFlat));
        Assert.Contains("столбцами", EditorLabels.GridGeometryHint(GridGeometry.HexFlat));
    }

    private static IReadOnlyList<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToArray();

    private static (EditorViewModel Editor, GridDefinition Grid, SceneObject Line) Editor(GridGeometry geometry)
    {
        var grid = new GridDefinition(GridId.New(), geometry, 64) { SnapEnabled = true };
        var line = new SceneObject(ObjectId.New(), SceneObjectKind.Line, "линия", 113.3, 87.9, 201.4, 97.1, 0,
            "#00000000", "#FF000000", 4, true, false)
        {
            Line = new LineStyle([new VectorNode(VectorNodeId.New(), 0, 0), new VectorNode(VectorNodeId.New(), 0.83, 0.61)],
                new VectorStrokeStyle()),
        };
        var document = new MapDocument(DocumentId.New(), "grid types", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [line]),
                new GridLayer(LayerId.New(), "Сетка", true, false, 1, [grid]),
            ]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog());
        editor.Session.Viewport.Resize(1200, 800);
        return (editor, grid, line);
    }
}
