using Map.Core.Documents;
using Map.Persistence;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>CPU terrain stage timings for the same saved regional document, without owner UI or state.</summary>
internal static class RegionalTerrainProbe
{
    public static object Run()
    {
        var path = Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT")
            ?? throw new InvalidOperationException("MAP_REGIONAL_PROJECT is required.");
        var source = MapProjectStore.Open(path).Document;
        var document = source.WithLayers(source.Layers.Select(l => l is ObjectLayer objects
            ? objects with { Objects = [], PlacementAreas = [], PlacementPaths = [] } : l).ToArray());
        var measurements = new List<object>();
        foreach (var scale in new[] { 1d, 3d })
        {
            const int width = 820, height = 783;
            var view = new WorldBounds(1280-width/2d/scale,1140-height/2d/scale,
                1280+width/2d/scale,1140+height/2d/scale);
            using var surface = RenderImageCache.CreateSurface(null,width+2,height+2,view,scale)!;
            using var tiles = new TileImageCache(1024,64L*1024*1024);
            tiles.BeginFrame();
            var stats = new RenderStats();
            SceneRenderer.Render(surface.Canvas,document,new RenderOptions { ViewScale=scale,VisibleWorld=view,
                TileCache=tiles,Stats=stats,LodTier=RenderLodTier.Exact });
            surface.Canvas.Flush();tiles.EndFrame();
            measurements.Add(new { scale,stats.TerrainMilliseconds,stats.LandMilliseconds,stats.WaterMilliseconds,
                stats.RoadMilliseconds,stats.RiverMilliseconds,stats.RiverPassMilliseconds });
        }
        return measurements;
    }
}
