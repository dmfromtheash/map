using System.Numerics;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

public sealed partial class SceneObjectList
{
    // A leaf is stable for the lifetime of its immutable chunk. A version owns only its
    // AVL root and persistent child -> parent links, never absolute chunk positions.
    // Rank lookup follows subtree counts; identity lookup walks the version's parents.
    private sealed class ChunkDirectory : IReadOnlyList<SceneObject[]>
    {
        private sealed class Node
        {
            private static long _nextId;
            public readonly ulong Id = unchecked((ulong)Interlocked.Increment(ref _nextId));
            public readonly SceneObject[]? Chunk;
            public readonly Node? Left, Right;
            public readonly int Height, Chunks, Objects, TextCount;
            public readonly int[] Labels;

            public Node(SceneObject[] chunk)
            {
                Chunk = chunk;
                Height = Chunks = 1;
                Objects = chunk.Length;
                var text = TextOf(chunk);
                TextCount = text.Offsets.Length;
                Labels = text.Counts;
            }

            public Node(Node left, Node right)
            {
                Left = left; Right = right;
                Height = 1 + Math.Max(left.Height, right.Height);
                Chunks = checked(left.Chunks + right.Chunks);
                Objects = checked(left.Objects + right.Objects);
                TextCount = checked(left.TextCount + right.TextCount);
                Labels = NoText.Counts;
                if (TextCount == 0) return;
                Labels = new int[LabelSettings.MaxImportance + 1];
                for (var i = LabelSettings.MinImportance; i <= LabelSettings.MaxImportance; i++)
                    Labels[i] = left.Labels[i] + right.Labels[i];
            }
        }

        private static readonly ConditionalWeakTable<SceneObject[], Node> Leaves = new();
        private readonly Node? _root;
        private readonly Links? _parents;
        // Text-free replacements keep the rank tree and change only a leaf value. The
        // active replacement leaf forwards to its stable rank leaf through _parents.
        private readonly Links? _values;
        public int Count => _root?.Chunks ?? 0;
        public int ObjectCount => _root?.Objects ?? 0;
        public int[] Labels => _root?.Labels ?? NoText.Counts;

        private ChunkDirectory(Node? root, Links? parents, Links? values)
        { _root = root; _parents = parents; _values = values; }

        private SceneObject[] ObjectsOf(Node leaf) => (Links.Get(_values, leaf) ?? leaf).Chunk!;

        public static ChunkDirectory From(SceneObject[][] chunks)
        {
            var builder = new Builder(new ChunkDirectory(null, null, null));
            builder.Initialize(chunks);
            return builder.Finish();
        }

        public SceneObject[] this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                var node = _root!;
                while (node.Chunk is null)
                {
                    if (index < node.Left!.Chunks) node = node.Left;
                    else { index -= node.Left.Chunks; node = node.Right!; }
                }
                return ObjectsOf(node);
            }
        }

        public (int Chunk, int Offset) Locate(int index)
        {
            if ((uint)index >= (uint)ObjectCount) throw new ArgumentOutOfRangeException(nameof(index));
            var node = _root!;
            var chunk = 0;
            while (node.Chunk is null)
            {
                if (index < node.Left!.Objects) node = node.Left;
                else { index -= node.Left.Objects; chunk += node.Left.Chunks; node = node.Right!; }
            }
            return (chunk, index);
        }

        public SceneObject ObjectAt(int index)
        {
            if ((uint)index >= (uint)ObjectCount) throw new ArgumentOutOfRangeException(nameof(index));
            var node = _root!;
            while (node.Chunk is null)
            {
                if (index < node.Left!.Objects) node = node.Left;
                else { index -= node.Left.Objects; node = node.Right!; }
            }
            return ObjectsOf(node)[index];
        }

        public int Start(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var node = _root!;
            var start = 0;
            while (node.Chunk is null)
            {
                if (index < node.Left!.Chunks) node = node.Left;
                else { index -= node.Left.Chunks; start += node.Left.Objects; node = node.Right!; }
            }
            return start;
        }

        public bool TryStart(SceneObject[] chunk, out int start)
        {
            start = 0;
            if (!Leaves.TryGetValue(chunk, out var node)) return false;
            var leaf = node;
            while (true)
            {
                if (node.Chunk is not null && Links.Get(_values, node) is { } value && !ReferenceEquals(value, leaf))
                    return false;
                if (ReferenceEquals(node, _root)) return true;
                var parent = Links.Get(_parents, node);
                if (parent is null) return false;
                if (ReferenceEquals(parent.Right, node)) start += parent.Left!.Objects;
                node = parent;
            }
        }

        public ChunkDirectory Replace(IReadOnlyDictionary<int, SceneObject[]> copied)
        {
            var builder = new Builder(this);
            builder.ReplaceCopies(copied);
            return builder.Finish();
        }

        public Builder Edit() => new(this);

        public IEnumerable<SceneObject[]> Range(int index, int count) => WalkRange(_root, index, count);

        private IEnumerable<SceneObject[]> WalkRange(Node? node, int index, int count)
        {
            if (node is null || count == 0) yield break;
            if (node.Chunk is not null) { yield return ObjectsOf(node); yield break; }
            var leftCount = Math.Min(count, Math.Max(0, node.Left!.Chunks - index));
            foreach (var item in WalkRange(node.Left, index, leftCount)) yield return item;
            foreach (var item in WalkRange(node.Right, Math.Max(0, index - node.Left.Chunks), count - leftCount)) yield return item;
        }

        public void Validate()
        {
            var aliases = 0;
            var nodes = Check(_root);
            if (Links.Count(_parents) != Math.Max(0, nodes - 1) + aliases || Links.Count(_values) != aliases)
                throw new InvalidOperationException("Obsolete directory parent links.");

            int Check(Node? node)
            {
                if (node is null) return 0;
                if (node.Chunk is not null)
                {
                    if (Links.Get(_values, node) is { } value)
                    {
                        aliases++;
                        if (!ReferenceEquals(Links.Get(_parents, value), node) || value.Objects != node.Objects
                            || value.TextCount != 0 || node.TextCount != 0)
                            throw new InvalidOperationException("Invalid replacement leaf link.");
                    }
                    return 1;
                }
                var left = node.Left!; var right = node.Right!;
                if (Math.Abs(left.Height - right.Height) > 1
                    || node.Height != 1 + Math.Max(left.Height, right.Height)
                    || node.Objects != left.Objects + right.Objects || node.Chunks != left.Chunks + right.Chunks
                    || !ReferenceEquals(Links.Get(_parents, left), node)
                    || !ReferenceEquals(Links.Get(_parents, right), node))
                    throw new InvalidOperationException("Invalid directory balance, counts or parent links.");
                return 1 + Check(left) + Check(right);
            }
        }

        public IEnumerator<SceneObject[]> GetEnumerator() => Walk(_root, false).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public IEnumerable<SceneObject[]> TextChunksOnly() => Walk(_root, true);

        private IEnumerable<SceneObject[]> Walk(Node? node, bool textOnly)
        {
            if (node is null || textOnly && node.TextCount == 0) yield break;
            var pending = new Stack<Node>();
            pending.Push(node);
            while (pending.TryPop(out node))
            {
                if (textOnly && node.TextCount == 0) continue;
                if (node.Chunk is not null) yield return ObjectsOf(node);
                else { pending.Push(node.Right!); pending.Push(node.Left!); }
            }
        }

        // Changes are collected before publishing links: intermediate split/join nodes must
        // never keep obsolete directories alive. Only active child -> parent edges survive.
        public sealed class Builder
        {
            private readonly ChunkDirectory _source;
            private readonly Dictionary<Node, Node?> _changes = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<Node, Node?> _valueChanges = new(ReferenceEqualityComparer.Instance);
            private Node? _root;
            public Builder(ChunkDirectory source) { _source = source; _root = source._root; }
            public void Initialize(SceneObject[][] chunks) => _root = Build(chunks, 0, chunks.Length);

            public void ReplaceCopies(IReadOnlyDictionary<int, SceneObject[]> copied)
            {
                var ordered = copied.OrderBy(p => p.Key).ToArray();
                var at = 0;
                _root = ReplacePath(_root!, 0);

                Node ReplacePath(Node node, int start)
                {
                    if (at == ordered.Length || ordered[at].Key >= start + node.Chunks) return node;
                    if (node.Chunk is not null)
                    {
                        var copy = Leaves.GetValue(ordered[at++].Value, static chunk => new Node(chunk));
                        var previous = ValueOf(node);
                        if (previous.TextCount == 0 && copy.TextCount == 0)
                        {
                            if (!ReferenceEquals(previous, node)) _changes[previous] = null;
                            _valueChanges[node] = copy;
                            _changes[copy] = node;
                            return node;
                        }
                        Drop(node);
                        return copy;
                    }
                    var left = ReplacePath(node.Left!, start);
                    var right = ReplacePath(node.Right!, start + node.Left!.Chunks);
                    if (ReferenceEquals(left, node.Left) && ReferenceEquals(right, node.Right)) return node;
                    Drop(node);
                    return Branch(left, right);
                }
            }

            private Node Branch(Node left, Node right)
            {
                var node = new Node(left, right);
                _changes[left] = node; _changes[right] = node;
                return node;
            }

            private Node ValueOf(Node node) => _valueChanges.TryGetValue(node, out var value)
                ? value ?? node : Links.Get(_source._values, node) ?? node;

            private void Drop(Node node)
            {
                _changes[node] = null;
                if (node.Chunk is null) return;
                var value = ValueOf(node);
                if (!ReferenceEquals(value, node)) _changes[value] = null;
                _valueChanges[node] = null;
            }
            private void DropTree(Node? node)
            {
                if (node is null) return;
                Drop(node);
                DropTree(node.Left); DropTree(node.Right);
            }

            private Node? Build(IReadOnlyList<SceneObject[]> chunks, int start, int count)
            {
                if (count == 0) return null;
                if (count == 1) return Leaves.GetValue(chunks[start], static chunk => new Node(chunk));
                var half = count / 2;
                return Branch(Build(chunks, start, half)!, Build(chunks, start + half, count - half)!);
            }

            private Node Balance(Node left, Node right)
            {
                if (left.Height > right.Height + 1)
                {
                    Drop(left);
                    if (left.Left!.Height >= left.Right!.Height)
                        return Branch(left.Left, Branch(left.Right, right));
                    var middle = left.Right;
                    Drop(middle);
                    return Branch(Branch(left.Left, middle.Left!), Branch(middle.Right!, right));
                }
                if (right.Height > left.Height + 1)
                {
                    Drop(right);
                    if (right.Right!.Height >= right.Left!.Height)
                        return Branch(Branch(left, right.Left), right.Right);
                    var middle = right.Left;
                    Drop(middle);
                    return Branch(Branch(left, middle.Left!), Branch(middle.Right!, right.Right));
                }
                return Branch(left, right);
            }

            private Node? Join(Node? left, Node? right)
            {
                if (left is null) return right;
                if (right is null) return left;
                if (left.Height > right.Height + 1)
                {
                    Drop(left);
                    return Balance(left.Left!, Join(left.Right, right)!);
                }
                if (right.Height > left.Height + 1)
                {
                    Drop(right);
                    return Balance(Join(left, right.Left)!, right.Right!);
                }
                return Branch(left, right);
            }

            private (Node? Left, Node? Right) Split(Node? node, int chunks)
            {
                if (chunks == 0) return (null, node);
                if (chunks == node!.Chunks) return (node, null);
                Drop(node);
                if (chunks < node.Left!.Chunks)
                {
                    var (left, rest) = Split(node.Left, chunks);
                    return (left, Join(rest, node.Right));
                }
                var (prefix, right) = Split(node.Right, chunks - node.Left.Chunks);
                return (Join(node.Left, prefix), right);
            }

            public void Replace(int index, int count, IReadOnlyList<SceneObject[]> chunks)
            {
                var (prefix, rest) = Split(_root, index);
                var (removed, suffix) = Split(rest, count);
                DropTree(removed);
                _root = Join(Join(prefix, Build(chunks, 0, chunks.Count)), suffix);
            }

            public ChunkDirectory Finish()
            {
                if (_root is not null) _changes[_root] = null;
                var parents = _source._parents;
                foreach (var (child, parent) in _changes) parents = Links.Set(parents, child, parent);
                var values = _source._values;
                foreach (var (leaf, value) in _valueChanges) values = Links.Set(values, leaf, value);
                return new ChunkDirectory(_root, parents, values);
            }
        }

        // Compressed binary trie of unique, process-local node numbers (never serialized).
        // Lookup/update takes at most 64 branch steps, without hash collisions or a second
        // ordered tree per AVL edge. Numbers only identify immutable nodes; they carry no rank.
        private sealed class Links
        {
            private readonly ulong _key, _mask;
            private readonly Links? _left, _right;
            private readonly Node? _parent;
            private Links(ulong key, Node parent) { _key = key; _parent = parent; }
            private Links(ulong key, ulong mask, Links left, Links right)
            { _key = key; _mask = mask; _left = left; _right = right; }
            private bool Matches(ulong key) => (key & ~(_mask | (_mask - 1))) == _key;

            public static int Count(Links? links) => links is null ? 0
                : links._mask == 0 ? 1 : Count(links._left) + Count(links._right);

            public static Node? Get(Links? links, Node node)
            {
                var key = node.Id;
                while (links is { _mask: not 0 })
                {
                    if (!links.Matches(key)) return null;
                    links = (key & links._mask) == 0 ? links._left : links._right;
                }
                return links?._key == key ? links._parent : null;
            }

            public static Links? Set(Links? links, Node node, Node? parent) => Set(links, node.Id, parent);

            private static Links? Set(Links? links, ulong key, Node? parent)
            {
                if (links is null) return parent is null ? null : new Links(key, parent);
                if (links._mask == 0 && key == links._key)
                    return ReferenceEquals(parent, links._parent) ? links : parent is null ? null : new Links(key, parent);
                if (links._mask != 0 && links.Matches(key))
                {
                    var left = links._left; var right = links._right;
                    if ((key & links._mask) == 0) left = Set(left, key, parent);
                    else right = Set(right, key, parent);
                    if (ReferenceEquals(left, links._left) && ReferenceEquals(right, links._right)) return links;
                    return left is null ? right : right is null ? left : new Links(links._key, links._mask, left, right);
                }
                if (parent is null) return links;
                var mask = 1UL << BitOperations.Log2(key ^ links._key);
                var prefix = key & ~(mask | (mask - 1));
                var leaf = new Links(key, parent);
                return (key & mask) == 0 ? new Links(prefix, mask, leaf, links) : new Links(prefix, mask, links, leaf);
            }
        }
    }
}
