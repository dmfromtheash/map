using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The geometry of a land's cliffs over one square of the map, computed once from the coverage and
/// kept: for every texel, how far below the edge of the land it lies along the line of sight (the
/// "depth", in world units; 0 on the land itself), how many level steps the drop has there, and
/// which way the edge it hangs from faces. Everything the wall looks like - faces, ink, hatching,
/// wash, height, gaps - is drawn from these three numbers by <see cref="LandCliffShader"/>, so a
/// slider, a pan or a zoom never recomputes them; only an edit of the land itself does.
/// A field covers 256×256 texels; at detail level <c>lod</c> one texel is 2^lod world units, so a
/// zoomed-out view uses a few coarse fields instead of thousands of fine ones.
/// </summary>
public sealed class LandCliffField
{
    public const int Size = 256;
    /// <summary>Depth of a texel no edge lies above within reach.</summary>
    public const float None = 60000f;

    private LandCliffField(int lod, int fx, int fy, Half[]? texels, WorldBounds? wall, bool[]? blocks, int reachTexels, object[] inputs)
    {
        Lod = lod; Fx = fx; Fy = fy; Texels = texels; Wall = wall; Blocks = blocks; ReachTexels = reachTexels; Inputs = inputs;
    }

    /// <summary>Side of the blocks the wall is drawn in, in texels.</summary>
    public const int Block = 16;
    private const int Blocks1D = Size / Block;

    public int Lod { get; }
    public int Fx { get; }
    public int Fy { get; }
    /// <summary>RGBA half floats: depth, steps, facing angle (radians from south), 1. Null when there is no wall.</summary>
    public Half[]? Texels { get; }
    /// <summary>World bounds of every texel that has a wall or its shadow; null when there is none.</summary>
    public WorldBounds? Wall { get; }
    /// <summary>Which 16×16-texel blocks hold some wall or shadow, row by row.</summary>
    private bool[]? Blocks { get; }

    /// <summary>
    /// The area the wall shader has to run over: the blocks that hold some wall, joined into runs
    /// along each row. A wall is a narrow band along the coast; its bounding square is mostly empty
    /// and the shader costs as much on an empty pixel as the CPU export path can afford.
    /// </summary>
    public SKPath WallArea()
    {
        var path = new SKPath();
        if (Blocks is null) return path;
        var block = Block * Texel;
        for (var by = 0; by < Blocks1D; by++)
            for (var bx = 0; bx < Blocks1D; bx++)
            {
                if (!Blocks[by * Blocks1D + bx]) continue;
                var start = bx;
                while (bx + 1 < Blocks1D && Blocks[by * Blocks1D + bx + 1]) bx++;
                path.AddRect(new SKRect((float)(Left + start * block), (float)(Top + by * block),
                    (float)(Left + (bx + 1) * block), (float)(Top + (by + 1) * block)));
            }
        return path;
    }
    private int ReachTexels { get; }
    private object[] Inputs { get; }

    public double Texel => 1 << Lod;
    public double WorldSize => Size * Texel;
    public double Left => Fx * WorldSize;
    public double Top => Fy * WorldSize;

    private readonly object _wallGate = new();
    private object? _wallKey;
    private byte[]? _wallPixels;

    /// <summary>
    /// The finished wall of this square at its own texel resolution, drawn once per look and kept:
    /// at zooms where a texel is no larger than a device pixel, a rebuilt terrain (a pan settling,
    /// a zoom, a return to a view) places this image instead of running the wall shader again.
    /// Null when the shader is unavailable or the square has no wall.
    /// </summary>
    internal SKImage? WallImage(LandCliffStyle style, string rimColor, double rimWidth)
    {
        if (Texels is null) return null;
        var key = (style, rimColor, rimWidth);
        byte[]? pixels;
        lock (_wallGate) pixels = Equals(_wallKey, key) ? _wallPixels : null;
        var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (pixels is null)
        {
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)(1 / Texel));
            surface.Canvas.Translate((float)-Left, (float)-Top);
            using (var fieldShader = ToShader(out var image))
            using (image)
            using (var shader = LandCliffShader.Create(fieldShader, style, rimColor, rimWidth, Texel))
            {
                if (shader is null) return null;
                using var paint = new SKPaint { Shader = shader };
                using var area = WallArea();
                surface.Canvas.DrawPath(area, paint);
            }
            using var bitmap = new SKBitmap(info);
            if (!surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0)) return null;
            pixels = bitmap.GetPixelSpan().ToArray();
            lock (_wallGate) { _wallKey = key; _wallPixels = pixels; }
        }
        using var data = SKData.CreateCopy(pixels);
        return SKImage.FromPixels(info, data, info.RowBytes);
    }

    /// <summary>The field as an image shader in world coordinates, for <see cref="LandCliffShader"/>.</summary>
    internal SKShader ToShader(out SKImage image)
    {
        var info = new SKImageInfo(Size, Size, SKColorType.RgbaF16, SKAlphaType.Premul);
        var bytes = new byte[Size * Size * 8];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(Texels.AsSpan()).CopyTo(bytes);
        using var data = SKData.CreateCopy(bytes);
        image = SKImage.FromPixels(info, data, info.RowBytes)!;
        var matrix = SKMatrix.CreateScaleTranslation((float)Texel, (float)Texel, (float)Left, (float)Top);
        return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), matrix);
    }

    // ------------------------------------------------------------------ cache

    private sealed record Key(LayerId Layer, int Lod, int Fx, int Fy);
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, LandCliffField Field)>> Entries = [];
    private static readonly LinkedList<(Key Key, LandCliffField Field)> Order = new();
    private static long _bytes;
    /// <summary>Managed bytes the kept fields may hold; the least recently used go first.</summary>
    public static long Budget { get; set; } = 160L * 1024 * 1024;
    public static int Computed;
    /// <summary>Diagnostics: total time spent computing fields and drawing walls, in milliseconds.</summary>
    public static double ComputeMilliseconds, DrawMilliseconds;

    /// <summary>
    /// The field of one square for this land, from the cache when nothing it was computed from has
    /// changed (same coverage tiles, same lower land and levels, same lean, reach at least as far).
    /// </summary>
    public static LandCliffField Get(LandLayer land, IReadOnlyList<LandLayer> beneath, double lean, double reach,
        int lod, int fx, int fy)
    {
        var texel = 1 << lod;
        var reachTexels = (int)Math.Ceiling(reach / texel) + 2;
        var leanKey = Math.Round(lean, 3);
        var inputs = InputsOf(land, beneath, leanKey, reachTexels, lod, fx, fy);
        var key = new Key(land.Id, lod, fx, fy);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var node) && node.Value.Field.ReachTexels >= reachTexels
                && SameInputs(node.Value.Field.Inputs, inputs))
            {
                Order.Remove(node); Order.AddFirst(node);
                return node.Value.Field;
            }
        }
        // A little more reach than asked, so dragging the height slider up does not recompute
        // the field on every step.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var computed = Compute(land, beneath, leanKey, reachTexels + (reachTexels / 3) + 4, lod, fx, fy, inputs);
        lock (Gate) ComputeMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Increment(ref Computed);
        lock (Gate)
        {
            if (Entries.Remove(key, out var old)) { Order.Remove(old); _bytes -= Bytes(old.Value.Field); }
            var node = Order.AddFirst((key, computed));
            Entries[key] = node; _bytes += Bytes(computed);
            while (_bytes > Budget && Order.Last is { } last && !ReferenceEquals(last, node))
            {
                Order.RemoveLast(); Entries.Remove(last.Value.Key); _bytes -= Bytes(last.Value.Field);
            }
        }
        return computed;
    }

    /// <summary>Forgets every kept field (memory pressure, tests).</summary>
    public static void Clear()
    {
        lock (Gate) { Entries.Clear(); Order.Clear(); _bytes = 0; }
    }

    private static long Bytes(LandCliffField field) => (field.Texels?.Length ?? 0) * 3L + 256;

    /// <summary>
    /// What a field depends on: the coverage tiles of this land in its window (above it, within
    /// reach, and sideways by the lean), and the coverage tiles and levels of the lower land in
    /// the square itself. Tiles are immutable arrays, so a reference is their identity.
    /// </summary>
    private static object[] InputsOf(LandLayer land, IReadOnlyList<LandLayer> beneath, double lean, int reachTexels,
        int lod, int fx, int fy)
    {
        var texel = 1 << lod;
        var (left, top, right, bottom) = Window(lean, reachTexels, lod, fx, fy);
        var list = new List<object> { land.Level, lean };
        foreach (var key in land.GeometryTiles.KeysIntersecting(left * texel, top * texel, right * texel, bottom * texel))
        { list.Add(key); list.Add(land.GeometryTiles.TryGet(key)!); }
        var size = Size * texel;
        foreach (var lower in beneath)
        {
            list.Add(lower.Level);
            foreach (var key in lower.GeometryTiles.KeysIntersecting(fx * size, fy * size, (fx + 1) * size, (fy + 1) * size))
            { list.Add(key); list.Add(lower.GeometryTiles.TryGet(key)!); }
        }
        return [.. list];
    }

    private static bool SameInputs(object[] a, object[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] is byte[] || b[i] is byte[]) { if (!ReferenceEquals(a[i], b[i])) return false; }
            else if (!Equals(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>Texel rectangle (global texel coordinates at this lod) the rays of a square can start in.</summary>
    private static (int Left, int Top, int Right, int Bottom) Window(double lean, int reachTexels, int lod, int fx, int fy)
    {
        var x0 = fx * Size; var y0 = fy * Size;
        var sideways = (int)Math.Ceiling(Math.Abs(lean) * reachTexels) + 3;
        return (x0 - (lean > 0 ? sideways : 3), y0 - reachTexels - 1, x0 + Size + (lean < 0 ? sideways : 3), y0 + Size);
    }

    // ------------------------------------------------------------------ computation

    private static LandCliffField Compute(LandLayer land, IReadOnlyList<LandLayer> beneath, double lean, int reachTexels,
        int lod, int fx, int fy, object[] inputs)
    {
        var texel = 1 << lod;
        var (left, top, right, bottom) = Window(lean, reachTexels, lod, fx, fy);
        var width = right - left; var height = bottom - top;
        var coverage = new byte[width * height];
        var sampler = new Sampler(land.GeometryTiles, lod);
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                coverage[row * width + column] = sampler.At(left + column, top + row);

        // Rows top to bottom; each texel continues the ray of the texel one row up and "lean"
        // texels to the side. The side step is the difference of floor(lean·row) between two
        // global rows, so a ray drifts by exactly lean texels per row however far it runs, and
        // two squares side by side follow the very same rays.
        var depth = new float[width]; var facing = new float[width]; var inside = new bool[width];
        var nextDepth = new float[width]; var nextFacing = new float[width]; var nextInside = new bool[width];
        Array.Fill(depth, None);
        var texels = new Half[Size * Size * 4];
        var floor = FloorLevels(beneath, lod, fx, fy);
        var any = false;
        var blocks = new bool[Blocks1D * Blocks1D];
        int minX = Size, minY = Size, maxX = -1, maxY = -1;
        var limit = (reachTexels + 1) * (float)texel;
        for (var row = 0; row < height; row++)
        {
            var globalRow = top + row;
            var shift = (int)(Math.Floor(lean * globalRow) - Math.Floor(lean * (globalRow - 1)));
            for (var column = 0; column < width; column++)
            {
                var value = coverage[row * width + column];
                if (value >= 128)
                {
                    nextDepth[column] = 0; nextInside[column] = true; nextFacing[column] = 0;
                    continue;
                }
                nextInside[column] = false;
                var source = column - shift;
                if (row == 0 || source < 0 || source >= width) { nextDepth[column] = None; nextFacing[column] = 0; continue; }
                if (inside[source])
                {
                    // The first texel off the land: the edge lies where the coverage crosses one half
                    // between the land texel above and this one.
                    var above = coverage[(row - 1) * width + source];
                    var fraction = above == value ? 1f : Math.Clamp((above - 127.5f) / (above - value), 0f, 1f);
                    nextDepth[column] = (1 - fraction) * texel + 0.001f;
                    nextFacing[column] = EdgeFacing(coverage, width, height, source, row - 1);
                }
                else
                {
                    var carried = depth[source];
                    nextDepth[column] = carried >= limit ? None : carried + texel;
                    nextFacing[column] = facing[source];
                }
            }
            (depth, nextDepth) = (nextDepth, depth);
            (facing, nextFacing) = (nextFacing, facing);
            (inside, nextInside) = (nextInside, inside);

            var y = globalRow - fy * Size;
            if (y < 0) continue;
            for (var x = 0; x < Size; x++)
            {
                var column = x + fx * Size - left;
                var d = depth[column];
                var steps = floor is null ? land.Level : land.Level - floor[y * Size + x];
                var offset = (y * Size + x) * 4;
                texels[offset] = (Half)d;
                texels[offset + 1] = (Half)Math.Max(0, steps);
                texels[offset + 2] = (Half)facing[column];
                texels[offset + 3] = (Half)1f;
                if (d > 0 && d < None && steps > 0)
                {
                    any = true;
                    blocks[(y / Block) * Blocks1D + (x / Block)] = true;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        WorldBounds? wall = any
            ? new WorldBounds((fx * Size + minX) * (double)texel, (fy * Size + minY) * (double)texel,
                (fx * Size + maxX + 1) * (double)texel, (fy * Size + maxY + 1) * (double)texel)
            : null;
        return new LandCliffField(lod, fx, fy, any ? texels : null, wall, any ? blocks : null, reachTexels, inputs);
    }

    /// <summary>
    /// The level of the ground under every texel of the square: the highest lower land covering
    /// it, or the sea at level 0. Null when no lower land touches the square (all sea).
    /// </summary>
    private static int[]? FloorLevels(IReadOnlyList<LandLayer> beneath, int lod, int fx, int fy)
    {
        int[]? floor = null;
        foreach (var lower in beneath.OrderBy(l => l.Level))
        {
            var sampler = new Sampler(lower.GeometryTiles, lod);
            var size = Size * (1 << lod);
            if (!lower.GeometryTiles.KeysIntersecting(fx * size, fy * size, (fx + 1) * size, (fy + 1) * size).Any()) continue;
            floor ??= new int[Size * Size];
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                    if (sampler.At(fx * Size + x, fy * Size + y) >= 128) floor[y * Size + x] = lower.Level;
        }
        return floor;
    }

    /// <summary>
    /// The direction an edge faces, as an angle from south (0) - negative towards the west, positive
    /// towards the east - read from the coverage gradient over a 5×5 neighbourhood, so a raster
    /// staircase does not make every texel a face of its own.
    /// </summary>
    private static float EdgeFacing(byte[] coverage, int width, int height, int column, int row)
    {
        float gx = 0, gy = 0;
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var c = Math.Clamp(column + dx, 0, width - 1); var r = Math.Clamp(row + dy, 0, height - 1);
                var v = coverage[r * width + c];
                gx += v * dx; gy += v * dy;
            }
        // The outward normal points against the gradient of coverage.
        var nx = -gx; var ny = -gy;
        if (nx * nx + ny * ny < 1e-3f) return 0;
        return MathF.Atan2(nx, ny);
    }

    /// <summary>Coverage of a tile map at a texel of some lod: one byte at lod 0, a 2×2 average above.</summary>
    private struct Sampler(TileMap map, int lod)
    {
        private TileKey _key = new(int.MinValue, int.MinValue);
        private byte[]? _tile;

        public byte At(int texelX, int texelY)
        {
            if (lod == 0) return Byte(texelX, texelY);
            var texel = 1 << lod; var quarter = texel / 4; var three = (3 * texel) / 4;
            var wx = texelX * texel; var wy = texelY * texel;
            return (byte)((Byte(wx + quarter, wy + quarter) + Byte(wx + three, wy + quarter)
                + Byte(wx + quarter, wy + three) + Byte(wx + three, wy + three) + 2) / 4);
        }

        private byte Byte(int x, int y)
        {
            var key = new TileKey(x >> 8, y >> 8);
            if (key != _key) { _key = key; _tile = map.TryGet(key); }
            return _tile is null ? (byte)0 : _tile[((y & 255) << 8) | (x & 255)];
        }
    }
}
