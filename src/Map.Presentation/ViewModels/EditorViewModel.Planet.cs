using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R39: the map as a whole planet - the flag (only for a two-to-one canvas), the seamless
/// meridian while drawing, the globe preview and the exports in other projections.
/// </summary>
public sealed partial class EditorViewModel
{
    public MapPlanet Planet => Session.Document.Planet;

    public bool CanvasFitsPlanet => MapPlanet.Fits(Session.Document.Canvas);

    public void SetPlanet(bool enabled, bool seamless)
    {
        if (enabled && !CanvasFitsPlanet)
        {
            StatusDetail = $"Планета — это карта 2:1. Сейчас {Session.Document.Canvas.Width}×{Session.Document.Canvas.Height}: измените размер холста (Холст → Изменить размер).";
            return;
        }
        var before = Planet;
        var after = before with { Enabled = enabled, SeamlessMeridian = seamless };
        if (after == before) return;
        Session.Execute(new SetPlanetCommand(before, after));
        Raise(nameof(Planet));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        StatusDetail = enabled ? "Карта — целая планета: левый край сходится с правым." : "Карта больше не планета.";
    }

    /// <summary>The planet as an equirectangular picture of the given width (two to one), for the globe and the projections.</summary>
    public SKBitmap RenderPlanetSource(int width)
    {
        var document = Session.Document;
        using var stamps = new ExportStampImageSource(document, DocumentPath, _assetCatalog);
        var png = ThumbnailRenderer.RenderPng(document, width, width / 2, stamps);
        return SKBitmap.Decode(png);
    }

    public async Task<bool> ExportProjectionAsync(string path, PlanetProjection projection, int width, double centerLongitude, double centerLatitude, int gores)
    {
        if (!Planet.Enabled) { StatusDetail = "Сначала отметьте карту как планету."; return false; }
        try
        {
            await Task.Run(() =>
            {
                // The source is a little wider than the output, so detail is not lost to resampling.
                using var source = RenderPlanetSource(Math.Clamp(width * 2, 1024, 8192));
                using var projected = PlanetProjections.Project(source, projection, width, centerLongitude, centerLatitude, gores);
                using var image = SKImage.FromBitmap(projected);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(path);
                data.SaveTo(file);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось сохранить проекцию: " + ex.Message;
            _log.Error("PLANET_PROJECTION_FAILED", ex);
            return false;
        }
        StatusDetail = $"Сохранено: {PlanetProjections.Title(projection)}.";
        return true;
    }

    public async Task<bool> PickAndExportProjectionAsync(PlanetProjection projection, int width, double centerLongitude, double centerLatitude, int gores)
    {
        if (await _filePicker.PickSaveAsync($"{SafeExportName(Session.Document.Title)} - {PlanetProjections.Title(projection)}.png") is not { } path) return false;
        return await ExportProjectionAsync(path, projection, width, centerLongitude, centerLatitude, gores);
    }
}
