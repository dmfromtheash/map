using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;

namespace Map.Presentation.Tests;

public sealed class PosterExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-r13-poster-").FullName;

    public static IEnumerable<object[]> Papers()
    {
        foreach (var hex in new[] { false, true })
            foreach (var landscape in new[] { false, true })
                foreach (var (format, w, h) in new[]
                {
                    (HexPaperFormat.A0, 841d, 1189d), (HexPaperFormat.A1, 594d, 841d),
                    (HexPaperFormat.A2, 420d, 594d), (HexPaperFormat.A3, 297d, 420d),
                    (HexPaperFormat.A4, 210d, 297d), (HexPaperFormat.Letter, 215.9, 279.4),
                    (HexPaperFormat.Custom, 180d, 240d),
                })
                    yield return [hex, landscape, format, landscape && format != HexPaperFormat.Custom ? h : w,
                        landscape && format != HexPaperFormat.Custom ? w : h];
    }

    [Theory]
    [MemberData(nameof(Papers))]
    public void Both_categories_write_real_PDF_sheets_and_overview_in_the_requested_size(
        bool hex, bool landscape, HexPaperFormat format, double width, double height)
    {
        var doc = Document(hex);
        var path = Path.Combine(_root, "poster.pdf");
        var options = new HexPrintOptions(PaperFormat: format, Landscape: landscape,
            CustomWidthMillimetres: 180, CustomHeightMillimetres: 240, IncludeKey: false,
            MapWidthMillimetres: 450, HexMillimetres: 40, CropMarks: true, BleedMillimetres: 3, OverlapMillimetres: 12);
        var result = HexPrintExporter.Write(new ExportScene(doc), path, options);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.StartsWith("%PDF", text);
        Assert.Equal(result.TotalPages, Regex.Matches(text, @"/Type\s*/Page\b").Count);
        Assert.Equal(1, result.OverviewPages);
        var boxes = Regex.Matches(text, @"/MediaBox\s*\[0 0 ([\d.]+) ([\d.]+)\]");
        Assert.NotEmpty(boxes);
        foreach (Match match in boxes)
        {
            Assert.InRange(Math.Abs(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - width * 72 / 25.4), 0, 0.121);
            Assert.InRange(Math.Abs(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - height * 72 / 25.4), 0, 0.121);
        }
        Assert.Equal(result.Columns * result.Rows, result.MapPages);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Overlap_and_bleed_preserve_scale_and_cover_the_entire_map(bool hex)
    {
        var scene = new ExportScene(Document(hex));
        var options = new HexPrintOptions(MapWidthMillimetres: 700, HexMillimetres: 100, IncludeKey: false);
        var plain = HexPrintExporter.Plan(scene, options with { OverlapMillimetres = 0 });
        var plan = HexPrintExporter.Plan(scene, options with { OverlapMillimetres = 20, BleedMillimetres = 5 });
        Assert.Equal(plain.Scale, plan.Scale);
        Assert.InRange(Math.Abs(plan.PrintWidth + 2 * plan.Bleed - plain.PrintWidth), 0, 0.001);
        Assert.InRange(Math.Abs(plan.PrintHeight + 2 * plan.Bleed - plain.PrintHeight), 0, 0.001);
        Assert.InRange(Math.Abs(plan.Overlap * plan.Scale - 20 * 72 / 25.4), 0, 0.001);
        Assert.True(plan.Columns >= plain.Columns && plan.Rows >= plain.Rows);
        Assert.True(plan.Columns > 1 && plan.Rows > 1);
        Assert.True((plan.Columns - 1) * (plan.StepWidth - plan.Overlap) + plan.StepWidth >= plan.Area.Width - 0.001);
        Assert.True((plan.Rows - 1) * (plan.StepHeight - plan.Overlap) + plan.StepHeight >= plan.Area.Height - 0.001);
        var result = HexPrintExporter.Write(scene, Path.Combine(_root, "no-overview.pdf"), options with { IncludeOverview = false });
        Assert.Equal(0, result.OverviewPages);
        Assert.Equal(result.MapPages, result.TotalPages);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Impossible_overlap_and_excessive_sheet_count_are_refused_before_creating_a_file(bool hex)
    {
        var scene = new ExportScene(Document(hex));
        var path = Path.Combine(_root, "refused.pdf");
        var tiny = new HexPrintOptions(PaperFormat: HexPaperFormat.Custom, CustomWidthMillimetres: 60,
            CustomHeightMillimetres: 60, OverlapMillimetres: 30, BleedMillimetres: 5);
        Assert.Throws<InvalidOperationException>(() => HexPrintExporter.Write(scene, path, tiny));
        Assert.False(File.Exists(path));
        var enormous = hex
            ? new ExportScene(HexMapFactory.Create("Large", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Pointy, 16, 200, 200, "#FFFFFFFF"))
            : scene;
        Assert.Throws<InvalidOperationException>(() => HexPrintExporter.Write(enormous, path,
            new HexPrintOptions(HexMillimetres: 200, MapWidthMillimetres: 20000)));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Standard_region_is_used_as_the_print_frame()
    {
        var region = new CanvasRegion(10, 20, 100, 80);
        var plan = HexPrintExporter.Plan(new ExportScene(Document(false), region: region), new HexPrintOptions(MapWidthMillimetres: 200));
        Assert.Equal(10, plan.Area.Left);
        Assert.Equal(20, plan.Area.Top);
        Assert.Equal(100, plan.Area.Width);
        Assert.Equal(200 * 72 / 25.4 / 100, plan.Scale, 6);
    }

    private static MapDocument Document(bool hex) => hex
        ? HexMapFactory.Create("Poster", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Pointy, 16, 8, 6, "#FFFFFFFF")
        : new(DocumentId.New(), "Poster", DateTimeOffset.UnixEpoch, new CanvasDefinition(640, 480, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
