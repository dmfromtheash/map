using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R17 🎨: the painted fog looks cover the unexplored hexes and leave the explored ones.</summary>
public sealed class HexFogLookTests
{
    [Theory]
    [InlineData(3)]
    public void A_painted_fog_hides_the_unknown_and_shows_the_explored(int look)
    {
        var (document, layer, explored, hidden) = Explored();
        using var plain = Render(document, 0, player: true);
        using var painted = Render(document, look, player: true);
        var (ex, ey) = layer.Board.Layout.Center(explored);
        var (hx, hy) = layer.Board.Layout.Center(hidden);
        // The explored hex looks the same; the hidden one is covered differently from the plain fog.
        Assert.Equal(plain.GetPixel((int)ex, (int)ey), painted.GetPixel((int)ex, (int)ey));
        Assert.NotEqual(plain.GetPixel((int)hx, (int)hy), painted.GetPixel((int)hx, (int)hy));
    }

    /// <summary>MAP_FOG_LOOKS=&lt;folder&gt;: the plain fog and the three painted looks, for players and for the game master.</summary>
    [Fact]
    public void Looks_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_FOG_LOOKS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var (document, _, _, _) = Explored();
        foreach (var look in new[] { 0, 3 })
            foreach (var player in new[] { true, false })
            {
                using var bitmap = Render(document, look, player);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(folder, $"fog-{look}-{(player ? "player" : "master")}.png"), data.ToArray());
            }
    }

    /// <summary>MAP_FOG_TIMING=1: frame time of the plain and the painted fog over a 128² board, half hidden.</summary>
    [Fact]
    public void Timing_for_review()
    {
        if (Environment.GetEnvironmentVariable("MAP_FOG_TIMING") is null) return;
        var document = HexMapFactory.Create("Большая", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 32, 128, 128, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var cells = new Dictionary<HexCoord, HexCell?>();
        for (var c = 0; c < 128; c++)
            for (var r = 0; r < 128; r++)
                cells[layer.Board.Layout.FromOffset(new HexOffset(c, r))] = HexCell.Of("starter/grass", 0, revealed: (c * 7 + r * 3) % 11 < 5 || c < 40);
        layer = layer with { Board = layer.Board.With(cells), FogEnabled = true };
        document = document.WithLayer(layer.Id, _ => layer);
        foreach (var look in new[] { 0, 3, 0, 3 })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++) { using var b = Render(document, look, player: true, scale: 0.25); }
            Console.WriteLine($"fog look {look}: {watch.ElapsedMilliseconds / 10.0:0.0} ms per frame");
        }
    }

    /// <summary>The workshop world, explored only along a winding party route and three hexes around it.</summary>
    private static (MapDocument Document, HexLayer Layer, HexCoord Explored, HexCoord Hidden) Explored()
    {
        var (document, layer, _, _) = HexWorkshopRenderingTests.World(HexOrientation.Flat);
        var layout = layer.Board.Layout;
        HexCoord At(int c, int r) => layout.FromOffset(new HexOffset(c, r));
        var route = new[] { At(1, 7), At(2, 6), At(3, 6), At(4, 5), At(5, 5), At(6, 4), At(7, 4), At(8, 3), At(9, 3) };
        var cells = new Dictionary<HexCoord, HexCell?>();
        for (var c = 0; c < 14; c++)
            for (var r = 0; r < 10; r++)
            {
                var hex = At(c, r);
                var near = route.Any(p => HexCoord.Distance(p, hex) <= 1) || HexCoord.Distance(At(9, 3), hex) <= 2;
                cells[hex] = layer.Board[hex]?.WithRevealed(near);
            }
        layer = layer with
        {
            Board = layer.Board.With(cells),
            Party = new HexParty { Position = route[^1], History = [.. route], Visited = [.. route] },
        };
        return (document.WithLayer(layer.Id, _ => layer), layer, At(5, 5), At(12, 8));
    }

    private static SKBitmap Render(MapDocument document, int look, bool player, double scale = 1)
    {
        var bitmap = new SKBitmap((int)(document.Canvas.Width * scale), (int)(document.Canvas.Height * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale((float)scale);
        var previous = HexLayerRenderer.FogLook;
        HexLayerRenderer.FogLook = look;
        try { SceneRenderer.Render(canvas, document, new RenderOptions { ViewScale = scale, HexPlayerView = player }); }
        finally { HexLayerRenderer.FogLook = previous; }
        return bitmap;
    }
}
