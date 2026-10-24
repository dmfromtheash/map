namespace Map.Rendering.Lighting;

/// <summary>
/// Hand-written form descriptions (Claude, V0 2026-10-24) of the owner's mountains: where the
/// peaks, ridges and bowls are, in the coordinates of a 700-px wide preview. Only numbers -
/// no pixels of the owner's art live here. Keyed by the asset's SHA-256.
/// </summary>
internal sealed class MountainForms
{
    private readonly (float X, float Y, float H, float R)[] _peaks;
    private readonly ((float X, float Y, float H)[] Points, float Width)[] _ridges;
    private readonly (float X, float Y, float Depth, float R)[] _bowls;

    private MountainForms(
        (float, float, float, float)[] peaks,
        ((float, float, float)[], float)[] ridges,
        (float, float, float, float)[] bowls)
    {
        _peaks = peaks; _ridges = ridges; _bowls = bowls;
    }

    private static readonly Dictionary<string, MountainForms> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // «Гора с ледником»
        ["40c290b7de28b188cc135a162f20c50f78aea9e13094f496497a2f1320a4777f"] = new(
            [(265, 18, 1.00f, 70), (482, 78, 0.86f, 60)],
            [
                ([(265, 18, 1.0f), (175, 120, 0.72f), (70, 255, 0.32f), (10, 300, 0.08f)], 34),
                ([(265, 18, 1.0f), (330, 70, 0.86f), (410, 70, 0.80f), (482, 78, 0.86f)], 30),
                ([(482, 78, 0.86f), (565, 185, 0.55f), (650, 300, 0.18f)], 34),
                ([(240, 150, 0.70f), (300, 220, 0.62f), (335, 330, 0.40f), (350, 420, 0.16f)], 40),
                ([(470, 170, 0.62f), (520, 240, 0.48f), (560, 350, 0.24f)], 36),
                ([(120, 230, 0.40f), (160, 330, 0.22f)], 26),
            ],
            [(395, 185, 0.30f, 85), (430, 280, 0.12f, 60)]),
        // «Двуглавая гора»
        ["a6ba8b2c7498b6ff54c2fa99c18c4b876cda1eaa9743515f05fc706c575e5aa9"] = new(
            [(240, 72, 0.88f, 55), (455, 14, 1.00f, 60)],
            [
                ([(240, 72, 0.88f), (150, 180, 0.55f), (60, 290, 0.22f), (10, 330, 0.06f)], 32),
                ([(240, 72, 0.88f), (300, 140, 0.66f), (350, 152, 0.60f), (400, 110, 0.74f), (455, 14, 1.0f)], 28),
                ([(455, 14, 1.0f), (545, 130, 0.64f), (610, 230, 0.36f), (680, 305, 0.08f)], 32),
                ([(455, 14, 1.0f), (430, 170, 0.66f), (395, 300, 0.40f), (370, 420, 0.14f)], 36),
                ([(240, 72, 0.88f), (255, 210, 0.55f), (300, 330, 0.30f), (320, 430, 0.10f)], 34),
                ([(545, 130, 0.64f), (540, 260, 0.36f)], 24),
            ],
            [(350, 190, 0.22f, 70)]),
        // «Ледяной пик»
        ["a12e23ef2d595b06f951e4eab37d2c7020c4800767b96ef1300106a7a4be38f9"] = new(
            [(345, 14, 1.00f, 60)],
            [
                ([(345, 14, 1.0f), (300, 110, 0.78f), (240, 215, 0.52f), (130, 340, 0.20f), (20, 385, 0.05f)], 34),
                ([(345, 14, 1.0f), (395, 110, 0.80f), (460, 210, 0.52f), (560, 310, 0.24f), (650, 385, 0.06f)], 34),
                ([(345, 14, 1.0f), (335, 150, 0.80f), (322, 300, 0.50f), (310, 470, 0.14f)], 36),
                ([(410, 120, 0.76f), (470, 300, 0.42f), (520, 460, 0.12f)], 30),
                ([(300, 110, 0.78f), (215, 300, 0.38f), (190, 440, 0.12f)], 28),
            ],
            [(270, 300, 0.10f, 50), (420, 330, 0.10f, 50)]),
    };

    public static bool TryGet(string sha256, out MountainForms? forms) => Known.TryGetValue(sha256, out forms);

    /// <summary>Writes the form height (0..1) into <paramref name="target"/>; <paramref name="s"/> = width / 700.</summary>
    public void Rasterize(float[] target, int w, int h, float s)
    {
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = 0f;
            foreach (var (px, py, ph, pr) in _peaks)
            {
                float dx = x - px * s, dy = y - py * s, rr = pr * s;
                v = Math.Max(v, ph * MathF.Exp(-(dx * dx + dy * dy) / (rr * rr) * 1.6f));
            }
            foreach (var (points, width) in _ridges)
            {
                for (var i = 0; i + 1 < points.Length; i++)
                {
                    var (ax, ay, ah) = points[i]; var (bx, by, bh) = points[i + 1];
                    float axs = ax * s, ays = ay * s, abx = bx * s - axs, aby = by * s - ays;
                    var t = Math.Clamp(((x - axs) * abx + (y - ays) * aby) / Math.Max(abx * abx + aby * aby, 1e-6f), 0, 1);
                    float ddx = x - (axs + t * abx), ddy = y - (ays + t * aby);
                    var d = MathF.Sqrt(ddx * ddx + ddy * ddy);
                    var hl = ah + (bh - ah) * t;
                    // Sharp crest with broad planar faces: the owner's rock is drawn as facets.
                    v = Math.Max(v, hl * Math.Clamp(1 - d / (width * s * (1.6f + 3.4f * hl)), 0, 1));
                }
            }
            foreach (var (bx, by, depth, br) in _bowls)
            {
                float dx = x - bx * s, dy = y - by * s, rr = br * s;
                v -= depth * MathF.Exp(-(dx * dx + dy * dy) / (rr * rr) * 2f);
            }
            target[y * w + x] = v;
        }
    }
}
