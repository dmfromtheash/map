using System.Collections;
using System.Collections.Immutable;

namespace Map.Rendering;

/// <summary>
/// A frame-local sparse substitution over document content. Construction visits only overrides;
/// indexing and enumeration retain exact list order and the unchanged geometry-bearing records.
/// Unlike a cached resolved array it cannot return a previous frame's editor draft.
/// </summary>
internal sealed class ResolvedFeatures<T> : IReadOnlyList<T> where T : class
{
    private readonly ImmutableArray<T> _source;
    private readonly Dictionary<Guid, T> _overrides;
    private readonly List<T> _extra;
    private readonly Func<T, Guid> _id;

    internal ResolvedFeatures(ImmutableArray<T> source, IReadOnlyList<T> overrides,
        bool append, Func<Guid, bool> contains, Func<T, Guid> id)
    {
        _source = source;
        _id = id;
        _overrides = new(overrides.Count);
        _extra = [];
        foreach (var item in overrides)
        {
            // Existing ids use the first override; new ids append every draft in input order,
            // including duplicates. These are the historical Resolve semantics.
            _overrides.TryAdd(id(item), item);
            if (append && !contains(id(item))) _extra.Add(item);
        }
    }

    public int Count => _source.Length + _extra.Count;
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index >= _source.Length) return _extra[index - _source.Length];
            var item = _source[index];
            return _overrides.GetValueOrDefault(_id(item), item);
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
