using System.Text;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Export;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StyleExportTests
{
    [Theory]
    [InlineData(MapStylePreset.OldMapBlaeu)]
    [InlineData(MapStylePreset.EngravingAquatint)]
    [InlineData(MapStylePreset.WatercolorBigShapes)]
    [InlineData(MapStylePreset.UkiyoeSosakuHanga)]
    [InlineData(MapStylePreset.PrintLetterpress)]
    [InlineData(MapStylePreset.PrintTintedLithograph)]
    [InlineData(MapStylePreset.OilAllaPrima)]
    [InlineData(MapStylePreset.OilImpastoRhythm)]
    [InlineData(MapStylePreset.CharcoalVineStump)]
    [InlineData(MapStylePreset.CharcoalHatching)]
    [InlineData(MapStylePreset.BlueprintSurvey)]
    [InlineData(MapStylePreset.MoonlightHasui)]
    [InlineData(MapStylePreset.MoonlightPastel)]
    public async Task Png_tiles_and_300_dpi_pdf_keep_the_program_style(MapStylePreset preset)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-r09-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = Fixture(preset);
            var exporter = new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 });
            var single = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
                null, 512, 384, ExportOutputMode.SingleImage, false, false));
            var package = exporter.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
                null, 512, 384, ExportOutputMode.TilePackage, false, false));
            var png = Path.Combine(root, "single.png");
            var tiles = Path.Combine(root, "tiles");
            await exporter.ExportAsync(document, single, png, null);
            await exporter.ExportAsync(document, package, tiles, null);
            using var actual = SKBitmap.Decode(png);
            using var surface = SKSurface.Create(new SKImageInfo(512, 384))!;
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions());
            using var screen = SKBitmap.FromImage(surface.Snapshot());
            var off = 0;
            for (var y = 0; y < 384; y++)
            for (var x = 0; x < 512; x++)
            {
                var a = actual.GetPixel(x, y); var b = screen.GetPixel(x, y);
                if (Math.Abs(a.Red - b.Red) > 4 || Math.Abs(a.Green - b.Green) > 4 || Math.Abs(a.Blue - b.Blue) > 4) off++;
            }
            Assert.True(off < 512 * 384 / 500, $"{off} screen/PNG pixels differ");
            var manifest = System.Text.Json.JsonSerializer.Deserialize<TilePackageManifest>(
                File.ReadAllText(Path.Combine(tiles, "manifest.json")))!;
            foreach (var tile in manifest.Tiles)
            {
                using var bitmap = SKBitmap.Decode(Path.Combine(tiles, tile.File));
                for (var y = 0; y < tile.Height; y++)
                for (var x = 0; x < tile.Width; x++)
                    Assert.Equal(actual.GetPixel(tile.X + x, tile.Y + y), bitmap.GetPixel(x, y));
            }
            // The same point-space contract as HexPrintExporter and PosterPrintLayout.
            // A 512x384px map at 300dpi occupies 122.88x92.16 PDF points; the style must still
            // embed 512x384 pixels. RasterDpi already supplies the device matrix: applying
            // 300/72 a second time would embed a 2134x1600 raster (1250 dpi).
            var pdfPath = Path.Combine(root, "style.pdf");
            using (var pdf = SKDocument.CreatePdf(pdfPath, new SKDocumentPdfMetadata { RasterDpi = 300, EncodingQuality = 100 }))
            {
                var canvas = pdf.BeginPage(512 * 72f / 300, 384 * 72f / 300);
                canvas.Scale(72f / 300);
                SceneRenderer.Render(canvas, document, new RenderOptions
                    { IsExport = true, ViewScale = 1, StyleRasterScale = 1 });
                pdf.EndPage(); pdf.Close();
            }
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
            Assert.StartsWith("%PDF", text);
            var sizes = Regex.Matches(text, @"/Width\s+(\d+)\s+/Height\s+(\d+)")
                .Select(m => (Width: int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Height: int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            Assert.Contains(sizes, s => s.Width == 512 && s.Height == 384);
            if (Environment.GetEnvironmentVariable("MAP_R09_EXPORTS") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                File.Copy(pdfPath, Path.Combine(output, preset + "-300dpi.pdf"), true);
                File.Copy(png, Path.Combine(output, preset + "-export.png"), true);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static MapDocument Fixture(MapStylePreset preset)
    {
        var tiles = TileMap.Empty(1);
        for (var tx = 0; tx < 2; tx++)
        for (var ty = 0; ty < 2; ty++)
        {
            var coverage = new byte[256 * 256];
            for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                coverage[y * 256 + x] = Math.Pow((x + tx * 256 - 256) / 200.0, 2)
                    + Math.Pow((y + ty * 256 - 192) / 130.0, 2) <= 1 ? (byte)255 : (byte)0;
            tiles = tiles.With(new TileKey(tx, ty), coverage);
        }
        var shape = new SceneObject(ObjectId.New(), SceneObjectKind.Ellipse, "Форма", 220, 120, 90, 90, 0,
            "#FF667744", "#FF223322", 2, true, false);
        return new MapDocument(DocumentId.New(), "Style export", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 384, "px", "#FF7F9FAE"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF7F9FAE"),
             new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, new LandEdgeStyle()),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [shape])])
            { Style = MapStyle.For(preset) };
    }
}
