using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R22: the «Рельеф» brush of the Land tool - raise, lower, ridge, smooth and erode the heights
/// inside the active land, one undo step per stroke - and a heightmap picture laid over the canvas.
/// </summary>
public sealed partial class EditorViewModel
{
    private bool _landReliefMode;

    public bool LandReliefMode => _landReliefMode;
    public ReliefAction LandReliefAction { get; private set; } = ReliefAction.Raise;

    public void SetLandReliefMode(bool on)
    {
        if (on == _landReliefMode && (!on || LandPaintMode)) return;
        if (on) { SetLandCliffMaskMode(false); SetLandPaintMode(true); }
        CancelStroke(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        _landReliefMode = on;
        Raise(nameof(LandReliefMode));
        if (on) StatusDetail = "Рельеф: ведите кистью по суше — «Поднять», «Опустить», «Хребет», «Сгладить», «Эрозия». Изменение видно, когда отпускаете кнопку мыши.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLandReliefAction(ReliefAction action)
    {
        CancelStroke(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
        LandReliefAction = action;
        Raise(nameof(LandReliefAction));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private string ReliefPreviewColor => LandReliefAction switch
    {
        ReliefAction.Lower => "#90303848",
        ReliefAction.Ridge => "#90FFE0B0",
        ReliefAction.Smooth => "#70FFFFFF",
        ReliefAction.Erode => "#90806040",
        _ => "#90FFF4E0",
    };

    private BrushSettings ReliefSettings() => BrushSettings with
    {
        Texture = null, Eraser = false, Color = "#FFFFFFFF", Scatter = 0, SizeJitter = 0, OpacityJitter = 0,
    };

    private LandReliefCommand CommitRelief(LayerId layerId, RasterStrokeSession stroke)
    {
        var land = (LandLayer)Session.Document.FindLayer(layerId)!;
        var (before, after) = ReliefEditor.Apply(land.ReliefTiles, stroke.CommittedCoverage, LandReliefAction,
            Math.Clamp(stroke.Settings.Opacity, 0, 1), GestureSeed.From(Session.Revision, "relief", 0, ++_brushSeedCounter));
        var name = LandReliefAction switch
        {
            ReliefAction.Lower => "Рельеф: опустить",
            ReliefAction.Ridge => "Рельеф: хребет",
            ReliefAction.Smooth => "Рельеф: сгладить",
            ReliefAction.Erode => "Рельеф: эрозия",
            _ => "Рельеф: поднять",
        };
        return new LandReliefCommand(layerId, before, after, name);
    }

    /// <summary>Lays a heightmap picture (grey levels, light = high) over the whole canvas of the active land.</summary>
    public bool ImportRelief(byte[] grey, int width, int height)
    {
        if (ActiveLayer is not LandLayer { Locked: false } land)
        { StatusDetail = "Выберите слой суши, чтобы положить на него карту высот."; return false; }
        var (before, after) = ReliefEditor.Import(land.ReliefTiles, grey, width, height,
            (int)Session.Document.Canvas.Width, (int)Session.Document.Canvas.Height);
        Session.Execute(new LandReliefCommand(land.Id, before, after, "Карта высот"));
        StatusDetail = $"Карта высот {width}×{height} растянута на холст. Видна только на суше; одна отмена убирает её.";
        AfterEdit(); return true;
    }

    public void ReportReliefImportFailure(string reason) => StatusDetail = reason;

    public bool ClearRelief()
    {
        if (ActiveLayer is not LandLayer { Locked: false } land || land.ReliefTiles.TileCount == 0) return false;
        var before = land.ReliefTiles.Keys.ToDictionary(k => k, k => land.ReliefTiles.TryGet(k));
        var after = before.Keys.ToDictionary(k => k, _ => (byte[]?)null);
        Session.Execute(new LandReliefCommand(land.Id, before, after, "Очистить рельеф"));
        AfterEdit(); return true;
    }
}
