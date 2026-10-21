using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Export;

namespace Map.Presentation.Tests;

public sealed class HexPrintTests
{
    [Theory]
    [InlineData(HexPaperFormat.Letter, false, 215.9, 279.4)]
    [InlineData(HexPaperFormat.Letter, true, 279.4, 215.9)]
    [InlineData(HexPaperFormat.Custom, false, 180, 240)]
    [InlineData(HexPaperFormat.Custom, true, 180, 240)]
    [InlineData(HexPaperFormat.A4, false, 210, 297)]
    [InlineData(HexPaperFormat.A3, false, 297, 420)]
    public void Pdf_media_box_matches_paper_millimetres(HexPaperFormat paper, bool landscape, double w, double h)
    {
        var bytes = Pdf(new HexPrintOptions(IncludeKey: false, PaperFormat: paper, Landscape: landscape,
            CustomWidthMillimetres: 180, CustomHeightMillimetres: 240));
        var match = Regex.Match(Encoding.Latin1.GetString(bytes), @"/MediaBox\s*\[0 0 ([\d.]+) ([\d.]+)\]");
        Assert.True(match.Success);
        Assert.InRange(Math.Abs(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - w * 72 / 25.4), 0, 0.121);
        Assert.InRange(Math.Abs(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - h * 72 / 25.4), 0, 0.121);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Crop_marks_add_eight_strokes_outside_the_trim_box_in_the_actual_pdf(bool hex)
    {
        var off = Content(Pdf(new HexPrintOptions(IncludeKey: false, CropMarks: false, BleedMillimetres: 3, MapWidthMillimetres: 100), hex));
        var on = Content(Pdf(new HexPrintOptions(IncludeKey: false, CropMarks: true, BleedMillimetres: 3, MapWidthMillimetres: 100), hex));
        // Skia quantizes page size and stroke widths at RasterDpi=300 (half a pixel <= 0.121 pt).
        Assert.Equal(8, Regex.Matches(on, @"\bS\b").Count - Regex.Matches(off, @"\bS\b").Count);
    }

    [Theory]
    [InlineData(0, 210, 0)] [InlineData(210, 20, 0)] [InlineData(210, 297, 6)] [InlineData(210, 297, -1)]
    public void Invalid_paper_or_bleed_is_rejected(double width, double height, double bleed) =>
        Assert.Throws<InvalidOperationException>(() => HexPrintExporter.PaperSizeMillimetres(new HexPrintOptions(
            PaperFormat: HexPaperFormat.Custom, CustomWidthMillimetres: width, CustomHeightMillimetres: height, BleedMillimetres: bleed)));

    private static byte[] Pdf(HexPrintOptions options, bool hex = true)
    {
        var doc = hex ? HexMapFactory.Create("Paper", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Pointy, 16, 2, 2, "#FFFFFFFF")
            : new MapDocument(DocumentId.New(), "Paper", DateTimeOffset.UnixEpoch, new CanvasDefinition(64, 48, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);
        var path = Path.Combine(Path.GetTempPath(), "map-r03-" + Guid.NewGuid().ToString("N") + ".pdf");
        try { HexPrintExporter.Write(doc, path, options); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }
    private static string Content(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes); var result = new StringBuilder();
        foreach (Match match in Regex.Matches(text, @"<<(?:(?!endobj).)*?/Filter /FlateDecode(?:(?!endobj).)*?>>\s*stream\r?\n", RegexOptions.Singleline))
        {
            var start = match.Index + match.Length; var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            using var input = new MemoryStream(bytes, start, end - start);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(z, Encoding.Latin1);
            result.Append(reader.ReadToEnd());
        }
        return result.ToString();
    }
}
