using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>A branch with its own look is drawn in that look even when it is the only look on the road.</summary>
public sealed class Stage20RoadAuditRenderingTests
{
    [Fact]
    public void A_single_branch_with_its_own_surface_colour_is_drawn_in_that_colour()
    {
        var road = RoadEditor.NewRoad([new(20, 128), new(236, 128)], 24, RoadStyle.Country() with { SurfaceColor = "#FFCC2222", TextureId = null, ToneVariation = 0 }, "Дорога");
        var branch = road.Branches[0] with { Style = RoadStyle.Country() with { SurfaceColor = "#FF2244CC", TextureId = null, ToneVariation = 0 } };
        road = road with { Branches = [branch] };
        road.Validate();
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Roads = [road] };
        var document = new MapDocument(DocumentId.New(), "road", DateTimeOffset.UnixEpoch, new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), objects]);
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        var centre = bitmap.GetPixel(128, 128);
        Assert.True(centre.Blue > centre.Red + 60, $"the branch's own blue must be drawn, got {centre}");
    }
}
