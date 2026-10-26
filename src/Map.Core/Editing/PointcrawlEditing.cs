using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The pointcrawl of the map replaced as one undo step (R32).</summary>
public sealed class SetPointcrawlCommand(MapPointcrawl before, MapPointcrawl after, string name) : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document)
    {
        after.Validate();
        return document with { Pointcrawl = after };
    }

    public MapDocument Revert(MapDocument document) => document with { Pointcrawl = before };
}

/// <summary>
/// R32 «Раскладка»: places pushed apart and ways pulled to a comfortable length (a spring layout
/// in the manner of Fruchterman and Reingold), starting from where the places stand and kept
/// inside the canvas with a margin. The same input gives the same result.
/// </summary>
public static class PointcrawlLayout
{
    public static MapPointcrawl Arrange(MapPointcrawl crawl, double width, double height, int iterations = 400)
    {
        var count = crawl.Nodes.Length;
        if (count < 2) return crawl;
        var margin = Math.Min(width, height) * .06;
        var (x, y) = (crawl.Nodes.Select(n => n.X).ToArray(), crawl.Nodes.Select(n => n.Y).ToArray());
        // Places at one spot are spread on a small circle first, deterministically.
        for (var i = 0; i < count; i++)
            for (var j = 0; j < i; j++)
                if (Math.Abs(x[i] - x[j]) < 1 && Math.Abs(y[i] - y[j]) < 1)
                {
                    x[i] += Math.Cos(i * 2.399) * 20; y[i] += Math.Sin(i * 2.399) * 20;
                }
        var area = (width - (2 * margin)) * (height - (2 * margin));
        var k = Math.Sqrt(area / count) * .8;
        var index = crawl.Nodes.Select((n, i) => (n.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var links = crawl.Edges.Select(e => (index[e.From], index[e.To])).ToArray();
        var temperature = Math.Min(width, height) / 8;
        for (var step = 0; step < iterations; step++)
        {
            var (dx, dy) = (new double[count], new double[count]);
            for (var i = 0; i < count; i++)
                for (var j = i + 1; j < count; j++)
                {
                    var (ox, oy) = (x[i] - x[j], y[i] - y[j]);
                    var d = Math.Max(1, Math.Sqrt((ox * ox) + (oy * oy)));
                    var force = k * k / d;
                    dx[i] += ox / d * force; dy[i] += oy / d * force;
                    dx[j] -= ox / d * force; dy[j] -= oy / d * force;
                }
            foreach (var (a, b) in links)
            {
                var (ox, oy) = (x[a] - x[b], y[a] - y[b]);
                var d = Math.Max(1, Math.Sqrt((ox * ox) + (oy * oy)));
                var force = d * d / k;
                dx[a] -= ox / d * force; dy[a] -= oy / d * force;
                dx[b] += ox / d * force; dy[b] += oy / d * force;
            }
            for (var i = 0; i < count; i++)
            {
                var length = Math.Max(1e-9, Math.Sqrt((dx[i] * dx[i]) + (dy[i] * dy[i])));
                var move = Math.Min(length, temperature);
                x[i] = Math.Clamp(x[i] + (dx[i] / length * move), margin, width - margin);
                y[i] = Math.Clamp(y[i] + (dy[i] / length * move), margin, height - margin);
            }
            temperature *= .985;
        }
        return crawl with { Nodes = [.. crawl.Nodes.Select((n, i) => n with { X = Math.Round(x[i], 1), Y = Math.Round(y[i], 1) })] };
    }
}
