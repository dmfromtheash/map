using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R30: the map compared with one of its versions - a summary in words, the changes highlighted on
/// the canvas and a «До / После» switch that shows the version read-only.
/// </summary>
public sealed partial class EditorViewModel
{
    private MapDocument? _compareBefore;
    private bool _compareShowsBefore;
    private bool _compareHighlight = true;

    public MapComparison? Comparison { get; private set; }
    public string ComparedVersionName { get; private set; } = "";

    /// <summary>The canvas shows the compared version instead of the map (read-only).</summary>
    public bool CompareShowsBefore
    {
        get => _compareShowsBefore && _compareBefore is not null;
        set
        {
            if (_compareShowsBefore == value) return;
            _compareShowsBefore = value;
            Raise(nameof(CompareShowsBefore));
            StatusDetail = value ? $"Показана версия «{ComparedVersionName}» — только для просмотра." : "Показана текущая карта.";
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
        }
    }

    public bool CompareHighlight
    {
        get => _compareHighlight;
        set { if (_compareHighlight == value) return; _compareHighlight = value; Raise(nameof(CompareHighlight)); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
    }

    /// <summary>The places to mark on the canvas: kind 0 added, 1 removed, 2 changed.</summary>
    public IReadOnlyList<(WorldBounds Bounds, int Kind)>? CompareMarks =>
        Comparison is { } diff && _compareHighlight
            ? [.. diff.Changes.Where(c => c.Bounds.Right > c.Bounds.Left).Select(c => (c.Bounds, (int)c.Kind))]
            : null;

    public bool CompareWithCheckpoint(CheckpointId id)
    {
        if (Session.Document.Checkpoints.FirstOrDefault(c => c.Id == id) is not { } checkpoint)
        {
            StatusDetail = "Версия не найдена.";
            return false;
        }

        _compareBefore = checkpoint.Snapshot.Materialize(Session.Document.Id, Session.Document.Checkpoints);
        Comparison = MapComparison.Compare(_compareBefore, Session.Document, IsSettlement);
        ComparedVersionName = checkpoint.Name;
        _compareShowsBefore = false;
        Raise(nameof(Comparison));
        Raise(nameof(CompareShowsBefore));
        StatusDetail = Comparison.IsEmpty ? $"С версии «{checkpoint.Name}» ничего не изменилось." : $"Сравнение с «{checkpoint.Name}»: " + string.Join(" · ", Comparison.Summary());
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    /// <summary>Recounts after an edit, so the summary follows the map while the comparison is open.</summary>
    private void RefreshComparison()
    {
        if (_compareBefore is null || Comparison is null) return;
        Comparison = MapComparison.Compare(_compareBefore, Session.Document, IsSettlement);
        Raise(nameof(Comparison));
    }

    public void CloseComparison()
    {
        if (Comparison is null) return;
        Comparison = null;
        _compareBefore = null;
        _compareShowsBefore = false;
        Raise(nameof(Comparison));
        Raise(nameof(CompareShowsBefore));
        StatusDetail = "Сравнение закрыто.";
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    internal void TellCompareIsReadOnly() =>
        StatusDetail = "Показана старая версия — только просмотр. Переключите сравнение на «После», чтобы править.";

    /// <summary>Brings a change into view.</summary>
    public void ShowChange(MapChange change)
    {
        var viewport = Session.Viewport;
        var (cx, cy) = ((change.Bounds.Left + change.Bounds.Right) / 2, (change.Bounds.Top + change.Bounds.Bottom) / 2);
        viewport.Set(cx - (viewport.ViewWidth / 2 / viewport.Scale), cy - (viewport.ViewHeight / 2 / viewport.Scale), viewport.Scale);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private bool IsSettlement(SceneObject item) =>
        item.Stamp is { } stamp && _assetCatalog?.FindSetForContent(stamp.AssetSha256)?.Placement?.Role is AssetRole.House or AssetRole.Tower;
}
