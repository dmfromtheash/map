using System.Collections.Immutable;

namespace Map.Core.Documents;

public sealed partial class SceneObjectList
{
    // Weighted AVL directory: rank is derived from subtree sizes, so removing a chunk does
    // not rewrite the starts of every later chunk. Immutable order keys let identity slots
    // find a chunk's current rank without retaining predecessor revisions.
    private sealed class Directory : IReadOnlyList<SceneObject[]>
    {
        private sealed class Node(string key, SceneObject[] chunk, Node? left, Node? right)
        {
            public string Key { get; } = key;
            public SceneObject[] Chunk { get; } = chunk;
            public Node? Left { get; } = left;
            public Node? Right { get; } = right;
            public int Height { get; } = 1 + Math.Max(H(left), H(right));
            public int Count { get; } = 1 + C(left) + C(right);
            public int Objects { get; } = chunk.Length + O(left) + O(right);
        }

        private readonly Node? _root;
        private readonly ImmutableDictionary<SceneObject[], string> _keys;
        private Directory(Node? root, ImmutableDictionary<SceneObject[], string> keys)
        { _root = root; _keys = keys; }

        public static Directory Create(SceneObject[][] chunks)
        {
            var keys = ImmutableDictionary.CreateBuilder<SceneObject[], string>(ReferenceEqualityComparer.Instance);
            Node? Build(int start, int end)
            {
                if (start == end) return null;
                var middle = (start + end) >>> 1;
                var key = new string([(char)(middle / 65536 + 1), (char)(middle % 65536), (char)32768]);
                keys.Add(chunks[middle], key);
                return new Node(key, chunks[middle], Build(start, middle), Build(middle + 1, end));
            }
            var root = Build(0, chunks.Length);
            return new(root, keys.ToImmutable());
        }

        private static int H(Node? n) => n?.Height ?? 0;
        private static int C(Node? n) => n?.Count ?? 0;
        private static int O(Node? n) => n?.Objects ?? 0;
        public int Count => C(_root);
        public int ObjectCount => O(_root);
        public int Length => Count;
        public string KeyOf(SceneObject[] chunk) => _keys[chunk];
        public SceneObject[] ChunkAtKey(string key)
        {
            var node = _root!;
            while (true)
            {
                var compare = string.CompareOrdinal(key, node.Key);
                if (compare == 0) return node.Chunk;
                node = (compare < 0 ? node.Left : node.Right)!;
            }
        }
        public SceneObject[] this[int index] => At(index).Chunk;
        private Node At(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var node = _root!;
            while (true)
            {
                var left = C(node.Left);
                if (index < left) node = node.Left!;
                else if (index == left) return node;
                else { index -= left + 1; node = node.Right!; }
            }
        }

        public (int Chunk, int Offset) Locate(int position)
        {
            var node = _root;
            var rank = 0;
            while (node is not null)
            {
                var left = O(node.Left);
                if (position < left) node = node.Left;
                else if (position < left + node.Chunk.Length) return (rank + C(node.Left), position - left);
                else { position -= left + node.Chunk.Length; rank += C(node.Left) + 1; node = node.Right; }
            }
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        public int Start(int index)
        {
            var node = At(index);
            return StartOfKey(node.Key);
        }
        public bool TryGetStart(SceneObject[] chunk, out int start)
        {
            if (!_keys.TryGetValue(chunk, out var key)) { start = 0; return false; }
            start = StartOfKey(key);
            return true;
        }
        private int StartOfKey(string key)
        {
            var start = 0;
            var node = _root!;
            while (true)
            {
                var compare = string.CompareOrdinal(key, node.Key);
                if (compare < 0) node = node.Left!;
                else if (compare == 0) return start + O(node.Left);
                else { start += O(node.Left) + node.Chunk.Length; node = node.Right!; }
            }
        }

        public Directory Splice(int index, int count, IReadOnlyList<SceneObject[]> added)
        {
            var root = _root;
            var keys = _keys.ToBuilder();
            for (var i = 0; i < count; i++)
            {
                var old = At(index + i);
                root = Remove(root!, old.Key);
                keys.Remove(old.Chunk);
            }
            var lower = index == 0 ? null : At(index - 1).Key;
            var upper = index + count == Count ? null : At(index + count).Key;
            foreach (var chunk in added)
            {
                lower = Between(lower, upper);
                root = Set(root, lower, chunk);
                keys.Add(chunk, lower);
            }
            return new(root, keys.ToImmutable());
        }

        public Directory Replace(int index, SceneObject[] chunk)
        {
            var old = At(index);
            return new(Set(_root, old.Key, chunk), _keys.Remove(old.Chunk).Add(chunk, old.Key));
        }

        // Lexicographic fractional keys. No key ends in zero, so even a prefix boundary
        // always has room for another key; repeated insertions never require relabelling.
        private static string Between(string? lower, string? upper)
        {
            var prefix = new System.Text.StringBuilder();
            for (var i = 0; ; i++)
            {
                var a = lower is not null && i < lower.Length ? lower[i] : 0;
                var b = upper is not null && i < upper.Length ? upper[i] : 65535;
                if (b - a > 1) return prefix.Append((char)(a + (b - a) / 2)).ToString();
                prefix.Append((char)a);
                if (a != b) upper = null;
            }
        }

        private static Node Set(Node? n, string key, SceneObject[] chunk)
        {
            if (n is null) return new(key, chunk, null, null);
            var compare = string.CompareOrdinal(key, n.Key);
            return Balance(compare < 0 ? new(n.Key, n.Chunk, Set(n.Left, key, chunk), n.Right)
                : compare > 0 ? new(n.Key, n.Chunk, n.Left, Set(n.Right, key, chunk))
                : new(key, chunk, n.Left, n.Right));
        }
        private static Node? Remove(Node n, string key)
        {
            var compare = string.CompareOrdinal(key, n.Key);
            if (compare < 0) return Balance(new(n.Key, n.Chunk, Remove(n.Left!, key), n.Right));
            if (compare > 0) return Balance(new(n.Key, n.Chunk, n.Left, Remove(n.Right!, key)));
            if (n.Left is null) return n.Right;
            if (n.Right is null) return n.Left;
            var next = n.Right;
            while (next.Left is not null) next = next.Left;
            return Balance(new(next.Key, next.Chunk, n.Left, Remove(n.Right, next.Key)));
        }
        private static Node Balance(Node n)
        {
            if (H(n.Left) > H(n.Right) + 1)
            {
                var left = n.Left!;
                if (H(left.Right) > H(left.Left)) left = RotateLeft(left);
                return RotateRight(new(n.Key, n.Chunk, left, n.Right));
            }
            if (H(n.Right) > H(n.Left) + 1)
            {
                var right = n.Right!;
                if (H(right.Left) > H(right.Right)) right = RotateRight(right);
                return RotateLeft(new(n.Key, n.Chunk, n.Left, right));
            }
            return n;
        }
        private static Node RotateLeft(Node n)
        {
            var right = n.Right!;
            return new(right.Key, right.Chunk, new(n.Key, n.Chunk, n.Left, right.Left), right.Right);
        }
        private static Node RotateRight(Node n)
        {
            var left = n.Left!;
            return new(left.Key, left.Chunk, left.Left, new(n.Key, n.Chunk, left.Right, n.Right));
        }
        public IEnumerator<SceneObject[]> GetEnumerator() => Walk(_root).GetEnumerator();
        private static IEnumerable<SceneObject[]> Walk(Node? node)
        {
            if (node is null) yield break;
            foreach (var chunk in Walk(node.Left)) yield return chunk;
            yield return node.Chunk;
            foreach (var chunk in Walk(node.Right)) yield return chunk;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
