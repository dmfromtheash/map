using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R29: a transition is marked at its ends on this map, only on the hex level it belongs to.</summary>
public sealed class TransitionRenderingTests
{
    [Fact]
    public void A_transition_is_marked_at_both_ends_on_this_map()
    {
        var document = new MapDocument(DocumentId.New(), "T", DateTimeOffset.UnixEpoch, new CanvasDefinition(400, 400, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")])
        {
            Transitions = [new MapTransition { Kind = TransitionKind.Stairs, From = new(null, 100, 100), To = new(null, 300, 300, 2) }],
        };
        using var near = Render(document, null);
        Assert.True(Inked(near, 100, 100));
        Assert.True(Inked(near, 300, 300));
        Assert.False(Inked(near, 200, 200));
        using var level0 = Render(document, 0);
        Assert.False(Inked(level0, 300, 300), "the second end is on level 2");
    }

    private static bool Inked(SKBitmap bitmap, int cx, int cy)
    {
        for (var y = cy - 10; y <= cy + 10; y++)
            for (var x = cx - 10; x <= cx + 10; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 90 && c.Green < 90 && c.Blue < 90) return true;
            }
        return false;
    }

    private static SKBitmap Render(MapDocument document, int? level)
    {
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1, HexLevel = level });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
