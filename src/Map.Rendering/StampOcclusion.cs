using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Conservative front-to-back coverage for a small detail tile. Only pixels proved fully
/// opaque may hide earlier stamps. Transparent edges, partial opacity and draw order survive.
/// This changes raster work, never the document or the number of placed objects.
/// </summary>
internal static class StampOcclusion
{
    private const int Cell = 8;
    private static readonly ConditionalWeakTable<SKImage, ImageCoverage> Images = [];
    private static readonly ConditionalWeakTable<SKImage, OverviewTint> Tints = [];

    public static IReadOnlyList<SceneObject> Cull(IReadOnlyList<SceneObject> ordered,
        WorldBounds tile, double scale, IStampImageSource? images)
    {
        if (ordered.Count < 64 || images is null) return ordered;
        var columns = (int)Math.Ceiling((tile.Right - tile.Left) * scale / Cell);
        var rows = (int)Math.Ceiling((tile.Bottom - tile.Top) * scale / Cell);
        var covered = new bool[columns * rows];
        var remaining = covered.Length;
        var visible = new List<SceneObject>();
        for (var index = ordered.Count - 1; index >= 0 && remaining > 0; index--)
        {
            var item = ordered[index];
            if (!item.Visible || item.ObjectOpacity <= 0) continue;
            var bounds = SceneGeometry.BoundsOf(item);
            var left = Math.Max(0, (int)Math.Floor((bounds.Left - tile.Left) * scale / Cell));
            var top = Math.Max(0, (int)Math.Floor((bounds.Top - tile.Top) * scale / Cell));
            var right = Math.Min(columns - 1, (int)Math.Floor((bounds.Right - tile.Left) * scale / Cell));
            var bottom = Math.Min(rows - 1, (int)Math.Floor((bounds.Bottom - tile.Top) * scale / Cell));
            var needed = false;
            for (var y = top; y <= bottom && !needed; y++)
            for (var x = left; x <= right; x++)
                if (!covered[y * columns + x]) { needed = true; break; }
            if (!needed) continue;
            visible.Add(item);
            if (item.ObjectOpacity < 1 || item.Stamp is not { } stamp
                || images.TryGetImage(stamp.AssetSha256) is not { } image) continue;
            var coverage = Images.GetValue(image, static i => new(i));
            if (!coverage.HasOpaqueCells) continue;
            var angle = -item.RotationDegrees * Math.PI / 180;
            var cos = Math.Cos(angle); var sin = Math.Sin(angle);
            var centerX = item.X + item.Width / 2; var centerY = item.Y + item.Height / 2;
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var cellIndex = y * columns + x;
                if (covered[cellIndex]) continue;
                var wx = tile.Left + x * Cell / scale; var wy = tile.Top + y * Cell / scale;
                var maxX = Math.Min(tile.Right, wx + Cell / scale);
                var maxY = Math.Min(tile.Bottom, wy + Cell / scale);
                var minU = double.PositiveInfinity; var minV = double.PositiveInfinity;
                var maxU = double.NegativeInfinity; var maxV = double.NegativeInfinity;
                Corner(wx, wy); Corner(maxX, wy); Corner(wx, maxY); Corner(maxX, maxY);
                // Include the bilinear/mipmap footprint and antialiasing boundary conservatively.
                var padding = 2 + Math.Max(image.Width / item.Width, image.Height / item.Height) / scale;
                if (!coverage.Opaque(minU - padding, minV - padding, maxU + padding, maxV + padding)) continue;
                covered[cellIndex] = true;
                remaining--;

                void Corner(double worldX, double worldY)
                {
                    var dx = worldX - centerX; var dy = worldY - centerY;
                    var u = ((dx * cos - dy * sin) / item.Width + .5) * image.Width;
                    var v = ((dx * sin + dy * cos) / item.Height + .5) * image.Height;
                    if (stamp.FlipHorizontal) u = image.Width - u;
                    if (stamp.FlipVertical) v = image.Height - v;
                    minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                    minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
                }
            }
        }
        visible.Reverse();
        return visible;
    }

    public static SKColor Color(SKImage image) => Tints.GetValue(image, static i =>
    {
        using var sample = SKSurface.Create(new SKImageInfo(32, 32))!;
        sample.Canvas.Clear(SKColors.Transparent);
        sample.Canvas.DrawImage(i, new SKRect(0, 0, 32, 32), new SKSamplingOptions(SKFilterMode.Linear));
        using var snapshot = sample.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        long red = 0, green = 0, blue = 0, alpha = 0;
        foreach (var pixel in bitmap.Pixels)
        {
            red += pixel.Red * pixel.Alpha; green += pixel.Green * pixel.Alpha;
            blue += pixel.Blue * pixel.Alpha; alpha += pixel.Alpha;
        }
        return new(alpha == 0 ? SKColors.Transparent : new SKColor(
            (byte)(red / alpha), (byte)(green / alpha), (byte)(blue / alpha), 217));
    }).Color;

    private sealed record OverviewTint(SKColor Color);

    private sealed class ImageCoverage
    {
        private readonly int _width, _height, _columns;
        private readonly int[] _transparent = [];
        public bool HasOpaqueCells { get; }

        public ImageCoverage(SKImage image)
        {
            _width = image.Width; _height = image.Height;
            // Large decoded sources keep their normal render path without a second huge allocation.
            if ((long)_width * _height > 16_777_216) return;
            using var bitmap = SKBitmap.FromImage(image);
            if (bitmap is null) return;
            var pixels = bitmap.Pixels;
            _columns = (_width + Cell - 1) / Cell;
            var rows = (_height + Cell - 1) / Cell;
            var bad = new bool[_columns * rows];
            for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                var pixel = pixels[y * _width + x];
                if (pixel.Alpha != 255) bad[y / Cell * _columns + x / Cell] = true;
            }
            HasOpaqueCells = bad.Any(b => !b);
            _transparent = new int[(_columns + 1) * (rows + 1)];
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < _columns; x++)
            {
                var index = (y + 1) * (_columns + 1) + x + 1;
                _transparent[index] = (bad[y * _columns + x] ? 1 : 0)
                    + _transparent[index - 1] + _transparent[index - _columns - 1] - _transparent[index - _columns - 2];
            }
        }

        public bool Opaque(double left, double top, double right, double bottom)
        {
            if (!HasOpaqueCells || left < 0 || top < 0 || right >= _width || bottom >= _height) return false;
            var x0 = (int)left / Cell; var y0 = (int)top / Cell;
            var x1 = (int)right / Cell + 1; var y1 = (int)bottom / Cell + 1;
            var stride = _columns + 1;
            return _transparent[y1 * stride + x1] - _transparent[y0 * stride + x1]
                - _transparent[y1 * stride + x0] + _transparent[y0 * stride + x0] == 0;
        }
    }
}
