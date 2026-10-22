using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// R17 🎨 — the fog over unexplored hexes as the owner chose it on 2026-10-22 («Облака», after
/// Civilization VI, Humankind and old explorers' maps): a blue-grey watercolour mist in the palette
/// of his mountains with a billowing edge, instead of a flat dark fill. For the game master the same
/// mist is see-through. The merged outline and its billowing edge are built once per board state
/// (the board is immutable: every reveal makes a new one); the clouds are one small tile repeated,
/// so a frame only paints.
/// </summary>
public static partial class HexLayerRenderer
{
    /// <summary>The look the owner chose on 2026-10-22: «Облака».</summary>
    public const int DefaultFogLook = 3;

    /// <summary>0 - the plain dark fog of older builds; 3 - «Облака». Per thread, so a review render never leaks into other drawing.</summary>
    public static int FogLook { get => _fogLook ?? DefaultFogLook; set => _fogLook = value; }

    [ThreadStatic] private static int? _fogLook;

    private static readonly ConditionalWeakTable<HexBoard, PaintedFogShape> PaintedFogShapes = [];

    /// <summary>Every hidden hex of a board merged into one outline with a billowing edge.</summary>
    private sealed class PaintedFogShape
    {
        public PaintedFogShape(HexBoard board)
        {
            using var all = new SKPath { FillType = SKPathFillType.Winding };
            var rect = BoardRect(board);
            foreach (var key in board.ChunksIn(new WorldBounds(rect.Left, rect.Top, rect.Right, rect.Bottom)))
            {
                var chunk = board.Chunk(key);
                if (chunk is not null && Info(chunk).Revealed == ExtentCount(board, key)) continue;
                using var part = BuildFogPath(board, key, chunk);
                all.AddPath(part);
            }
            using var merged = new SKPath();
            if (all.IsEmpty || !all.Simplify(merged)) { merged.Reset(); merged.AddPath(all); }
            Edge = Billow(merged, (float)board.Layout.Size);
            _layout = board.Layout;
            Bounds = BoardRect(board);
            Bounds.Inflate((float)board.Layout.Size * 2, (float)board.Layout.Size * 2);
        }

        private readonly HexLayout _layout;

        public SKPath Edge { get; }

        /// <summary>The board area the painted image covers.</summary>
        public SKRect Bounds { get; }

        /// <summary>
        /// The painted fog of the whole board for players (opaque) or the game master, drawn once at
        /// up to 2048 px on its long side - the mist is soft, a frame only places the picture.
        /// </summary>
        public SKImage Image(bool player)
        {
            lock (RecentImages)
            {
                for (var i = 0; i < RecentImages.Count; i++)
                {
                    var (owner, forPlayer, image) = RecentImages[i];
                    if (forPlayer != player || !owner.TryGetTarget(out var shape) || !ReferenceEquals(shape, this)) continue;
                    RecentImages.RemoveAt(i);
                    RecentImages.Add((owner, forPlayer, image));
                    return image;
                }
                var resolution = Math.Min(1f, 2048f / Math.Max(Bounds.Width, Bounds.Height));
                var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * resolution));
                var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * resolution));
                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
                surface.Canvas.Scale(resolution);
                surface.Canvas.Translate(-Bounds.Left, -Bounds.Top);
                PaintFog(surface.Canvas, Edge, player, resolution, _layout);
                var made = surface.Snapshot();
                // Only the last few pictures are kept: the undo history holds many boards, and a
                // picture for each would hold hundreds of megabytes.
                // A picture pushed out is left to the collector, not disposed: a frame on another
                // thread may still be drawing it.
                if (RecentImages.Count >= 4) RecentImages.RemoveAt(0);
                RecentImages.Add((new WeakReference<PaintedFogShape>(this), player, made));
                return made;
            }
        }
    }

    /// <summary>The painted fog pictures in use, oldest first; at most four.</summary>
    private static readonly List<(WeakReference<PaintedFogShape> Owner, bool Player, SKImage Image)> RecentImages = [];

    /// <summary>The fog outline broken into a soft billowing line, about a sixth of a hex deep.</summary>
    private static SKPath Billow(SKPath fog, float size)
    {
        var edge = new SKPath();
        using var shaper = new SKPaint { Style = SKPaintStyle.Fill, PathEffect = SKPathEffect.CreateDiscrete(size * 0.5f, size * 0.16f, 7) };
        if (fog.IsEmpty || !shaper.GetFillPath(fog, edge)) { edge.Reset(); edge.AddPath(fog); }
        return edge;
    }

    /// <summary>A tile of two-tone blue-grey cloud, made once and repeated: noise is far too dear to compute per frame.</summary>
    private static readonly Lazy<SKImage> CloudTile = new(() =>
    {
        const int side = 256;
        using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        using var noise = SKShader.CreatePerlinNoiseFractalNoise(4f / side, 4f / side, 4, 11, new SKPointI(side, side));
        using var tint = SKColorFilter.CreateColorMatrix(
        [
            0, 0, 0, 0, 0x5D / 255f,
            0, 0, 0, 0, 0x6E / 255f,
            0, 0, 0, 0, 0x82 / 255f,
            0.9f, 0, 0, 0, -0.32f,
        ]);
        using var paint = new SKPaint { Shader = noise, ColorFilter = tint };
        surface.Canvas.DrawRect(new SKRect(0, 0, side, side), paint);
        return surface.Snapshot();
    });

    private static void PaintFog(SKCanvas canvas, SKPath edge, bool player, double scale, HexLayout layout, double share = 1, WorldBounds? view = null)
    {
        var bounds = edge.Bounds;
        if (bounds.IsEmpty) return;
        var size = (float)layout.Size;
        var area = SKRect.Inflate(bounds, size * 1.2f, size * 1.2f);
        // Only what is on screen is painted.
        if (view is { } v)
        {
            var screen = SKRect.Inflate(new SKRect((float)v.Left, (float)v.Top, (float)v.Right, (float)v.Bottom), size * 1.2f, size * 1.2f);
            if (!area.IntersectsWith(screen)) return;
            area.Intersect(screen);
        }
        using var group = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round((player ? 255 : 120) * Math.Clamp(share, 0, 1))) };
        canvas.SaveLayer(area, group);
        using (var mist = new SKPaint
        {
            Color = new SKColor(0x9C, 0xA9, 0xB6), IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.18f),
        })
            canvas.DrawPath(edge, mist);
        canvas.Save();
        canvas.ClipPath(edge, SKClipOperation.Intersect, antialias: true);
        // The cloud tile spans about four hexes of the board, so the mist keeps its size as the view zooms.
        var tileScale = size * 4f / CloudTile.Value.Width;
        using (var clouds = new SKPaint
        {
            Shader = CloudTile.Value.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), SKMatrix.CreateScale(tileScale, tileScale)),
        })
            canvas.DrawRect(area, clouds);
        using (var light = new SKPaint
        {
            Color = new SKColor(0xE2, 0xEF, 0xF5, 120), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.3f, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.15f),
        })
            canvas.DrawPath(edge, light);
        canvas.Restore();
        canvas.Restore();
    }
}
