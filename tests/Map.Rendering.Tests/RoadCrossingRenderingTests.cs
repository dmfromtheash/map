using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R06: the crossing is drawn over the water in the kind chosen for it.</summary>
public sealed class RoadCrossingRenderingTests
{
    [Fact]
    public void A_bridge_covers_the_water_a_ford_lightens_it_and_none_leaves_the_river()
    {
        var document = Document(RoadCrossingKind.None);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var road = layer.Roads.Single();
        MapDocument WithCrossing(RoadCrossingKind kind) => document.WithLayer(layer.Id, _ => layer with
        {
            Roads = [RoadEditor.SetCrossingKind(road, road.Branches[0].Id, layer.Rivers[0].Channels[0].Id, 0, kind)],
        });
        // Compare the same road (including its seed and branch IDs); only the crossing changes.
        using var none = Render(document);
        using var bridge = Render(WithCrossing(RoadCrossingKind.Bridge));
        using var ford = Render(WithCrossing(RoadCrossingKind.Ford));
        var water = none.GetPixel(200, 200);
        var deck = bridge.GetPixel(200, 200);
        var shallow = ford.GetPixel(200, 200);
        Assert.True(water.Blue > water.Red, $"without a crossing the river shows at the meeting point, got {water}");
        Assert.True(deck.Red > deck.Blue + 20, $"the bridge deck is warm wood over the water, got {deck}");
        Assert.True(shallow.Red + shallow.Green + shallow.Blue > water.Red + water.Green + water.Blue + 30,
            $"the ford lightens the water, got {shallow} over {water}");
        // Away from the river the road itself is unchanged by the crossing.
        var a = none.GetPixel(60, 200); var b = bridge.GetPixel(60, 200);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(RoadCrossingKind.Bridge)]
    [InlineData(RoadCrossingKind.Ford)]
    [InlineData(RoadCrossingKind.Ferry)]
    public void A_crossing_just_outside_the_view_still_draws_its_part_inside(RoadCrossingKind kind)
    {
        // The crossing sits at x = 200; the view ends at 185 and at 199, the bridge, ford or ferry
        // reaches past that, so it must still be drawn (crossings far from the view are left out).
        foreach (var (left, right) in new[] { (0, 185), (0, 199), (201, 400), (215, 400) })
        {
            var view = new Map.Core.Documents.WorldBounds(left, 0, right, 400);
            using var with = RenderView(Document(kind), view);
            using var without = RenderView(Document(RoadCrossingKind.None), view);
            var differs = false;
            for (var y = 170; y < 230 && !differs; y++)
            for (var x = 0; x < with.Width && !differs; x++)
                differs = with.GetPixel(x, y) != without.GetPixel(x, y);
            Assert.True(differs, $"{kind}: nothing of the crossing in the view {left}-{right}");
        }
    }

    [Fact]
    public void Many_rivers_and_roads_in_a_custom_order_draw_as_ordered()
    {
        // The roads under every river, then reversed: the explicit order decides what covers what.
        var rivers = Enumerable.Range(0, 12).Select(i => new River
        {
            Name = $"Река {i}", Style = RiverStyle.Illustrated() with { EdgeRoughness = 0, HighlightOpacity = 0 },
            Channels = [RiverEditor.NewChannel([new(20 + 30 * i, 0), new(20 + 30 * i, 400)], 14)],
        }).ToArray();
        var roads = Enumerable.Range(0, 12).Select(i => RoadEditor.NewRoad([new(0, 20 + 30 * i), new(400, 20 + 30 * i)], 10)).ToArray();
        roads = [.. roads.Select(r => r.Branches.Aggregate(r, (road, branch) =>
            rivers.Aggregate(road, (current, river) => RoadEditor.SetCrossingKind(current, branch.Id, river.Channels[0].Id, 0, RoadCrossingKind.None))))];
        var layer = new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Rivers = [.. rivers], Roads = [.. roads] };
        MapDocument With(ObjectLayer l) => new(DocumentId.New(), "order", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), l]);
        var defaultOrder = With(layer);
        var explicitDefault = With(layer with { SurfaceOrder = [.. roads.Select(r => r.Id), .. rivers.Select(r => r.Id)] });
        var waterFirst = With(layer with { SurfaceOrder = [.. rivers.Select(r => r.Id), .. roads.Select(r => r.Id)] });
        using var a = Render(defaultOrder);
        using var b = Render(explicitDefault);
        using var c = Render(waterFirst);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.NotEqual(a.Bytes, c.Bytes);
        // Where road 0 (y = 20) meets river 0 (x = 20): water over the road by default, road over the water after.
        var meet = a.GetPixel(20, 20); var under = c.GetPixel(20, 20);
        Assert.True(meet.Blue > meet.Red, $"default: the river covers the road, got {meet}");
        Assert.True(under != meet, $"water first: the road covers the river, got {under}");
    }

    private static SKBitmap RenderView(MapDocument document, Map.Core.Documents.WorldBounds view)
    {
        var info = new SKImageInfo((int)(view.Right - view.Left), (int)(view.Bottom - view.Top));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1, VisibleWorld = view });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    private static MapDocument Document(RoadCrossingKind kind)
    {
        var river = new River { Name = "Река", Style = RiverStyle.Illustrated() with { EdgeRoughness = 0, HighlightOpacity = 0 }, Channels = [RiverEditor.NewChannel([new(200, 0), new(200, 400)], 40)] };
        var road = RoadEditor.NewRoad([new(0, 200), new(400, 200)], 24);
        road = RoadEditor.SetCrossingKind(road, road.Branches[0].Id, river.Channels[0].Id, 0, kind);
        var layer = new ObjectLayer(LayerId.New(), "Дороги", true, false, 1, []) { Rivers = [river], Roads = [road] };
        return new MapDocument(DocumentId.New(), "crossing", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
