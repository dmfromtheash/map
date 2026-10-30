using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

/// <summary>
/// R40 «Книга-атлас»: the maps of an atlas in one PDF (A4 landscape) - a contents page, a page per
/// map in the order of the atlas tree (a parent before its detail sheets) with each detail sheet
/// outlined on its parent and marked «см. с. N», the key of each map (its notes and the cards bound
/// to it), and an index of names (labels and cards) with page numbers. For players the scenes are
/// taken through <see cref="PlayerScene"/>: no master's notes, nothing under the fog.
/// </summary>
public static class AtlasBook
{
    private const float PageWidth = 842, PageHeight = 595, Margin = 36;

    /// <summary>The maps of the atlas, each parent followed by its children (depth first, roots in atlas order).</summary>
    public static IReadOnlyList<AtlasMap> Order(MapAtlas atlas)
    {
        var children = atlas.Links.GroupBy(l => l.Parent).ToDictionary(g => g.Key, g => g.Select(l => l.Child).ToList());
        var childIds = atlas.Links.Select(l => l.Child).ToHashSet();
        var result = new List<AtlasMap>();
        void Visit(AtlasMap map)
        {
            if (result.Contains(map)) return;
            result.Add(map);
            foreach (var child in children.GetValueOrDefault(map.Id) ?? [])
                if (atlas.Maps.FirstOrDefault(m => m.Id == child) is { } next) Visit(next);
        }
        foreach (var root in atlas.Maps.Where(m => !childIds.Contains(m.Id))) Visit(root);
        foreach (var map in atlas.Maps) Visit(map);
        return result;
    }

    /// <param name="stamps">
    /// The pictures of one map of the book, asked for each page in turn: every map has its own embedded assets and
    /// project folder. A source that is <see cref="IDisposable"/> is disposed when its page is drawn.
    /// </param>
    public static int Write(string path, string title, IReadOnlyList<(Guid Id, MapDocument Document)> maps, MapAtlas? atlas, bool players,
        Func<Guid, MapDocument, IStampImageSource?>? stamps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var scenes = maps.Select(m => (m.Id, Document: players ? PlayerScene.Sanitize(m.Document) : m.Document)).ToList();

        // Page numbers first: contents is page 1; each map page is followed by its key pages.
        using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var regular = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        using var titleFont = new SKFont(bold, 18);
        using var headFont = new SKFont(bold, 11.5f);
        using var bodyFont = new SKFont(regular, 10);
        var keys = scenes.Select(s => KeyLines(s.Document)).ToList();
        var linesPerPage = (int)((PageHeight - (2 * Margin) - 30) / (bodyFont.Size * 1.4f));
        var pageOfMap = new Dictionary<Guid, int>();
        var page = 2;
        for (var i = 0; i < scenes.Count; i++)
        {
            pageOfMap[scenes[i].Id] = page;
            page += 1 + (keys[i].Count == 0 ? 0 : (int)Math.Ceiling(keys[i].Count / (double)linesPerPage));
        }

        // The index of names: labels and cards, with the page of their map.
        var index = scenes.SelectMany(s => Names(s.Document).Select(n => (Name: n, Page: pageOfMap[s.Id])))
            .GroupBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Name: g.First().Name, Pages: g.Select(e => e.Page).Distinct().OrderBy(p => p).ToList()))
            .OrderBy(e => e.Name, StringComparer.CurrentCulture).ToList();

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var pages = 0;
        try
        {
            using (var stream = new SKFileWStream(temporary))
            using (var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title + " — атлас", Creator = "Map", Producer = "Map", RasterDpi = 200 }))
            {
                using var ink = new SKPaint { Color = new SKColor(0x2E, 0x26, 0x1F), IsAntialias = true };
                using var faint = new SKPaint { Color = new SKColor(0x6A, 0x60, 0x56), IsAntialias = true };
                using var frame = new SKPaint { Color = new SKColor(0x9A, 0x2E, 0x22), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };

                // Contents.
                var canvas = pdf.BeginPage(PageWidth, PageHeight);
                canvas.Clear(SKColors.White);
                canvas.DrawText(title + (players ? " — атлас для игроков" : " — атлас"), Margin, Margin + 6, SKTextAlign.Left, titleFont, ink);
                var y = Margin + 40;
                canvas.DrawText("Содержание", Margin, y, SKTextAlign.Left, headFont, ink); y += 22;
                foreach (var (id, document) in scenes)
                {
                    var depth = atlas is null ? 0 : Depth(atlas, id);
                    canvas.DrawText(document.Title, Margin + (depth * 16), y, SKTextAlign.Left, bodyFont, ink);
                    canvas.DrawText(pageOfMap[id].ToString(System.Globalization.CultureInfo.InvariantCulture), PageWidth - Margin, y, SKTextAlign.Right, bodyFont, ink);
                    y += 16;
                    if (y > PageHeight - Margin) break;
                }
                if (index.Count > 0) { y += 8; canvas.DrawText($"Указатель названий — с. {page}", Margin, y, SKTextAlign.Left, bodyFont, faint); }
                pdf.EndPage(); pages++;

                for (var i = 0; i < scenes.Count; i++)
                {
                    var (id, document) = scenes[i];
                    // The map page.
                    canvas = pdf.BeginPage(PageWidth, PageHeight);
                    canvas.Clear(SKColors.White);
                    var crumbs = atlas is null ? document.Title : string.Join(" › ", Breadcrumbs(atlas, id));
                    canvas.DrawText(crumbs, Margin, Margin, SKTextAlign.Left, headFont, ink);
                    canvas.DrawText(pageOfMap[id].ToString(System.Globalization.CultureInfo.InvariantCulture), PageWidth - Margin, PageHeight - 16, SKTextAlign.Right, bodyFont, faint);
                    var (areaW, areaH) = (PageWidth - (2 * Margin), PageHeight - (2 * Margin) - 22);
                    var scale = Math.Min(areaW / document.Canvas.Width, areaH / document.Canvas.Height);
                    var (ox, oy) = (Margin + (float)((areaW - (document.Canvas.Width * scale)) / 2), Margin + 18 + (float)((areaH - (document.Canvas.Height * scale)) / 2));
                    canvas.Save();
                    canvas.Translate(ox, oy);
                    canvas.Scale((float)scale);
                    var pictures = stamps?.Invoke(id, maps[i].Document);
                    try
                    {
                        SceneRenderer.Render(canvas, document, new RenderOptions
                        {
                            IsExport = true, IncludeBaseColor = true, ViewScale = scale * 200 / 72.0, StyleRasterScale = 1, HexPlayerView = players, Stamps = pictures,
                        });
                    }
                    finally { (pictures as IDisposable)?.Dispose(); }
                    canvas.Restore();
                    // Detail sheets outlined on their parent with a page reference.
                    if (atlas is not null)
                        foreach (var link in atlas.Links.Where(l => l.Parent == id && pageOfMap.ContainsKey(l.Child)))
                        {
                            var t = link.Transform;
                            var rect = new SKRect(ox + (float)(t.X * scale), oy + (float)(t.Y * scale), ox + (float)((t.X + t.Width) * scale), oy + (float)((t.Y + t.Height) * scale));
                            canvas.DrawRect(rect, frame);
                            var tag = "см. с. " + pageOfMap[link.Child].ToString(System.Globalization.CultureInfo.InvariantCulture);
                            using var paper = new SKPaint { Color = SKColors.White.WithAlpha(220) };
                            var w = bodyFont.MeasureText(tag) + 6;
                            canvas.DrawRect(new SKRect(rect.Left, rect.Top - 13, rect.Left + w, rect.Top), paper);
                            canvas.DrawText(tag, rect.Left + 3, rect.Top - 3, SKTextAlign.Left, bodyFont, ink);
                        }
                    pdf.EndPage(); pages++;

                    // The key pages.
                    var lines = keys[i];
                    for (var start = 0; start < lines.Count; start += linesPerPage)
                    {
                        canvas = pdf.BeginPage(PageWidth, PageHeight);
                        canvas.Clear(SKColors.White);
                        canvas.DrawText("Ключ — " + document.Title, Margin, Margin, SKTextAlign.Left, headFont, ink);
                        y = Margin + 26;
                        foreach (var (text, heading) in lines.Skip(start).Take(linesPerPage))
                        {
                            canvas.DrawText(text, Margin + (heading ? 0 : 12), y, SKTextAlign.Left, heading ? headFont : bodyFont, heading ? ink : faint);
                            y += bodyFont.Size * 1.4f;
                        }
                        pdf.EndPage(); pages++;
                    }
                }

                // The index of names, in two columns.
                if (index.Count > 0)
                {
                    var rows = (int)((PageHeight - (2 * Margin) - 30) / 14);
                    for (var start = 0; start < index.Count; start += rows * 2)
                    {
                        canvas = pdf.BeginPage(PageWidth, PageHeight);
                        canvas.Clear(SKColors.White);
                        canvas.DrawText("Указатель названий", Margin, Margin, SKTextAlign.Left, headFont, ink);
                        for (var k = 0; k < rows * 2 && start + k < index.Count; k++)
                        {
                            var (name, numbers) = index[start + k];
                            var columnX = Margin + ((k / rows) * ((PageWidth - (2 * Margin)) / 2));
                            var rowY = Margin + 26 + ((k % rows) * 14);
                            canvas.DrawText(Clip(name, bodyFont, 260), columnX, rowY, SKTextAlign.Left, bodyFont, ink);
                            canvas.DrawText(string.Join(", ", numbers), columnX + 300, rowY, SKTextAlign.Left, bodyFont, faint);
                        }
                        pdf.EndPage(); pages++;
                    }
                }
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

    private static List<(string Text, bool Heading)> KeyLines(MapDocument document)
    {
        var lines = new List<(string, bool)>();
        foreach (var note in document.Notes)
        {
            lines.Add((note.Title, true));
            foreach (var part in Wrap(note.Markdown, 120)) lines.Add((part, false));
        }
        foreach (var card in document.Entities.Where(e => e.Bindings.Any(b => b.MapId is null || b.MapId == document.Id.Value)))
        {
            lines.Add((card.Name + " · " + MapEntities.KindTitle(card.Kind), true));
            foreach (var part in Wrap(card.Markdown, 120)) lines.Add((part, false));
        }
        return lines;
    }

    private static IEnumerable<string> Names(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).SelectMany(l => l.Objects)
            .Where(o => o.Kind == SceneObjectKind.Text && o.Visible && !string.IsNullOrWhiteSpace(o.Text?.Content))
            .Select(o => o.Text!.Content.Replace('\n', ' ').Trim())
            .Concat(document.Entities.Select(e => e.Name))
            .Where(n => n.Length > 0 && n != "?");

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + word.Length + 1 > width && line.Length > 0) { yield return line; line = word; }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            if (line.Length > 0) yield return line;
        }
    }

    private static string Clip(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }

    private static int Depth(MapAtlas atlas, Guid id)
    {
        var depth = 0;
        while (atlas.Links.FirstOrDefault(l => l.Child == id) is { } link && depth < 32) { id = link.Parent; depth++; }
        return depth;
    }

    private static IEnumerable<string> Breadcrumbs(MapAtlas atlas, Guid id)
    {
        try { return atlas.Breadcrumbs(id).Select(m => m.Title); }
        catch (InvalidOperationException) { return [atlas.Maps.FirstOrDefault(m => m.Id == id)?.Title ?? ""]; }
    }
}
