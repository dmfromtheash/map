using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage7TextGridTests
{
    [Theory]
    [InlineData(0.005)]
    [InlineData(1)]
    [InlineData(64)]
    public void Grid_snap_is_the_same_world_result_at_every_zoom(double zoom)
    {
        var grid = new GridDefinition(GridId.New(), GridGeometry.HexPointy, 96)
        {
            Subdivisions = 2, OffsetX = 13, OffsetY = -7, RotationDegrees = 17,
        };
        var viewport = new Viewport(); viewport.Resize(1000, 700); viewport.Set(-200, 300, zoom);
        var screen = viewport.WorldToScreen(431.25, 278.75);
        var world = viewport.ScreenToWorld(screen.X, screen.Y);

        var snapped = GridSnapService.Snap(grid, new VectorPoint(world.X, world.Y));
        var expected = GridSnapService.Snap(grid, new VectorPoint(431.25, 278.75));

        Assert.Equal(expected.X, snapped.X, 8);
        Assert.Equal(expected.Y, snapped.Y, 8);
    }

    [Fact]
    public void Notes_validate_markdown_keep_stable_id_and_report_detached_anchor()
    {
        var document = Document();
        var missing = ObjectId.New();
        var note = new MapNote(NoteId.New(), "place", "[safe](https://example.com)",
            NoteAnchorKind.Object, objectId: missing);
        var session = new EditingSession(document);
        session.Execute(new AddNoteCommand(note));

        Assert.True(session.Document.Notes.Single().IsDetached(session.Document));
        Assert.Equal(note.Id, session.Document.Notes.Single().Id);
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Notes);
        Assert.Throws<ArgumentException>(() => new MapNote(
            NoteId.New(), "bad", "<script>", NoteAnchorKind.World));
    }

    [Fact]
    public void Object_node_and_portal_snap_preserve_their_ids()
    {
        var grid = new GridDefinition(GridId.New(), GridGeometry.Square, 100);
        var line = VectorGeometry.CreateLine(ObjectId.New(), "road",
            [new VectorPoint(21, 19), new VectorPoint(184, 91)]);
        line = VectorObjectEditor.AddPortal(line, 0.43, 12);
        var nodeId = line.Line!.Nodes[0].Id; var portalId = line.Line.Portals[0].Id;

        var objectSnap = GridSnapService.SnapObject(line, grid);
        var nodeSnap = GridSnapService.SnapNode(line, nodeId, grid);
        var portalSnap = GridSnapService.SnapPortal(line, portalId, grid);

        Assert.Equal(line.Id, objectSnap.Id);
        Assert.Contains(nodeSnap.Line!.Nodes, node => node.Id == nodeId);
        Assert.Contains(portalSnap.Line!.Portals, portal => portal.Id == portalId);
    }

    private static MapDocument Document() => new(
        DocumentId.New(), "stage7", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF")]);
}
