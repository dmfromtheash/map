using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class TilePackagePngExportTests
{
    [Theory]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 4)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 4)]
    public async Task Painted_tiles_preserve_straight_rgba_and_use_rgb_only_when_opaque(
        bool transparent, bool pageVisible, int workers)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-tile-png-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = PaintedMap();
            if (!pageVisible) document = document.WithLayer(document.Layers[0].Id, l => l with { Visible = false });
            var engine = new ImageExportEngine(new ExportEngineSettings
            {
                TileEdge = 64, RenderParallelism = workers, CompressionParallelism = workers,
            });
            var request = new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
                null, 173, 119, ExportOutputMode.SingleImage, transparent, false);
            var singlePath = Path.Combine(root, "single.png");
            await engine.ExportAsync(document, engine.Plan(document, request), singlePath, null);
            var tilesPath = Path.Combine(root, "tiles");
            await engine.ExportAsync(document, engine.Plan(document, request with { OutputMode = ExportOutputMode.TilePackage }),
                tilesPath, null);
            var manifest = JsonSerializer.Deserialize<TilePackageManifest>(
                File.ReadAllText(Path.Combine(tilesPath, "manifest.json")))!;
            Assert.Equal(6, manifest.Tiles.Count);
            using var single = DecodeStraightRgba(singlePath);
            foreach (var tile in manifest.Tiles)
            {
                var tilePath = Path.Combine(tilesPath, tile.File);
                using var actual = DecodeStraightRgba(tilePath);
                Assert.Equal((tile.Width, tile.Height), (actual.Width, actual.Height));
                var opaque = !transparent && pageVisible || tile.X >= 128;
                using (var file = File.OpenRead(tilePath))
                {
                    var header = new byte[26];
                    file.ReadExactly(header);
                    Assert.Equal("IHDR"u8.ToArray(), header[12..16]);
                    Assert.Equal(opaque ? 2 : 6, header[25]);
                }
                for (var y = 0; y < tile.Height; y++)
                {
                    var expected = single.GetPixelSpan().Slice((tile.Y + y) * single.RowBytes + tile.X * 4, tile.Width * 4);
                    var row = actual.GetPixelSpan().Slice(y * actual.RowBytes, tile.Width * 4);
                    if (opaque)
                        Assert.True(expected.SequenceEqual(row), $"Straight RGBA differs in {tile.File}, row {y}.");
                    else
                        // Skia's own PNG encoder still writes these tiles; its unpremultiplication may round
                        // a partly transparent colour one step apart from ReadPixels. Alpha and every fully
                        // opaque pixel must still be exactly the single image's.
                        for (var x = 0; x < tile.Width * 4; x += 4)
                            Assert.True(row[x + 3] == expected[x + 3] && (row[x + 3] != 255 || row.Slice(x, 4).SequenceEqual(expected.Slice(x, 4))),
                                $"{tile.File} pixel {x / 4},{y}");
                }
                var alpha = actual.GetPixelSpan().ToArray().Where((_, i) => i % 4 == 3).ToArray();
                if (opaque) Assert.All(alpha, a => Assert.Equal(255, a));
                else
                {
                    Assert.Contains((byte)0, alpha);
                    Assert.Contains(alpha, a => a is > 0 and < 255);
                    // The first band has 40 opaque rows before the RGB attempt gives up.
                    if (tile.Y == 0) Assert.Equal((byte)255, alpha[0]);
                }
            }
            Assert.Single(Directory.GetDirectories(root));
            Assert.Equal(7, Directory.GetFiles(tilesPath).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static SKBitmap DecodeStraightRgba(string path)
    {
        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Unpremul));
        try
        {
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(bitmap.Info, bitmap.GetPixels()));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static MapDocument PaintedMap()
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var y = 0; y < TileMap.TileSize; y++)
        for (var x = 0; x < TileMap.TileSize; x++)
        {
            var i = (y * TileMap.TileSize + x) * 4;
            // Mixed alpha (including zero) after opaque rows, plus a fully opaque right-hand
            // column: a transparent-background request must still encode those tiles as RGB.
            var a = y < 40 || x >= 128 ? 255 : (x + y) % 17 == 0 ? 0 : 1 + (x * 3 + y * 7) % 254;
            pixels[i] = (byte)((32 + (x / 5 + y / 9) % 200) * a / 255);
            pixels[i + 1] = (byte)((24 + (x / 11 + y / 3) % 208) * a / 255);
            pixels[i + 2] = (byte)((16 + (x / 7 + y / 13) % 216) * a / 255);
            pixels[i + 3] = (byte)a;
        }
        return new MapDocument(DocumentId.New(), "painted", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(173, 119, "px", "#FF304A60"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FF304A60"),
             new BrushLayer(LayerId.New(), "paint", true, false, 1, TileMap.Empty(4).With(new TileKey(0, 0), pixels))]);
    }
}
