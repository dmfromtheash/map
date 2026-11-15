using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

public static partial class LabelPriority
{
    private static readonly ConditionalWeakTable<MapDocument, FramePreparation> FramePreparations = new();

    private sealed class FramePreparation
    {
        private readonly object _gate = new();
        private Task? _task;
        private Action? _ready;
        private bool _completed;
        private readonly Dictionary<int, FrameVisibility> _fallbacks = new();

        public FrameVisibility Request(MapDocument document, int shown, Action ready)
        {
            bool completed;
            FrameVisibility result;
            lock (_gate)
            {
                if (!_fallbacks.TryGetValue(shown, out result))
                    _fallbacks.Add(shown, result = new(document, shown));
                completed = _completed;
                if (!completed)
                {
                    // Keep only the newest frame callback, not one closure per pending frame.
                    _ready = ready;
                    _task ??= Task.Run(() =>
                    {
                        using var load = Resources.BackgroundLoad.Enter(Resources.BackgroundProducer.LabelIndex);
                        try { Prepare(document); }
                        finally
                        {
                            Action? notify;
                            lock (_gate) { _completed = true; notify = _ready; _ready = null; }
                            notify?.Invoke();
                        }
                    });
                }
            }
            if (completed) ready();
            return result;
        }
    }

    /// <summary>
    /// Interactive path: never builds/waits for a cold index on the frame thread. Exports and
    /// callers without a refinement callback use the exact synchronous API. While preparing,
    /// scale/cap decisions are exact; overlap decisions have a fixed pair-test budget. Only
    /// labels whose decision is still unknown are deferred until the completion callback.
    /// </summary>
    public static FrameVisibility ForFrame(MapDocument document, double scale, Action? requestRefinedFrame)
    {
        if (!document.Labels.ByScale && !document.Labels.ResolveOverlaps) return new(None);
        if (requestRefinedFrame is null || IsPrepared(document)) return new(Hidden(document, scale));
        var shown = 0;
        for (var i = LabelSettings.MinImportance; i <= LabelSettings.MaxImportance; i++)
            if (!document.Labels.ByScale || scale >= document.Labels.MinScale(i)) shown |= 1 << i;
        return FramePreparations.GetValue(document, static _ => new()).Request(document, shown, requestRefinedFrame);
    }

    public readonly struct FrameVisibility
    {
        private readonly IReadOnlySet<ObjectId>? _hidden;
        private readonly PendingVisibility? _pending;
        internal FrameVisibility(IReadOnlySet<ObjectId> hidden) => _hidden = hidden;
        internal FrameVisibility(MapDocument document, int shown) => _pending = new(document, shown);
        public bool IsPending => _pending?.IsPending == true;
        public bool Contains(SceneObject item) => _hidden?.Contains(item.Id) ?? _pending!.Contains(item);
    }

    private sealed class PendingVisibility
    {
        // Shared by all layers/frames of this pending snapshot and scale threshold: at most
        // 4,000 shown labels, 32,768 pair tests and one bounded sort until preparation finishes.
        // The cold source is text chunks, never all objects or the general identity table.
        private const int PairBudget = 32_768;
        private readonly MapDocument? _document;
        private readonly int _shown;
        private readonly bool _overlaps;
        private List<(SceneObject Object, int Importance, int Order)>? _candidates;
        private readonly Dictionary<ObjectId, bool> _resolved = new();
        private readonly List<WorldBounds> _kept = new();
        private int _next;
        private int _remaining = PairBudget;
        private readonly object _gate = new();

        /// <summary>A provisional overlap result must not enter a retained rendered-layer cache.</summary>
        public bool IsPending => _document is not null && _overlaps;

        internal PendingVisibility(MapDocument document, int shown)
        {
            _document = document;
            _shown = shown;
            var candidates = 0;
            foreach (var layer in document.Layers.OfType<ObjectLayer>())
                if (layer.Visible) candidates += layer.ObjectStorage.LabelCount(shown);
            _overlaps = document.Labels.ResolveOverlaps && candidates <= MaxOverlapLabels;
        }

        public bool Contains(SceneObject item)
        {
            lock (_gate) return ContainsCore(item);
        }

        private bool ContainsCore(SceneObject item)
        {
            if (item.Kind != SceneObjectKind.Text || !item.Visible || item.Text is not { } text) return false;
            var importance = Math.Clamp(text.Importance, LabelSettings.MinImportance, LabelSettings.MaxImportance);
            if ((_shown & (1 << importance)) == 0) return true;
            if (!_overlaps) return false;
            if (_resolved.TryGetValue(item.Id, out var cached)) return cached;
            if (_remaining == 0) return true;
            if (_candidates is null)
            {
                _candidates = new(MaxOverlapLabels);
                foreach (var layer in _document!.Layers.OfType<ObjectLayer>())
                    if (layer.Visible)
                        foreach (var label in layer.ObjectStorage.TextObjects(_shown))
                            _candidates.Add((label, Math.Clamp(label.Text!.Importance,
                                LabelSettings.MinImportance, LabelSettings.MaxImportance), _candidates.Count));
                _candidates.Sort(static (a, b) =>
                {
                    var priority = b.Importance.CompareTo(a.Importance);
                    if (priority != 0) return priority;
                    var size = b.Object.Text!.FontSize.CompareTo(a.Object.Text!.FontSize);
                    return size != 0 ? size : a.Order.CompareTo(b.Order);
                });
            }
            while (_next < _candidates.Count)
            {
                var candidate = _candidates[_next].Object;
                var bounds = Inset(SceneGeometry.BoundsOf(candidate));
                var hidden = false;
                foreach (var kept in _kept)
                {
                    if (_remaining-- <= 0) { _remaining = 0; return true; }
                    if (kept.Intersects(bounds)) { hidden = true; break; }
                }
                if (!hidden) _kept.Add(bounds);
                _resolved[candidate.Id] = hidden;
                _next++;
                if (candidate.Id == item.Id) return hidden;
            }
            // A renderer override that is not a document label keeps the existing behavior.
            return false;
        }
    }
}
