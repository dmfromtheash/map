using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>Derived name occupancy; persistent deltas share the unchanged tree across undo snapshots.</summary>
internal sealed class ObjectNameIndex
{
    private static readonly ConditionalWeakTable<MapDocument, ObjectNameIndex> Cache = [];
    private readonly ImmutableDictionary<string, int> _counts;
    private readonly ConcurrentDictionary<string, int> _firstFree;

    private ObjectNameIndex(ImmutableDictionary<string, int> counts,
        IEnumerable<KeyValuePair<string, int>>? cursors = null)
    {
        _counts = counts;
        _firstFree = new(cursors ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public static ObjectNameIndex For(MapDocument document) => Cache.GetValue(document, static value =>
    {
        var counts = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects))
            counts[item.Name] = counts.GetValueOrDefault(item.Name) + 1;
        return new ObjectNameIndex(counts.ToImmutable());
    });

    public IReadOnlyList<string> Allocate(string basis, int count, bool suffixFirst)
    {
        var result = new string[count];
        var offset = 0;
        if (count > 0 && !suffixFirst && !_counts.ContainsKey(basis)) result[offset++] = basis;
        var cursor = _firstFree.GetOrAdd(basis, 1);
        while (offset < count)
        {
            string candidate;
            do
            {
                if (cursor == int.MaxValue) throw new InvalidOperationException("Не удалось подобрать свободное имя.");
                candidate = basis + " " + cursor++.ToString(CultureInfo.InvariantCulture);
            } while (_counts.ContainsKey(candidate));
            // Memoize only occupancy of this snapshot, never reserve an uncommitted name.
            if (offset == (suffixFirst || _counts.ContainsKey(basis) ? 0 : 1)) _firstFree[basis] = cursor - 1;
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
        var counts = value._counts.ToBuilder();
        var cursors = new Dictionary<string, int>(value._firstFree, StringComparer.OrdinalIgnoreCase);
        foreach (var item in removed)
        {
            var count = counts.GetValueOrDefault(item.Name);
            if (count <= 1) counts.Remove(item.Name); else counts[item.Name] = count - 1;
            var space = item.Name.LastIndexOf(' ');
            if (space > 0 && int.TryParse(item.Name.AsSpan(space + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var suffix) && suffix > 0)
            {
                var basis = item.Name[..space];
                if (cursors.TryGetValue(basis, out var cursor) && suffix < cursor) cursors[basis] = suffix;
            }
        }
        foreach (var item in added) counts[item.Name] = counts.GetValueOrDefault(item.Name) + 1;
        // The child owns committed occupancy, so advance past the newly filled range once.
        // The source cursor remains at its old first hole for cancellation and undo snapshots.
        foreach (var basis in cursors.Keys.ToArray())
        {
            var cursor = cursors[basis];
            while (cursor < int.MaxValue && counts.ContainsKey(basis + " " + cursor.ToString(CultureInfo.InvariantCulture))) cursor++;
            cursors[basis] = cursor;
        }
        Cache.Add(target, new ObjectNameIndex(counts.ToImmutable(), cursors));
    }
}
