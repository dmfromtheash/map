using SkiaSharp;

namespace Map.Export;

/// <summary>The projections a planet map can be exported in (R39).</summary>
public enum PlanetProjection { Mercator, Robinson, Mollweide, WinkelTripel, Orthographic, Icosahedral, Gores }

/// <summary>
/// R39: the equirectangular planet (two to one) re-projected. Every output pixel is taken back to
/// longitude and latitude by the inverse of its projection and sampled from the source with
/// bilinear filtering, longitude wrapping across the seam. Pixels outside the projection stay
/// transparent. The icosahedral net is gnomonic on each of twenty faces laid out in three rows
/// (in the manner of Fuller's Dymaxion); gores are sinusoidal strips for gluing a globe.
/// </summary>
public static class PlanetProjections
{
    public static string Title(PlanetProjection projection) => projection switch
    {
        PlanetProjection.Mercator => "Меркатор",
        PlanetProjection.Robinson => "Робинсон",
        PlanetProjection.Mollweide => "Мольвейде",
        PlanetProjection.WinkelTripel => "Винкель-Трипель",
        PlanetProjection.Orthographic => "Ортографическая (глобус)",
        PlanetProjection.Icosahedral => "Икосаэдр (развёртка Dymaxion)",
        _ => "Клинья для склейки глобуса",
    };

    /// <summary>The output size for a width: each projection has its own proportions.</summary>
    public static (int Width, int Height) Size(PlanetProjection projection, int width) => projection switch
    {
        PlanetProjection.Mercator => (width, width),
        PlanetProjection.Orthographic => (width, width),
        PlanetProjection.Icosahedral => (width, (int)Math.Round(width * IcoHeight / IcoWidth)),
        PlanetProjection.Robinson => (width, (int)Math.Round(width * (1.3523 / (0.8487 * Math.PI)))),
        PlanetProjection.WinkelTripel => (width, (int)Math.Round(width * WinkelExtent.Y / WinkelExtent.X)),
        _ => (width, width / 2),
    };

    /// <summary>The source pixels read once, for many projections of the same planet (the turning globe).</summary>
    public sealed class Source(SKBitmap bitmap)
    {
        public int Width { get; } = bitmap.Width;
        public int Height { get; } = bitmap.Height;
        internal uint[] Pixels { get; } = bitmap.Pixels.Select(c => (uint)((c.Alpha << 24) | (c.Blue << 16) | (c.Green << 8) | c.Red)).ToArray();
    }

    public static SKBitmap Project(SKBitmap source, PlanetProjection projection, int width, double centerLongitude = 0, double centerLatitude = 0, int gores = 12)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Project(new Source(source), projection, width, centerLongitude, centerLatitude, gores);
    }

    public static SKBitmap Project(Source source, PlanetProjection projection, int width, double centerLongitude = 0, double centerLatitude = 0, int gores = 12)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (w, h) = Size(projection, Math.Clamp(width, 16, 8192));
        var output = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = new uint[w * h];
        var src = source.Pixels;
        var (sw, sh) = (source.Width, source.Height);
        var (lambda0, phi0) = (centerLongitude * Math.PI / 180, centerLatitude * Math.PI / 180);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var (u, v) = ((x + .5) / w, (y + .5) / h);
                if (Inverse(projection, u, v, lambda0, phi0, gores) is not { } geo) continue;
                pixels[(y * w) + x] = Sample(src, sw, sh, geo.Longitude, geo.Latitude);
            }
        });
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, output.GetPixels(), bytes.Length);
        return output;
    }

    /// <summary>Longitude and latitude in radians of a point of the output (u, v in 0…1, v downwards); null outside.</summary>
    public static (double Longitude, double Latitude)? Inverse(PlanetProjection projection, double u, double v, double lambda0 = 0, double phi0 = 0, int gores = 12)
    {
        switch (projection)
        {
            case PlanetProjection.Mercator:
            {
                var x = (u - .5) * 2 * Math.PI;
                var y = (.5 - v) * 2 * Math.PI;
                return (Wrap(x + lambda0), Math.Atan(Math.Sinh(y)));
            }
            case PlanetProjection.Mollweide:
            {
                var x = (u - .5) * 4 * Math.Sqrt(2);
                var y = (.5 - v) * 2 * Math.Sqrt(2);
                var theta = Math.Asin(Math.Clamp(y / Math.Sqrt(2), -1, 1));
                var lon = Math.PI * x / (2 * Math.Sqrt(2) * Math.Cos(theta));
                if (Math.Abs(lon) > Math.PI) return null;
                return (Wrap(lon + lambda0), Math.Asin(Math.Clamp(((2 * theta) + Math.Sin(2 * theta)) / Math.PI, -1, 1)));
            }
            case PlanetProjection.Robinson:
            {
                var x = (u - .5) * 2 * 0.8487 * Math.PI;
                var y = (.5 - v) * 2 * 1.3523;
                var ay = Math.Abs(y) / 1.3523;
                if (ay > RobinsonY[^1]) return null;
                var i = 0;
                while (i < RobinsonY.Length - 2 && RobinsonY[i + 1] < ay) i++;
                var t = (ay - RobinsonY[i]) / (RobinsonY[i + 1] - RobinsonY[i]);
                var lat = (i + t) * 5 * Math.PI / 180 * Math.Sign(y);
                var length = RobinsonX[i] + ((RobinsonX[i + 1] - RobinsonX[i]) * t);
                var lon = x / (0.8487 * length);
                if (Math.Abs(lon) > Math.PI) return null;
                return (Wrap(lon + lambda0), lat);
            }
            case PlanetProjection.WinkelTripel:
            {
                var x = (u - .5) * 2 * WinkelExtent.X;
                var y = (.5 - v) * 2 * WinkelExtent.Y;
                return WinkelInverse(x, y) is { } g ? (Wrap(g.Longitude + lambda0), g.Latitude) : null;
            }
            case PlanetProjection.Orthographic:
            {
                var x = (u - .5) * 2;
                var y = (.5 - v) * 2;
                var rho = Math.Sqrt((x * x) + (y * y));
                if (rho > 1) return null;
                var c = Math.Asin(rho);
                var (sinC, cosC) = (Math.Sin(c), Math.Cos(c));
                var lat = rho < 1e-12 ? phi0 : Math.Asin((cosC * Math.Sin(phi0)) + (y * sinC * Math.Cos(phi0) / rho));
                var lon = lambda0 + Math.Atan2(x * sinC, (rho * Math.Cos(phi0) * cosC) - (y * Math.Sin(phi0) * sinC));
                return (Wrap(lon), lat);
            }
            case PlanetProjection.Icosahedral:
                return Icosahedral(u * IcoWidth, v * IcoHeight) is { } p ? (Wrap(p.Longitude + lambda0), p.Latitude) : null;
            default:
            {
                // Gores: sinusoidal strips, each 360/n degrees wide.
                var n = Math.Clamp(gores, 4, 36);
                var gore = Math.Min(n - 1, (int)(u * n));
                var local = ((u * n) - gore - .5) * 2; // −1…1 across the gore at the equator
                var lat = (.5 - v) * Math.PI;
                var half = Math.Cos(lat);
                if (Math.Abs(local) > half) return null;
                var lon = (-Math.PI + ((gore + .5) * 2 * Math.PI / n)) + (local * Math.PI / n / Math.Max(1e-9, half));
                return (Wrap(lon + lambda0), lat);
            }
        }
    }

    private static uint Sample(uint[] src, int sw, int sh, double lon, double lat)
    {
        var fx = ((lon / (2 * Math.PI)) + .5) * sw - .5;
        var fy = ((.5 - (lat / Math.PI)) * sh) - .5;
        var x0 = (int)Math.Floor(fx); var y0 = (int)Math.Floor(fy);
        var (tx, ty) = (fx - x0, fy - y0);
        uint P(int x, int y) => src[(Math.Clamp(y, 0, sh - 1) * sw) + (((x % sw) + sw) % sw)];
        var (a, b, c, d) = (P(x0, y0), P(x0 + 1, y0), P(x0, y0 + 1), P(x0 + 1, y0 + 1));
        uint Channel(int shift)
        {
            double Get(uint p) => (p >> shift) & 0xFF;
            var top = Get(a) + ((Get(b) - Get(a)) * tx);
            var bottom = Get(c) + ((Get(d) - Get(c)) * tx);
            return (uint)Math.Clamp(Math.Round(top + ((bottom - top) * ty)), 0, 255) << shift;
        }
        return Channel(0) | Channel(8) | Channel(16) | Channel(24);
    }

    private static double Wrap(double lon) => lon - (2 * Math.PI * Math.Floor((lon + Math.PI) / (2 * Math.PI)));

    // Robinson's table: every 5° of latitude, the parallel's length and its distance from the equator.
    private static readonly double[] RobinsonX = [1.0000, 0.9986, 0.9954, 0.9900, 0.9822, 0.9730, 0.9600, 0.9427, 0.9216, 0.8962, 0.8679, 0.8350, 0.7986, 0.7597, 0.7186, 0.6732, 0.6213, 0.5722, 0.5322];
    private static readonly double[] RobinsonY = [0.0000, 0.0620, 0.1240, 0.1860, 0.2480, 0.3100, 0.3720, 0.4340, 0.4958, 0.5571, 0.6176, 0.6769, 0.7346, 0.7903, 0.8435, 0.8936, 0.9394, 0.9761, 1.0000];

    // Winkel tripel with the standard parallel arccos(2/π).
    private static readonly double CosPhi1 = 2 / Math.PI;

    private static (double X, double Y) WinkelForward(double lambda, double phi)
    {
        var alpha = Math.Acos(Math.Clamp(Math.Cos(phi) * Math.Cos(lambda / 2), -1, 1));
        var sinc = alpha < 1e-12 ? 1 : Math.Sin(alpha) / alpha;
        var x = .5 * ((lambda * CosPhi1) + (2 * Math.Cos(phi) * Math.Sin(lambda / 2) / sinc));
        var y = .5 * (phi + (Math.Sin(phi) / sinc));
        return (x, y);
    }

    private static readonly (double X, double Y) WinkelExtent = (WinkelForward(Math.PI, 0).X, WinkelForward(0, Math.PI / 2).Y);

    /// <summary>Newton's method on the forward equations (with a numeric Jacobian); null when it leaves the map.</summary>
    private static (double Longitude, double Latitude)? WinkelInverse(double x, double y)
    {
        var (lambda, phi) = (x / WinkelExtent.X * Math.PI, y / WinkelExtent.Y * Math.PI / 2);
        for (var i = 0; i < 25; i++)
        {
            var (fx, fy) = WinkelForward(lambda, phi);
            var (ex, ey) = (fx - x, fy - y);
            if (Math.Abs(ex) < 1e-9 && Math.Abs(ey) < 1e-9) break;
            const double e = 1e-6;
            var (ax, ay) = WinkelForward(lambda + e, phi);
            var (bx, by) = WinkelForward(lambda, phi + e);
            var (j11, j12, j21, j22) = ((ax - fx) / e, (bx - fx) / e, (ay - fy) / e, (by - fy) / e);
            var det = (j11 * j22) - (j12 * j21);
            if (Math.Abs(det) < 1e-12) return null;
            lambda -= ((j22 * ex) - (j12 * ey)) / det;
            phi -= ((-j21 * ex) + (j11 * ey)) / det;
            phi = Math.Clamp(phi, -Math.PI / 2, Math.PI / 2);
        }
        if (Math.Abs(lambda) > Math.PI + 1e-6) return null;
        var (cx, cy) = WinkelForward(lambda, phi);
        return Math.Abs(cx - x) < 1e-4 && Math.Abs(cy - y) < 1e-4 ? (lambda, phi) : null;
    }

    // The icosahedral net: three rows of triangles of side 1, five apexes on top and bottom.
    private static readonly double H = Math.Sqrt(3) / 2;
    private static readonly double IcoWidth = 5.5, IcoHeight = 3 * Math.Sqrt(3) / 2;
    private static readonly (double Lon, double Lat) North = (0, 90), South = (0, -90);
    private static readonly double Ring = Math.Atan(.5) * 180 / Math.PI;

    private static readonly ((double X, double Y) A, (double X, double Y) B, (double X, double Y) C, (double Lon, double Lat) GA, (double Lon, double Lat) GB, (double Lon, double Lat) GC)[] Faces = BuildFaces();

    private static ((double, double), (double, double), (double, double), (double, double), (double, double), (double, double))[] BuildFaces()
    {
        var faces = new List<((double, double), (double, double), (double, double), (double, double), (double, double), (double, double))>();
        (double, double) U(int i) => (i, H);
        (double, double) L(int i) => (i + .5, 2 * H);
        (double, double) GU(int i) => (72.0 * i, Ring);
        (double, double) GL(int i) => ((72.0 * i) + 36, -Ring);
        for (var i = 0; i < 5; i++)
        {
            faces.Add(((i + .5, 0), U(i), U(i + 1), North, GU(i), GU(i + 1)));
            faces.Add((U(i), U(i + 1), L(i), GU(i), GU(i + 1), GL(i)));
            faces.Add((U(i + 1), L(i), L(i + 1), GU(i + 1), GL(i), GL(i + 1)));
            faces.Add((L(i), L(i + 1), (i + 1.0, 3 * H), GL(i), GL(i + 1), South));
        }
        return [.. faces];
    }

    private static (double Longitude, double Latitude)? Icosahedral(double x, double y)
    {
        foreach (var (a, b, c, ga, gb, gc) in Faces)
        {
            var d = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
            var wa = (((b.Y - c.Y) * (x - c.X)) + ((c.X - b.X) * (y - c.Y))) / d;
            var wb = (((c.Y - a.Y) * (x - c.X)) + ((a.X - c.X) * (y - c.Y))) / d;
            var wc = 1 - wa - wb;
            if (wa < -1e-9 || wb < -1e-9 || wc < -1e-9) continue;
            var (pa, pb, pc) = (Vector(ga), Vector(gb), Vector(gc));
            var p = (X: (pa.X * wa) + (pb.X * wb) + (pc.X * wc), Y: (pa.Y * wa) + (pb.Y * wb) + (pc.Y * wc), Z: (pa.Z * wa) + (pb.Z * wb) + (pc.Z * wc));
            var length = Math.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z));
            return (Math.Atan2(p.Y, p.X), Math.Asin(p.Z / length));
        }
        return null;
    }

    private static (double X, double Y, double Z) Vector((double Lon, double Lat) g)
    {
        var (lon, lat) = (g.Lon * Math.PI / 180, g.Lat * Math.PI / 180);
        return (Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }
}
