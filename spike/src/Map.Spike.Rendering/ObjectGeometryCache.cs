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

    /// <summary>
    /// Largest distance by which any chunk's real bounds stick out past its own cell, measured from
    /// the built data rather than assumed. This is what makes <see cref="VisibleChunks"/> correct for
    /// an object of any size instead of only for objects smaller than one chunk.
    /// </summary>
    public double MaxOverhang { get; private set; }

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

            cache._chunks[entry.Key] = new Chunk(
                entry.Key, vertices, bounds, entry.Value.Count, positions.Count / 3);
            cache.TriangleCount += positions.Count / 3;

            // How far this chunk's content reaches outside its own cell, in each direction.
            var cellLeft = entry.Key.X * chunkSize;
            var cellTop = entry.Key.Y * chunkSize;

            cache.MaxOverhang = Math.Max(cache.MaxOverhang, Math.Max(
                Math.Max(cellLeft - bounds.X, bounds.Right - (cellLeft + chunkSize)),
                Math.Max(cellTop - bounds.Y, bounds.Bottom - (cellTop + chunkSize))));
        }

        return cache;
    }

    /// <summary>
    /// Chunks whose actual content overlaps <paramref name="visibleWorld"/>.
    ///
    /// An object is filed under the chunk of its CENTRE, so its bounds routinely stick out past that
    /// chunk's cell. Scanning only the cells the viewport itself covers therefore drops objects that
    /// genuinely intersect the viewport, and they blink out at the edge during pan and zoom.
    ///
    /// The scan range is widened by <see cref="MaxOverhang"/> - the largest distance by which any
    /// chunk's real bounds exceed its own cell, measured from the data at build time. Because every
    /// chunk's bounds fit inside its cell inflated by that value, a chunk touching the viewport is
    /// always inside the widened range, whatever the object sizes are. The exact
    /// <see cref="Rect2.Intersects"/> test below then drops the extra candidates, so the drawn set
    /// stays exact and each object is still drawn exactly once.
    /// </summary>
    public IEnumerable<Chunk> VisibleChunks(Rect2 visibleWorld)
    {
        var search = MaxOverhang > 0 ? visibleWorld.Inflate(MaxOverhang) : visibleWorld;

        var minX = (int)Math.Floor(search.X / ChunkSize);
        var maxX = (int)Math.Floor(search.Right / ChunkSize);
        var minY = (int)Math.Floor(search.Y / ChunkSize);
        var maxY = (int)Math.Floor(search.Bottom / ChunkSize);

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

    /// <param name="Cell">Grid coordinate this chunk is stored under. Exposed so a test can compare
    /// culling against a brute-force oracle, and so diagnostics can name a chunk.</param>
    public sealed record Chunk(
        (int X, int Y) Cell,
        SKVertices Vertices,
        Rect2 WorldBounds,
        int ObjectCount,
        int TriangleCount);
}
