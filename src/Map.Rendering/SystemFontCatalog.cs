using SkiaSharp;

namespace Map.Rendering;

/// <summary>Read-only, process-local catalog of the font families exposed by the OS to Skia.</summary>
public static class SystemFontCatalog
{
    private const int MaximumFamilyCount = 4_096;

    private static readonly Lazy<IReadOnlyList<string>> Cached = new(
        () => Normalize(SKFontManager.Default.FontFamilies),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<string> Families => Cached.Value;

    public static IReadOnlyList<string> Normalize(IEnumerable<string?> families)
    {
        var unique = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in families)
        {
            var family = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(family) || family.Length > 256)
            {
                continue;
            }

            if (unique.TryAdd(family, family) && unique.Count > MaximumFamilyCount)
            {
                unique.Remove(unique.Last().Key);
            }
        }

        return unique.Values.ToArray();
    }
}
