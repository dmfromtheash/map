using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The cost of one styled coast at editor size, with the distance field and with the filter
/// passes. Written to MAP_ROUGH_COAST_PNG/timing.txt for review; the assertion only guards
/// against a gross regression, because this PC's load varies several-fold between runs.
/// </summary>
public sealed class CoastTimingTests
{
    [Fact]
    public void Distance_field_coast_is_not_slower_than_the_filter_passes_at_editor_size()
    {
        var output = Environment.GetEnvironmentVariable("MAP_ROUGH_COAST_PNG");
        if (string.IsNullOrWhiteSpace(output)) return; // measured on request only

        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля") with
        {
            EdgeStyle = new LandEdgeStyle
            {
                FillColor = "#FFD6DBC0", OutlineWidth = 3, OuterShadowOpacity = .25, InnerShadowOpacity = .18,
                ShadowBlur = 8, RippleWidth = 2, RippleSpacing = 10, RippleCount = 3,
            },
        };
        var stroke = new RasterStrokeSession(land.GeometryTiles,
            new BrushSettings { Radius = 220, RoughCellSize = 64, RoughSmoothing = .5, Spacing = .15 });
        foreach (var (x, y) in new[] { (500.0, 600.0), (900.0, 400.0), (1400.0, 500.0), (1900.0, 800.0), (1500.0, 1100.0), (900.0, 1000.0) })
            stroke.AddPoint(x, y);
        var (_, after) = stroke.CommitCoverage(0, false);
        land = land with { GeometryTiles = land.GeometryTiles.WithMany(after) };
        var document = new MapDocument(DocumentId.New(), "Время", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2560, 1440, "px", "#FF9FB7C4"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9FB7C4"), land,
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);

        double Measure(bool field)
        {
            var best = double.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var info = new SKImageInfo(2560, 1440);
                using var surface = SKSurface.Create(info)!;
                var started = Stopwatch.GetTimestamp();
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, CoastFilters = !field });
                surface.Canvas.Flush();
                best = Math.Min(best, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            return best;
        }

        var withFilters = Measure(false);
        var withField = Measure(true);

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "timing.txt"),
            $"2560x1440 styled coast, best of 3: filters {withFilters:0} ms, distance field {withField:0} ms{Environment.NewLine}");
        Assert.True(withField < withFilters * 3, $"field {withField:0} ms vs filters {withFilters:0} ms");
    }
}
