using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R31: the timeline - the year slider (dragging previews, release commits one undo step), the
/// span, the political mode and its legend; the years, border key and political role of the
/// selection; and the series by years as PNG files or an animated GIF.
/// </summary>
public sealed partial class EditorViewModel
{
    private int? _timelinePreviewYear;

    public MapTimeline Timeline => Session.Document.Timeline;

    /// <summary>The year the canvas shows: the dragged one, else the map's.</summary>
    public int TimelineYearShown => _timelinePreviewYear ?? Timeline.Year;

    /// <summary>The map as the timeline shows it (the year's things, blended borders, political look).</summary>
    private MapDocument TimelineShown(MapDocument document)
    {
        if (!document.Timeline.Enabled && !document.Timeline.PoliticalMode) return document;
        return TimelineView.At(document, document.Timeline.Enabled ? TimelineYearShown : null, document.Timeline.PoliticalMode);
    }

    public void SetTimeline(MapTimeline after, string name = "Шкала времени")
    {
        var before = Timeline;
        if (after == before) return;
        try { Session.Execute(new SetTimelineCommand(before, after, name)); }
        catch (ArgumentException ex) { StatusDetail = ex.Message; return; }
        _timelinePreviewYear = null;
        Raise(nameof(Timeline));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>The year from the slider: while dragging only the canvas follows; the release is one undo step.</summary>
    public void SetTimelineYear(int year, bool commit)
    {
        year = Math.Clamp(year, Timeline.First, Timeline.Last);
        if (!commit)
        {
            if (_timelinePreviewYear == year) return;
            _timelinePreviewYear = year;
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }
        _timelinePreviewYear = null;
        if (year == Timeline.Year) { InvalidateScene(EditorChangeReason.CanvasInvalidation); return; }
        SetTimeline(Timeline with { Year = year }, $"Год {year}");
        StatusDetail = $"Карта на {year} год.";
    }

    public void SetTimelineSpan(int first, int last)
    {
        if (first > last) (first, last) = (last, first);
        SetTimeline(Timeline with { First = first, Last = last, Year = Math.Clamp(Timeline.Year, first, last) }, "Годы шкалы");
    }

    /// <summary>The states and disputed lands of the year shown.</summary>
    public IReadOnlyList<(string Name, string Color, PoliticalRole Role)> PoliticalLegend =>
        TimelineView.PoliticalLegend(TimelineShown(Session.Document));

    // ------------------------------------------------------------------ the selection

    /// <summary>The years of the selected object, road or river.</summary>
    public Era? SelectedEra => Session.SelectedObject?.Era ?? (Session.SelectedObject is null ? SelectedRoad?.Era ?? SelectedRiver?.Era : null);

    public bool HasTimedSelection => Session.SelectedObject is not null || SelectedRoad is not null || SelectedRiver is not null;

    public void SetSelectedEra(int? from, int? to)
    {
        var era = from is null && to is null ? null : new Era(from, to);
        if (era is { IsValid: false }) { StatusDetail = "Начало не может быть позже конца."; return; }
        if (Session.SelectedObject is { } item)
        {
            if (item.Era == era) return;
            ReplaceSelected(item, item with { Era = era }, "Годы объекта");
            StatusDetail = $"«{item.Name}»: {era?.ToString() ?? "всегда"}.";
            return;
        }
        if (SelectedRoad is { } road && FindRoadLayer(road.Id) is { } layer)
        {
            if (road.Era == era) return;
            var after = road with { Era = era };
            Session.Execute(new RoadCommand(layer.Id, road.Id, after, "Годы дороги"));
            AfterEdit();
            StatusDetail = $"«{road.Name}»: {era?.ToString() ?? "всегда"}.";
            return;
        }
        if (SelectedRiver is { } river && RiverLayerOf(river.Id) is { } riverLayer)
        {
            if (river.Era == era) return;
            Session.Execute(new RiverCommand(riverLayer.Id, river.Id, river with { Era = era }, "Годы реки"));
            AfterEdit();
            StatusDetail = $"«{river.Name}»: {era?.ToString() ?? "всегда"}.";
        }
    }

    public void SetSelectedMorphKey(string? key)
    {
        if (Session.SelectedObject is not { Shape: not null } item) { StatusDetail = "Ключ границы бывает у фигуры."; return; }
        key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (key is { Length: > 120 }) key = key[..120];
        if (item.MorphKey == key) return;
        ReplaceSelected(item, item with { MorphKey = key }, "Ключ границы");
        StatusDetail = key is null ? "Фигура больше не связана с другими эпохами." : $"Фигуры с ключом «{key}» перетекают друг в друга в промежутке между своими годами.";
    }

    public void SetSelectedPolitical(PoliticalRole role)
    {
        if (Session.SelectedObject is not { Shape: not null } item) { StatusDetail = "Государство или спорная земля — это фигура."; return; }
        if (item.Political == role) return;
        ReplaceSelected(item, item with { Political = role }, "Роль на политической карте");
    }

    private ObjectLayer? FindRoadLayer(Guid roadId) =>
        Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.Roads.Any(r => r.Id == roadId));

    // ------------------------------------------------------------------ the series

    /// <summary>
    /// The map in every <paramref name="step"/>-th year from <paramref name="first"/> to <paramref name="last"/>,
    /// fitted into <paramref name="maxSide"/> pixels: PNG files into a folder, or one looping GIF.
    /// </summary>
    public async Task<int> ExportTimelineSeriesAsync(string target, int first, int last, int step, int maxSide, bool gif)
    {
        if (first > last) (first, last) = (last, first);
        step = Math.Max(1, step);
        var years = Enumerable.Range(0, ((last - first) / step) + 1).Select(i => first + (i * step)).Take(400).ToArray();
        var document = Session.Document;
        var scale = Math.Min(1, maxSide / (double)Math.Max(document.Canvas.Width, document.Canvas.Height));
        var (width, height) = (Math.Max(1, (int)Math.Round(document.Canvas.Width * scale)), Math.Max(1, (int)Math.Round(document.Canvas.Height * scale)));
        try
        {
            await Task.Run(() =>
            {
                using var stamps = new ExportStampImageSource(document, DocumentPath, _assetCatalog);
                var frames = new List<SKBitmap>();
                try
                {
                    if (!gif) Directory.CreateDirectory(target);
                    foreach (var year in years)
                    {
                        var png = ThumbnailRenderer.RenderPng(TimelineView.At(document, year, document.Timeline.PoliticalMode), width, height, stamps);
                        if (gif) frames.Add(SKBitmap.Decode(png));
                        else File.WriteAllBytes(Path.Combine(target, $"{SafeExportName(document.Title)} - {year}.png"), png);
                    }
                    if (gif)
                    {
                        using var file = File.Create(target);
                        GifWriter.Write(file, frames, delayHundredths: 60);
                    }
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось сохранить серию: " + ex.Message;
            _log.Error("TIMELINE_SERIES_FAILED", ex);
            return 0;
        }
        StatusDetail = gif ? $"GIF из {years.Length} кадров сохранён." : $"Сохранено кадров: {years.Length}.";
        return years.Length;
    }

    public async Task<int> PickAndExportTimelineGifAsync(int first, int last, int step, int maxSide)
    {
        if (await _filePicker.PickSaveAsync(SafeExportName(Session.Document.Title) + " - годы.gif") is not { } path) return 0;
        return await ExportTimelineSeriesAsync(path, first, last, step, maxSide, gif: true);
    }

    public async Task<int> PickAndExportTimelinePngAsync(int first, int last, int step, int maxSide)
    {
        if (await _filePicker.PickSaveAsync(SafeExportName(Session.Document.Title) + " - годы.png") is not { } path) return 0;
        // The chosen name gives the folder: the frames go next to it, in a folder of that name.
        var folder = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path));
        return await ExportTimelineSeriesAsync(folder, first, last, step, maxSide, gif: false);
    }
}
