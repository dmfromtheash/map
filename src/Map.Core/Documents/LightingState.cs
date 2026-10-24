namespace Map.Core.Documents;

/// <summary>
/// The light of one moment, derived from <see cref="MapLighting"/>: everything the renderer
/// needs and nothing it caches. Colours are linear RGB. The three keys follow the lighting
/// design chosen in V0 (DEC-057): day sun from the upper left (Imhof), key:fill about 2.5:1;
/// golden-hour evening with a cool lavender fill; night under a weak moon on the same side,
/// where the map's own lights carry the picture; a cool "day for night" white balance.
/// </summary>
public sealed record LightingState(
    double SunX, double SunY, double SunZ,
    (double R, double G, double B) Sun,
    (double R, double G, double B) Ambient,
    (double R, double G, double B) FogColor,
    double FogDensity,
    double LightsLevel,
    double ShadowLength,
    double Exposure,
    double Saturation,
    (double R, double G, double B) WhiteBalance,
    (double R, double G, double B) InvNeutral)
{
    private sealed record Key(
        double T, double[] SunDir, string SunColor, double SunIntensity, string AmbientColor, double AmbientIntensity,
        string FogColor, double FogDensity, double LightsIntensity, double ShadowLength, double Exposure, double Saturation,
        double[] WhiteBalance);

    private static readonly Key[] Keys =
    [
        new(0.0, [-0.55, 0.55, 0.63], "#fff3dc", 1.0, "#c3cddc", 0.48, "#dfe4ea", 0.0, 0.0, 0.35, 0.0, 1.0, [1.0, 1.0, 1.0]),
        new(0.5, [-0.86, 0.18, 0.33], "#ffad6b", 0.9, "#8e7ca6", 0.42, "#c99c8f", 0.18, 0.55, 1.2, -0.1, 1.0, [1.0, 0.97, 1.02]),
        new(1.0, [-0.38, 0.55, 0.74], "#a7bbe6", 0.38, "#2f3a58", 0.26, "#38456a", 0.22, 1.0, 0.6, -0.3, 0.75, [0.80, 0.90, 1.18]),
    ];

    /// <summary>
    /// The state at the lighting's time of day, with each effect's switch and strength applied:
    /// colour off keeps the day's neutral grade, fog off removes the haze, lights off darkens the sources.
    /// </summary>
    public static LightingState At(MapLighting lighting)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        var t = MapLighting.Clamp01(lighting.TimeOfDay);
        var i = t <= Keys[1].T ? 0 : 1;
        Key a = Keys[i], b = Keys[i + 1];
        var u = Math.Clamp((t - a.T) / (b.T - a.T), 0, 1);
        u = u * u * (3 - 2 * u);
        double L(double x, double y) => x + (y - x) * u;
        (double, double, double) Col(string c0, double i0, string c1, double i1)
        {
            var p = Linear(c0); var q = Linear(c1);
            return (L(p.R * i0, q.R * i1), L(p.G * i0, q.G * i1), L(p.B * i0, q.B * i1));
        }

        var sx = L(a.SunDir[0], b.SunDir[0]); var sy = L(a.SunDir[1], b.SunDir[1]); var sz = L(a.SunDir[2], b.SunDir[2]);
        var len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        var day = Keys[0];
        var ds = Linear(day.SunColor); var da = Linear(day.AmbientColor);
        var inv = (1 / (da.R * day.AmbientIntensity + ds.R * day.SunIntensity),
                   1 / (da.G * day.AmbientIntensity + ds.G * day.SunIntensity),
                   1 / (da.B * day.AmbientIntensity + ds.B * day.SunIntensity));
        var color = lighting.Color.Effective;
        double C(double x, double neutral) => neutral + (x - neutral) * color;
        var sun = Col(a.SunColor, a.SunIntensity, b.SunColor, b.SunIntensity);
        var ambient = Col(a.AmbientColor, a.AmbientIntensity, b.AmbientColor, b.AmbientIntensity);
        return new LightingState(
            sx / len, sy / len, sz / len,
            sun, ambient,
            Col(a.FogColor, 1, b.FogColor, 1),
            L(a.FogDensity, b.FogDensity) * 2 * lighting.Fog.Effective,
            L(a.LightsIntensity, b.LightsIntensity) * 1.25 * lighting.Lights.Effective,
            L(a.ShadowLength, b.ShadowLength),
            C(L(a.Exposure, b.Exposure), 0),
            C(L(a.Saturation, b.Saturation), 1),
            (C(L(a.WhiteBalance[0], b.WhiteBalance[0]), 1), C(L(a.WhiteBalance[1], b.WhiteBalance[1]), 1),
             C(L(a.WhiteBalance[2], b.WhiteBalance[2]), 1)),
            inv);
    }

    /// <summary>Share of the key (sun) in key + fill luminance: how dark a full cast shadow is.</summary>
    public double KeyShare
    {
        get
        {
            var key = Luma(Sun); var fill = Luma(Ambient);
            return key + fill > 1e-6 ? key / (key + fill) : 0;
        }
    }

    public static double Luma((double R, double G, double B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    /// <summary>sRGB "#rrggbb" to linear RGB (the same polynomial the light shader uses).</summary>
    public static (double R, double G, double B) Linear(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8) s = s[2..];
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return (1, 1, 1);
        static double Lin(int c) { var x = c / 255.0; return x * x * (x * 0.2848 + 0.7152); }
        return (Lin((v >> 16) & 255), Lin((v >> 8) & 255), Lin(v & 255));
    }
}
