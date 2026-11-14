using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Map.Core.Documents;

/// <summary>
/// Derived name occupancy. The root is one plain hash table of the loaded snapshot, built once
/// and never mutated; each edit derives a persistent overlay of the changed names only (a count
/// of zero marks a name that left), so undo snapshots share the root and per-edit work is
/// proportional to the changed objects.
/// </summary>
internal sealed class ObjectNameIndex
{
    private static readonly ConditionalWeakTable<MapDocument, ObjectNameIndex> Cache = [];
    private readonly Dictionary<string, int> _root;
    private readonly ImmutableDictionary<string, int> _overlay;
    private readonly ConcurrentDictionary<string, int> _firstFree;
    // First free number of each numbered family in the root snapshot. Shared, never copied:
    // the cursors above hold only the families this session allocated from or removed from.
    private readonly Dictionary<string, int> _rootHoles;
    // Numbers below a family's cursor that removals freed (and nothing re-took). The cursor keeps
    // its place, so Undo of a delete takes the number back without rescanning the family.
    private readonly ImmutableDictionary<string, ImmutableSortedSet<int>> _removedHoles;
    private static readonly ImmutableDictionary<string, ImmutableSortedSet<int>> NoHoles =
        ImmutableDictionary.Create<string, ImmutableSortedSet<int>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of full name tables built (every object visited). Diagnostics and tests only.</summary>
    internal static long RootBuilds => Interlocked.Read(ref _rootBuilds);
    private static long _rootBuilds;

    private ObjectNameIndex(Dictionary<string, int> root, ImmutableDictionary<string, int> overlay,
        Dictionary<string, int> rootHoles, IEnumerable<KeyValuePair<string, int>>? cursors = null,
        ImmutableDictionary<string, ImmutableSortedSet<int>>? removedHoles = null)
    {
        _root = root;
        _overlay = overlay;
        _rootHoles = rootHoles;
        _firstFree = new(cursors ?? [], StringComparer.OrdinalIgnoreCase);
        _removedHoles = removedHoles ?? NoHoles;
    }

    // Every number below the root hole was occupied in the root; a later removal below it
    // records its own cursor, so starting the search here never skips a free number.
    private int RootHole(string basis) => _rootHoles.GetValueOrDefault(basis, 1);

    public static ObjectNameIndex For(MapDocument document) => Cache.GetValue(document, static value =>
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visited = 0;
        foreach (var layer in value.Layers)
            if (layer is ObjectLayer objects)
                foreach (var item in objects.Objects)
                {
                    // A background build pauses here while the user pans or zooms (no-op elsewhere).
                    if ((++visited & 0xFFFF) == 0) Resources.BackgroundWork.YieldToInteraction();
                    ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, item.Name, out _);
                    count++;
                }
        Interlocked.Increment(ref _rootBuilds);
        return new ObjectNameIndex(counts, ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(FirstHoles(counts.Keys), StringComparer.OrdinalIgnoreCase));
    });

    /// <summary>
    /// The first free number of every «basis N» family, found in the same pass as the counts.
    /// A map placed from one set holds «Гора 1» … «Гора 999999»; without this the first
    /// placement of «Гора» in a session tried every occupied number in turn. The first hole of
    /// a family of k names is at most k + 1, so larger numbers are not tracked.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, int>> FirstHoles(IReadOnlyCollection<string> names)
    {
        // Two passes over the names, looked up by span: the family size first, then the marks in
        // an array of exactly that size. A substring and a growing list per numbered name were
        // most of what this build allocated on a million «Гора N» objects.
        var families = new Dictionary<string, (int Count, bool[]? Seen)>(StringComparer.OrdinalIgnoreCase);
        var lookup = families.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var name in names)
        {
            if (!Suffix(name, out var space, out _)) continue;
            ref var family = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, name.AsSpan(0, space), out _);
            family.Count++;
        }
        foreach (var name in names)
        {
            if (!Suffix(name, out var space, out var suffix)) continue;
            ref var family = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, name.AsSpan(0, space), out _);
            family.Seen ??= new bool[family.Count + 2];
            if (suffix < family.Seen.Length) family.Seen[suffix] = true;
        }
        foreach (var (basis, family) in families)
        {
            var seen = family.Seen!;
            var hole = 1;
            while (seen[hole]) hole++;
            yield return new(basis, hole);
        }

        static bool Suffix(string name, out int space, out int suffix)
        {
            space = name.LastIndexOf(' ');
            suffix = 0;
            return space > 0 && int.TryParse(name.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out suffix)
                && suffix > 0 && name.Length - space - 1 == CountDigits(suffix); // «Гора 01» does not occupy «Гора 1»
        }
    }

    private static int CountDigits(int value)
    {
        var digits = 1;
        while (value >= 10) { value /= 10; digits++; }
        return digits;
    }

    internal static bool IsPrepared(MapDocument document) => Cache.TryGetValue(document, out _);

    private int CountOf(string name) =>
        _overlay.TryGetValue(name, out var changed) ? changed : _root.GetValueOrDefault(name);

    private bool Occupied(string name)
    {
        _probes++;
        return CountOf(name) > 0;
    }

    /// <summary>Candidate names tried by allocation and cursor advance on this thread. Diagnostics and tests only.</summary>
    internal static long ProbesOnThisThread => _probes;
    [ThreadStatic] private static long _probes;

    public IReadOnlyList<string> Allocate(string basis, int count, bool suffixFirst)
    {
        var result = new string[count];
        var offset = 0;
        if (count > 0 && !suffixFirst && !Occupied(basis)) result[offset++] = basis;
        // Freed numbers first: every number below the cursor that is not one of them is taken.
        if (_removedHoles.TryGetValue(basis, out var holes))
            foreach (var hole in holes)
            {
                if (offset == count) break;
                var candidate = basis + " " + hole.ToString(CultureInfo.InvariantCulture);
                if (!Occupied(candidate)) result[offset++] = candidate;
            }
        var cursor = _firstFree.GetOrAdd(basis, static (b, self) => self.RootHole(b), this);
        while (offset < count)
        {
            string candidate;
            do
            {
                if (cursor == int.MaxValue) throw new InvalidOperationException("Не удалось подобрать свободное имя.");
                candidate = basis + " " + cursor++.ToString(CultureInfo.InvariantCulture);
            } while (Occupied(candidate));
            // Memoize only occupancy of this snapshot, never reserve an uncommitted name.
            if (offset == (suffixFirst || Occupied(basis) ? 0 : 1)) _firstFree[basis] = cursor - 1;
            result[offset++] = candidate;
        }
        return result;
    }

    internal static void Share(MapDocument source, MapDocument target)
    {
        if (Cache.TryGetValue(source, out var value)) Cache.Add(target, value);
    }

    internal static void Register(MapDocument source, MapDocument target,
        IEnumerable<SceneObject> removed, IEnumerable<SceneObject> added)
    {
        if (!Cache.TryGetValue(source, out var value)) return;
        var overlay = value._overlay.ToBuilder();
        var cursors = new Dictionary<string, int>(value._firstFree, StringComparer.OrdinalIgnoreCase);
        var holes = value._removedHoles.ToBuilder();
        int Current(string name) => overlay.TryGetValue(name, out var changed) ? changed : value._root.GetValueOrDefault(name);
        void Set(string name, int count)
        {
            // Back to the root's own value: the overlay entry is no longer needed.
            if (count == value._root.GetValueOrDefault(name)) overlay.Remove(name); else overlay[name] = count;
        }
        foreach (var item in removed)
        {
            Set(item.Name, Math.Max(0, Current(item.Name) - 1));
            if (Current(item.Name) == 0 && Numbered(item.Name, out var basis, out var suffix))
            {
                // A family this session never touched starts at its root hole, so the hole a
                // removal opens below it must be recorded here or the search would skip it.
                var cursor = cursors.TryGetValue(basis, out var known) ? known : value.RootHole(basis);
                if (suffix < cursor) holes[basis] = holes.GetValueOrDefault(basis, []).Add(suffix);
            }
        }
        foreach (var item in added)
        {
            Set(item.Name, Current(item.Name) + 1);
            if (Numbered(item.Name, out var basis, out var suffix) && holes.TryGetValue(basis, out var free) && free.Contains(suffix))
            {
                free = free.Remove(suffix);
                if (free.IsEmpty) holes.Remove(basis); else holes[basis] = free;
            }
        }
        // The child owns committed occupancy, so advance past the newly filled range once.
        // The source cursor remains at its old first hole for cancellation and undo snapshots.
        foreach (var basis in cursors.Keys.ToArray())
        {
            var cursor = cursors[basis];
            while (cursor < int.MaxValue && Current(basis + " " + cursor.ToString(CultureInfo.InvariantCulture)) > 0) { cursor++; _probes++; }
            cursors[basis] = cursor;
        }
        Cache.Add(target, new ObjectNameIndex(value._root, overlay.ToImmutable(), value._rootHoles, cursors, holes.ToImmutable()));
    }

    /// <summary>«basis N» with N a positive number written without leading zeros.</summary>
    private static bool Numbered(string name, out string basis, out int suffix)
    {
        basis = "";
        var space = name.LastIndexOf(' ');
        if (space <= 0 || !int.TryParse(name.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out suffix)
            || suffix <= 0 || name.Length - space - 1 != CountDigits(suffix)) // «Гора 01» is not number 1
        {
            suffix = 0;
            return false;
        }

        basis = name[..space];
        return true;
    }
}
