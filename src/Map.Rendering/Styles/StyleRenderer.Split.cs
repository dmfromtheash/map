using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Rendering.Styles;

public static partial class StyleRenderer
{
    /// <summary>
    /// The texts of one object list: their indices in it (ascending), the list without them (the very same list when
    /// it has none) and the texts alone. A list made by an edit is derived from the split of the list before it through
    /// the edit's delta (<see cref="ObjectLayerChanges"/>), so a million-object layer is not filtered again per edit.
    /// </summary>
    private sealed class TextSplit(int[] texts, IReadOnlyList<SceneObject> bare, IReadOnlyList<SceneObject> names)
    {
        public int[] Texts { get; } = texts;
        public IReadOnlyList<SceneObject> Bare { get; } = bare;
        public IReadOnlyList<SceneObject> Names { get; } = names;
        /// <summary>The bare list this one was derived from and the change between them, in bare indices.</summary>
        public IReadOnlyList<SceneObject>? BasisBare { get; init; }
        public int[] BareRemoved { get; init; } = [];
        public int[] BareAdded { get; init; } = [];
        public bool BareCompacted { get; init; }
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<SceneObject>, TextSplit> TextSplits = [];

    /// <summary>A value kept per object list, derived from the value of the list before an edit when the edit is known.</summary>
    private static T PerObjects<T>(ConditionalWeakTable<IReadOnlyList<SceneObject>, T> table, ObjectLayer layer,
        Func<IReadOnlyList<SceneObject>, T> scan, Func<T, ObjectLayerChange, IReadOnlyList<SceneObject>, T?> apply, int depth = 0)
        where T : class
    {
        if (table.TryGetValue(layer.Objects, out var known)) return known;
        T? value = null;
        // The same bound as the edit chain itself keeps its sources alive (ObjectLayerChanges).
        if (depth < 64 && ObjectLayerChanges.TryGet(layer, out var delta)
            && delta.Source.Objects.Count - delta.Removed.Count + delta.Added.Count == layer.Objects.Count
            && (long)delta.Removed.Count + delta.Added.Count <= Math.Max(256, layer.Objects.Count / 8))
            value = apply(PerObjects(table, delta.Source, scan, apply, depth + 1), delta, layer.Objects);
        value ??= scan(layer.Objects);
        return table.GetValue(layer.Objects, _ => value);
    }

    private static TextSplit SplitOf(ObjectLayer layer) => PerObjects(TextSplits, layer, ScanTexts, DeriveTexts);

    private static TextSplit ScanTexts(IReadOnlyList<SceneObject> objects)
    {
        var texts = new List<int>();
        var index = 0;
        foreach (var item in objects)
        {
            if (item.Kind == SceneObjectKind.Text) texts.Add(index);
            index++;
        }
        if (texts.Count == 0) return new TextSplit([], objects, SceneObjectList.Empty);
        var bare = new SceneObject[objects.Count - texts.Count];
        var names = new SceneObject[texts.Count];
        int b = 0, n = 0;
        foreach (var item in objects)
            if (item.Kind == SceneObjectKind.Text) names[n++] = item; else bare[b++] = item;
        return new TextSplit([.. texts], SceneObjectList.From(bare), SceneObjectList.From(names));
    }

    /// <summary>
    /// The split after an edit from the split before it. The new list is the old one without the removed indices, with
    /// the added ones (indices of the new list) put in; the bare list takes the same change in its own indices.
    /// Null when the change does not fit (the caller scans the list).
    /// </summary>
    private static TextSplit? DeriveTexts(TextSplit before, ObjectLayerChange delta, IReadOnlyList<SceneObject> objects)
    {
        var removed = delta.Removed.ToArray(); Array.Sort(removed);
        var added = delta.Added.ToArray(); Array.Sort(added);
        var oldTexts = before.Texts;
        var textsChanged = false;
        var bareRemoved = new List<int>(removed.Length);
        foreach (var index in removed)
        {
            var at = Array.BinarySearch(oldTexts, index);
            if (at >= 0) textsChanged = true;
            else bareRemoved.Add(index - ~at);
        }
        // The kept texts move by the removals before them and then by the insertions up to their new place.
        var texts = new List<int>(oldTexts.Length + 4);
        int r = 0, a = 0;
        foreach (var text in oldTexts)
        {
            while (r < removed.Length && removed[r] < text) r++;
            if (r < removed.Length && removed[r] == text) continue;
            var kept = text - r;
            while (a < added.Length && added[a] <= kept + a) a++;
            texts.Add(kept + a);
        }
        var addedTexts = new List<int>();
        foreach (var index in added)
            if (objects[index].Kind == SceneObjectKind.Text) addedTexts.Add(index);
        if (addedTexts.Count > 0)
        {
            textsChanged = true;
            texts.AddRange(addedTexts);
            texts.Sort();
        }
        var newTexts = texts.ToArray();
        var bareAdded = new List<int>(added.Length);
        var bareItems = new List<SceneObject>(added.Length);
        foreach (var index in added)
        {
            var item = objects[index];
            if (item.Kind == SceneObjectKind.Text) continue;
            var at = Array.BinarySearch(newTexts, index);
            bareAdded.Add(index - ~at);
            bareItems.Add(item);
        }
        if (newTexts.Length == 0) return new TextSplit([], objects, SceneObjectList.Empty);

        var oldBare = (SceneObjectList)SceneObjectList.From(before.Bare);
        SceneObjectList bare;
        var compacted = delta.OrderCompacted;
        if (bareRemoved.SequenceEqual(bareAdded))
        {
            // Replacements in place (or none): every other bare object keeps its index.
            bare = bareAdded.Count == 0 ? oldBare
                : oldBare.ReplaceMany(bareAdded.Select((index, i) => (index, bareItems[i])).ToDictionary(p => p.index, p => p.Item2));
        }
        else
        {
            var oldCount = oldBare.Count;
            var newCount = oldCount - bareRemoved.Count + bareAdded.Count;
            var survivors = oldCount - bareRemoved.Count;
            // Only a change at the very end keeps the indices of the objects before it.
            compacted |= !(bareRemoved.Select((index, i) => index == survivors + i).All(x => x)
                && bareAdded.Select((index, i) => index == survivors + i).All(x => x));
            bare = Spliced(oldBare, bareRemoved, bareAdded, bareItems)
                ?? SceneObjectList.From([.. objects.Where(o => o.Kind != SceneObjectKind.Text)]);
            if (bare.Count != newCount) return null;
        }
        var names = !textsChanged && before.Names.Count == newTexts.Length ? before.Names
            : SceneObjectList.From([.. newTexts.Select(i => objects[i])]);
        return new TextSplit(newTexts, bare, names)
        {
            BasisBare = before.Bare, BareRemoved = [.. bareRemoved], BareAdded = [.. bareAdded], BareCompacted = compacted,
        };
    }

    /// <summary>Removes runs of indices (descending) and inserts runs (ascending); null when there are too many runs.</summary>
    private static SceneObjectList? Spliced(SceneObjectList list, List<int> removed, List<int> added, List<SceneObject> items)
    {
        var runs = 0;
        for (var end = removed.Count - 1; end >= 0;)
        {
            var start = end;
            while (start > 0 && removed[start - 1] == removed[start] - 1) start--;
            if (++runs > 64) return null;
            list = list.RemoveRange(removed[start], end - start + 1);
            end = start - 1;
        }
        for (var start = 0; start < added.Count;)
        {
            var end = start;
            while (end + 1 < added.Count && added[end + 1] == added[end] + 1) end++;
            if (++runs > 64) return null;
            list = list.InsertRange(added[start], items.GetRange(start, end - start + 1));
            start = end + 1;
        }
        return list;
    }

    /// <summary>
    /// The record of a text-carrying layer without its texts. When the layer came from an edit, its bare record is
    /// linked to the bare record of the layer before it, so the spatial index and the scene's plans of the bare layer
    /// follow the edit as those of the layer itself do (before: a fresh million-object index and plans per edit).
    /// </summary>
    private static ObjectLayer BareLayerOf(ObjectLayer layer, TextSplit split, int depth = 0)
    {
        if (BareLayers.TryGetValue(layer, out var known)) return known;
        var bare = layer with { Objects = split.Bare };
        if (depth < 64 && ObjectLayerChanges.TryGet(layer, out var delta))
        {
            var before = SplitOf(delta.Source);
            var previous = ReferenceEquals(before.Bare, delta.Source.Objects) ? delta.Source
                : BareLayerOf(delta.Source, before, depth + 1);
            if (ReferenceEquals(split.Bare, before.Bare))
                ObjectLayerChanges.RegisterDerived(previous, bare, [], []);
            else if (ReferenceEquals(split.BasisBare, before.Bare))
                ObjectLayerChanges.RegisterDerived(previous, bare, split.BareRemoved, split.BareAdded, split.BareCompacted);
        }
        return BareLayers.GetValue(layer, _ => bare);
    }
}
