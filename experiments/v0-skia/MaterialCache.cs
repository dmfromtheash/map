using SkiaSharp;

namespace V0Skia;

internal sealed record FrameBuffers(SKImage Albedo, SKImage Normal, SKImage Data) : IDisposable
{
    public void Dispose() { Albedo.Dispose(); Normal.Dispose(); Data.Dispose(); }
}

/// <summary>
/// The light-independent half of the pipeline: tiles of albedo, normals and data (light mask,
/// relief height, water/shore) drawn once from the document's art. Changing the light never
/// touches these tiles; <see cref="TileBuilds"/> proves it. A frame is assembled from the visible
/// tiles into three frame buffers (a cheap blit), which the light pass then reads.
/// </summary>
internal sealed class MaterialCache : IDisposable
{
    public const int TileSize = 512;
    private readonly SceneInputs _scene;
    private readonly GRContext? _gpu;
    private readonly Dictionary<(int, int), Tile> _tiles = [];

    public int TileBuilds { get; private set; }
    public int FrameAssemblies { get; private set; }

    public MaterialCache(SceneInputs scene, GRContext? gpu)
    {
        _scene = scene;
        _gpu = gpu;
    }

    private sealed record Tile(SKImage Albedo, SKImage Normal, SKImage Data) : IDisposable
    {
        public void Dispose() { Albedo.Dispose(); Normal.Dispose(); Data.Dispose(); }
    }

    public SKSurface NewSurface(int w, int h, SKColorType type = SKColorType.Rgba8888) =>
        (_gpu is null
            ? SKSurface.Create(new SKImageInfo(w, h, type, SKAlphaType.Premul))
            : SKSurface.Create(_gpu, false, new SKImageInfo(w, h, type, SKAlphaType.Premul)))
        ?? throw new InvalidOperationException("surface");

    private Tile GetTile(int tx, int ty)
    {
        if (_tiles.TryGetValue((tx, ty), out var t)) return t;
        TileBuilds++;
        var origin = new SKPoint(tx * TileSize, ty * TileSize);
        t = new Tile(Draw(origin, 0), Draw(origin, 1), Draw(origin, 2));
        _tiles[(tx, ty)] = t;
        return t;
    }

    private SKImage Draw(SKPoint origin, int layer)
    {
        using var surface = NewSurface(TileSize, TileSize);
        var c = surface.Canvas;
        c.Clear(SKColors.Black);
        c.Translate(-origin.X, -origin.Y);
        // Data and normals are numbers: linear resampling of the sprites only, no colour filters.
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        var ground = layer switch { 0 => _scene.GroundAlbedo, 1 => _scene.GroundNormal, _ => _scene.GroundData };
        c.DrawImage(ground, 0, 0);
        foreach (var m in _scene.Mountains)
        {
            var img = layer switch { 0 => m.Albedo, 1 => m.Normal, _ => m.Data };
            var dst = SKRect.Create(m.X, m.Y, img.Width * m.Scale, img.Height * m.Scale);
            c.DrawImage(img, dst, sampling);
        }
        return surface.Snapshot();
    }

    /// <summary>
    /// Assembles the frame rect (document px) from tiles into frame-sized buffers.
    /// Light-independent: done on pan, not on light change.
    /// </summary>
    public FrameBuffers AssembleFrame(SKRectI frame)
    {
        var (albedo, normal, data) = AssembleRaw(frame);
        return new FrameBuffers(albedo, normal, data);
    }

    private (SKImage Albedo, SKImage Normal, SKImage Data) AssembleRaw(SKRectI frame)
    {
        FrameAssemblies++;
        using var a = NewSurface(frame.Width, frame.Height);
        using var n = NewSurface(frame.Width, frame.Height);
        using var d = NewSurface(frame.Width, frame.Height);
        foreach (var s in new[] { a, n, d }) s.Canvas.Clear(SKColors.Black);
        int tx0 = (int)Math.Floor(frame.Left / (double)TileSize), ty0 = (int)Math.Floor(frame.Top / (double)TileSize);
        int tx1 = (int)Math.Floor((frame.Right - 1) / (double)TileSize), ty1 = (int)Math.Floor((frame.Bottom - 1) / (double)TileSize);
        for (var ty = ty0; ty <= ty1; ty++)
        for (var tx = tx0; tx <= tx1; tx++)
        {
            if (tx < 0 || ty < 0 || tx * TileSize >= _scene.Width || ty * TileSize >= _scene.Height) continue;
            var tile = GetTile(tx, ty);
            float x = tx * TileSize - frame.Left, y = ty * TileSize - frame.Top;
            a.Canvas.DrawImage(tile.Albedo, x, y);
            n.Canvas.DrawImage(tile.Normal, x, y);
            d.Canvas.DrawImage(tile.Data, x, y);
        }
        return (a.Snapshot(), n.Snapshot(), d.Snapshot());
    }

    public void Dispose()
    {
        foreach (var t in _tiles.Values) t.Dispose();
        _tiles.Clear();
    }
}
