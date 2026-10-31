using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Plan 1.5: small mountains with a shadow (or blur, colour) are painted exactly into the cached
/// overview chunk instead of being drawn object by object on every overview frame. The overview
/// then looks as the exact drawing does and a later frame reuses the chunk.
/// </summary>
public sealed class OverviewPaintedStampTests
{
    private const double Scale = 1.0 / 16;

    [Fact]
    public void Shadowed_small_mountains_go_into_the_cached_chunk_and_look_as_drawn_exactly()
    {
        using var mountain = OwnerMountain();
        var sha = new string('a', 64);
        var objects = new List<SceneObject>();
        for (var row = 0; row < 16; row++)
        for (var column = 0; column < 16; column++)
        {
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, $"m{row}-{column}",
                64 + column * 240, 64 + row * 240, 32, 32, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Stamp = new StampStyle(sha) { ReferenceWidth = 32, ReferenceHeight = 32, ShadowOpacity = 0.7, ShadowBlur = 6 },
            });
        }
        var document = new MapDocument(DocumentId.New(), "1.5", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFF4EEDD"),
                new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects),
            ],
            [new MapAssetReference(sha, "Гора владельца", "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "test", "Map", catalogId: "owner-mountain-01")]);
        var images = new Images(mountain);
        using var cache = new ObjectOverviewCache();

        var stats = new RenderStats();
        using var overview = Render(document, Options(images, RenderLodTier.Overview, cache, stats));
        Assert.Equal(objects.Count, stats.OverviewObjects);
        Assert.True(stats.ExactObjects == 0, $"exact {stats.ExactObjects}, chunks {stats.OverviewChunks}, atlas {stats.StampAtlasBatches}, dense {stats.DenseSkip}, drawn {stats.ObjectsDrawn}");
        Assert.Equal(0, stats.MissingAssets);
        var misses = cache.Misses;

        var again = new RenderStats();
        using var second = Render(document, Options(images, RenderLodTier.Overview, cache, again));
        Assert.Equal(misses, cache.Misses);
        Assert.True(cache.Hits > 0);

        using var exact = Render(document, Options(images, RenderLodTier.Exact, null, new RenderStats()));
        var (mean, darkened) = Compare(overview, exact);
        Assert.True(mean <= 2.0, $"mean channel difference {mean:0.00}");
        // The shadows are there: the overview darkens the paper around the mountains as the exact drawing does.
        Assert.True(darkened > 0.9, $"exact-dark pixels also dark in the overview: {darkened:P0}");
    }

    private static RenderOptions Options(IStampImageSource images, RenderLodTier tier, ObjectOverviewCache? cache, RenderStats stats) => new()
    {
        Stamps = images,
        VisibleWorld = new WorldBounds(0, 0, 4_096, 4_096),
        ViewScale = Scale,
        LodTier = tier,
        ObjectCache = cache,
        Stats = stats,
    };

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)Scale);
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    /// <summary>Mean absolute channel difference, and the share of pixels the exact drawing darkens that the overview darkens too.</summary>
    private static (double Mean, double Darkened) Compare(SKBitmap actual, SKBitmap expected)
    {
        var paper = expected.GetPixel(2, 2);
        double sum = 0; var dark = 0; var both = 0;
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var a = actual.GetPixel(x, y); var e = expected.GetPixel(x, y);
            sum += Math.Abs(a.Red - e.Red) + Math.Abs(a.Green - e.Green) + Math.Abs(a.Blue - e.Blue);
            if (Luma(e) < Luma(paper) - 12)
            {
                dark++;
                if (Luma(a) < Luma(paper) - 6) both++;
            }
        }
        Assert.True(dark > 0, "the exact drawing shows the mountains");
        return (sum / (3.0 * expected.Width * expected.Height), both / (double)dark);
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    /// <summary>The owner's first mountain (DEC-076), the picture the overview has to keep.</summary>
    private static SKImage OwnerMountain()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Map.slnx"),
            "tests", "fixtures", "assets", "owner-mountain-01.png");
        return SKImage.FromEncodedData(path) ?? throw new InvalidDataException(path);
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
