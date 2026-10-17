using System.Globalization;

namespace Map.Core.Documents;

/// <summary>One-click colour look of a filter layer: a gradient map plus post-processing.</summary>
public sealed record FilterColorPreset(
    string Id, string Title, string Description, FilterGradientMap GradientMap, FilterPostProcess PostProcess);

/// <summary>
/// The colour pass of a filter layer as plain arithmetic. The renderer builds its colour filters
/// from <see cref="PostMatrix"/> and <see cref="GradientLut"/>; <see cref="Apply"/> is the
/// reference for one pixel, shared by tests and anything that needs the result without Skia.
/// Order: gradient map (mixed with the original by its strength) → brightness, contrast,
/// saturation → sharpness. <see cref="FilterSettings.ColorStrength"/> scales all of them.
/// </summary>
public static class FilterColorEffects
{
    public const double LumaR = 0.2126, LumaG = 0.7152, LumaB = 0.0722;

    /// <summary>Own colour looks; the colours are chosen for Map and are not taken from any other editor.</summary>
    public static IReadOnlyList<FilterColorPreset> Presets { get; } =
    [
        new("clarity", "Ясность", "Чётче границы и чуть ярче цвета, без смены оттенка.",
            new(), new() { Saturation = 0.10, Brightness = 0.03, Contrast = 0.18, Sharpness = 0.45 }),
        new("vivid", "Насыщенность", "Сочные краски: вода синее, леса зеленее.",
            new(), new() { Saturation = 0.45, Contrast = 0.08, Sharpness = 0.10 }),
        new("faded", "Выцветшая", "Старая выгоревшая печать: мягкие тени, бледные цвета.",
            Map("#FF4A4458", "#FF9C9488", "#FFF2EADB", 0.35), new() { Saturation = -0.35, Brightness = 0.06, Contrast = -0.30 }),
        new("sharp", "Резкость", "Только подчёркивает мелкие детали.",
            new(), new() { Contrast = 0.06, Sharpness = 0.85 }),
        new("sepia", "Сепия", "Коричневый тон старой фотографии.",
            Map("#FF2B1A0E", "#FF9A6B3F", "#FFF5E2C0", 1), new() { Contrast = 0.05 }),
        new("grey", "Серая", "Без цвета, мягкие серые тона.",
            new(), new() { Saturation = -1 }),
        new("bw", "Чёрно-белая", "Без цвета и с жёстким контрастом, как оттиск тушью.",
            Map("#FF000000", "#FF808080", "#FFFFFFFF", 1), new() { Contrast = 0.55, Sharpness = 0.2 }),
        new("winter", "Зима", "Холодные голубые тени и белые светлые места.",
            Map("#FF1B2A44", "#FF8AA3BC", "#FFF4F8FF", 0.45), new() { Saturation = -0.25, Brightness = 0.08, Contrast = 0.05 }),
        new("red-sky", "Красное небо", "Закат или пожар: багровые тени, золотые светлые места.",
            Map("#FF2A0A12", "#FFB8452C", "#FFFFD7A1", 0.55), new() { Saturation = 0.15, Contrast = 0.12 }),
        new("night", "Ночь", "Тёмная синяя ночь, светлые места как в лунном свете.",
            Map("#FF05081A", "#FF22325E", "#FF9DB2E0", 0.75), new() { Saturation = -0.20, Brightness = -0.25, Contrast = 0.10 }),
        new("overcast", "Пасмурно", "Серое небо: приглушённые цвета и мягкий свет.",
            Map("#FF2E3236", "#FF7E858A", "#FFD9DCDD", 0.30), new() { Saturation = -0.40, Brightness = -0.06, Contrast = -0.15 }),
        new("parchment", "Пергамент", "Тёплая бумага: охристые тени и кремовые светлые места.",
            Map("#FF3B2614", "#FFB08A5A", "#FFF1E2BE", 0.70), new() { Saturation = -0.10, Brightness = 0.04, Contrast = -0.08, Sharpness = 0.15 }),
    ];

    public static FilterColorPreset? Preset(string? id) => Presets.FirstOrDefault(preset => preset.Id == id);

    /// <summary>The settings with <paramref name="preset"/> applied at full strength; texture and old overlays are kept.</summary>
    public static FilterSettings WithPreset(FilterSettings settings, FilterColorPreset preset) => settings with
    {
        GradientMap = preset.GradientMap,
        PostProcess = preset.PostProcess,
        ColorStrength = 1,
        ColorPresetId = preset.Id,
    };

    /// <summary>The settings with the colour pass switched off (old overlays and texture kept).</summary>
    public static FilterSettings WithoutColor(FilterSettings settings) => settings with
    {
        GradientMap = new(), PostProcess = new(), ColorStrength = 1, ColorPresetId = null,
    };

    public static bool HasGradient(FilterSettings settings) => settings.GradientMap.Strength > 0;

    public static bool HasPost(FilterSettings settings) => settings.PostProcess is var post
        && (post.Saturation != 0 || post.Brightness != 0 || post.Contrast != 0 || post.Sharpness > 0);

    /// <summary>True when the colour pass changes any pixel.</summary>
    public static bool IsActive(FilterSettings settings) =>
        settings.ColorStrength > 0 && (HasGradient(settings) || HasPost(settings));

    /// <summary>
    /// Brightness (a multiplier), contrast around mid-grey and saturation around luminance, in
    /// that order, as one 4×5 row-major colour matrix on 0..1 channels (the fifth column is an
    /// offset in the same units). <paramref name="strength"/> scales each adjustment's distance
    /// from neutral, so 50 % of a preset is half of each change, not a blend of two pictures.
    /// </summary>
    public static float[] PostMatrix(FilterPostProcess post, double strength = 1)
    {
        var w = Math.Clamp(strength, 0, 1);
        var bright = 1 + (Math.Clamp(post.Brightness, -1, 1) * w);
        var contrast = 1 + (Math.Clamp(post.Contrast, -1, 1) * w);
        var s = 1 + (Math.Clamp(post.Saturation, -1, 1) * w);
        double[,] sat =
        {
            { LumaR * (1 - s) + s, LumaG * (1 - s), LumaB * (1 - s) },
            { LumaR * (1 - s), LumaG * (1 - s) + s, LumaB * (1 - s) },
            { LumaR * (1 - s), LumaG * (1 - s), LumaB * (1 - s) + s },
        };
        // Saturation keeps greys, so Sat·(k·B·c + o) = k·B·Sat·c + o.
        var offset = 0.5 * (1 - contrast);
        var matrix = new float[20];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
                matrix[(row * 5) + column] = (float)(contrast * bright * sat[row, column]);
            matrix[(row * 5) + 4] = (float)offset;
        }
        matrix[18] = 1; // alpha unchanged
        return matrix;
    }

    /// <summary>Weight of each of the eight neighbours in the sharpening kernel (centre = 1 + 8·weight).</summary>
    public static double SharpenWeight(FilterSettings settings) =>
        0.5 * Math.Clamp(settings.PostProcess.Sharpness, 0, 1) * Math.Clamp(settings.ColorStrength, 0, 1);

    /// <summary>The gradient as three 256-entry tables indexed by luminance (0..255).</summary>
    public static (byte[] Red, byte[] Green, byte[] Blue) GradientLut(FilterGradientMap map)
    {
        var shadow = ParseColor(map.ShadowColor, (0, 0, 0));
        var mid = ParseColor(map.MidtoneColor, (128, 128, 128));
        var high = ParseColor(map.HighlightColor, (255, 255, 255));
        byte[] red = new byte[256], green = new byte[256], blue = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255.0;
            var (from, to, k) = t < 0.5 ? (shadow, mid, t * 2) : (mid, high, (t - 0.5) * 2);
            red[i] = Mix(from.R, to.R, k); green[i] = Mix(from.G, to.G, k); blue[i] = Mix(from.B, to.B, k);
        }
        return (red, green, blue);
    }

    /// <summary>Reference result for one opaque pixel without sharpening (sharpening needs neighbours).</summary>
    public static (byte R, byte G, byte B) Apply(FilterSettings settings, byte r, byte g, byte b)
    {
        double R = r / 255.0, G = g / 255.0, B = b / 255.0;
        var strength = GradientWeight(settings);
        if (strength > 0)
        {
            var (lr, lg, lb) = GradientLut(settings.GradientMap);
            var index = LumaIndex(R, G, B);
            R += (lr[index] / 255.0 - R) * strength;
            G += (lg[index] / 255.0 - G) * strength;
            B += (lb[index] / 255.0 - B) * strength;
        }
        var m = PostMatrix(settings.PostProcess, settings.ColorStrength);
        return (
            ToByte((m[0] * R) + (m[1] * G) + (m[2] * B) + m[4]),
            ToByte((m[5] * R) + (m[6] * G) + (m[7] * B) + m[9]),
            ToByte((m[10] * R) + (m[11] * G) + (m[12] * B) + m[14]));
    }

    /// <summary>How much of the gradient reaches the map: its own strength × the effect strength.</summary>
    public static double GradientWeight(FilterSettings settings) =>
        Math.Clamp(settings.GradientMap.Strength, 0, 1) * Math.Clamp(settings.ColorStrength, 0, 1);

    private static int LumaIndex(double r, double g, double b) =>
        (int)Math.Clamp(Math.Round(((LumaR * r) + (LumaG * g) + (LumaB * b)) * 255), 0, 255);

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    public static (byte R, byte G, byte B) ParseColor(string? value, (byte R, byte G, byte B) fallback)
    {
        if (value is not { Length: 7 or 9 } || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return fallback;
        return ((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    public static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    private static FilterGradientMap Map(string shadow, string mid, string high, double strength) =>
        new() { ShadowColor = shadow, MidtoneColor = mid, HighlightColor = high, Strength = strength };

    private static byte Mix(double from, double to, double k) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * k)), 0, 255);
}
