using System.Buffers.Binary;
using System.Collections.Concurrent;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>
/// Overview colours that survive the program: one small file in the cache root, read once when the store starts.
/// A colour belongs to an asset's content hash, so a remembered one is valid for that hash; a header that arrives
/// later with another colour replaces it (stale-while-revalidate), and a changed source file forgets it.
/// Without it every open re-read the header of each asset of the library on one worker before the whole-map
/// layer could start - two minutes for 16 208 assets on the owner's PC (live, 2026-11-15), the frame meanwhile
/// drawing the large stamps one by one.
/// </summary>
public sealed partial class StampDerivedAssetStore
{
    private const uint ColorsMagic = 0x4D434F4C; // "LOCM"
    private const int ColorsFormat = 1;
    private const int ColorsSaveEvery = 512;
    private readonly ConcurrentDictionary<string, uint> _knownColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _colorsSaveGate = new();
    private int _colorsUnsaved;
    private long _colorsSavedAt;

    private string ColorsPath => Path.Combine(_root, "v" + Version, "overview-colors.bin");

    /// <summary>Colours remembered from earlier runs and this one (count), for diagnostics and tests.</summary>
    public int KnownColorCount => _knownColors.Count;

    /// <summary>The asset's overview colour as last prepared, without any file access; null when never prepared.</summary>
    public SKColor? KnownColor(string sha) => _knownColors.TryGetValue(sha, out var color) ? new SKColor(color) : null;

    private void LoadKnownColors()
    {
        try
        {
            var path = ColorsPath;
            if (!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ColorsMagic
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != ColorsFormat
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version) return;
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            if (count < 0 || 16L + count * 36L != bytes.Length) return;
            for (var i = 0; i < count; i++)
            {
                var offset = 16 + i * 36;
                var sha = Convert.ToHexStringLower(bytes, offset, 32);
                _knownColors[sha] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 32));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { _knownColors.Clear(); }
    }

    // Under _requestsGate, together with checking the completing request's identity.
    // The caller saves outside that gate so file I/O cannot block a canvas request.
    private bool RememberColor(string sha, uint color)
    {
        if (_knownColors.TryGetValue(sha, out var known) && known == color) return false;
        _knownColors[sha] = color;
        return Interlocked.Increment(ref _colorsUnsaved) >= ColorsSaveEvery
            || System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _colorsSavedAt)).TotalSeconds >= 5;
    }

    private void ForgetColor(string sha)
    {
        if (_knownColors.TryRemove(sha, out _)) Interlocked.Increment(ref _colorsUnsaved);
    }

    /// <summary>Atomic: a temporary file moved over the previous one. Worker threads and shutdown only.</summary>
    private void SaveKnownColors()
    {
        lock (_colorsSaveGate)
        {
            if (Interlocked.Exchange(ref _colorsUnsaved, 0) == 0) return;
            Interlocked.Exchange(ref _colorsSavedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            var entries = _knownColors.ToArray();
            var bytes = new byte[16 + entries.Length * 36];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, ColorsMagic);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), ColorsFormat);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
            var written = 0;
            foreach (var (sha, color) in entries)
            {
                var offset = 16 + written * 36;
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) continue;
                Convert.FromHexString(sha).CopyTo(bytes, offset);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 32), color);
                written++;
            }
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), written);
            var path = ColorsPath;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporary, bytes.AsSpan(0, 16 + written * 36).ToArray());
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The previous file is still authoritative on disk. Keep the mutation
                // dirty so a later worker or awaited shutdown retries the snapshot.
                Interlocked.Increment(ref _colorsUnsaved);
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                ReportFailure("overview colours", ex);
            }
        }
    }
}
