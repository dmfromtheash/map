using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class HexExportSceneTests
{
    [Fact]
    public void Underground_scene_excludes_surface_walls_and_hidden_layers_without_mutating_document()
    {
        var document = World(out var surface, out var under);
        var scene = new ExportScene(document, -1, ExportRecipient.Players);
        var wall = Assert.Single(VttGeometry.Collect(scene).Walls);
        Assert.Equal(HexPathGeometry.Flatten(under.Paths[0], under.Board.Layout), wall);
        Assert.Equal(document.Category, scene.Category);
        Assert.True(scene.Players);
        Assert.DoesNotContain(surface.Id, scene.VisibleLayerIds);
        Assert.Contains(under.Id, scene.VisibleLayerIds);
        Assert.False(scene.RenderDocument().Layers.Single(l => l.Id == surface.Id).Visible);
        Assert.True(document.Layers.Single(l => l.Id == surface.Id).Visible);
        var hidden = document.WithLayer(under.Id, l => l with { Visible = false });
        Assert.Empty(VttGeometry.Collect(new ExportScene(hidden, -1)).Walls);
        Assert.Empty(VttGeometry.Collect(new ExportScene(document, -1, visibleLayerIds: [surface.Id])).Walls);
    }

    [Fact]
    public void A_new_political_border_has_no_wall_and_independent_flags_do_not_implicitly_enable_sight()
    {
        var document = World(out var surface, out _);
        var border = surface.Paths[0] with { BlocksSight = false };
        Assert.False(border.BlocksMove);
        Assert.False(border.BlocksLight);
        document = document.WithLayer(surface.Id, _ => surface with { Paths = [border with { BlocksMove = true, BlocksLight = true }] });
        Assert.Empty(VttGeometry.Collect(new ExportScene(document, 0)).Walls);
        Assert.False(border.SameAs(border with { BlocksSight = true }));
        Assert.False(border.SameAs(border with { BlocksMove = true }));
        Assert.False(border.SameAs(border with { BlocksLight = true }));
    }

    [Theory]
    [InlineData(HexRouteStyle.Edges)]
    [InlineData(HexRouteStyle.Centers)]
    [InlineData(HexRouteStyle.Meander)]
    public void Explicit_sight_flags_work_for_each_route_and_survive_board_remapping(HexRouteStyle style)
    {
        var document = World(out var surface, out _);
        var layout = surface.Board.Layout;
        var a = layout.FromOffset(new HexOffset(2, 2));
        var b = a.Neighbor(0);
        var path = new HexPath(HexPathId.New(), HexPathKind.Road, style,
            style == HexRouteStyle.Edges ? [] : [a, b],
            style == HexRouteStyle.Edges ? [layout.Canonical(new(a, 0)), layout.Canonical(new(a, 1))] : [])
            { BlocksSight = true, BlocksMove = true, BlocksLight = true };
        var remapped = Assert.Single(HexPaths.Remap([path], layout, h => h));
        Assert.True(path.SameAs(remapped));
        document = document.WithLayer(surface.Id, _ => surface with { Paths = [remapped] });
        var points = Assert.Single(VttGeometry.Collect(new ExportScene(document, 0)).Walls);
        Assert.True(points.Count >= 2);
        if (style != HexRouteStyle.Edges)
        {
            var (x, y) = layout.Center(a);
            Assert.Equal(new VectorPoint(x, y), points[0]);
            (x, y) = layout.Center(b);
            Assert.Equal(new VectorPoint(x, y), points[^1]);
        }
    }

    private static MapDocument World(out HexLayer surface, out HexLayer under)
    {
        var document = HexMapFactory.Create("Уровни", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Flat, 24, 8, 6, "#FFFFFFFF");
        surface = document.Layers.OfType<HexLayer>().Single();
        var layout = surface.Board.Layout;
        HexPath Wall(int column)
        {
            var hex = layout.FromOffset(new HexOffset(column, 2));
            return new(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
                [layout.Canonical(new(hex, 0)), layout.Canonical(new(hex, 1))]) { BlocksSight = true };
        }
        surface = surface with { Paths = [Wall(2)] };
        under = (HexLayer)(surface with { Level = -1, Paths = [Wall(5)] }).WithNewIds();
        var ground = surface;
        return document.WithLayers([.. document.Layers.Select(l => l.Id == ground.Id ? ground : l), under]);
    }
}
