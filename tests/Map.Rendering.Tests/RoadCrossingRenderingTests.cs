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
