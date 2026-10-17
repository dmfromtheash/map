using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class Stage10EGridViewModelTests
{
    [Fact]
    public void Four_geometries_have_exact_russian_labels()
    {
        Assert.Equal("Квадратная", EditorLabels.GridGeometry(GridGeometry.Square));
        Assert.Equal("Изометрическая", EditorLabels.GridGeometry(GridGeometry.IsometricSquares));
        Assert.Equal("Гексы: вертикальные столбцы (плоский верх)", EditorLabels.GridGeometry(GridGeometry.HexFlat));
        Assert.Equal("Гексы: горизонтальные ряды (острый верх)", EditorLabels.GridGeometry(GridGeometry.HexPointy));
    }

    [Fact]
    public void Every_property_commit_is_one_command_and_preserves_grid_and_layer_ids()
    {
        var (editor, layerId, gridId) = NewEditor();
        var originalDepth = editor.Session.UndoDepth;

        Assert.True(editor.SetActiveGridGeometry(GridGeometry.IsometricSquares));
        Assert.True(editor.SetActiveGridCellSize(96.5));
        Assert.True(editor.SetActiveGridSubdivisions(4));
        Assert.True(editor.SetActiveGridLineWidth(2.75));
        Assert.True(editor.SetActiveGridColor("#CC224466"));
        Assert.True(editor.SetActiveGridOpacityPercent(65));
        Assert.True(editor.SetActiveGridOffsetX(13.5));
        Assert.True(editor.SetActiveGridOffsetY(-17.25));
        Assert.True(editor.SetActiveGridRotation(-725.5));
        Assert.True(editor.SetActiveGridSnapEnabled(false));
        Assert.True(editor.SetActiveGridIncludeInExport(true));

        Assert.Equal(originalDepth + 11, editor.Session.UndoDepth);
        var layer = Assert.IsType<GridLayer>(editor.ActiveLayer);
        var grid = Assert.Single(layer.Grids);
        Assert.Equal(layerId, layer.Id);
        Assert.Equal(gridId, grid.Id);
        Assert.Equal(GridGeometry.IsometricSquares, grid.Geometry);
        Assert.Equal(96.5, grid.CellSize);
        Assert.Equal(4, grid.Subdivisions);
        Assert.Equal(2.75, grid.LineWidth);
        Assert.Equal("#CC224466", grid.Color);
        Assert.Equal(0.65, grid.Opacity, 12);
        Assert.Equal(13.5, grid.OffsetX);
        Assert.Equal(-17.25, grid.OffsetY);
        Assert.Equal(-725.5, grid.RotationDegrees);
        Assert.False(grid.SnapEnabled);
        Assert.True(grid.IncludeInExport);
    }

    [Fact]
    public void Geometry_undo_redo_restores_exact_values_without_resetting_other_properties()
    {
        var source = Grid() with
        {
            CellSize = 77,
            Subdivisions = 3,
            LineWidth = 2,
            Color = "#AA123456",
            Opacity = 0.42,
            OffsetX = 99,
            OffsetY = -101,
            RotationDegrees = 28,
            SnapEnabled = false,
            IncludeInExport = true,
        };
        var (editor, _, _) = NewEditor(grid: source);

        Assert.True(editor.SetActiveGridGeometry(GridGeometry.IsometricSquares));
        var changed = ActiveGrid(editor);
        Assert.Equal(source with { Geometry = GridGeometry.IsometricSquares }, changed);

        editor.Undo();
        Assert.Equal(source, ActiveGrid(editor));
        editor.Redo();
        Assert.Equal(changed, ActiveGrid(editor));
    }

    [Fact]
    public void No_op_invalid_and_locked_edits_never_enter_history()
    {
        var (editor, _, _) = NewEditor();
        var snapshot = editor.Session.Document;

        Assert.False(editor.SetActiveGridGeometry(GridGeometry.Square));
        Assert.False(editor.SetActiveGridCellSize(128));
        Assert.False(editor.SetActiveGridSubdivisions(2.5));
        Assert.False(editor.SetActiveGridSubdivisions(0));
        Assert.False(editor.SetActiveGridLineWidth(double.PositiveInfinity));
        Assert.False(editor.SetActiveGridColor("not a colour"));
        Assert.False(editor.SetActiveGridOpacityPercent(101));
        Assert.False(editor.SetActiveGridOffsetX(double.NaN));
        Assert.False(editor.SetActiveGridRotation(double.NegativeInfinity));
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Same(snapshot, editor.Session.Document);
        Assert.NotEmpty(editor.InspectorError);

        var (locked, _, _) = NewEditor(locked: true);
        var lockedGrid = ActiveGrid(locked);
        Assert.False(locked.SetActiveGridGeometry(GridGeometry.IsometricSquares));
        Assert.False(locked.SetActiveGridSnapEnabled(false));
        Assert.Equal(0, locked.Session.UndoDepth);
        Assert.Equal(lockedGrid, ActiveGrid(locked));
        Assert.Contains("только для чтения", locked.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage10EPortalSnapReviewFix_success_no_op_and_refusal_have_exact_history_and_status()
    {
        var representable = VectorGeometry.CreateLine(ObjectId.New(), "road",
            [new VectorPoint(-10, 0), new VectorPoint(10, 0)]);
        representable = VectorObjectEditor.AddPortal(representable, 0.35, 12);
        var representablePortalId = representable.Line!.Portals[0].Id;
        var successEditor = NewPortalEditor(representable, Grid());

        successEditor.SnapSelectedPortalToGrid();

        Assert.Equal(1, successEditor.Session.UndoDepth);
        Assert.Equal("Проход привязан к сетке.", successEditor.StatusDetail);
        Assert.Equal(0.5, SelectedLine(successEditor).Line!.Portals[0].Position, 12);
        var snappedDocument = successEditor.Session.Document;

        successEditor.SnapSelectedPortalToGrid();

        Assert.Same(snappedDocument, successEditor.Session.Document);
        Assert.Equal(1, successEditor.Session.UndoDepth);
        Assert.Equal("Проход уже привязан к сетке.", successEditor.StatusDetail);
        successEditor.Undo();
        Assert.Equal(0.35, SelectedLine(successEditor).Line!.Portals.Single(
            portal => portal.Id == representablePortalId).Position, 12);
        successEditor.Redo();
        Assert.Equal(0.5, SelectedLine(successEditor).Line!.Portals.Single(
            portal => portal.Id == representablePortalId).Position, 12);

        var unrotated = VectorGeometry.CreateLine(ObjectId.New(), "refusal",
            [new VectorPoint(21, 19), new VectorPoint(74, 181), new VectorPoint(184, 91)]);
        var impossible = WithRotation(unrotated, 23);
        impossible = VectorObjectEditor.AddPortal(impossible, 0.43, 12);
        var refusalEditor = NewPortalEditor(impossible, Grid(GridGeometry.IsometricSquares));
        var originalDocument = refusalEditor.Session.Document;

        refusalEditor.SnapSelectedPortalToGrid();

        Assert.Same(originalDocument, refusalEditor.Session.Document);
        Assert.Equal(0, refusalEditor.Session.UndoDepth);
        Assert.Equal(0, refusalEditor.Session.RedoDepth);
        Assert.Equal("Точная привязка прохода невозможна без перемещения линии.", refusalEditor.StatusDetail);
        Assert.Same(impossible, SelectedLine(refusalEditor));
    }

    private static GridDefinition ActiveGrid(EditorViewModel editor) =>
        Assert.Single(Assert.IsType<GridLayer>(editor.ActiveLayer).Grids);

    private static GridDefinition Grid(GridGeometry geometry = GridGeometry.Square) =>
        new(GridId.New(), geometry, 128)
        {
            Subdivisions = 1,
            LineWidth = 1,
            Color = "#FF405060",
            Opacity = 0.7,
            SnapEnabled = true,
        };

    private static (EditorViewModel Editor, LayerId LayerId, GridId GridId) NewEditor(
        bool locked = false, GridDefinition? grid = null)
    {
        grid ??= Grid();
        var layer = new GridLayer(LayerId.New(), "Сетка", true, locked, 1, [grid]);
        var document = new MapDocument(
            DocumentId.New(), "grid editor", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
                layer,
            ]);
        var editor = new EditorViewModel(
            document, null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog());
        editor.SelectLayer(layer.Id);
        return (editor, layer.Id, grid.Id);
    }

    private static EditorViewModel NewPortalEditor(SceneObject line, GridDefinition grid)
    {
        var document = new MapDocument(
            DocumentId.New(), "portal snap", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [line]),
                new GridLayer(LayerId.New(), "Сетка", true, false, 1, [grid]),
            ]);
        var editor = new EditorViewModel(
            document, null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog());
        editor.SelectObject(line.Id);
        return editor;
    }

    private static SceneObject SelectedLine(EditorViewModel editor) =>
        Assert.IsType<SceneObject>(editor.Session.SelectedObject);

    private static SceneObject WithRotation(SceneObject source, double rotationDegrees) => new(
        source.Id, source.Kind, source.Name,
        source.X, source.Y, source.Width, source.Height, rotationDegrees,
        source.FillColor, source.StrokeColor, source.StrokeWidth,
        source.Visible, source.Locked)
    {
        Sublayer = source.Sublayer,
        GroupId = source.GroupId,
        GroupName = source.GroupName,
        Stamp = source.Stamp,
        Line = source.Line,
        Shape = source.Shape,
        Text = source.Text,
        ObjectOpacity = source.ObjectOpacity,
        Composition = source.Composition,
        PlaceholderReason = source.PlaceholderReason,
    };
}
