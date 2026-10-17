using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// Palette study for the regional map, read-only: opens MAP_REGIONAL_PROJECT, applies one named
/// variant of existing style parameters to an in-memory copy and renders it the way export does.
/// Nothing is saved; the chosen variant is then applied through the editor by native-regional-finish.
/// </summary>
internal static class RegionalArtProbe
{
    internal sealed record Palette(string Name, string Land, double Texture, string Outline, double InnerShadow,
        string Sea, string Water, string Bank, string RoadSurface, string RoadTone, string RoadEdge, double RoadEdgeOpacity,
        double RoadRuts, double RoadShoulder, double ObjectShadow, string ShadowColor);

    internal static readonly Palette[] Palettes =
    [
        new("original", "", 0, "", 0, "", "", "", "", "", "", 0, 0, 0, 0, ""),
        new("richer", "#FFC3C78F", .40, "#FF55665A", .12, "#FF7199A3", "#FF7199A3", "#FF4E665F",
            "#FFD2BC8F", "#FFB59C6D", "#FF5A4A34", .70, .20, .26, .32, "#FF2E3A2C"),
        new("deeper", "#FFB2BC80", .46, "#FF4B5C50", .14, "#FF638F9C", "#FF638F9C", "#FF465E58",
            "#FFD0B889", "#FFAE9464", "#FF514230", .78, .22, .30, .42, "#FF27321F"),
    ];

    internal static MapDocument Apply(MapDocument source, Palette palette)
    {
        if (palette.Name == "original") return source;
        var layers = source.Layers.Select(layer => layer switch
        {
            BaseColorLayer sea => (MapLayer)(sea with { Color = palette.Sea }),
            LandLayer land => land with
            {
                EdgeStyle = land.EdgeStyle with
                {
                    FillColor = palette.Land, FillTextureOpacity = palette.Texture,
                    OutlineColor = palette.Outline, InnerShadowOpacity = palette.InnerShadow,
                },
            },
            ObjectLayer objects => objects with
            {
                Rivers = [.. objects.Rivers.Select(r => r with { Style = r.Style with { WaterColor = palette.Water, BankColor = palette.Bank } })],
                Roads = [.. objects.Roads.Select(r => r with
                {
                    Style = r.Style with
                    {
                        SurfaceColor = palette.RoadSurface, ToneColor = palette.RoadTone, EdgeColor = palette.RoadEdge,
                        EdgeOpacity = palette.RoadEdgeOpacity, RutOpacity = palette.RoadRuts, ShoulderOpacity = palette.RoadShoulder,
                    },
                })],
                Effects = objects.Objects.Any(o => o.Stamp is not null) && palette.ObjectShadow > 0
                    ? objects.Effects with
                    {
                        OuterShadowOpacity = palette.ObjectShadow, ShadowColor = palette.ShadowColor,
                        ShadowBlur = 3, ShadowOffsetX = 1.5, ShadowOffsetY = 2.5,
                    }
                    : objects.Effects,
            },
            _ => layer,
        }).ToArray();
        return source.WithLayers(layers);
    }

    public static object Run(PerformanceOptions options)
    {
        var path = Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT")
            ?? throw new InvalidOperationException("MAP_REGIONAL_PROJECT is required.");
        var source = MapProjectStore.Open(path).Document;
        var results = new List<object>();
        foreach (var palette in Palettes)
        {
            var document = Apply(source, palette);
            using var stamps = new ExportStampImageSource(document, path, null);
            const double scale = 0.5;
            var width = (int)(document.Canvas.Width * scale); var height = (int)(document.Canvas.Height * scale);
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
            using var tiles = new TileImageCache(2048, 256L * 1024 * 1024);
            tiles.BeginFrame();
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)scale);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                IsExport = true, ViewScale = scale, VisibleWorld = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height),
                TileCache = tiles, Stamps = stamps, LodTier = RenderLodTier.Exact,
            });
            surface.Canvas.Flush(); tiles.EndFrame();
            var file = Path.ChangeExtension(options.OutputPath, "." + palette.Name + ".png");
            using (var image = surface.Snapshot()) using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var output = File.Create(file)) data.SaveTo(output);
            results.Add(new { palette = palette.Name, file, milliseconds = watch.Elapsed.TotalMilliseconds });
            Console.WriteLine($"{palette.Name}: {file} {watch.Elapsed.TotalMilliseconds:0} ms");
        }
        return results;
    }
}
