using System.Security.Cryptography;

namespace Map.Persistence;

public static partial class MapProjectStore
{
    private sealed record WrittenSnapshot(Guid DocumentId, long Length, string Sha256);
    private static readonly object WrittenSnapshotsGate = new();
    private static readonly Dictionary<string, WrittenSnapshot> WrittenSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> WrittenSnapshotOrder = new();

    private static void RememberWrittenSnapshot(string path, Guid documentId, ContainerWriteState state)
    {
        lock (WrittenSnapshotsGate)
        {
            if (!WrittenSnapshots.ContainsKey(path)) WrittenSnapshotOrder.Enqueue(path);
            WrittenSnapshots[path] = new(documentId, state.FileLength, state.FileSha256);
            while (WrittenSnapshotOrder.Count > 32) WrittenSnapshots.Remove(WrittenSnapshotOrder.Dequeue());
        }
    }

    // An immutable autosave generation just written by this process need not build a second
    // document to become the previous generation. Check its entire compressed-file fingerprint
    // every time: timestamps alone cannot prove that a file has not been changed or damaged.
    internal static bool IsKnownWrittenSnapshot(string path, Guid documentId)
    {
        WrittenSnapshot? receipt;
        lock (WrittenSnapshotsGate) WrittenSnapshots.TryGetValue(Path.GetFullPath(path), out receipt);
        if (receipt is null || receipt.DocumentId != documentId) return false;
        using var stream = File.OpenRead(path);
        return stream.Length == receipt.Length
            && string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), receipt.Sha256, StringComparison.Ordinal);
    }
}
