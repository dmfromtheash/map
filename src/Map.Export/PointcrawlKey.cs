using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

/// <summary>
/// R32 «PDF-ключ»: the first page is the map with its pointcrawl fitted to A4 landscape; the next
/// pages list the places by number - name, visited, the master's note or the card's text - and the
/// ways out of each with hours, danger and flags. For players only what they may know is written.
/// </summary>
public static class PointcrawlKey
{
    private const float PageWidth = 842, PageHeight = 595, Margin = 36;

    public static int Write(string path, MapDocument document, bool players, IStampImageSource? stamps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = players ? PlayerScene.Sanitize(document) : document;
        var crawl = source.Pointcrawl;
        var pages = 0;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new SKFileWStream(temporary))
            using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = document.Title + " — пойнткроул", Creator = "Map", Producer = "Map", RasterDpi = 200 }))
            {
                using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
                using var regular = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
                using var titleFont = new SKFont(bold, 16);
                using var headFont = new SKFont(bold, 11.5f);
                using var bodyFont = new SKFont(regular, 10);
                using var ink = new SKPaint { Color = new SKColor(0x2E, 0x26, 0x1F), IsAntialias = true };
                using var faint = new SKPaint { Color = new SKColor(0x6A, 0x60, 0x56), IsAntialias = true };

                // The map page.
                var canvas = pdf.BeginPage(PageWidth, PageHeight);
                canvas.Clear(SKColors.White);
                canvas.DrawText(document.Title + " — пойнткроул" + (players ? " (для игроков)" : ""), Margin, Margin, SKTextAlign.Left, titleFont, ink);
                var (areaW, areaH) = (PageWidth - (2 * Margin), PageHeight - (2 * Margin) - 18);
                var scale = Math.Min(areaW / source.Canvas.Width, areaH / source.Canvas.Height);
                canvas.Save();
                canvas.Translate(Margin + (float)((areaW - (source.Canvas.Width * scale)) / 2), Margin + 18 + (float)((areaH - (source.Canvas.Height * scale)) / 2));
                canvas.Scale((float)scale);
                SceneRenderer.Render(canvas, source, new RenderOptions
                {
                    IsExport = true, IncludeBaseColor = true, ViewScale = scale * 200 / 72.0, StyleRasterScale = 1, HexPlayerView = players, Stamps = stamps,
                });
                canvas.Restore();
                pdf.EndPage();
                pages++;

                // The key pages.
                var y = PageHeight;
                SKCanvas? page = null;
                void NewPage()
                {
                    if (page is not null) pdf.EndPage();
                    page = pdf.BeginPage(PageWidth, PageHeight);
                    page.Clear(SKColors.White);
                    page.DrawText("Ключ мест", Margin, Margin, SKTextAlign.Left, titleFont, ink);
                    y = Margin + 26;
                    pages++;
                }
                void Line(string text, SKFont font, SKPaint paint, float indent = 0)
                {
                    foreach (var piece in Wrap(text, font, PageWidth - (2 * Margin) - indent))
                    {
                        if (page is null || y > PageHeight - Margin) NewPage();
                        page!.DrawText(piece, Margin + indent, y, SKTextAlign.Left, font, paint);
                        y += font.Size * 1.35f;
                    }
                }

                NewPage();
                var nodes = crawl.Nodes.ToDictionary(n => n.Id);
                foreach (var node in crawl.Nodes)
                {
                    var number = crawl.NumberOf(node.Id);
                    Line($"{number}. {node.Name}" + (!players && node.Visited ? " · посещено" : ""), headFont, ink);
                    var text = node.Note.Length > 0 ? node.Note
                        : node.EntityId is { } id && source.Entities.FirstOrDefault(e => e.Id == id) is { } card ? card.Markdown : "";
                    if (text.Length > 0) Line(text.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' '), bodyFont, ink, 12);
                    foreach (var edge in crawl.Edges.Where(e => e.From == node.Id || (!e.OneWay && e.To == node.Id)))
                    {
                        var other = nodes[edge.From == node.Id ? edge.To : edge.From];
                        var flags = new List<string> { $"{edge.Hours:0.#} ч" };
                        if (edge.Danger > 0) flags.Add($"опасность {edge.Danger}");
                        if (edge.OneWay) flags.Add("в одну сторону");
                        if (edge.Hidden) flags.Add("скрытый");
                        Line($"→ {crawl.NumberOf(other.Id)}. {other.Name}{(edge.Name.Length > 0 ? " — " + edge.Name : "")} ({string.Join(", ", flags)})", bodyFont, faint, 12);
                    }
                    y += 6;
                }
                if (page is not null) pdf.EndPage();
                pdf.Close();
            }
            File.Move(temporary, path, overwrite: true);
            return pages;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(next) <= width || line.Length == 0) { line = next; continue; }
            yield return line;
            line = word;
        }
        if (line.Length > 0) yield return line;
    }
}
