using SkiaSharp;

namespace Map.Assets;

/// <summary>Creates the tiny original Map starter pack in the selected local catalog.</summary>
public static class StarterPackBootstrapper
{
    public const string PackName = "Map · базовые формы";
    public const string NaturePackName = "Map · тестовая природа";
    public const string ProvenanceSource = "map-original-generated-starter";

    public static AssetSet? EnsureInstalled(AssetCatalog catalog, DateTimeOffset installedUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Status == AssetLibraryStatus.Damaged)
        {
            return null;
        }

        var existing = catalog.Sets.FirstOrDefault(set =>
            string.Equals(set.Name, PackName, StringComparison.Ordinal));
        if (existing is null)
        {
            var sourceRoot = Path.Combine(catalog.Root, "starter-pack-source");
            Directory.CreateDirectory(sourceRoot);
            var sources = new[]
            {
                Create(sourceRoot, "map-starter-circle.png", DrawCircle),
                Create(sourceRoot, "map-starter-diamond.png", DrawDiamond),
                Create(sourceRoot, "map-starter-brush-tip.png", DrawBrushTip),
            };
            existing = catalog.ImportSet(sources, PackName, new AssetImportOptions
            {
                Pack = PackName,
                Tags = ["map-original", "starter", "geometric"],
                Provenance = StarterProvenance(),
            }, installedUtc);
        }

        EnsureNatureSamplesInstalled(catalog, installedUtc);
        return existing;
    }

    private static void EnsureNatureSamplesInstalled(AssetCatalog catalog, DateTimeOffset installedUtc)
    {
        var sourceRoot = Path.Combine(catalog.Root, "starter-pack-source");
        Directory.CreateDirectory(sourceRoot);
        var samples = new[]
        {
            (Resource: "Map.Assets.StarterAssets.sample-mountains.png", File: "Горы.png", Tag: "mountain"),
            (Resource: "Map.Assets.StarterAssets.sample-tree.png", File: "Дерево.png", Tag: "tree"),
        };

        foreach (var sample in samples)
        {
            var path = MaterializeEmbedded(sourceRoot, sample.Resource, sample.File);
            catalog.Import(path, new AssetImportOptions
            {
                Pack = NaturePackName,
                Tags = ["map-original", "starter", "nature", sample.Tag],
                Provenance = StarterProvenance(),
            }, installedUtc);
        }
    }

    /// <summary>The art sets whose cards are not in the catalog yet, by set name.</summary>
    public static IReadOnlyList<StarterArtForge.ArtSet> MissingArtSets(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Status == AssetLibraryStatus.Damaged) return [];
        var present = new HashSet<string>(catalog.Sets.Select(set => set.Name), StringComparer.Ordinal);
        return [.. StarterArtForge.Sets.Where(set => !present.Contains(set.Name))];
    }

    /// <summary>
    /// Writes the PNG files of the given art sets under the catalog root. Pure Skia and file work:
    /// meant for a worker thread, because painting twenty-seven pictures at 2048 px takes
    /// seconds, and the catalog index is not touched here.
    /// </summary>
    public static IReadOnlyList<(StarterArtForge.ArtSet Set, string[] Files)> RenderArtFiles(
        string catalogRoot, IReadOnlyList<StarterArtForge.ArtSet> sets, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogRoot);
        ArgumentNullException.ThrowIfNull(sets);
        var root = Path.Combine(catalogRoot, "starter-pack-source", "art");
        Directory.CreateDirectory(root);
        var result = new List<(StarterArtForge.ArtSet, string[])>();
        foreach (var set in sets)
        {
            var files = new string[set.VariantCount];
            for (var variant = 0; variant < set.VariantCount; variant++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(root, StarterArtForge.FileName(set, variant));
                if (!File.Exists(path))
                {
                    var temp = path + ".tmp";
                    File.WriteAllBytes(temp, StarterArtForge.Render(set, variant));
                    File.Move(temp, path, overwrite: true);
                }
                files[variant] = path;
            }
            result.Add((set, files));
        }
        return result;
    }

    /// <summary>Adds rendered art sets to the catalog as ordered variant sets. UI thread, like every import.</summary>
    public static int ImportArt(AssetCatalog catalog,
        IReadOnlyList<(StarterArtForge.ArtSet Set, string[] Files)> rendered, DateTimeOffset installedUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rendered);
        var imported = 0;
        foreach (var (set, files) in rendered)
        {
            if (catalog.Sets.Any(existing => string.Equals(existing.Name, set.Name, StringComparison.Ordinal))) continue;
            catalog.ImportSet(files, set.Name, new AssetImportOptions
            {
                Pack = set.Collection,
                Tags = ["map-original", "starter", .. set.Tags],
                Provenance = StarterProvenance(),
            }, installedUtc);
            imported++;
        }
        return imported;
    }

    private static AssetProvenance StarterProvenance() => new()
    {
        Source = ProvenanceSource,
        Author = "Map",
        LicenseId = "Map original local starter",
        LicenseStatus = AssetLicenseStatus.UserOwned,
        CommercialUseAllowed = true,
    };

    private static string MaterializeEmbedded(string root, string resourceName, string fileName)
    {
        var path = Path.Combine(root, fileName);
        using var source = typeof(StarterPackBootstrapper).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Starter asset resource is missing: {resourceName}");
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        var bytes = memory.ToArray();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            File.WriteAllBytes(path, bytes);
        }

        return path;
    }

    private static string Create(string root, string name, Action<SKCanvas> draw)
    {
        var path = Path.Combine(root, name);
        if (File.Exists(path)) return path;
        using var surface = SKSurface.Create(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Не удалось создать starter asset.");
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Не удалось закодировать starter asset.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static void DrawCircle(SKCanvas canvas)
    {
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(83, 126, 101) };
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6,
            Color = new SKColor(32, 55, 46),
        };
        canvas.DrawCircle(64, 64, 44, fill);
        canvas.DrawCircle(64, 64, 44, stroke);
    }

    private static void DrawDiamond(SKCanvas canvas)
    {
        using var path = new SKPath();
        path.MoveTo(64, 12); path.LineTo(116, 64); path.LineTo(64, 116); path.LineTo(12, 64); path.Close();
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(92, 116, 151) };
        canvas.DrawPath(path, paint);
    }

    private static void DrawBrushTip(SKCanvas canvas)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawOval(new SKRect(18, 36, 110, 92), paint);
    }
}
