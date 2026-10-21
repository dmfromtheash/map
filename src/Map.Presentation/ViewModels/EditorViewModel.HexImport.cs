using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    public HexImportResult? LastHexImportResult { get; private set; }
    public bool ImportHexJson(string json) => ImportHexJson(json, ActiveHexLayer?.Id);

    /// <summary>Null target creates a separate level; existing layer content is replaced, never merged silently.</summary>
    public bool ImportHexJson(string json, LayerId? targetId, int sourceIndex = 0)
    {
        LastHexImportResult = null;
        if (!IsHexWorkshop || ActiveHexLayer is not { } reference) return false;
        var target = targetId is { } id ? Session.Document.FindLayer(id) as HexLayer : null;
        if (targetId is not null && (target is null || target.Locked))
        { StatusDetail = "Слой недоступен или заблокирован. Карта не изменена."; return false; }
        target ??= new HexLayer(LayerId.New(), "Импорт JSON", true, false, 1,
            HexBoard.FromCells(reference.Board.Layout, reference.Board.Columns, reference.Board.Rows, []))
            { Level = HexLevels.Min() - 1, Grid = reference.Grid, Travel = reference.Travel };
        try
        {
            var result = HexJsonExporter.ReadWithReport(json, target, sourceIndex);
            Session.Execute(new HexImportCommand(Session.Document, result.Layer));
            AfterEdit();
            Raise(nameof(HexLevels));
            HexLevel = target.Level;
            SelectLayer(target.Id);
            _hexSelection.Clear();
            RaiseHexSelection();
            foreach (var property in new[] { nameof(CanUndo), nameof(CanRedo), nameof(HexBoardSummary),
                nameof(HexPaths), nameof(HexRegions), nameof(HexKeys), nameof(HexOverlay) }) Raise(property);
            LastHexImportResult = result;
            StatusDetail = result.Summary;
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        { StatusDetail = error.Message; return false; }
    }
}
