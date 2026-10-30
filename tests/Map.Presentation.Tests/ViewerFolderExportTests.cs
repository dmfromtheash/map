using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// The folder to look at the whole map (owner, 2026-10-30): «Открыть карту.html» with Map's own
/// offline viewer and a lossless WebP pyramid whose full-size level is the PNG export, pixel for pixel.
/// </summary>
public sealed class ViewerFolderExportTests
{
    [Fact]
    public async Task Full_size_tiles_are_the_png_pixels_and_the_page_needs_no_internet()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-viewer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exporter = new ImageExportEngine();
            var document = Painted(2100, 1100);
            var png = Path.Combine(root, "picture.png");
            await exporter.ExportAsync(document, exporter.Plan(document, Request(ExportOutputMode.SingleImage)), png, stamps: null);
            var folder = Path.Combine(root, "Карта — просмотр");
            var result = await exporter.ExportAsync(document, exporter.Plan(document, Request(ExportOutputMode.ViewerFolder)), folder, stamps: null);

            Assert.Equal(ExportOutputMode.ViewerFolder, result.OutputMode);
            Assert.Equal(new[] { "picture.png" }, Directory.GetFiles(root).Select(Path.GetFileName));
            Assert.Equal(new[] { folder }, Directory.GetDirectories(root)); // no scratch left beside it

            var page = File.ReadAllText(Path.Combine(folder, ViewerFolderWriter.PageName));
            Assert.DoesNotContain("MAP_SETTINGS", page);
            Assert.DoesNotContain("http", page); // nothing from the internet
            var json = page[(page.IndexOf("const S = ", StringComparison.Ordinal) + 10)..];
            using var settings = JsonDocument.Parse(json[..(json.IndexOf('}') + 1)]);
            Assert.Equal(2100, settings.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(3, settings.RootElement.GetProperty("levels").GetInt32());
            Assert.Equal("webp", settings.RootElement.GetProperty("format").GetString());

            // 2100×1100 with 1024 tiles: level 0 → 3×2, level 1 → 2×1, level 2 → 1×1.
            Assert.Equal(6, Directory.GetFiles(Path.Combine(folder, "tiles", "0")).Length);
            Assert.Equal(9 + 1, result.FileCount);
            using var whole = Straight(File.ReadAllBytes(png));
            foreach (var file in Directory.GetFiles(Path.Combine(folder, "tiles", "0"), "*.webp"))
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                var (column, row) = (int.Parse(parts[0]), int.Parse(parts[1]));
                using var tile = Straight(File.ReadAllBytes(file));
                for (var y = 0; y < tile.Height; y++)
                    Assert.True(tile.GetPixelSpan().Slice(y * tile.Width * 4, tile.Width * 4).SequenceEqual(
                        whole.GetPixelSpan().Slice(((row * 1024 + y) * 2100 + column * 1024) * 4, tile.Width * 4)), $"{file} row {y}");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Viewer_folder_needs_png()
    {
        var exporter = new ImageExportEngine();
        Assert.Throws<ExportValidationException>(() => exporter.Plan(Painted(300, 200),
            Request(ExportOutputMode.ViewerFolder) with { Format = ExportImageFormat.Jpeg, OutputWidth = 300, OutputHeight = 200 }));
    }

    private static ExportRequest Request(ExportOutputMode mode) =>
        new(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null, 2100, 1100, mode, false, false);

    private static SKBitmap Straight(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded))!;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));
        return bitmap;
    }

    private static MapDocument Painted(int width, int height)
    {
        var shapes = Enumerable.Range(0, 80).Select(i => new SceneObject(ObjectId.New(),
            i % 2 == 0 ? SceneObjectKind.Ellipse : SceneObjectKind.Rectangle, "shape " + i,
            i * 37 % (width - 120), i * 53 % (height - 120), 60 + i % 7 * 15, 50 + i % 5 * 20, i * 13,
            $"#FF{i * 41 % 256:X2}{i * 97 % 256:X2}{i * 23 % 256:X2}", "#FF101010", 2, true, false)).ToArray();
        return new MapDocument(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", "#FF7F9FAE"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF7F9FAE"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, shapes)]);
    }
}
