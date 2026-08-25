using SkiaSharp;

namespace Map.Rendering;

/// <summary>Machine-local resolution; only requested/fallback names are persisted.</summary>
public sealed record TextFontResolution(string RequestedFamily, string ResolvedFamily, bool UsedFallback);

public static class TextFontResolver
{
    private const int CacheLimit = 64;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, TextFontResolution> ResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> ResolutionOrder = new();
    private static readonly Dictionary<string, WeakReference<SKTypeface>> TypefaceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> TypefaceOrder = new();

    public static TextFontResolution Resolve(string requestedFamily, string fallbackFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedFamily);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFamily);
        var key = requestedFamily + "\0" + fallbackFamily;
        lock (Gate)
        {
            if (ResolutionCache.TryGetValue(key, out var cached)) return cached;
        }

        using var requested = SKFontManager.Default.MatchFamily(requestedFamily);
        TextFontResolution result;
        if (requested is not null
            && string.Equals(requested.FamilyName, requestedFamily, StringComparison.OrdinalIgnoreCase))
        {
            result = new(requestedFamily, requested.FamilyName, false);
        }
        else
        {
            using var fallback = SKFontManager.Default.MatchFamily(fallbackFamily);
            var resolved = fallback?.FamilyName ?? SKTypeface.Default.FamilyName;
            result = new(requestedFamily, resolved, true);
        }
        lock (Gate)
        {
            ResolutionCache[key] = result; ResolutionOrder.Enqueue(key);
            while (ResolutionOrder.Count > CacheLimit) ResolutionCache.Remove(ResolutionOrder.Dequeue());
        }
        return result;
    }

    public static SKTypeface GetTypeface(TextFontResolution resolution, int weight)
    {
        var key = resolution.ResolvedFamily + "\0" + weight;
        lock (Gate)
        {
            if (TypefaceCache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached)) return cached;
            var created = SKTypeface.FromFamilyName(resolution.ResolvedFamily,
                new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright))
                ?? SKTypeface.Default;
            TypefaceCache[key] = new WeakReference<SKTypeface>(created); TypefaceOrder.Enqueue(key);
            while (TypefaceOrder.Count > CacheLimit) TypefaceCache.Remove(TypefaceOrder.Dequeue());
            return created;
        }
    }
}
