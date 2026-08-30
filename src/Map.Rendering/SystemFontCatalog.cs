using SkiaSharp;

namespace Map.Rendering;

/// <summary>Read-only, process-local catalog of the font families exposed by the OS to Skia.</summary>
public static class SystemFontCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> Cached = new(
        () => Normalize(SKFontManager.Default.FontFamilies),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<string> Families => Cached.Value;

    public static IReadOnlyList<string> Normalize(IEnumerable<string?> families)
    {
        var unique = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in families)
        {
            var family = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(family) || family.Length > 256)
            {
                continue;
            }

            unique.TryAdd(family, family);
        }

        return unique.Values
            .OrderBy(family => family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(family => family, StringComparer.Ordinal)
            .ToArray();
    }
}
