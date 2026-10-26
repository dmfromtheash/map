using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R27: the players see the hidden part of a painted map as opaque dark ground; the master sees a veil; without fog nothing changes.</summary>
public sealed class FogRenderingTests
{
    [Fact]
    public void Players_see_dark_ground_where_hidden_the_master_a_veil()
    {
        var stroke = MapFogEditor.Begin(new MapFog { Enabled = true });
        MapFogEditor.Dab(stroke, 100, 200, 90, true, 400, 400);
        var document = Document() with { Fog = MapFogEditor.Finish(stroke) };

        using var players = Render(document, new RenderOptions { ViewScale = 1, HexPlayerView = true });
        Assert.True(Bright(players.GetPixel(100, 200)), "the revealed circle shows the map");
        Assert.True(Dark(players.GetPixel(330, 200)), "the hidden part is dark ground");

        using var master = Render(document, new RenderOptions { ViewScale = 1, FogVeil = .55 });
        var veiled = master.GetPixel(330, 200);
        Assert.False(Dark(veiled));
        Assert.False(Bright(veiled), "the master sees the map dimmed");
        Assert.True(Bright(master.GetPixel(100, 200)));

        using var plain = Render(document with { Fog = document.Fog with { Enabled = false } }, new RenderOptions { ViewScale = 1, HexPlayerView = true });
        Assert.True(Bright(plain.GetPixel(330, 200)));
    }

    private static bool Bright(SKColor c) => c.Red > 200 && c.Green > 200 && c.Blue > 200;
    private static bool Dark(SKColor c) => c.Red < 70 && c.Green < 70 && c.Blue < 70;

    private static MapDocument Document() =>
        new(DocumentId.New(), "Fog", DateTimeOffset.UnixEpoch, new CanvasDefinition(400, 400, "px", "#FFF4EEDC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF4EEDC")]);

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
