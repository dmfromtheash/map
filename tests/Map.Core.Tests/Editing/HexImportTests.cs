using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class HexImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_expands_canvas_and_reverts_new_or_replaced_layer_in_one_step(bool newLayer)
    {
        var doc = HexMapFactory.Create("Import", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Pointy, 16, 4, 4, "#FFFFFFFF");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        var after = layer with { Id = newLayer ? LayerId.New() : layer.Id, Board = HexBoard.Create(HexOrientation.Pointy, 16, 40, 30) };
        var session = new EditingSession(doc);
        session.Execute(new HexImportCommand(doc, after));
        Assert.Equal(1, session.UndoDepth);
        Assert.True(session.Document.Canvas.Width >= after.Board.PixelWidth);
        Assert.True(session.Document.Canvas.Height >= after.Board.PixelHeight);
        Assert.Equal(newLayer ? 2 : 1, session.Document.Layers.OfType<HexLayer>().Count());
        Assert.Equal(doc.Category, session.Document.Category);
        Assert.True(session.Undo()); Assert.Same(doc, session.Document);
        Assert.True(session.Redo()); Assert.Equal(40, ((HexLayer)session.Document.FindLayer(after.Id)!).Board.Columns);
    }
}
