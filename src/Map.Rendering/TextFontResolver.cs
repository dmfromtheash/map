using SkiaSharp;

namespace Map.Rendering;

/// <summary>Machine-local resolution; only requested/fallback names are persisted.</summary>
public sealed record TextFontResolution(string RequestedFamily, string ResolvedFamily, bool UsedFallback);

public static class TextFontResolver
{
    public static TextFontResolution Resolve(string requestedFamily, string fallbackFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedFamily);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFamily);

        using var requested = SKFontManager.Default.MatchFamily(requestedFamily);
        if (requested is not null
            && string.Equals(requested.FamilyName, requestedFamily, StringComparison.OrdinalIgnoreCase))
        {
            return new(requestedFamily, requested.FamilyName, false);
        }

        using var fallback = SKFontManager.Default.MatchFamily(fallbackFamily);
        var resolved = fallback?.FamilyName ?? SKTypeface.Default.FamilyName;
        return new(requestedFamily, resolved, true);
    }
}
