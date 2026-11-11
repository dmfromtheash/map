using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>Builds the full path edit on the worker: the path command (for its counts) and what the history executes.</summary>
internal delegate (PlacementPathCommand Path, IEditCommand Edit) PathCalculation(CancellationToken token);

/// <summary>
/// R20b: placement along a path goes through the background pipeline. The full path (every stamp of the
/// changed segments) is calculated from a snapshot off the UI thread; the quick 64-sample preview of the
/// draft stays on the canvas meanwhile. When the result is ready and the map has not changed, it lands as one
/// Ctrl+Z (owner 2026-11-11: «Фон + сам»). A changed map is never overwritten: the result is dropped and
/// «Пересчитать» repeats the calculation on the current map. Esc or «Отмена» stops it; the document stays as it was.
/// </summary>
public sealed partial class EditorViewModel
{
    private readonly BackgroundGeneration _pathCalculation = new();
    private long _pathCalculationJob;
    private PlacementPath? _pathPending;
    private bool _pathPendingNew;
    private IReadOnlyList<SceneObject> _pathPendingObjects = [], _pathPendingOverrides = [];
    private Func<bool>? _pathRetry;

    /// <summary>The running calculation of a path change (tests and smokes await it).</summary>
    public Task PendingPathCalculation { get; private set; } = Task.CompletedTask;
    public bool PathCalculationBusy => _pathCalculation.IsBusy;
    /// <summary>The map changed during the calculation; nothing was applied. «Пересчитать» repeats it.</summary>
    public bool PathCalculationConflict { get; private set; }

    private bool StartPathCalculation(PlacementPath shown, bool isNew, Func<PathCalculation?> prepare,
        IReadOnlyList<SceneObject> objects, IReadOnlyList<SceneObject> overrides, Action? applied)
    {
        if (prepare() is not { } calculate) return false;
        var job = ++_pathCalculationJob;
        _pathPending = shown; _pathPendingNew = isNew;
        _pathPendingObjects = objects; _pathPendingOverrides = overrides;
        PathCalculationConflict = false;
        _pathRetry = () => StartPathCalculation(shown, isNew, prepare, objects, overrides, applied);
        PlacementPathCommand? path = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var work = _pathCalculation.CalculateAsync(Session, (_, token) =>
        {
            var built = calculate(token);
            path = built.Path;
            return built.Edit;
        });
        PendingPathCalculation = Land();
        RaisePathState();
        return true;

        async Task Land()
        {
            try { await work; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
            {
                if (job != _pathCalculationJob) return;
                _pathCalculation.Cancel(); ClearPendingPath();
                StatusDetail = "Путь не изменён: " + ex.Message; RaisePathState(); return;
            }
            if (job != _pathCalculationJob) return;
            ClearPendingPath();
            if (_pathCalculation.Preview is null) { RaisePathState(); return; }
            var result = _pathCalculation.TryApply(Session);
            switch (result)
            {
                case GenerationApplyResult.Applied:
                    LastPathChangedObjects = path!.ChangedObjects; LastPathRegeneratedSegments = path.RegeneratedSegments;
                    LastPathCalculationMs = watch.Elapsed.TotalMilliseconds;
                    if (applied is null) AfterEdit(geometryOnly: true); else applied();
                    break;
                case GenerationApplyResult.Conflict:
                    _pathCalculation.Cancel(); PathCalculationConflict = true;
                    StatusDetail = "Карта изменилась во время расчёта пути. Изменение пути не применено. Нажмите «Пересчитать».";
                    break;
                default:
                    _pathCalculation.Cancel();
                    StatusDetail = Session.LastResourceError ?? "Изменение пути не применено.";
                    break;
            }
            RaisePathState();
        }
    }

    /// <summary>Milliseconds from the change to the applied path (diagnostics for the smoke).</summary>
    public double LastPathCalculationMs { get; private set; }

    public bool CancelPathCalculation()
    {
        if (!PathCalculationBusy && _pathPending is null && !PathCalculationConflict) return false;
        ++_pathCalculationJob; _pathCalculation.Cancel(); ClearPendingPath();
        PathCalculationConflict = false; _pathRetry = null;
        StatusDetail = "Расчёт пути отменён. Документ не изменён.";
        RaisePathState(); return true;
    }

    public void RecalculatePath()
    {
        if (_pathRetry is not { } retry) return;
        PathCalculationConflict = false; _pathRetry = null;
        if (!retry()) RaisePathState();
    }

    private void ClearPendingPath()
    {
        _pathPending = null; _pathPendingNew = false;
        _pathPendingObjects = []; _pathPendingOverrides = [];
    }
}
