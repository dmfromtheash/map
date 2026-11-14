using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The first click and the first placement on a large map used to build every lookup they need
/// (identity slots, revision index, names, side-list kinds) on the input thread: about 3.3 µs
/// per object, 3.3 s for one click on a million objects. The map prepares them in the
/// background right after open and whenever a snapshot arrives without them; every later
/// snapshot derives them from its delta, so an edit costs what it changes.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>Below this many objects the lazy build is a few milliseconds and stays on demand.</summary>
    public static int DerivedIndexPreparationMinimumObjects { get; set; } = 20_000;

    private int _derivedIndexPreparationRunning;

    /// <summary>The background preparation in flight, or a completed task. Probes and tests await it.</summary>
    public Task DerivedIndexPreparation { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the current snapshot already has every lookup an edit needs.</summary>
    public bool DerivedIndexesPrepared => IsPrepared(Session.Document);

    private static bool IsPrepared(MapDocument document) =>
        DocumentDerivedIndexes.IsPrepared(document) && ObjectTreeRows.LayerIndexesPrepared(document);

    private void QueueDerivedIndexPreparation()
    {
        var document = Session.Document;
        if (DocumentDerivedIndexes.ObjectCount(document) < DerivedIndexPreparationMinimumObjects
            || IsPrepared(document) || _workingMemory.IsUnderPressure) return;
        if (Interlocked.Exchange(ref _derivedIndexPreparationRunning, 1) == 1) return;
        // A below-normal thread of its own that steps aside between lookups while the canvas pans
        // or zooms: right after open it would otherwise take a core from the first gestures.
        DerivedIndexPreparation = Map.Core.Resources.BackgroundWork.RunAsync(() =>
        {
            try
            {
                // Bounded: a burst of edits during preparation is followed, an endless one is
                // left to the next scene change or to the lazy build of the lookup itself.
                for (var round = 0; round < 4; round++)
                {
                    var current = Session.Document;
                    if (IsPrepared(current)) break;
                    DocumentDerivedIndexes.Prepare(current);
                    Map.Core.Resources.BackgroundWork.YieldToInteraction();
                    ObjectTreeRows.PrepareLayerIndexes(current);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Derived data only: the lookup that failed here is built again on demand, where
                // the same failure is reported in the context of the edit that needs it.
            }
            finally
            {
                Volatile.Write(ref _derivedIndexPreparationRunning, 0);
            }
        }, "Map derived lookups");
    }
}
