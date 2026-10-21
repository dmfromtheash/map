using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class PlayerExportSceneTests
{
    [Fact]
    public void Player_scene_strips_hidden_cells_regions_private_keys_and_links_without_editing_source()
    {
        var doc = World(out var layer);
        var scene = new ExportScene(doc, 0, ExportRecipient.Players);
        var clean = Assert.Single(scene.Layers.OfType<HexLayer>());
        Assert.Equal(3, clean.Board.CellCount);
        var note = Assert.Single(clean.Notes).Value;
        Assert.Equal("Public title", note.Title);
        Assert.Equal("Public text", note.PlayerText);
        Assert.Empty(note.GmText);
        Assert.Null(note.ChildMap);
        Assert.Single(clean.Regions);
        Assert.Equal(1, clean.Regions[0].Id);
        Assert.Equal(3, scene.Package.NotesTotal);
        Assert.Equal(1, scene.Package.NotesIncluded);
        Assert.Equal(1, scene.Package.LabelsIncluded);
        Assert.Equal(1, scene.Package.FogNotes);
        Assert.Equal(layer.Board.Columns * layer.Board.Rows - 3, scene.Package.FogHexes);
        Assert.Equal(4, layer.Board.CellCount);
        Assert.Equal("GM secret", layer.Notes[H(layer, 2)].GmText);
        Assert.Same(doc, new ExportScene(doc).RenderDocument());
        Assert.Same(layer, Assert.Single(doc.Layers.OfType<HexLayer>()));
        var twice = new ExportScene(scene.RenderDocument(), 0, ExportRecipient.Players);
        Assert.Equal(clean.Notes, Assert.Single(twice.Layers.OfType<HexLayer>()).Notes);
    }

    [Theory]
    [InlineData(HexRouteStyle.Centers)]
    [InlineData(HexRouteStyle.Meander)]
    public void A_path_cannot_bridge_hidden_hexes_in_player_data_or_vtt(HexRouteStyle style)
    {
        var doc = World(out var layer);
        var route = new[] { H(layer, 2), H(layer, 3), H(layer, 4), H(layer, 5), H(layer, 6) };
        layer = layer with
        {
            Board = layer.Board.With(route.Select((h, i) => new KeyValuePair<HexCoord, HexCell?>(h,
                HexCell.Of("starter/grass", revealed: i != 2)))),
            Paths = [new HexPath(HexPathId.New(), HexPathKind.Road, style, [.. route], []) { BlocksSight = true }],
        };
        doc = doc.WithLayer(layer.Id, _ => layer);
        var scene = new ExportScene(doc, 0, ExportRecipient.Players);
        var clean = Assert.Single(scene.Layers.OfType<HexLayer>());
        Assert.Equal(2, clean.Paths.Count);
        Assert.Equal(route[..2], clean.Paths[0].Hexes);
        Assert.Equal(route[3..], clean.Paths[1].Hexes);
        Assert.Equal(2, VttGeometry.Collect(scene).Walls.Count);
        Assert.Single(VttGeometry.Collect(doc).Walls);
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void Edge_walls_are_removed_on_either_hidden_side_regardless_of_canonical_corner(HexOrientation orientation)
    {
        var doc = HexMapFactory.Create("Edges", DateTimeOffset.UnixEpoch, new("hex"), orientation, 24, 8, 6, "#FFFFFFFF");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        var hex = layout.FromOffset(new(3, 3));
        var path = new HexPath(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
            [layout.Canonical(new(hex, 0)), layout.Canonical(new(hex, 1))]) { BlocksSight = true };
        var area = hex.Neighbors().Prepend(hex).ToArray();
        layer = layer with { FogEnabled = true, Paths = [path], Board = layer.Board.With(area.Select(h =>
            new KeyValuePair<HexCoord, HexCell?>(h, HexCell.Of("starter/grass", revealed: true)))) };
        Assert.Single(VttGeometry.Collect(new ExportScene(doc.WithLayer(layer.Id, _ => layer), 0, ExportRecipient.Players)).Walls);
        layer = layer with { Board = layer.Board.With([new(hex, HexCell.Of("starter/grass"))]) };
        Assert.Empty(VttGeometry.Collect(new ExportScene(doc.WithLayer(layer.Id, _ => layer), 0, ExportRecipient.Players)).Walls);
    }

    [Fact]
    public void Fog_off_keeps_unrevealed_content_but_still_removes_private_notes_and_links()
    {
        var doc = World(out var layer);
        layer = layer with { FogEnabled = false };
        var clean = new ExportScene(doc.WithLayer(layer.Id, _ => layer), 0, ExportRecipient.Players).Layers.OfType<HexLayer>().Single();
        Assert.Equal(4, clean.Board.CellCount);
        Assert.Equal(2, clean.Notes.Count);
        Assert.All(clean.Notes.Values, n => { Assert.Empty(n.GmText); Assert.Null(n.ChildMap); });
    }

    private static HexCoord H(HexLayer l, int c) => l.Board.Layout.FromOffset(new(c, 2));

    private static MapDocument World(out HexLayer layer)
    {
        var doc = HexMapFactory.Create("Package", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Pointy, 24, 8, 6, "#FFFFFFFF");
        layer = doc.Layers.OfType<HexLayer>().Single();
        var publicHex = H(layer, 2);
        var gmHex = H(layer, 3);
        var blankHex = H(layer, 4);
        var hiddenHex = H(layer, 5);
        layer = layer with
        {
            FogEnabled = true,
            Board = layer.Board.With([new(publicHex, HexCell.Of("starter/grass", region: 1, revealed: true)),
                new(gmHex, HexCell.Of("starter/grass", revealed: true)), new(blankHex, HexCell.Of("starter/grass", revealed: true)),
                new(hiddenHex, HexCell.Of("starter/peaks", region: 2))]),
            Regions = [new(1, "Public region", "#FF008800"), new(2, "Hidden region", "#FF880000")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty
                .Add(publicHex, new("Public title", gmText: "GM secret", playerText: "Public text", childMap: "secret.mapproj"))
                .Add(gmHex, new("GM title", gmText: "GM body", playerText: "  "))
                .Add(hiddenHex, new("Hidden title", playerText: "Hidden text")),
        };
        var result = layer;
        return doc.WithLayer(layer.Id, _ => result);
    }
}
