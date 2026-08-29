using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage10EGridRenderingTests
{
    private const int Width = 192;
    private const int Height = 128;

    [Fact]
    public void Four_fixed_geometries_render_distinct_pixels_and_isometric_is_not_square_plus_45()
    {
        var hashes = Enum.GetValues<GridGeometry>()
            .Select(geometry => Hash(RenderPng(Document(Grid(geometry)), true)))
            .ToArray();

        Assert.Equal(4, hashes.Distinct(StringComparer.Ordinal).Count());
        var isometric = hashes[(int)GridGeometry.IsometricSquares];
        var square45 = Hash(RenderPng(Document(Grid(GridGeometry.Square) with { RotationDegrees = 45 }), true));
        Assert.NotEqual(isometric, square45);
    }

    [Fact]
    public void Viewport_preview_and_export_use_the_same_fixed_geometry()
    {
        var document = Document(Grid(GridGeometry.IsometricSquares));
        var viewport = RenderPng(document, false);
        var export = RenderPng(document, true);
        var preview = ThumbnailRenderer.RenderPng(document, Width, Height);

        Assert.Equal(viewport, export);
        Assert.Equal(export, preview);
    }

    [Fact]
    public void Include_in_export_is_independent_from_editor_visibility()
    {
        var includedHidden = Document(Grid(GridGeometry.IsometricSquares), visible: false);
        var excludedVisible = Document(
            Grid(GridGeometry.IsometricSquares) with { IncludeInExport = false }, visible: true);

        Assert.Equal(0, NonBackgroundPixels(RenderPng(includedHidden, false)));
        Assert.True(NonBackgroundPixels(RenderPng(includedHidden, true)) > 0);
        Assert.True(NonBackgroundPixels(RenderPng(excludedVisible, false)) > 0);
        Assert.Equal(0, NonBackgroundPixels(RenderPng(excludedVisible, true)));
    }

    [Fact]
    public void Four_proposed_golden_fixtures_match_without_overwriting_expected()
    {
        var repository = FindRepositoryRoot(AppContext.BaseDirectory);
        var expectedDirectory = Path.Combine(repository, "tests", "fixtures", "grid-goldens");
        var actualDirectory = Path.Combine(repository, "artifacts", "stage10e-grid-geometry", "goldens");
        Directory.CreateDirectory(actualDirectory);

        foreach (var geometry in Enum.GetValues<GridGeometry>())
        {
            var fileName = FileName(geometry);
            var expectedPath = Path.Combine(expectedDirectory, fileName);
            var actualPath = Path.Combine(actualDirectory, fileName);
            var differencePath = Path.Combine(actualDirectory, Path.GetFileNameWithoutExtension(fileName) + "-diff.png");
            var expected = File.ReadAllBytes(expectedPath);
            var expectedHashBefore = Hash(expected);
            var actual = RenderPng(Document(Grid(geometry)), true);
            File.WriteAllBytes(actualPath, actual);

            var changedPixels = WriteDifference(expected, actual, differencePath);

            Assert.Equal(0, changedPixels);
            Assert.Equal(expectedHashBefore, Hash(File.ReadAllBytes(expectedPath)));
        }
    }

    private static GridDefinition Grid(GridGeometry geometry) => new(GridId.New(), geometry, 32)
    {
        Subdivisions = 2,
        LineWidth = 1.5,
        Color = "#FF365A78",
        Opacity = 0.85,
        OffsetX = 11,
        OffsetY = -7,
        RotationDegrees = 13,
        IncludeInExport = true,
    };

    private static MapDocument Document(GridDefinition grid, bool visible = true) => new(
        DocumentId.New(), "grid golden", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(Width, Height, "px", "#FFF4F1E8"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFF4F1E8"),
            new GridLayer(LayerId.New(), "grid", visible, false, 1, [grid]),
        ]);

    private static byte[] RenderPng(MapDocument document, bool export)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = export });
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static int NonBackgroundPixels(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        var background = new SKColor(244, 241, 232, 255);
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) != background) count++;
        return count;
    }

    private static int WriteDifference(byte[] expected, byte[] actual, string path)
    {
        using var left = SKBitmap.Decode(expected);
        using var right = SKBitmap.Decode(actual);
        Assert.Equal((left.Width, left.Height), (right.Width, right.Height));
        using var difference = new SKBitmap(left.Width, left.Height);
        var changed = 0;
        for (var y = 0; y < left.Height; y++)
            for (var x = 0; x < left.Width; x++)
            {
                var a = left.GetPixel(x, y);
                var b = right.GetPixel(x, y);
                var pixel = new SKColor(
                    (byte)Math.Abs(a.Red - b.Red),
                    (byte)Math.Abs(a.Green - b.Green),
                    (byte)Math.Abs(a.Blue - b.Blue),
                    255);
                difference.SetPixel(x, y, pixel);
                if (a != b) changed++;
            }
        using var image = SKImage.FromBitmap(difference);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return changed;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string FileName(GridGeometry geometry) => geometry switch
    {
        GridGeometry.Square => "square.png",
        GridGeometry.IsometricSquares => "isometric-squares.png",
        GridGeometry.HexFlat => "hex-flat.png",
        GridGeometry.HexPointy => "hex-pointy.png",
        _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
    };

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Map.slnx was not found.");
    }
}
