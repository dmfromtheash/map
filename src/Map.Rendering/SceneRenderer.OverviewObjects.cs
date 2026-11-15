using System.Collections;
using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Rendering;

public static partial class SceneRenderer
{
    /// <summary>
    /// Persistent draw-order sequence. Each leaf owns at most 256 entries; an edit copies
    /// bounded leaves and AVL paths, never the population of a dense world chunk.
    /// No predecessor chain or tombstones: removed objects leave the current root immediately.
    /// </summary>
    internal sealed class OverviewObjects : IEnumerable<(long Order, SceneObject Item)>
    {
        private const int LeafSize = 256;
        private sealed class Node
        {
            internal readonly ImmutableArray<(long Order, SceneObject Item)> Items;
            internal readonly int Offset, Count, Height;
            internal readonly Node? Left, Right;
            internal bool Leaf => Left is null;
            internal (long Order, SceneObject Item) Last => Leaf ? Items[Offset + Count - 1] : Right!.Last;
            internal Node(ImmutableArray<(long, SceneObject)> items, int offset, int count)
            { Items = items; Offset = offset; Count = count; Height = 1; }
            internal Node(Node left, Node right)
            { Left = left; Right = right; Count = left.Count + right.Count; Height = 1 + Math.Max(left.Height, right.Height); }
        }

        private readonly Node? _root;
        public int Length => _root?.Count ?? 0;
        public (long Order, SceneObject Item) this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
                var node = _root!;
                while (!node.Leaf)
                {
                    if (index < node.Left!.Count) node = node.Left;
                    else { index -= node.Left.Count; node = node.Right!; }
                }
                return node.Items[node.Offset + index];
            }
        }
        public OverviewObjects(ImmutableArray<(long Order, SceneObject Item)> items)
            : this(FromPages(CopyPages(items))) { }
        private OverviewObjects(Node? root) => _root = root;

        // Cold plans count each sublayer first, then fill these final pages in document order.
        // No temporary full-chunk array or per-object tree nodes are needed.
        internal sealed class Builder
        {
            internal readonly (long Order, SceneObject Item)[][] Pages;
            private int _next;
            internal Builder(int count)
            {
                Pages = new (long, SceneObject)[(count + LeafSize - 1) / LeafSize][];
                for (var i = 0; i < Pages.Length; i++) Pages[i] = new (long, SceneObject)[Math.Min(LeafSize, count - i * LeafSize)];
            }
            internal void Add(long order, SceneObject item)
            { Pages[_next / LeafSize][_next % LeafSize] = (order, item); _next++; }
        }

        internal static OverviewObjects FromBuilders(IEnumerable<Builder> builders) =>
            new(FromPages(builders.SelectMany(b => b.Pages)));

        private static IEnumerable<(long Order, SceneObject Item)[]> CopyPages(ImmutableArray<(long Order, SceneObject Item)> items)
        {
            for (var offset = 0; offset < items.Length; offset += LeafSize)
                yield return items.AsSpan(offset, Math.Min(LeafSize, items.Length - offset)).ToArray();
        }

        private static Node? FromPages(IEnumerable<(long Order, SceneObject Item)[]> pages)
        {
            var leaves = pages.Select(page => new Node(
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(page), 0, page.Length)).ToArray();
            return Join(0, leaves.Length);
            Node? Join(int offset, int count)
            {
                if (count == 0) return null;
                if (count == 1) return leaves[offset];
                var half = count / 2;
                return new(Join(offset, half)!, Join(offset + half, count - half)!);
            }
        }

        private static Node? Build(ImmutableArray<(long Order, SceneObject Item)> items, int offset, int count)
        {
            if (count == 0) return null;
            if (count <= LeafSize) return new(items, offset, count);
            var half = count / 2;
            return new(Build(items, offset, half)!, Build(items, offset + half, count - half)!);
        }

        public OverviewObjects Change((long Order, SceneObject Item) item, SceneObjectList order, bool add) =>
            new(Change(_root, item, order, add));

        private static int Compare(SceneObject a, SceneObject b, SceneObjectList order)
        {
            var band = a.Sublayer.CompareTo(b.Sublayer);
            return band != 0 ? band : a.Id == b.Id ? 0 : order.IndexOfId(a.Id).CompareTo(order.IndexOfId(b.Id));
        }

        private static Node? Change(Node? node, (long Order, SceneObject Item) item, SceneObjectList order, bool add)
        {
            if (node is null) return add ? new([item], 0, 1) : null;
            if (!node.Leaf)
            {
                var left = node.Left!; var right = node.Right!;
                if (Compare(item.Item, left.Last.Item, order) <= 0) left = Change(left, item, order, add);
                else right = Change(right, item, order, add);
                return Balance(left, right);
            }
            var lo = 0; var hi = node.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Compare(node.Items[node.Offset + mid].Item, item.Item, order) < 0) lo = mid + 1;
                else hi = mid;
            }
            var found = lo < node.Count && node.Items[node.Offset + lo].Item.Id == item.Item.Id;
            if (!add && !found) return node;
            var count = node.Count + (add ? found ? 0 : 1 : -1);
            if (count == 0) return null;
            var values = new (long Order, SceneObject Item)[count];
            var target = 0;
            for (var i = 0; i < node.Count; i++)
            {
                if (i == lo)
                {
                    if (add) values[target++] = item;
                    if (found) continue;
                }
                values[target++] = node.Items[node.Offset + i];
            }
            if (add && lo == node.Count) values[target] = item;
            return Build(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(values), 0, count);
        }

        private static Node? Balance(Node? left, Node? right)
        {
            if (left is null) return right;
            if (right is null) return left;
            if (left.Leaf && right.Leaf && left.Count + right.Count <= LeafSize)
            {
                var values = new (long Order, SceneObject Item)[left.Count + right.Count];
                left.Items.AsSpan(left.Offset, left.Count).CopyTo(values);
                right.Items.AsSpan(right.Offset, right.Count).CopyTo(values.AsSpan(left.Count));
                return new(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(values), 0, values.Length);
            }
            if (left.Height > right.Height + 1)
            {
                var a = left.Left!; var b = left.Right!;
                return a.Height >= b.Height ? new(a, new Node(b, right))
                    : new(new Node(a, b.Left!), new Node(b.Right!, right));
            }
            if (right.Height > left.Height + 1)
            {
                var a = right.Left!; var b = right.Right!;
                return b.Height >= a.Height ? new(new Node(left, a), b)
                    : new(new Node(left, a.Left!), new Node(a.Right!, b));
            }
            return new(left, right);
        }

        public IEnumerator<(long Order, SceneObject Item)> GetEnumerator() => Enumerate(_root).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private static IEnumerable<(long Order, SceneObject Item)> Enumerate(Node? node)
        {
            if (node is null) yield break;
            // Explicit stack avoids one iterator allocation per directory node.
            var pending = new Stack<Node>();
            while (true)
            {
                while (!node.Leaf) { pending.Push(node.Right!); node = node.Left!; }
                for (var i = 0; i < node.Count; i++) yield return node.Items[node.Offset + i];
                if (!pending.TryPop(out node)) yield break;
            }
        }
    }
}
