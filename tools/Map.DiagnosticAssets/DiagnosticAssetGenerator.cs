using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace Map.DiagnosticAssets;

public sealed record DiagnosticAsset(string FileName, byte[] Bytes);

public sealed record DiagnosticAssetEntry
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool HasAlpha { get; init; }
    public required string Label { get; init; }
    public string Author { get; init; } = DiagnosticAssetGenerator.Author;
    public string License { get; init; } = DiagnosticAssetGenerator.License;
    public string Generator { get; init; } = DiagnosticAssetGenerator.GeneratorPath;
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record DiagnosticAssetSet
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> VariantIds { get; init; }
    public required string CoverVariantId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record DiagnosticAssetManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required IReadOnlyList<DiagnosticAssetEntry> Assets { get; init; }
    public required IReadOnlyList<DiagnosticAssetSet> Sets { get; init; }
}

/// <summary>
/// Draws the project's own diagnostic control images. These are not artwork and not a starter
/// pack: every shape exists to make one rendering property measurable - scale, aspect, pivot,
/// both flips, hue/saturation/brightness/contrast, opacity, shadow, blur, blend and sublayer.
/// Nothing here is derived from, or made to resemble, anyone else's assets.
/// </summary>
public static class DiagnosticAssetGenerator
{
    public const string StampSetId = "diagnostic-stamp-set";
    public const string BrushSetId = "diagnostic-brush-tip-set";
    public const string Author = "Map project";
    public const string License = "CC0-1.0";
    public const string GeneratorPath = "tools/Map.DiagnosticAssets";
    public const int StampSize = 512;
    public const int BrushSize = 256;
    public const int VariantCount = 12;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>All thirteen images, in a fixed order, byte-identical on every run.</summary>
    public static IReadOnlyList<DiagnosticAsset> Generate()
    {
        var assets = new List<DiagnosticAsset>(VariantCount + 1);
        for (var index = 1; index <= VariantCount; index++)
        {
            assets.Add(new DiagnosticAsset($"diagnostic-stamp-{index:D2}.png", StampVariant(index)));
        }

        assets.Add(new DiagnosticAsset("diagnostic-brush-tip.png", BrushTip()));
        return assets;
    }

    public static DiagnosticAssetManifest BuildManifest(IReadOnlyList<DiagnosticAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var entries = new List<DiagnosticAssetEntry>(assets.Count);
        foreach (var asset in assets)
        {
            var id = Path.GetFileNameWithoutExtension(asset.FileName);
            var brush = id.StartsWith("diagnostic-brush-tip", StringComparison.Ordinal);
            entries.Add(new DiagnosticAssetEntry
            {
                Id = id,
                FileName = asset.FileName,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(asset.Bytes)),
                Width = brush ? BrushSize : StampSize,
                Height = brush ? BrushSize : StampSize,
                HasAlpha = true,
                Label = brush ? "Диагностический кончик кисти" : $"Вариант {id[^2..]}",
                Tags = brush ? ["diagnostic", "brush"] : ["diagnostic", "stamp"],
            });
        }

        var variantIds = entries
            .Where(entry => entry.Id.StartsWith("diagnostic-stamp-", StringComparison.Ordinal))
            .Select(entry => entry.Id)
            .ToList();

        return new DiagnosticAssetManifest
        {
            Assets = entries,
            Sets =
            [
                new DiagnosticAssetSet
                {
                    Id = StampSetId,
                    Name = "Диагностический набор",
                    VariantIds = variantIds,
                    CoverVariantId = variantIds[0],
                    Tags = ["diagnostic", "stamp"],
                },
                new DiagnosticAssetSet
                {
                    Id = BrushSetId,
                    Name = "Диагностический кончик кисти",
                    VariantIds = ["diagnostic-brush-tip"],
                    CoverVariantId = "diagnostic-brush-tip",
                    Tags = ["diagnostic", "brush"],
                },
            ],
        };
    }

    /// <summary>
    /// One 512x512 RGBA variant. The silhouette is intentionally asymmetric in both axes so a
    /// horizontal flip, a vertical flip and a rotation are all visually distinguishable, and the
    /// alpha staircase makes opacity and blur measurable rather than merely plausible.
    /// </summary>
    private static byte[] StampVariant(int index)
    {
        var info = new SKImageInfo(StampSize, StampSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot create the fixture surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // A wedge plus a notched foot: no axis of symmetry, and the notch pins the pivot.
        using var silhouette = new SKPath();
        var lean = 24f + (index * 6f);
        silhouette.MoveTo(96, 452);
        silhouette.LineTo(96 + lean, 108);
        silhouette.LineTo(300, 60);
        silhouette.LineTo(416, 176);
        silhouette.LineTo(372, 336);
        silhouette.LineTo(268, 300);
        silhouette.LineTo(300, 452);
        silhouette.LineTo(232, 452);
        silhouette.LineTo(214, 388);
        silhouette.LineTo(176, 452);
        silhouette.Close();

        canvas.Save();
        canvas.ClipPath(silhouette, SKClipOperation.Intersect, antialias: true);

        // Four saturated sectors, rotated one step per variant so a rotation is readable.
        var sectors = new[]
        {
            new SKColor(214, 58, 48, 255),
            new SKColor(46, 148, 74, 255),
            new SKColor(40, 96, 208, 255),
            new SKColor(226, 176, 32, 255),
        };
        using var sectorPaint = new SKPaint { IsAntialias = false };
        for (var sector = 0; sector < 4; sector++)
        {
            sectorPaint.Color = sectors[(sector + index) % 4];
            var left = sector % 2 == 0 ? 0 : StampSize / 2f;
            var top = sector < 2 ? 0 : StampSize / 2f;
            canvas.DrawRect(left, top, StampSize / 2f, StampSize / 2f, sectorPaint);
        }

        // Neutral grey band: the reference for brightness, contrast and saturation changes.
        using var greyPaint = new SKPaint { Color = new SKColor(128, 128, 128, 255), IsAntialias = false };
        canvas.DrawRect(0, 232, StampSize, 48, greyPaint);

        // White and black marks bracket the tonal range on both sides of the grey band. They sit
        // right of the steepest possible leading edge so every variant really carries both.
        using var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = false };
        using var blackPaint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        canvas.DrawRect(200, 188, 40, 40, whitePaint);
        canvas.DrawRect(200, 284, 40, 40, blackPaint);

        // Alpha staircase 25/50/75/100 percent, so partial transparency is measurable.
        using var alphaPaint = new SKPaint { IsAntialias = false };
        for (var step = 0; step < 4; step++)
        {
            alphaPaint.Color = new SKColor(255, 255, 255, (byte)(64 + (step * 64) - (step == 3 ? 1 : 0)));
            canvas.DrawRect(300, 96 + (step * 34), 96, 30, alphaPaint);
        }

        // Countable per-variant pips. They are drawn inside the clip and inside the left foot,
        // clear of the notch at every lean, so no variant leaves marks on the transparent ground.
        using var markerPaint = new SKPaint
        {
            Color = new SKColor(20, 20, 24, 255),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        for (var pip = 0; pip < index; pip++)
        {
            canvas.DrawCircle(120 + (pip % 4 * 18), 400 + (pip / 4 * 16), 5, markerPaint);
        }

        canvas.Restore();

        // Hard outline: makes scale, aspect and blur immediately visible at any zoom.
        using var outlinePaint = new SKPaint
        {
            Color = new SKColor(16, 16, 20, 255),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 6,
        };
        canvas.DrawPath(silhouette, outlinePaint);

        return Encode(surface);
    }

    /// <summary>
    /// The 256x256 brush tip: a hard core, soft alpha rings, one coloured directional marker and
    /// an empty inner sector, so spacing, softness, angle, flip and stroke seams are all visible.
    /// </summary>
    private static byte[] BrushTip()
    {
        var info = new SKImageInfo(BrushSize, BrushSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Cannot create the fixture surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        const float centre = BrushSize / 2f;

        // Concentric rings from the outside in: an explicit, countable softness ramp.
        using var ringPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        for (var ring = 5; ring >= 1; ring--)
        {
            ringPaint.Color = new SKColor(255, 255, 255, (byte)(255 - (ring * 40)));
            canvas.DrawCircle(centre, centre, 22f + (ring * 18f), ringPaint);
        }

        ringPaint.Color = SKColors.White;
        canvas.DrawCircle(centre, centre, 28, ringPaint);

        // Empty inner sector: a stroke that seams incorrectly smears this wedge shut.
        using var clearPaint = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true };
        using var wedge = new SKPath();
        wedge.MoveTo(centre, centre);
        wedge.ArcTo(new SKRect(centre - 60, centre - 60, centre + 60, centre + 60), 200, 46, false);
        wedge.Close();
        canvas.DrawPath(wedge, clearPaint);

        // One coloured directional marker: the tip is asymmetric, so angle and flip are readable.
        using var markerPaint = new SKPaint
        {
            Color = new SKColor(226, 84, 32, 255),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        using var marker = new SKPath();
        marker.MoveTo(centre + 4, 26);
        marker.LineTo(centre + 46, 92);
        marker.LineTo(centre - 18, 78);
        marker.Close();
        canvas.DrawPath(marker, markerPaint);

        return Encode(surface);
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Cannot encode the fixture PNG.");
        return data.ToArray();
    }
}
