using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class HexTravelRenderingTests
{
    private static byte[] Draw(HexLayer layer, HexPresentation presentation, HexOverlay? overlay = null, bool player = false)
    {
        using var s = SKSurface.Create(new SKImageInfo(800, 600)); s.Canvas.Clear(SKColors.White);
        HexLayerRenderer.Draw(s.Canvas, layer, new(0, 0, 800, 600), new RenderOptions
        { IsExport = true, ViewScale = 1, HexPresentation = presentation, HexOverlay = overlay, HexPlayerView = player });
        using var image = s.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    [Fact]
    public void Winter_tint_only_changes_travel_view_and_render_does_not_mutate_document()
    {
        var board = HexBoard.Create(HexOrientation.Flat, 40, 10, 8);
        var h = new HexCoord(2, 2); board = board.With([new(h, HexCell.Of("starter/grass"))]);
        var summer = new HexLayer(LayerId.New(), "Мир", true, false, 1, board) { CampaignDay = 90 };
        var winter = summer with { CampaignDay = 270 };
        Assert.Equal(Draw(summer, HexPresentation.Terrain), Draw(winter, HexPresentation.Terrain));
        Assert.NotEqual(Draw(summer, HexPresentation.Travel), Draw(winter, HexPresentation.Travel));
        Assert.Same(board, winter.Board); Assert.True(winter.Journey.IsDefault);
    }
    [Fact]
    public void Four_route_styles_render_distinctly_and_player_never_receives_overlay()
    {
        var board = HexBoard.Create(HexOrientation.Flat, 40, 10, 8);
        var a = board.Layout.FromOffset(new(1, 3)); var b = board.Layout.FromOffset(new(8, 3));
        var l = new HexLayer(LayerId.New(), "Мир", true, false, 1, board) { CampaignDay = 90 };
        var journeys = new HexJourneyPlanner(l, _ => 1, _ => false).Routes(a, b);
        var look = new HexOverlay(l.Id) { Journeys = journeys };
        var hashes = Enum.GetValues<TravelMode>().Select(mode => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Draw(l, HexPresentation.Terrain, look with { TravelMode = mode }))));
        Assert.Equal(4, hashes.Distinct().Count());
        Assert.Equal(Draw(l, HexPresentation.Terrain, player: true), Draw(l, HexPresentation.Terrain, look, player: true));
    }
}
