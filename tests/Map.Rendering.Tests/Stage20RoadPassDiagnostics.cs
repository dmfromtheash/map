using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>Stress of the body construction over fresh ids: every union must stay near the sum of its parts.</summary>
public sealed class Stage20RoadPassDiagnostics
{
    private static double Area(SKPath path)
    {
        var info = new SKImageInfo(512, 384, SKColorType.Alpha8, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(0.5f);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        surface.Canvas.DrawPath(path, paint);
        using var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        var count = 0;
        var span = bitmap.GetPixelSpan();
        foreach (var b in span) if (b > 0) count++;
        return count;
    }

    [Fact]
    public void Body_construction_stress()
    {
        var failures = new List<string>();
        for (var run = 0; run < 40; run++)
        {
            var scene = Stage20RoadRenderingTests.Settlement(RoadDrawing.PenRuts, withRiver: false, withHouses: false);
            foreach (var (road, name) in new[] { (scene.Main, "main"), (scene.Block, "block") })
            {
                var set = RoadGeometry.Evaluate(road);
                var parts = new List<SKPath>();
                foreach (var g in set.Branches.Values)
                {
                    var o = RoadGeometry.Outline(g, road.Seed);
                    using var poly = new SKPath { FillType = SKPathFillType.Winding };
                    foreach (var contour in o.Contours)
                    {
                        poly.MoveTo((float)contour[0].X, (float)contour[0].Y);
                        for (var i = 1; i < contour.Count; i++) poly.LineTo((float)contour[i].X, (float)contour[i].Y);
                        poly.Close();
                    }
                    foreach (var (centre, jointRadius) in o.Joints) poly.AddCircle((float)centre.X, (float)centre.Y, (float)jointRadius);
                    var polyArea = Area(poly);
                    var body = RoadRenderer.BuildBranchBody(g, road.Seed);
                    var bodyArea = Area(body);
                    if (Math.Abs(bodyArea - polyArea) > polyArea * 0.08 + 20)
                        failures.Add($"run {run} {name} branch {g.Branch.Name}: simplify area {bodyArea} vs raw {polyArea}");
                    parts.Add(body);
                }
                var union = new SKPath(parts[0]);
                for (var i = 1; i < parts.Count; i++)
                {
                    var merged = new SKPath();
                    var ok = union.Op(parts[i], SKPathOp.Union, merged);
                    if (!ok) { failures.Add($"run {run} {name}: union op failed at {i}"); merged.Dispose(); union.AddPath(parts[i]); }
                    else { union.Dispose(); union = merged; }
                }
                var unionArea = Area(union);
                var sum = parts.Sum(Area);
                if (unionArea > sum * 1.05 + 20) failures.Add($"run {run} {name}: union area {unionArea} > sum of parts {sum}");
                using var filleted = RoadRenderer.JoinFillets(union, set);
                var filletArea = Area(filleted);
                if (filletArea > unionArea * 1.2 + 40 || filletArea < unionArea - 2)
                    failures.Add($"run {run} {name}: fillets area {filletArea} vs union {unionArea}");
                foreach (var part in parts) part.Dispose();
                union.Dispose();
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}

public sealed class Stage20RoadJunctionCloseup
{
    [Fact]
    public void Two_looks_junction_at_six_times()
    {
        var scene = Stage20RoadRenderingTests.Settlement(RoadDrawing.Country, withRiver: false, withHouses: false);
        var node = scene.Main.FindNode(scene.Main.Branches[0].Vertices[1].NodeId)!;
        using var closeup = Stage20RoadRenderingTests.Render(scene.Document, 6, new WorldBounds(node.X - 70, node.Y - 60, node.X + 70, node.Y + 60), 840, 720);
        Stage20RoadRenderingTests.SaveForReview(closeup, "two-looks-6x.png");
        var fork = scene.Main.FindNode(scene.ForkNode)!;
        using var forkCloseup = Stage20RoadRenderingTests.Render(scene.Document, 6, new WorldBounds(fork.X - 70, fork.Y - 60, fork.X + 70, fork.Y + 60), 840, 720);
        Stage20RoadRenderingTests.SaveForReview(forkCloseup, "fork-6x.png");
    }
}
