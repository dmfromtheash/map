using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class HexStructureRenderingTests
{
    [Fact]
    public void Presentations_render_distinctly_without_mutation_and_player_pixels_match_sanitized_data()
    {
        var doc = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 48, 5, 5, "#FFFFFFFF");
        var layer = doc.Layers.OfType<HexLayer>().Single();
        var hex = layer.Board.Layout.FromOffset(new HexOffset(2,2));
        layer = layer with { Board = layer.Board.With([new(hex, HexCell.Of("starter/grass", region:1).WithPointsOfInterest(
            [new HexPoi("starter/village", "Город"), new HexPoi("starter/ruins", "Секрет", false)]))]), Regions = [new HexRegion(1,"Край","#FFBB3344")] };
        var before = layer.Board;
        byte[] Draw(HexLayer source, HexPresentation view)
        {
            using var surface = SKSurface.Create(new SKImageInfo(512,512));
            surface.Canvas.Clear(SKColors.White);
            HexLayerRenderer.Draw(surface.Canvas, source, new WorldBounds(0,0,512,512), new RenderOptions
                { IsExport = true, ViewScale = 1, HexPresentation = view });
            using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png,100);
            return data.ToArray();
        }
        var views = Enum.GetValues<HexPresentation>().Select(view => Draw(layer,view)).ToArray();
        Assert.Equal(4, views.Select(bytes => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))).Distinct().Count());
        Assert.Equal(Draw(ExportPlayerVisibility.Sanitize(layer),HexPresentation.PlayerKnowledge), views[3]);
        Assert.Same(before, layer.Board); Assert.Equal(2, layer.Board[hex]!.PointsOfInterest.Count);
    }
}
