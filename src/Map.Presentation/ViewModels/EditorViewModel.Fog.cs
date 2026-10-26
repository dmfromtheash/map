using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R27: the fog of war of a painted map. The brush (Заметки → Туман) reveals or hides what the
/// players have seen; a stroke is one undo step. The master sees the fog as a veil over the map,
/// the players' window and the players' export as opaque ground.
/// </summary>
public sealed partial class EditorViewModel
{
    private MapFogEditor.Stroke? _fogStroke;
    private (double X, double Y)? _fogLast;
    private bool _fogBrush;
    private bool _fogReveal = true;
    private double _fogRadius = 120;
    private bool _fogVeilShown = true;

    public MapFog Fog => Session.Document.Fog;

    /// <summary>The brush paints fog while the «Туман» tab of the notes tool is open and «Кисть» is on.</summary>
    public bool FogBrushOn => _fogBrush && ActiveTool == EditorTool.Notes && Fog.Enabled;

    public bool FogBrush
    {
        get => _fogBrush;
        set { if (_fogBrush == value) return; _fogBrush = value; Raise(nameof(FogBrush)); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
    }

    /// <summary>True - the brush reveals («Открыть»); false - it hides again («Скрыть»).</summary>
    public bool FogReveal
    {
        get => _fogReveal;
        set { if (_fogReveal == value) return; _fogReveal = value; Raise(nameof(FogReveal)); }
    }

    public double FogBrushRadius
    {
        get => _fogRadius;
        set { if (!double.IsFinite(value)) return; _fogRadius = Math.Clamp(Math.Round(value), 16, 4000); Raise(nameof(FogBrushRadius)); }
    }

    /// <summary>The master sees the fog as a veil over the map («Показывать туман»).</summary>
    public bool FogVeilShown
    {
        get => _fogVeilShown;
        set { if (_fogVeilShown == value) return; _fogVeilShown = value; Raise(nameof(FogVeilShown)); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
    }

    /// <summary>The veil the master's canvas draws, 0…1.</summary>
    public double FogVeilToDraw => Fog.Enabled && FogVeilShown ? .55 : 0;

    /// <summary>The fog mid-stroke, drawn instead of the stored one until the stroke ends.</summary>
    public MapFog? FogStrokePreview => _fogStroke?.Current;

    public void SetFogEnabled(bool enabled)
    {
        if (Fog.Enabled == enabled) return;
        SetFog(Fog with { Enabled = enabled }, enabled ? "Включить туман войны" : "Выключить туман войны");
        StatusDetail = enabled ? "Туман включён: игроки видят только открытое. Откройте кистью то, что они уже видели." : "Туман выключен: игроки видят всю карту.";
    }

    public void RevealAllFog() => SetFog(MapFogEditor.RevealAll(Fog, Session.Document.Canvas.Width, Session.Document.Canvas.Height), "Открыть всю карту");

    public void HideAllFog() => SetFog(MapFogEditor.HideAll(Fog), "Скрыть всю карту");

    private void SetFog(MapFog after, string name)
    {
        try { Session.Execute(new SetFogCommand(Fog, after, name)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { StatusDetail = ex.Message; return; }
        Raise(nameof(Fog));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool BeginFogStroke(double x, double y)
    {
        if (!FogBrushOn) return false;
        _fogStroke = MapFogEditor.Begin(Fog);
        _fogLast = null;
        ContinueFogStroke(x, y);
        return true;
    }

    public void ContinueFogStroke(double x, double y)
    {
        if (_fogStroke is not { } stroke) return;
        var (fromX, fromY) = _fogLast ?? (x, y);
        var length = double.Hypot(x - fromX, y - fromY);
        var steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, _fogRadius / 4)));
        for (var i = _fogLast is null ? steps : 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            MapFogEditor.Dab(stroke, fromX + ((x - fromX) * t), fromY + ((y - fromY) * t), _fogRadius, _fogReveal,
                Session.Document.Canvas.Width, Session.Document.Canvas.Height);
        }
        _fogLast = (x, y);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool EndFogStroke()
    {
        if (_fogStroke is not { } stroke) return false;
        _fogStroke = null; _fogLast = null;
        var after = MapFogEditor.Finish(stroke);
        if (after.Revealed.Count == Fog.Revealed.Count && after.Revealed.All(p => Fog.Revealed.TryGetValue(p.Key, out var rows) && rows.AsSpan().SequenceEqual(p.Value.AsSpan())))
        { InvalidateScene(EditorChangeReason.CanvasInvalidation); return false; }
        SetFog(after, _fogReveal ? "Открыть туман" : "Скрыть туманом");
        return true;
    }

    public bool CancelFogStroke()
    {
        if (_fogStroke is null) return false;
        _fogStroke = null; _fogLast = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    /// <summary>The share of the canvas the players have seen, for the panel.</summary>
    public double FogRevealedShare
    {
        get
        {
            var total = Math.Ceiling(Session.Document.Canvas.Width / (double)MapFog.Cell) * Math.Ceiling(Session.Document.Canvas.Height / (double)MapFog.Cell);
            return total <= 0 ? 0 : Math.Clamp(Fog.RevealedCells / total, 0, 1);
        }
    }

    private bool _exportFogPlayers;

    /// <summary>Picture exports of a painted map with fog show it as the players see it.</summary>
    public bool ExportFogPlayers
    {
        get => _exportFogPlayers;
        set { if (_exportFogPlayers == value) return; _exportFogPlayers = value; Raise(nameof(ExportFogPlayers)); }
    }

    /// <summary>The players' window: of a hex map, or of a painted map with its fog.</summary>
    public void OpenPlayerView() => OpenHexPlayerView();
}
