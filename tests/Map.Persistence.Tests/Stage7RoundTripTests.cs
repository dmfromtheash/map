using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class Stage7RoundTripTests
{
    [Fact]
    public void Text_notes_grid_and_grid_mask_survive_reopen()
    {
        using var workspace = new TempWorkspace();
        var text = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label",
            10, 20, 300, 120, 4, "#FF111111", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Line one\nLine two", RequestedFontFamily = "Definitely Missing Font",
                FallbackFontFamily = "Arial", FontWeight = 700, Curve = 8,
                Alignment = TextAlignment.Center, FlipHorizontal = true,
            },
        };
        var maskPixels = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var gridLayer = new GridLayer(LayerId.New(), "grid", true, false, 0.8,
        [
            new GridDefinition(GridId.New(), GridGeometry.HexFlat, 128)
            {
                Subdivisions = 4, OffsetX = 13, RotationDegrees = 15, IncludeInExport = true,
            },
        ], new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), maskPixels), true));
        var note = new MapNote(NoteId.New(), "label note", "**local**", NoteAnchorKind.Object,
            objectId: text.Id);
        var document = new MapDocument(DocumentId.New(), "stage7", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [text]), gridLayer,
            ], notes: [note]);
        var path = workspace.File("stage7.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        Assert.Equivalent(text.Text, reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single().Text, strict: true);
        Assert.Equivalent(gridLayer.Grids, reopened.Layers.OfType<GridLayer>().Single().Grids, strict: true);
        Assert.Equal(maskPixels, reopened.Layers.OfType<GridLayer>().Single().Mask!.Tiles.TryGet(new TileKey(0, 0)));
        Assert.Equivalent(note, reopened.Notes.Single(), strict: true);
    }
}
