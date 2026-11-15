using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    /// <summary>
    /// Keeps the virtualized list and its realized controls when placement splices a picture's
    /// stack. The backing rows remain immutable document views; only changed rows are announced.
    /// </summary>
    private sealed class LiveObjectRows(MapDocument document, IReadOnlyList<ObjectTreeRow> rows)
        : IReadOnlyList<ObjectTreeRow>, IList, INotifyCollectionChanged
    {
        private MapDocument _document = document;
        private IReadOnlyList<ObjectTreeRow> _rows = rows;
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public int Count => _rows.Count;
        public ObjectTreeRow this[int index] => _rows[index];

        public bool TryUpdate(MapDocument next, IReadOnlyList<ObjectTreeRow> rows)
        {
            if (Count == 0 || rows.Count == 0
                || this[0] is not { Kind: ObjectTreeRowKind.AssetKindBreadcrumb, AssetKey: { } key }
                || rows[0].Kind != ObjectTreeRowKind.AssetKindBreadcrumb || rows[0].AssetKey != key
                || !ObjectTreeRows.TryAssetKindPrefixChange(_document, next, key, out var index, out var removed, out var added)
                || Count - removed + added != rows.Count) return false;
            var oldHeader = this[0];
            var oldItems = Enumerable.Range(index, removed).Select(i => this[i]).ToArray();
            var newItems = Enumerable.Range(index, added).Select(i => rows[i]).ToArray();
            _document = next;
            _rows = rows;
            if (removed > 0)
                CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Remove, oldItems, index));
            if (added > 0)
                CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Add, newItems, index));
            CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Replace, rows[0], oldHeader, 0));
            return true;
        }

        public IEnumerator<ObjectTreeRow> GetEnumerator() => _rows.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        object? IList.this[int index] { get => this[index]; set => throw ReadOnly(); }
        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => false;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;
        int IList.Add(object? value) => throw ReadOnly();
        void IList.Clear() => throw ReadOnly();
        void IList.Insert(int index, object? value) => throw ReadOnly();
        void IList.Remove(object? value) => throw ReadOnly();
        void IList.RemoveAt(int index) => throw ReadOnly();
        bool IList.Contains(object? value) => IndexOf(value) >= 0;
        int IList.IndexOf(object? value) => IndexOf(value);
        private int IndexOf(object? value) => ((IList)_rows).IndexOf(value);
        void ICollection.CopyTo(Array array, int index)
        {
            for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
        }
        private static NotSupportedException ReadOnly() => new("The object list is read-only.");
    }
}
