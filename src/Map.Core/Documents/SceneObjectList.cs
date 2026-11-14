using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>
/// Immutable object-order storage. A point replacement copies bounded chunks and persistent
/// directory paths; insert/remove rebuild only boundary chunks and share every untouched chunk.
/// Enumeration and the public index contract preserve the exact serialized/draw order.
/// </summary>
public sealed partial class SceneObjectList : IReadOnlyList<SceneObject>
{
    private IdentityTable? _identitySlots;
    private IdentityTable? _textIdentitySlots;
    // Created with each new immutable chunk, while loading/editing, never by rendering.
    // Empty stamp chunks share one value; there is no entry/object per stamp.
    private sealed record TextChunk(int[] Offsets, int[] Counts);
    private static readonly TextChunk NoText = new([], new int[LabelSettings.MaxImportance + 1]);
    private static readonly ConditionalWeakTable<SceneObject[], TextChunk> TextChunks = new();

    /// <summary>Number of full identity tables built (every object visited). Diagnostics and tests only.</summary>
    internal static long IdentityRootBuilds => System.Threading.Interlocked.Read(ref _identityRootBuilds);
    private static long _identityRootBuilds;

    /// <summary>Objects visited by those identity table builds. Diagnostics and tests only.</summary>
    internal static long IdentityRootObjects => System.Threading.Interlocked.Read(ref _identityRootObjects);
    private static long _identityRootObjects;

    /// <summary>
    /// Object id → position. The root is one plain hash table built once per loaded list (about
    /// 15x cheaper than a persistent tree of a million entries) and shared, never mutated, by
    /// every derived list. Its slots name an origin chunk and an offset. Chunks are immutable, so
    /// while an origin chunk is part of the list its slots are exact. When an edit rebuilds a
    /// chunk, the derived list records per chunk, not per object, where the kept runs of each
    /// origin went (<see cref="IdentityTable.Forward"/>) and which origin runs each new chunk
    /// holds (<see cref="IdentityTable.Holdings"/>), so a later rebuild of that chunk forwards
    /// the origins directly to the newest chunk. Only objects new to the list get their own slot
    /// in a small persistent overlay. An edit therefore costs the chunks it touches plus the
    /// objects it adds or removes, never one entry per object of a rebuilt chunk.
    /// </summary>
    private sealed class IdentityTable(
        Dictionary<ObjectId, Slot> root,
        ImmutableDictionary<ObjectId, Slot> overlay,
        ImmutableDictionary<SceneObject[], Run[]> forward,
        ImmutableDictionary<SceneObject[], Piece[]> holdings)
    {
        public Dictionary<ObjectId, Slot> Root { get; } = root;
        public ImmutableDictionary<ObjectId, Slot> Overlay { get; } = overlay;
        /// <summary>Origin chunk no longer in the list → its kept runs, by origin offset.</summary>
        public ImmutableDictionary<SceneObject[], Run[]> Forward { get; } = forward;
        /// <summary>Rebuilt chunk → the origin runs it holds, by chunk offset. Absent: it is its own origin.</summary>
        public ImmutableDictionary<SceneObject[], Piece[]> Holdings { get; } = holdings;
    }

    private readonly record struct Slot(SceneObject[] Chunk, int Offset);
    private readonly record struct Run(int OriginStart, int Length, SceneObject[] Chunk, int ChunkOffset);
    private readonly record struct Piece(SceneObject[] Origin, int OriginStart, int Length, int Offset);
    // A run of a rebuilt chunk (or of an edit buffer) at Offset: Length elements copied from a
    // dropped chunk starting at SourceStart, or, with no Source, objects new to the list.
    private readonly record struct Segment(SceneObject[]? Source, int SourceStart, int Length, int Offset);

    /// <summary>The chunks an edit dropped, the chunks it created as runs, and the objects that left.</summary>
    private sealed class Rewrite
    {
        public List<SceneObject[]> Dropped { get; } = [];
        public List<(SceneObject[] Chunk, Segment[] Segments)> Created { get; } = [];
        public List<ObjectId> Left { get; } = [];
    }

    private static readonly ImmutableDictionary<ObjectId, Slot> NoOverlay = ImmutableDictionary<ObjectId, Slot>.Empty;
    private static readonly ImmutableDictionary<SceneObject[], Run[]> NoForward =
        ImmutableDictionary.Create<SceneObject[], Run[]>(ReferenceEqualityComparer.Instance);
    private static readonly ImmutableDictionary<SceneObject[], Piece[]> NoHoldings =
        ImmutableDictionary.Create<SceneObject[], Piece[]>(ReferenceEqualityComparer.Instance);

    private static readonly IdentityTable EmptyIdentities = new([], NoOverlay, NoForward, NoHoldings);

    public const int ChunkSize = 256;

    // Persistence keys derived bytes by immutable chunk identity, never by layer id or position.
    // Friend assemblies must treat these arrays as read-only, just like the storage itself.
    internal IReadOnlyList<SceneObject[]> StorageChunks => _chunks;
    internal void ValidateDirectory() => _chunks.Validate();

    private static readonly Lazy<SceneObjectList> EmptyInstance = new(() => new([], 0));
    private readonly ChunkDirectory _chunks;

    private SceneObjectList(SceneObject[][] chunks, int count)
    {
        _chunks = ChunkDirectory.From(chunks);
        Count = count;
        if (_chunks.ObjectCount != count)
            throw new ArgumentException("Scene object chunks do not match their declared count.", nameof(chunks));
        (_visibleLegend, _namedLegend) = DeriveLegend(null, null);
    }

    private SceneObjectList(ChunkDirectory chunks, SceneObjectList source, Rewrite rewrite)
    {
        _chunks = chunks;
        Count = chunks.ObjectCount;
        (_visibleLegend, _namedLegend) = DeriveLegend(source, rewrite);
        if ((source._identitySlots ?? (source.Count == 0 ? EmptyIdentities : null)) is { } slots) _identitySlots = Derive(slots, rewrite);
        else if (source._textIdentitySlots is { } textSlots) _textIdentitySlots = Derive(textSlots, rewrite, textOnly: true);
    }

    private static TextChunk TextOf(SceneObject[] objects) =>
        TextChunks.GetValue(objects, static chunk =>
            {
                var textCount = 0;
                foreach (var item in chunk)
                    if (item.Kind == SceneObjectKind.Text) textCount++;
                if (textCount == 0) return NoText;
                var offsets = new int[textCount];
                var next = 0;
                var counts = new int[LabelSettings.MaxImportance + 1];
                for (var i = 0; i < chunk.Length; i++)
                    if (chunk[i].Kind == SceneObjectKind.Text)
                    {
                        offsets[next++] = i;
                        if (chunk[i].Visible && chunk[i].Text is { } style)
                            counts[Math.Clamp(style.Importance, LabelSettings.MinImportance, LabelSettings.MaxImportance)]++;
                    }
                return new TextChunk(offsets, counts);
            });

    /// <summary>True when id lookups of this list no longer visit every object.</summary>
    internal bool HasIdentityTable => _identitySlots is not null;

    public static SceneObjectList Empty => EmptyInstance.Value;

    /// <summary>An immutable input may be shared; every other caller list is copied.</summary>
    public static SceneObjectList From(IReadOnlyList<SceneObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects is SceneObjectList persistent)
        {
            return persistent;
        }

        if (objects.Count == 0)
        {
            return Empty;
        }

        var chunks = new SceneObject[(objects.Count + ChunkSize - 1) / ChunkSize][];
        for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var start = chunkIndex * ChunkSize;
            var length = Math.Min(ChunkSize, objects.Count - start);
            var chunk = new SceneObject[length];
            for (var offset = 0; offset < length; offset++)
            {
                chunk[offset] = objects[start + offset];
            }

            chunks[chunkIndex] = chunk;
        }

        return new SceneObjectList(chunks, objects.Count);
    }

    public int Count { get; }

    public int IndexOfId(ObjectId id)
    {
        var table = _identitySlots;
        if (table is null)
        {
            var root = new Dictionary<ObjectId, Slot>(Count);
            foreach (var chunk in _chunks)
                for (var i = 0; i < chunk.Length; i++) root.Add(chunk[i].Id, new Slot(chunk, i));
            System.Threading.Interlocked.Increment(ref _identityRootBuilds);
            System.Threading.Interlocked.Add(ref _identityRootObjects, Count);
            System.Threading.Interlocked.CompareExchange(ref _identitySlots,
                new IdentityTable(root, NoOverlay, NoForward, NoHoldings), null);
            table = _identitySlots;
        }

        return Position(table, id);
    }

    internal int LabelCount(int shown)
    {
        var count = 0;
        for (var i = LabelSettings.MinImportance; i <= LabelSettings.MaxImportance; i++)
            if ((shown & (1 << i)) != 0) count += _chunks.Labels[i];
        return count;
    }

    internal IEnumerable<SceneObject> TextObjects(int shown = -1)
    {
        foreach (var chunk in _chunks.TextChunksOnly())
        {
            var text = TextOf(chunk);
            var relevant = false;
            for (var i = LabelSettings.MinImportance; i <= LabelSettings.MaxImportance; i++)
                if ((shown & (1 << i)) != 0 && text.Counts[i] > 0) { relevant = true; break; }
            if (!relevant) continue;
            foreach (var offset in text.Offsets)
            {
                var item = chunk[offset];
                if (item.Visible && item.Text is { } style
                    && (shown & (1 << Math.Clamp(style.Importance, LabelSettings.MinImportance, LabelSettings.MaxImportance))) != 0)
                    yield return item;
            }
        }
    }

    // The label tie order needs slots for texts only. The editor's general identity table
    // remains a separate derived lookup prepared on its existing background path.
    internal int IndexOfTextId(ObjectId id)
    {
        if (_identitySlots is { } general) return Position(general, id);
        var table = _textIdentitySlots;
        if (table is null)
        {
            var root = new Dictionary<ObjectId, Slot>();
            foreach (var chunk in _chunks.TextChunksOnly())
                foreach (var offset in TextOf(chunk).Offsets) root.Add(chunk[offset].Id, new Slot(chunk, offset));
            Interlocked.CompareExchange(ref _textIdentitySlots,
                new IdentityTable(root, NoOverlay, NoForward, NoHoldings), null);
            table = _textIdentitySlots;
        }
        return Position(table, id);
    }

    private int Position(IdentityTable table, ObjectId id)
    {
        // An overlay slot is newer than any root slot of the same id.
        if (!table.Overlay.TryGetValue(id, out var slot) && !table.Root.TryGetValue(id, out slot)) return -1;
        if (TryChunkStart(slot.Chunk, out var start)) return start + slot.Offset;
        if (!table.Forward.TryGetValue(slot.Chunk, out var runs)) return -1;
        var run = FindRun(runs, slot.Offset);
        return run >= 0 && TryChunkStart(runs[run].Chunk, out start)
            ? start + runs[run].ChunkOffset + slot.Offset - runs[run].OriginStart
            : -1;
    }

    private bool TryChunkStart(SceneObject[] chunk, out int start) => _chunks.TryStart(chunk, out start);

    private static int FindRun(Run[] runs, int originOffset)
    {
        int low = 0, high = runs.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            var run = runs[middle];
            if (originOffset < run.OriginStart) high = middle - 1;
            else if (originOffset >= run.OriginStart + run.Length) low = middle + 1;
            else return middle;
        }

        return -1;
    }

    /// <summary>
    /// The identity table of a derived list: work proportional to the dropped and created chunks
    /// (run by run, not object by object) and to the objects that enter or leave the list, never
    /// to the objects of the whole list.
    /// </summary>
    private static IdentityTable Derive(IdentityTable slots, Rewrite rewrite, bool textOnly = false)
    {
        var overlay = slots.Overlay.ToBuilder();
        var forward = slots.Forward.ToBuilder();
        var holdings = slots.Holdings.ToBuilder();
        var dropped = new HashSet<SceneObject[]>(rewrite.Dropped, ReferenceEqualityComparer.Instance);
        var affected = new HashSet<SceneObject[]>(ReferenceEqualityComparer.Instance);
        foreach (var chunk in rewrite.Dropped)
        {
            if (holdings.TryGetValue(chunk, out var held)) foreach (var piece in held) affected.Add(piece.Origin);
            else affected.Add(chunk);
        }

        var runs = new Dictionary<SceneObject[], List<Run>>(ReferenceEqualityComparer.Instance);
        var inserted = new List<Slot>();
        var pieces = new List<Piece>();
        foreach (var (chunk, segments) in rewrite.Created)
        {
            pieces.Clear();
            foreach (var segment in segments)
            {
                if (segment.Source is not { } from)
                {
                    // New to the list: the created chunk is its own origin, with overlay slots.
                    Add(chunk, segment.Offset, segment.Length, segment.Offset);
                    for (var i = 0; i < segment.Length; i++)
                        if (!textOnly || chunk[segment.Offset + i].Kind == SceneObjectKind.Text)
                            inserted.Add(new Slot(chunk, segment.Offset + i));
                }
                else if (!holdings.TryGetValue(from, out var held))
                {
                    Add(from, segment.SourceStart, segment.Length, segment.Offset);
                }
                else
                {
                    // Compose through the pieces the dropped chunk held, clipped to this run.
                    var end = segment.SourceStart + segment.Length;
                    foreach (var piece in held)
                    {
                        var start = Math.Max(segment.SourceStart, piece.Offset);
                        var stop = Math.Min(end, piece.Offset + piece.Length);
                        if (start < stop)
                            Add(piece.Origin, piece.OriginStart + start - piece.Offset, stop - start,
                                segment.Offset + start - segment.SourceStart);
                    }
                }
            }

            foreach (var piece in pieces)
            {
                if (ReferenceEquals(piece.Origin, chunk)) continue;
                if (!runs.TryGetValue(piece.Origin, out var list)) runs.Add(piece.Origin, list = []);
                list.Add(new Run(piece.OriginStart, piece.Length, chunk, piece.Offset));
            }

            if (pieces.Count != 1 || !ReferenceEquals(pieces[0].Origin, chunk)) holdings[chunk] = [.. pieces];
        }

        void Add(SceneObject[] origin, int originStart, int length, int offset)
        {
            if (pieces.Count > 0 && pieces[^1] is var last && ReferenceEquals(last.Origin, origin)
                && last.OriginStart + last.Length == originStart && last.Offset + last.Length == offset)
                pieces[^1] = last with { Length = last.Length + length };
            else
                pieces.Add(new Piece(origin, originStart, length, offset));
        }

        // Objects that left the list: their own overlay slots go with them.
        foreach (var id in rewrite.Left) overlay.Remove(id);
        foreach (var slot in inserted) overlay[slot.Chunk[slot.Offset].Id] = slot;

        // Every origin held by a dropped chunk forwards its kept runs to the newest chunks.
        foreach (var origin in affected)
        {
            var next = new List<Run>();
            if (forward.TryGetValue(origin, out var previous))
                foreach (var run in previous) if (!dropped.Contains(run.Chunk)) next.Add(run);
            if (runs.TryGetValue(origin, out var added)) next.AddRange(added);
            if (next.Count == 0) forward.Remove(origin);
            else
            {
                next.Sort(static (left, right) => left.OriginStart.CompareTo(right.OriginStart));
                forward[origin] = [.. next];
            }
        }

        foreach (var chunk in rewrite.Dropped) holdings.Remove(chunk);
        return new IdentityTable(slots.Root, overlay.ToImmutable(), forward.ToImmutable(), holdings.ToImmutable());
    }

    internal SceneObjectList RemoveIds(IReadOnlySet<ObjectId> ids) =>
        RemovePositions([.. ids.Select(IndexOfId).Where(i => i >= 0).Distinct().Order()]);

    /// <summary>
    /// Removes the objects at the given ascending, distinct positions. Untouched chunks are shared;
    /// only the chunks holding a removed object are rebuilt, so the cost follows the removal.
    /// </summary>
    internal SceneObjectList RemovePositions(IReadOnlyList<int> ascending) =>
        SplicePositions(ascending, ImmutableDictionary<int, IReadOnlyList<SceneObject>>.Empty);

    public SceneObject this[int index] => _chunks.ObjectAt(index);

    public SceneObjectList ReplaceAt(int index, SceneObject replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var (chunkIndex, offset) = Locate(index);
        if (ReferenceEquals(_chunks[chunkIndex][offset], replacement))
        {
            return this;
        }

        return ReplaceMany(new Dictionary<int, SceneObject> { [index] = replacement });
    }

    /// <summary>Copies every touched chunk once even when a group edit replaces many members.</summary>
    public SceneObjectList ReplaceMany(IReadOnlyDictionary<int, SceneObject> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            return this;
        }

        var copied = new Dictionary<int, SceneObject[]>();
        var reidentified = new Dictionary<int, List<int>>();
        var rewrite = new Rewrite();
        foreach (var (index, replacement) in replacements)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            var (chunkIndex, offset) = Locate(index);
            var original = _chunks[chunkIndex];
            if (!copied.TryGetValue(chunkIndex, out var copy))
            {
                copy = (SceneObject[])original.Clone();
                copied.Add(chunkIndex, copy);
                rewrite.Dropped.Add(original);
            }

            // A kind change also enters/leaves the text-only identity root. Give it its
            // own origin slot so later chunk rewrites forward it just like a new identity.
            if (original[offset].Id != replacement.Id || original[offset].Kind != replacement.Kind)
            {
                if (!reidentified.TryGetValue(chunkIndex, out var offsets)) reidentified.Add(chunkIndex, offsets = []);
                offsets.Add(offset);
                rewrite.Left.Add(original[offset].Id);
            }

            copy[offset] = replacement;
        }

        foreach (var (chunkIndex, copy) in copied)
        {
            var original = _chunks[chunkIndex];
            if (!reidentified.TryGetValue(chunkIndex, out var offsets))
            {
                rewrite.Created.Add((copy, [new Segment(original, 0, original.Length, 0)]));
                continue;
            }

            offsets.Sort();
            var segments = new List<Segment>();
            var from = 0;
            foreach (var offset in offsets)
            {
                if (offset > from) segments.Add(new Segment(original, from, offset - from, from));
                segments.Add(new Segment(null, 0, 1, offset));
                from = offset + 1;
            }

            if (from < original.Length) segments.Add(new Segment(original, from, original.Length - from, from));
            rewrite.Created.Add((copy, [.. segments]));
        }

        return new SceneObjectList(_chunks.Replace(copied), this, rewrite);
    }

    public SceneObjectList InsertRange(int index, IReadOnlyList<SceneObject> inserted) =>
        Splice(index, removeCount: 0, inserted);

    /// <summary>
    /// One removal/insertion transaction. Keys refer to the reduced list, before any insertions.
    /// Only affected chunks and their small neighbours enter the edit buffer; all other directory
    /// subtrees and identity runs stay shared with the source snapshot.
    /// </summary>
    internal SceneObjectList SplicePositions(IReadOnlyList<int> removed,
        IReadOnlyDictionary<int, IReadOnlyList<SceneObject>> insertions)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(insertions);
        for (var i = 0; i < removed.Count; i++)
        {
            if ((uint)removed[i] >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(removed));
            if (i > 0 && removed[i] <= removed[i - 1])
                throw new ArgumentException("Positions must ascend.", nameof(removed));
        }
        var ordered = insertions.OrderBy(p => p.Key).ToArray();
        var sourceKeys = new int[ordered.Length];
        var passed = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var key = ordered[i].Key;
            if (key < 0 || key > Count - removed.Count) throw new ArgumentOutOfRangeException(nameof(insertions));
            ArgumentNullException.ThrowIfNull(ordered[i].Value);
            while (passed < removed.Count && removed[passed] - passed <= key) passed++;
            sourceKeys[i] = key + passed;
        }
        if (removed.Count == 0 && ordered.All(p => p.Value.Count == 0)) return this;
        if (Count == 0) return From(ordered.SelectMany(p => p.Value).ToArray());

        var touched = new SortedSet<int>();
        for (var i = 0; i < removed.Count;)
        {
            var (chunk, _) = Locate(removed[i]);
            touched.Add(chunk);
            var end = _chunks.Start(chunk) + _chunks[chunk].Length;
            do { i++; } while (i < removed.Count && removed[i] < end);
        }
        for (var i = 0; i < ordered.Length; i++)
            if (ordered[i].Value.Count > 0) touched.Add(LocateBoundary(sourceKeys[i]).Chunk);

        // At most one small neighbour on each side is needed. Buffers are evenly split,
        // so no edit creates a pair of adjacent chunks smaller than half a full chunk.
        var expanded = new SortedSet<int>(touched);
        foreach (var chunk in touched)
        {
            if (chunk > 0 && _chunks[chunk - 1].Length < ChunkSize / 2) expanded.Add(chunk - 1);
            if (chunk + 1 < _chunks.Count && _chunks[chunk + 1].Length < ChunkSize / 2) expanded.Add(chunk + 1);
        }
        var groups = new List<(int Start, int End)>();
        foreach (var chunk in expanded)
        {
            if (groups.Count > 0 && groups[^1].End + 1 == chunk)
                groups[^1] = (groups[^1].Start, chunk);
            else groups.Add((chunk, chunk));
        }

        var directory = _chunks.Edit();
        var rewrite = new Rewrite();
        var nextRemoval = 0;
        var nextInsertion = 0;
        var delta = 0;
        foreach (var (first, last) in groups)
        {
            var buffer = new List<SceneObject>();
            var segments = new List<Segment>();
            var start = _chunks.Start(first);
            foreach (var chunk in _chunks.Range(first, last - first + 1))
            {
                var end = start + chunk.Length;
                var offset = 0;
                rewrite.Dropped.Add(chunk);
                while (offset < chunk.Length)
                {
                    // Empty batches still consume their original key, including in untouched gaps.
                    while (nextInsertion < ordered.Length && ordered[nextInsertion].Value.Count == 0) nextInsertion++;
                    var removal = nextRemoval < removed.Count ? removed[nextRemoval] : Count;
                    var insertion = nextInsertion < ordered.Length ? sourceKeys[nextInsertion] : Count;
                    var stop = Math.Min(end, Math.Min(removal, insertion));
                    var kept = stop - start - offset;
                    if (kept > 0)
                    {
                        segments.Add(new Segment(chunk, offset, kept, buffer.Count));
                        for (var i = 0; i < kept; i++) buffer.Add(chunk[offset++]);
                    }
                    if (stop == end) break;
                    if (stop == insertion)
                    {
                        var batch = ordered[nextInsertion++].Value;
                        segments.Add(new Segment(null, 0, batch.Count, buffer.Count));
                        buffer.AddRange(batch);
                    }
                    if (stop == removal)
                    {
                        rewrite.Left.Add(chunk[offset++].Id);
                        nextRemoval++;
                    }
                }
                start = end;
            }
            if (last == _chunks.Count - 1)
                while (nextInsertion < ordered.Length)
                {
                    var tail = ordered[nextInsertion++].Value;
                    if (tail.Count > 0) segments.Add(new Segment(null, 0, tail.Count, buffer.Count));
                    buffer.AddRange(tail);
                }
            var chunks = new List<SceneObject[]>();
            AddChunks(chunks, rewrite, buffer, segments);
            directory.Replace(first + delta, last - first + 1, chunks);
            delta += chunks.Count - (last - first + 1);
        }
        var result = directory.Finish();
        return result.ObjectCount == 0 ? Empty : new SceneObjectList(result, this, rewrite);
    }

    /// <summary>Batch insertions at original indices; only intersected chunks are rebuilt.</summary>
    internal SceneObjectList InsertMany(IReadOnlyDictionary<int, IReadOnlyList<SceneObject>> insertions) =>
        SplicePositions(Array.Empty<int>(), insertions);

    /// <summary>Splits an edit buffer into chunks, clipping its runs to each chunk.</summary>
    private static void AddChunks(List<SceneObject[]> chunks, Rewrite rewrite,
        IReadOnlyList<SceneObject> buffer, IReadOnlyList<Segment> segments)
    {
        var next = 0;
        var parts = new List<Segment>();
        var chunkCount = (buffer.Count + ChunkSize - 1) / ChunkSize;
        var start = 0;
        for (var c = 0; c < chunkCount; c++)
        {
            var part = new SceneObject[buffer.Count / chunkCount + (c < buffer.Count % chunkCount ? 1 : 0)];
            for (var i = 0; i < part.Length; i++) part[i] = buffer[start + i];
            var end = start + part.Length;
            parts.Clear();
            while (next < segments.Count && segments[next].Offset < end)
            {
                var segment = segments[next];
                var from = Math.Max(segment.Offset, start);
                var to = Math.Min(segment.Offset + segment.Length, end);
                if (from < to)
                    parts.Add(new Segment(segment.Source, segment.SourceStart + from - segment.Offset, to - from, from - start));
                if (segment.Offset + segment.Length > end) break;
                next++;
            }

            chunks.Add(part);
            rewrite.Created.Add((part, [.. parts]));
            start = end;
        }
    }

    /// <summary>
    /// Appends a placement/paste batch without flattening the already stored prefix. Only the
    /// last boundary chunk, the new chunks and their directory paths are copied.
    /// </summary>
    public SceneObjectList AppendRange(IReadOnlyList<SceneObject> appended) =>
        Splice(Count, removeCount: 0, appended);

    public SceneObjectList RemoveRange(int index, int count) =>
        Splice(index, count, Array.Empty<SceneObject>());

    public SceneObjectList ReplaceRange(
        int index, int removeCount, IReadOnlyList<SceneObject> replacements) =>
        Splice(index, removeCount, replacements);

    private SceneObjectList Splice(
        int index, int removeCount, IReadOnlyList<SceneObject> inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        if (index < 0 || index > Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (removeCount < 0 || removeCount > Count - index) throw new ArgumentOutOfRangeException(nameof(removeCount));
        if (removeCount == 0 && inserted.Count == 0) return this;
        if (Count == 0) return From(inserted);
        return SplicePositions(Enumerable.Range(index, removeCount).ToArray(),
            new Dictionary<int, IReadOnlyList<SceneObject>> { [index] = inserted });
    }

    private (int Chunk, int Offset) Locate(int index) => _chunks.Locate(index);

    private (int Chunk, int Offset) LocateBoundary(int index)
    {
        if (index == Count)
        {
            return (_chunks.Count - 1, _chunks[^1].Length);
        }

        return Locate(index);
    }

    /// <summary>
    /// Objects reached by enumerating any list on the calling thread (counted per chunk entered).
    /// Diagnostics and tests only: an edit that walks the whole layer shows up here.
    /// </summary>
    internal static long EnumeratedOnThisThread => _enumerated;
    [ThreadStatic] private static long _enumerated;

    public IEnumerator<SceneObject> GetEnumerator()
    {
        foreach (var chunk in _chunks)
        {
            _enumerated += chunk.Length;
            for (var index = 0; index < chunk.Length; index++)
            {
                yield return chunk[index];
            }
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
