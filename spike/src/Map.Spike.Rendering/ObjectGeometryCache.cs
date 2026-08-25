using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;
using SkiaSharp;

namespace Map.Spike.Rendering;

/// <summary>
/// Static, chunked triangle cache for the object layer.
///
/// Objects in the spike never move, so every outline is triangulated once into world-space
/// vertex buffers, one per spatial chunk, with per-vertex colours. A frame then issues one
/// DrawVertices call per visible chunk. Panning changes only which chunks are visited; nothing is
/// rebuilt.
///
/// Triangles rather than merged paths: a path holding thousands of sub-outlines is not convex, so
/// Skia renders it with stencil-and-cover and re-tessellates it when the transform changes. That
/// was measured at ~17 ms median for this scene, which cannot hold 60 FPS. Triangles skip both.
///
/// The cache is derived state. It is rebuilt from the document and never written back to it.
/// </summary>
public sealed class ObjectGeometryCache : IDisposable
{
    public const double DefaultChunkSize = 512;
    private const int CircleSegments = 16;

    private readonly Dictionary<(int X, int Y), Chunk> _chunks = [];
    private bool _disposed;

    private ObjectGeometryCache(double chunkSize) => ChunkSize = chunkSize;

    public double ChunkSize { get; }
    public int ChunkCount => _chunks.Count;
    public int SourceObjectCount { get; private set; }
    public int TriangleCount { get; private set; }

    /// <summary>Bytes of vertex data held, for the memory report: 8 per position + 4 per colour.</summary>
    public long ApproximateVertexBytes => (long)TriangleCount * 3 * 12;

    public static ObjectGeometryCache Build(ObjectLayer layer, double chunkSize = DefaultChunkSize)
    {
        var cache = new ObjectGeometryCache(chunkSize) { SourceObjectCount = layer.Count };

        var grouped = new Dictionary<(int X, int Y), List<SpikeObject>>();

        foreach (var o in layer.Objects)
        {
            var key = (
                (int)Math.Floor(o.Center.X / chunkSize),
                (int)Math.Floor(o.Center.Y / chunkSize));

            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }

            list.Add(o);
        }

        foreach (var entry in grouped)
        {
            var positions = new List<SKPoint>();
            var colors = new List<SKColor>();
            var bounds = Rect2.Empty;

            foreach (var o in entry.Value)
            {
                AppendTriangles(o, positions, colors);
                bounds = bounds.Union(o.Bounds);
            }

            if (positions.Count == 0)
            {
                continue;
            }

            var vertices = SKVertices.CreateCopy(
                SKVertexMode.Triangles,
                [.. positions],
                [.. colors]);

            cache._chunks[entry.Key] = new Chunk(vertices, bounds, entry.Value.Count, positions.Count / 3);
            cache.TriangleCount += positions.Count / 3;
        }

        return cache;
    }

    /// <summary>Chunks overlapping <paramref name="visibleWorld"/>.</summary>
    public IEnumerable<Chunk> VisibleChunks(Rect2 visibleWorld)
    {
        var minX = (int)Math.Floor(visibleWorld.X / ChunkSize);
        var maxX = (int)Math.Floor(visibleWorld.Right / ChunkSize);
        var minY = (int)Math.Floor(visibleWorld.Y / ChunkSize);
        var maxY = (int)Math.Floor(visibleWorld.Bottom / ChunkSize);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (_chunks.TryGetValue((x, y), out var chunk) && chunk.WorldBounds.Intersects(visibleWorld))
                {
                    yield return chunk;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var chunk in _chunks.Values)
        {
            chunk.Vertices.Dispose();
        }

        _chunks.Clear();
    }

    private static void AppendTriangles(SpikeObject o, List<SKPoint> positions, List<SKColor> colors)
    {
        var color = ToSkColor(o.ColorArgb);
        var half = (float)(o.Size / 2);
        var cx = (float)o.Center.X;
        var cy = (float)o.Center.Y;
        var cos = (float)Math.Cos(o.Rotation);
        var sin = (float)Math.Sin(o.Rotation);

        SKPoint Rotate(float lx, float ly) =>
            new(cx + ((lx * cos) - (ly * sin)), cy + ((lx * sin) + (ly * cos)));

        void Triangle(SKPoint a, SKPoint b, SKPoint c)
        {
            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
            colors.Add(color);
            colors.Add(color);
            colors.Add(color);
        }

        switch (o.Kind)
        {
            case SpikeObjectKind.Square:
                {
                    var tl = Rotate(-half, -half);
                    var tr = Rotate(half, -half);
                    var br = Rotate(half, half);
                    var bl = Rotate(-half, half);
                    Triangle(tl, tr, br);
                    Triangle(tl, br, bl);
                    break;
                }

            case SpikeObjectKind.Triangle:
                Triangle(Rotate(0, -half), Rotate(half, half), Rotate(-half, half));
                break;

            case SpikeObjectKind.Circle:
                {
                    // Fixed 16-gon fan. At the zoom levels this scene is inspected at the difference
                    // from a true circle is sub-pixel.
                    var center = new SKPoint(cx, cy);
                    var previous = Rotate(half, 0);

                    for (var i = 1; i <= CircleSegments; i++)
                    {
                        var angle = i * 2 * Math.PI / CircleSegments;
                        var next = Rotate((float)(Math.Cos(angle) * half), (float)(Math.Sin(angle) * half));
                        Triangle(center, previous, next);
                        previous = next;
                    }

                    break;
                }
        }
    }

    public static SKColor ToSkColor(uint argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)((argb >> 24) & 0xFF));

    public sealed record Chunk(SKVertices Vertices, Rect2 WorldBounds, int ObjectCount, int TriangleCount);
}
