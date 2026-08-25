using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class LargeMapStage3Tests
{
    [Fact]
    public void Brush_gesture_on_65k_canvas_changes_only_touched_tiles_and_undoes_once()
    {
        var brushId = LayerId.New();
        var document = new MapDocument(
            DocumentId.New(), "large", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                BrushLayer.CreateEmpty(brushId, "paint"),
            ]);
        var session = new EditingSession(document);
        var stroke = new RasterStrokeSession(TileMap.Empty(4), new BrushSettings
        {
            Radius = 32,
            Softness = 0.4,
            Opacity = 0.8,
            Color = "#FF336699",
        });

        for (var x = 100; x <= 1_100; x += 20)
        {
            stroke.AddPoint(x, 32_000);
        }

        var (Before, After) = stroke.Commit();
        var command = new BrushStrokeCommand(brushId, Before, After, "large stroke");
        session.Execute(command);

        var painted = (BrushLayer)session.Document.FindLayer(brushId)!;
        Assert.InRange(command.TouchedTileCount, 4, 12);
        Assert.Equal(command.TouchedTileCount, painted.Tiles.TileCount);
        Assert.True(command.ApproximateMemoryBytes < 8L * 1024 * 1024);
        Assert.Equal(1, session.UndoDepth);

        Assert.True(session.Undo());
        Assert.Equal(0, ((BrushLayer)session.Document.FindLayer(brushId)!).Tiles.TileCount);
        Assert.True(session.Redo());
        Assert.Equal(command.TouchedTileCount,
            ((BrushLayer)session.Document.FindLayer(brushId)!).Tiles.TileCount);
    }

    [Fact]
    public void Spatial_index_for_50k_objects_returns_only_viewport_candidates()
    {
        var objects = new List<SceneObject>(50_000);
        for (var i = 0; i < 50_000; i++)
        {
            var x = (i % 250) * 260.0;
            var y = (i / 250) * 260.0;
            objects.Add(new SceneObject(
                ObjectId.New(), SceneObjectKind.Rectangle, $"o{i}",
                x, y, 24, 24, 0, "#FFFFFFFF", "#00000000", 0, true, false));
        }

        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        var index = ObjectSpatialIndex.For(layer);
        var candidates = index.Query(new WorldBounds(10_000, 10_000, 11_920, 11_080));

        Assert.Equal(50_000, index.ObjectCount);
        Assert.NotEmpty(candidates);
        Assert.True(candidates.Count < 100, $"Viewport returned {candidates.Count} candidates.");
        Assert.All(candidates, item =>
            Assert.True(SceneGeometry.BoundsOf(item).Intersects(
                new WorldBounds(10_000, 10_000, 11_920, 11_080))));
    }

    [Fact]
    public void Mask_command_changes_only_its_owner_and_round_trips_undo()
    {
        var owner = new ObjectLayer(
            LayerId.New(), "owner", true, false, 1, [], LayerMask.CreateEmpty());
        var neighbour = new ObjectLayer(LayerId.New(), "neighbour", true, false, 1, []);
        var document = new MapDocument(
            DocumentId.New(), "mask", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                owner,
                neighbour,
            ]);
        var stroke = new RasterStrokeSession(owner.Mask!.Tiles, new BrushSettings { Radius = 20 });
        stroke.AddPoint(300, 300);
        var (Before, After) = stroke.CommitCoverage(255, subtract: true);
        var session = new EditingSession(document);

        session.Execute(new MaskStrokeCommand(owner.Id, Before, After));

        Assert.NotEmpty(session.Document.FindLayer(owner.Id)!.Mask!.Tiles.Keys);
        Assert.Null(session.Document.FindLayer(neighbour.Id)!.Mask);
        Assert.True(session.Undo());
        Assert.Empty(session.Document.FindLayer(owner.Id)!.Mask!.Tiles.Keys);
    }
}
