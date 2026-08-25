using Map.Spike.Core.Commands;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;

namespace Map.Spike.Tests.Core;

public class BrushAndMaskCommandTests
{
    private static SpikeDocument NewDocument() =>
        SpikeFixtures.CreateObjectScene(50, canvasSize: 1024);

    [Fact]
    public void One_stroke_produces_exactly_one_history_entry()
    {
        var document = NewDocument();
        var history = new CommandHistory();

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        for (var i = 0; i < 40; i++)
        {
            session.AddPoint(new Vec2(300 + (i * 6), 300 + (i * 3)));
        }

        var command = session.Complete();
        Assert.NotNull(command);
        history.Execute(document, command!);

        Assert.Equal(1, history.UndoDepth);
        Assert.Equal(0, history.RedoDepth);
    }

    [Fact]
    public void Stroke_touches_only_the_tiles_it_covers()
    {
        var document = NewDocument();

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(300, 300));
        session.AddPoint(new Vec2(340, 320));
        var command = session.Complete();

        Assert.NotNull(command);

        var expected = new HashSet<TileKey>();
        var radius = BrushSettings.Default.Radius;
        foreach (var key in command!.TouchedTiles)
        {
            expected.Add(key);
        }

        // Everything painted lies inside a 1-tile neighbourhood of the segment, so a stroke
        // crossing a 256px grid near (300,300)..(340,320) must not reach tile (0,0) or (3,3).
        Assert.DoesNotContain(new TileKey(0, 0), expected);
        Assert.DoesNotContain(new TileKey(3, 3), expected);
        Assert.Contains(new TileKey(1, 1), expected);
        Assert.True(expected.Count <= 6, $"Stroke touched {expected.Count} tiles, expected a handful.");
        Assert.True(radius > 0);
    }

    [Fact]
    public void Undo_then_redo_restores_the_layer_byte_for_byte()
    {
        var document = NewDocument();
        var history = new CommandHistory();

        var before = SnapshotRaster(document.Brush);

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(600, 600));
        session.AddPoint(new Vec2(660, 640));
        history.Execute(document, session.Complete()!);

        var afterStroke = SnapshotRaster(document.Brush);
        Assert.NotEqual(before.Count, afterStroke.Count);

        Assert.True(history.Undo(document));
        AssertRasterEquals(before, SnapshotRaster(document.Brush));

        Assert.True(history.Redo(document));
        AssertRasterEquals(afterStroke, SnapshotRaster(document.Brush));
    }

    [Fact]
    public void Undo_of_a_first_touch_stroke_removes_the_tiles_rather_than_leaving_blanks()
    {
        var document = NewDocument();
        var history = new CommandHistory();

        Assert.Equal(0, document.Brush.TileCount);

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(700, 700));
        history.Execute(document, session.Complete()!);

        Assert.True(document.Brush.TileCount > 0);

        history.Undo(document);
        Assert.Equal(0, document.Brush.TileCount);
    }

    [Fact]
    public void Stroke_does_not_touch_the_other_raster_layer()
    {
        var document = NewDocument();
        SpikeFixtures.FillDecorPatch(document, 2, 2, 0x203040FF);
        var decorBefore = SnapshotRaster(document.Decor);

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(100, 100));
        session.AddPoint(new Vec2(400, 400));
        new CommandHistory().Execute(document, session.Complete()!);

        AssertRasterEquals(decorBefore, SnapshotRaster(document.Decor));
    }

    [Fact]
    public void Mask_stroke_changes_the_mask_only()
    {
        var document = NewDocument();
        SpikeFixtures.FillDecorPatch(document, 2, 2, 0x203040FF);

        var brushSession = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        brushSession.AddPoint(new Vec2(300, 300));
        brushSession.AddPoint(new Vec2(400, 400));
        var history = new CommandHistory();
        history.Execute(document, brushSession.Complete()!);

        var brushBefore = SnapshotRaster(document.Brush);
        var decorBefore = SnapshotRaster(document.Decor);

        var maskSession = new MaskStrokeSession(document.Mask, BrushSettings.Default);
        maskSession.AddPoint(new Vec2(320, 320));
        maskSession.AddPoint(new Vec2(380, 380));
        var maskCommand = maskSession.Complete();

        Assert.NotNull(maskCommand);
        history.Execute(document, maskCommand!);

        AssertRasterEquals(brushBefore, SnapshotRaster(document.Brush));
        AssertRasterEquals(decorBefore, SnapshotRaster(document.Decor));
        Assert.True(document.Mask.TileCount > 0);

        // And it is reversible in one step, like any other edit.
        Assert.True(history.Undo(document));
        Assert.Equal(0, document.Mask.TileCount);
    }

    [Fact]
    public void Mask_targets_the_brush_layer_only()
    {
        var document = NewDocument();
        Assert.Equal(document.Brush.Id, document.Mask.TargetLayerId);
        Assert.NotEqual(document.Decor.Id, document.Mask.TargetLayerId);
    }

    [Fact]
    public void Executing_a_command_marks_the_document_dirty()
    {
        var document = NewDocument();
        document.MarkSaved(document.Revision);
        Assert.False(document.IsDirty);

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(500, 500));
        new CommandHistory().Execute(document, session.Complete()!);

        Assert.True(document.IsDirty);
    }

    [Fact]
    public void A_gesture_that_paints_nothing_creates_no_history_entry()
    {
        var document = NewDocument();
        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        Assert.Null(session.Complete());
    }

    [Fact]
    public void Undo_memory_is_bounded_by_the_touched_tiles_not_the_layer()
    {
        var document = NewDocument();
        var history = new CommandHistory();

        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(300, 300));
        session.AddPoint(new Vec2(330, 320));
        var command = session.Complete()!;
        history.Execute(document, command);

        var touched = command.TouchedTiles.Count;
        var tileBytes = document.Brush.TileByteLength;

        // before + after images of the touched tiles, and nothing else.
        Assert.True(command.ApproximateMemoryBytes <= (long)touched * tileBytes * 2);
        Assert.True(command.ApproximateMemoryBytes > 0);
    }

    private static Dictionary<TileKey, byte[]> SnapshotRaster(RasterLayer layer)
    {
        var snapshot = new Dictionary<TileKey, byte[]>();
        foreach (var key in layer.TileKeys)
        {
            snapshot[key] = (byte[])layer.TryGetTile(key)!.Clone();
        }

        return snapshot;
    }

    private static void AssertRasterEquals(
        Dictionary<TileKey, byte[]> expected, Dictionary<TileKey, byte[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, bytes) in expected)
        {
            Assert.True(actual.ContainsKey(key), $"Tile {key} is missing.");
            Assert.True(bytes.AsSpan().SequenceEqual(actual[key]), $"Tile {key} differs.");
        }
    }
}
