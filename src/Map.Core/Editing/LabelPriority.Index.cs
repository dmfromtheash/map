using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

public static partial class LabelPriority
{
    private readonly record struct LabelEntry(SceneObject Object, int Importance, WorldBounds Bounds);
    private sealed record LabelLayer(ObjectLayer Layer, LabelIndex Index);
    private readonly record struct Candidate(int Layer, LabelEntry Entry);

    /// <summary>
    /// Label-only persistent derivative. Stamp edits share the entire index; label edits replace
    /// only their entries/cells. Values retain neither source layers nor million-object storage.
    /// ObjectStorage supplies the incremental tie order, including insert/delete and Undo.
    /// </summary>
    private sealed class LabelIndex
    {
        // Most map cells contain one label. Keep that ID inline in the persistent cell node;
        // allocate a persistent set only for genuinely shared cells. Dense edits stay logarithmic.
        private readonly record struct CellLabels(ObjectId Single, ImmutableHashSet<ObjectId>? Many, int Count)
        {
            public CellLabels Add(ObjectId id)
            {
                if (Count == 0) return new(id, null, 1);
                if (Count == 1)
                    return Single == id ? this : new(default, ImmutableHashSet.Create(Single, id), 2);
                var ids = Many!.Add(id);
                return new(default, ids, ids.Count);
            }

            public CellLabels Remove(ObjectId id)
            {
                if (Count == 0) return this;
                if (Count == 1) return Single == id ? default : this;
                var ids = Many!.Remove(id);
                return ids.Count == 1 ? new(ids.Single(), null, 1) : new(default, ids, ids.Count);
            }
        }

        private const double CellSize = 256;
        private static readonly ConditionalWeakTable<IReadOnlyList<SceneObject>, Lazy<LabelIndex>> Indexes = new();
        public ImmutableDictionary<ObjectId, LabelEntry> Labels { get; private init; } = ImmutableDictionary<ObjectId, LabelEntry>.Empty;
        private ImmutableDictionary<(int X, int Y), CellLabels> Cells { get; init; } =
            ImmutableDictionary<(int, int), CellLabels>.Empty;
        private ImmutableHashSet<ObjectId> Oversized { get; init; } = ImmutableHashSet<ObjectId>.Empty;
        public int[] Counts { get; private init; } = new int[LabelSettings.MaxImportance + 1];

        public static LabelIndex For(ObjectLayer layer) => Find(layer, allowBuild: true)!;

        public static bool TryFor(ObjectLayer layer) => Find(layer, allowBuild: false) is not null;

        private static LabelIndex? Find(ObjectLayer layer, bool allowBuild)
        {
            LabelIndex? Ready(IReadOnlyList<SceneObject> objects) =>
                Indexes.TryGetValue(objects, out var value) && value.IsValueCreated ? value.Value : null;
            var cached = Ready(layer.Objects);
            if (cached is not null) return cached;
            var pending = new Stack<(ObjectLayer Layer, ObjectLayerChange Delta)>();
            var source = layer;
            while ((cached = Ready(source.Objects)) is null && ObjectLayerChanges.TryGet(source, out var delta)
                && !ReferenceEquals(source.Objects, delta.Source.Objects))
            {
                pending.Push((source, delta));
                source = delta.Source;
            }
            if (cached is null && !allowBuild) return null;
            cached ??= Indexes.GetValue(source.Objects, static objects => new Lazy<LabelIndex>(() =>
            {
                var storage = (SceneObjectList)objects;
                var result = new LabelIndex().Change([], storage.TextObjects());
                _ = storage.IndexOfTextId(default);
                return result;
            })).Value;
            while (pending.TryPop(out var entry))
            {
                var previous = cached;
                if (!allowBuild)
                {
                    if (Indexes.TryGetValue(entry.Layer.Objects, out var inFlight) && !inFlight.IsValueCreated) return null;
                    var derived = previous.Change(
                        entry.Delta.Removed.Select(i => entry.Delta.Source.Objects[i]),
                        entry.Delta.Added.Select(i => entry.Layer.Objects[i]));
                    var completed = new Lazy<LabelIndex>(() => derived);
                    _ = completed.Value;
                    var published = Indexes.GetValue(entry.Layer.Objects, _ => completed);
                    if (!published.IsValueCreated) return null;
                    cached = published.Value;
                    continue;
                }
                var value = Indexes.GetValue(entry.Layer.Objects, _ => new Lazy<LabelIndex>(() => previous.Change(
                    entry.Delta.Removed.Select(i => entry.Delta.Source.Objects[i]),
                    entry.Delta.Added.Select(i => entry.Layer.Objects[i]))));
                cached = value.Value;
            }
            return cached;
        }

        private LabelIndex Change(IEnumerable<SceneObject> removed, IEnumerable<SceneObject> added)
        {
            var labels = Labels.ToBuilder();
            var cells = Cells.ToBuilder();
            var oversized = Oversized.ToBuilder();
            int[]? counts = null;
            foreach (var item in removed)
                if (labels.TryGetValue(item.Id, out var entry)) ChangeEntry(entry, false);
            foreach (var item in added)
                if (item.Kind == SceneObjectKind.Text && item.Visible && item.Text is { } text)
                    ChangeEntry(new(item, Math.Clamp(text.Importance, LabelSettings.MinImportance, LabelSettings.MaxImportance),
                        Inset(SceneGeometry.BoundsOf(item))), true);
            return counts is null ? this : new LabelIndex
            {
                Labels = labels.ToImmutable(), Cells = cells.ToImmutable(), Oversized = oversized.ToImmutable(), Counts = counts,
            };

            void ChangeEntry(LabelEntry entry, bool add)
            {
                counts ??= (int[])Counts.Clone();
                var id = entry.Object.Id;
                counts[entry.Importance] += add ? 1 : -1;
                if (add) labels[id] = entry; else labels.Remove(id);
                if (!Range(entry.Bounds, out var minX, out var maxX, out var minY, out var maxY))
                {
                    if (add) oversized.Add(id); else oversized.Remove(id);
                    return;
                }
                for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                {
                    var bucket = cells.GetValueOrDefault((x, y));
                    bucket = add ? bucket.Add(id) : bucket.Remove(id);
                    if (bucket.Count == 0) cells.Remove((x, y)); else cells[(x, y)] = bucket;
                }
            }
        }

        public IEnumerable<LabelEntry> Intersecting(WorldBounds bounds)
        {
            // Very large, inverted or non-finite boxes use the exact original predicate.
            if (!Range(bounds, out var minX, out var maxX, out var minY, out var maxY))
            {
                foreach (var entry in Labels.Values)
                    if (entry.Bounds.Intersects(bounds)) yield return entry;
                yield break;
            }
            var seen = new HashSet<ObjectId>();
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                if (Cells.TryGetValue((x, y), out var bucket))
                {
                    if (bucket.Count == 1)
                    {
                        var id = bucket.Single;
                        if (seen.Add(id) && Labels[id].Bounds.Intersects(bounds)) yield return Labels[id];
                    }
                    else
                        foreach (var id in bucket.Many!)
                            if (seen.Add(id) && Labels[id].Bounds.Intersects(bounds)) yield return Labels[id];
                }
            foreach (var id in Oversized)
                if (Labels[id].Bounds.Intersects(bounds)) yield return Labels[id];
        }

        private static bool Range(WorldBounds b, out int minX, out int maxX, out int minY, out int maxY)
        {
            minX = maxX = minY = maxY = 0;
            var x0 = Math.Floor(b.Left / CellSize);
            var x1 = Math.Floor(b.Right / CellSize);
            var y0 = Math.Floor(b.Top / CellSize);
            var y1 = Math.Floor(b.Bottom / CellSize);
            if (!double.IsFinite(x0) || !double.IsFinite(x1) || !double.IsFinite(y0) || !double.IsFinite(y1)
                || x0 < int.MinValue || y0 < int.MinValue || x1 >= int.MaxValue || y1 >= int.MaxValue
                || x1 < x0 || y1 < y0 || (x1 - x0 + 1) * (y1 - y0 + 1) > 64) return false;
            minX = (int)x0; maxX = (int)x1; minY = (int)y0; maxY = (int)y1;
            return true;
        }
    }

    /// <summary>
    /// Membership resolves only queried labels and their earlier overlapping dependencies. This
    /// preserves the global greedy result even when an offscreen label affects a visible label.
    /// Count/enumeration deliberately resolve the whole set; renderers should use Contains.
    /// </summary>
    private sealed class HiddenSet(LabelLayer[] layers, bool overlaps, int shown) : IReadOnlySet<ObjectId>
    {
        private readonly object _gate = new();
        private readonly bool _overlaps = overlaps && CandidateCount(layers, shown) <= MaxOverlapLabels;
        private readonly Dictionary<ObjectId, bool> _resolved = [];
        private HashSet<ObjectId>? _full;

        private static int CandidateCount(LabelLayer[] layers, int shown)
        {
            var count = 0;
            foreach (var layer in layers)
                for (var i = LabelSettings.MinImportance; i <= LabelSettings.MaxImportance; i++)
                    if ((shown & (1 << i)) != 0) count += layer.Index.Counts[i];
            return count;
        }

        public bool Contains(ObjectId id)
        {
            lock (_gate)
            {
                if (_full is not null) return _full.Contains(id);
                for (var i = 0; i < layers.Length; i++)
                    if (layers[i].Index.Labels.TryGetValue(id, out var entry))
                        return (shown & (1 << entry.Importance)) == 0 || (_overlaps && Resolve(new(i, entry)));
                return false;
            }
        }

        private sealed class Frame(Candidate candidate, IEnumerator<Candidate> earlier) : IDisposable
        {
            public Candidate Candidate { get; } = candidate;
            public IEnumerator<Candidate> Earlier { get; } = earlier;
            public Candidate? Pending { get; set; }
            public void Dispose() => Earlier.Dispose();
        }

        private bool Resolve(Candidate candidate)
        {
            if (_resolved.TryGetValue(candidate.Entry.Object.Id, out var hidden)) return hidden;
            var stack = new Stack<Frame>();
            stack.Push(new(candidate, Earlier(candidate).GetEnumerator()));
            try
            {
                while (stack.TryPeek(out var frame))
                {
                    if (frame.Pending is null)
                    {
                        if (!frame.Earlier.MoveNext()) { Finish(false); continue; }
                        frame.Pending = frame.Earlier.Current;
                    }
                    var before = frame.Pending.Value;
                    if (!_resolved.TryGetValue(before.Entry.Object.Id, out var givesWay))
                        stack.Push(new(before, Earlier(before).GetEnumerator()));
                    else if (!givesWay) Finish(true);
                    else frame.Pending = null;
                }
            }
            finally { foreach (var frame in stack) frame.Dispose(); }
            return _resolved[candidate.Entry.Object.Id];

            void Finish(bool value)
            {
                var frame = stack.Pop();
                _resolved[frame.Candidate.Entry.Object.Id] = value;
                frame.Dispose();
            }
        }

        private IEnumerable<Candidate> Earlier(Candidate candidate)
        {
            for (var i = 0; i < layers.Length; i++)
                foreach (var entry in layers[i].Index.Intersecting(candidate.Entry.Bounds))
                {
                    var other = new Candidate(i, entry);
                    if (entry.Object.Id != candidate.Entry.Object.Id && (shown & (1 << entry.Importance)) != 0
                        && Precedes(other, candidate)) yield return other;
                }
        }

        private bool Precedes(Candidate a, Candidate b)
        {
            var importance = a.Entry.Importance.CompareTo(b.Entry.Importance);
            if (importance != 0) return importance > 0;
            var size = a.Entry.Object.Text!.FontSize.CompareTo(b.Entry.Object.Text!.FontSize);
            if (size != 0) return size > 0;
            if (a.Layer != b.Layer) return a.Layer < b.Layer;
            return layers[a.Layer].Layer.ObjectStorage.IndexOfTextId(a.Entry.Object.Id)
                < layers[b.Layer].Layer.ObjectStorage.IndexOfTextId(b.Entry.Object.Id);
        }

        private HashSet<ObjectId> Full
        {
            get
            {
                lock (_gate)
                {
                    if (_full is not null) return _full;
                    var result = new HashSet<ObjectId>();
                    foreach (var layer in layers)
                        foreach (var id in layer.Index.Labels.Keys)
                            if (Contains(id)) result.Add(id);
                    return _full = result;
                }
            }
        }

        public int Count => Full.Count;
        public IEnumerator<ObjectId> GetEnumerator() => Full.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<ObjectId> other) => Full.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<ObjectId> other) => Full.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<ObjectId> other) => Full.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<ObjectId> other) => Full.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<ObjectId> other) => Full.Overlaps(other);
        public bool SetEquals(IEnumerable<ObjectId> other) => Full.SetEquals(other);
    }
}
