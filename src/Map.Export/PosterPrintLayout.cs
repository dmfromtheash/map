using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Export;

/// <summary>Physical points and world coordinates of the same poster layout used by both categories.</summary>
public sealed record PosterPrintLayout(float PageWidth, float PageHeight, float Bleed,
    float PrintWidth, float PrintHeight, double Scale, WorldBounds Area,
    double StepWidth, double StepHeight, double Overlap, int Columns, int Rows);

public static partial class HexPrintExporter
{
    private const float Margin = 28; // ≈10 mm of unprinted paper around the bleed and trim marks.
    private const float Header = 22;

    public static PosterPrintLayout Plan(ExportScene scene, HexPrintOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        var (paperW, paperH) = PaperSizeMillimetres(options);
        var pageW = (float)(paperW * PointsPerMillimetre);
        var pageH = (float)(paperH * PointsPerMillimetre);
        var bleed = (float)(options.BleedMillimetres * PointsPerMillimetre);
        var printW = pageW - 2 * Margin - 2 * bleed;
        var printH = pageH - 2 * Margin - Header - 2 * bleed;
        var overlapPoints = options.OverlapMillimetres * PointsPerMillimetre;
        if (printW <= overlapPoints || printH <= overlapPoints)
            throw new InvalidOperationException("Перекрытие должно быть меньше области печати. Увеличьте лист или уменьшите перекрытие/вылет.");
        WorldBounds area;
        double scale;
        if (scene.Category.Value == "hex")
        {
            var board = scene.Layers.OfType<HexLayer>().FirstOrDefault()?.Board
                ?? throw new InvalidOperationException("На карте нет видимого слоя гексов.");
            area = HexLayerRenderer.BoardRect(board);
            scale = Math.Clamp(options.HexMillimetres, 5, 200) * PointsPerMillimetre / (board.Layout.Size * HexLayout.Sqrt3);
        }
        else
        {
            var region = scene.Region;
            if (region.X < 0 || region.Y < 0 || region.Right > scene.Document.Canvas.Width || region.Bottom > scene.Document.Canvas.Height)
                throw new InvalidOperationException("Область печати должна находиться внутри карты.");
            if (!double.IsFinite(options.MapWidthMillimetres) || options.MapWidthMillimetres is < 10 or > 20000)
                throw new InvalidOperationException("Ширина карты на бумаге: от 10 до 20000 мм.");
            area = new WorldBounds(region.X, region.Y, region.Right, region.Bottom);
            scale = options.MapWidthMillimetres * PointsPerMillimetre / area.Width;
        }
        var stepW = printW / scale;
        var stepH = printH / scale;
        var overlap = overlapPoints / scale;
        var columns = Math.Max(1, Math.Ceiling((area.Width - overlap) / (stepW - overlap)));
        var rows = Math.Max(1, Math.Ceiling((area.Height - overlap) / (stepH - overlap)));
        if (!double.IsFinite(columns * rows) || columns * rows > 400)
            throw new InvalidOperationException("Получится больше 400 листов: увеличьте лист или уменьшите масштаб карты.");
        return new(pageW, pageH, bleed, printW, printH, scale, area, stepW, stepH, overlap, (int)columns, (int)rows);
    }

    private static string SheetName(int row, int column)
    {
        var letters = "";
        for (var n = row + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        return letters + (column + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteOverview(SKDocument pdf, ExportScene scene, HexPrintOptions options,
        PosterPrintLayout layout, IStampImageSource? stamps, TileImageCache tiles, SKTypeface typeface)
    {
        var canvas = pdf.BeginPage(layout.PageWidth, layout.PageHeight);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { IsAntialias = true, Color = SKColors.Black };
        using var frame = new SKPaint { IsAntialias = true, Color = new SKColor(0xC0, 0x35, 0x23), Style = SKPaintStyle.Stroke, StrokeWidth = 0.7f };
        using var font = new SKFont(typeface, 10);
        canvas.DrawText("Схема склейки", Margin, Margin + 12, SKTextAlign.Left, font, ink);
        var area = layout.Area;
        var availableW = layout.PageWidth - 2 * Margin;
        var availableH = layout.PageHeight - 2 * Margin - Header - 26;
        var scale = Math.Min(availableW / area.Width, availableH / area.Height);
        var left = Margin + (availableW - area.Width * scale) / 2;
        var top = Margin + Header;
        var rect = new SKRect((float)left, top, (float)(left + area.Width * scale), (float)(top + area.Height * scale));
        canvas.Save();
        canvas.ClipRect(rect);
        canvas.Translate((float)left, top);
        canvas.Scale((float)scale);
        canvas.Translate((float)-area.Left, (float)-area.Top);
        SceneRenderer.Render(canvas, scene.RenderDocument(), new RenderOptions
        {
            IsExport = true, IncludeBaseColor = true, IncludeExportGrids = options.IncludeGrid,
            VisibleWorld = area, ViewScale = scale * 300 / 72, HexPlayerView = scene.Players,
            HexLevel = scene.HexLevel, Stamps = stamps, TileCache = tiles,
        });
        canvas.Restore();
        using var label = new SKFont(typeface, (float)Math.Clamp(Math.Min(availableW / layout.Columns, availableH / layout.Rows) / 4, 4, 16)) { Embolden = true };
        using var backing = new SKPaint { Color = new SKColor(255, 255, 255, 230) };
        for (var row = 0; row < layout.Rows; row++)
            for (var column = 0; column < layout.Columns; column++)
            {
                var x = left + column * (layout.StepWidth - layout.Overlap) * scale;
                var y = top + row * (layout.StepHeight - layout.Overlap) * scale;
                var right = Math.Min(rect.Right, x + layout.StepWidth * scale);
                var bottom = Math.Min(rect.Bottom, y + layout.StepHeight * scale);
                canvas.DrawRect(new SKRect((float)x, (float)y, (float)right, (float)bottom), frame);
                var text = SheetName(row, column);
                var cx = (float)((x + right) / 2);
                var cy = (float)((y + bottom) / 2);
                var half = label.MeasureText(text) / 2 + 2;
                canvas.DrawRect(new SKRect(cx - half, cy - label.Size, cx + half, cy + 3), backing);
                canvas.DrawText(text, cx, cy, SKTextAlign.Center, label, ink);
            }
        var footer = $"{layout.Columns} × {layout.Rows} листов · перекрытие {options.OverlapMillimetres:0.##} мм";
        font.Size = Math.Min(10, 10 * availableW / Math.Max(1, font.MeasureText(footer)));
        canvas.DrawText(footer, Margin, layout.PageHeight - Margin - 12, SKTextAlign.Left, font, ink);
        const string instruction = "Печатать 100%, без подгонки. Обзор не для склейки.";
        font.Size = 10;
        font.Size = Math.Min(10, 10 * availableW / Math.Max(1, font.MeasureText(instruction)));
        canvas.DrawText(instruction, Margin, layout.PageHeight - Margin, SKTextAlign.Left, font, ink);
        pdf.EndPage();
    }
}
