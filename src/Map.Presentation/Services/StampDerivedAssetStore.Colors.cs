using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Assets;
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
    // Format 1 has neither integrity nor source stamps; rebuild that expendable cache once.
    private const int ColorsFormat = 2;
    private const int ColorEntryBytes = 68; // SHA (32), colour (4), source-stamp fingerprint (32).
    private const int ColorsSaveEvery = 512;
    private sealed record KnownOverviewColor(uint Color, string Sources, bool Verified);
    private readonly ConcurrentDictionary<string, KnownOverviewColor> _knownColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _colorsSaveGate = new();
    private int _colorsUnsaved;
    private long _colorsSavedAt;

    private string ColorsPath => Path.Combine(_root, "v" + Version, "overview-colors.bin");

    /// <summary>Colours remembered from earlier runs and this one (count), for diagnostics and tests.</summary>
    public int KnownColorCount => _knownColors.Count;

    /// <summary>The asset's overview colour as last prepared, without any file access; null when never prepared.</summary>
    public SKColor? KnownColor(string sha) => _knownColors.TryGetValue(sha, out var color) ? new SKColor(color.Color) : null;

    private void LoadKnownColors()
    {
        try
        {
            var path = ColorsPath;
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 64 * 1024 * 1024) return;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ColorsMagic
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != ColorsFormat
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version) return;
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            if (count < 0 || 48L + count * (long)ColorEntryBytes != bytes.Length
                || !SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 32))) return;
            for (var i = 0; i < count; i++)
            {
                var offset = 16 + i * ColorEntryBytes;
                var sha = Convert.ToHexStringLower(bytes, offset, 32);
                _knownColors[sha] = new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 32)),
                    Convert.ToHexStringLower(bytes, offset + 36, 32), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { _knownColors.Clear(); }
    }

    // Under _requestsGate, together with checking the completing request's identity.
    // The caller saves outside that gate so file I/O cannot block a canvas request.
    private bool RememberColor(string sha, Header header)
    {
        var sources = ColorSourceStamp(header.Original, header.Working);
        var verified = header.PortablePath is null
            || _portableTrust.TryGetValue(sha, out var trust) && ColorSourcesVerified(sources, trust);
        var color = new KnownOverviewColor(header.Color, sources, verified);
        if (_knownColors.TryGetValue(sha, out var known) && known == color) return false;
        _knownColors[sha] = color;
        return Interlocked.Increment(ref _colorsUnsaved) >= ColorsSaveEvery
            || System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _colorsSavedAt)).TotalSeconds >= 5;
    }

    private bool ForgetColor(string sha)
    {
        if (!_knownColors.TryRemove(sha, out _)) return false;
        Interlocked.Increment(ref _colorsUnsaved);
        return true;
    }

    private static string ColorSourceStamp(FileStamp? original, FileStamp? working) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { original, working })));

    private static bool ColorSourcesVerified(string sources, PortableTrust trust) => trust.Sources.Length > 0
        && trust.Sources.Any(working => ColorSourceStamp(trust.Sources[0], working) == sources);

    // Under _requestsGate: portable colours may be shown provisionally, but only a proved
    // source may survive shutdown (the next run would otherwise bypass the trust queue).
    private void ConfirmKnownColor(string sha, PortableTrust trust)
    {
        if (_disposed || !_knownColors.TryGetValue(sha, out var color) || color.Verified
            || !ColorSourcesVerified(color.Sources, trust)) return;
        _knownColors[sha] = color with { Verified = true };
        Interlocked.Increment(ref _colorsUnsaved);
    }

    // A remembered palette must not suppress source validation and watcher registration.
    // This startup pass only stats files; it never reads headers, decodes, or gates the overview.
    private void ValidateKnownColorSources()
    {
        foreach (var (sha, remembered) in _knownColors.ToArray())
        {
            if (_disposed) break;
            if (_catalog.Find(sha) is not { } record || _catalog.SourcePath(record) is not { } path) continue;
            try
            {
                Watch(record, path);
                if (AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0)
                    Watch(record, Path.Combine(_catalog.RootFor(record), "ladder", sha.ToLowerInvariant(), "1.png"));
                var original = FileStamp.Read(path);
                var working = FileStamp.Read(_catalog.TryDetailLevelPath(record, 1, out var step) ? step : path);
                if (original is not null && working is not null && ColorSourceStamp(original, working) == remembered.Sources) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // Compare under the invalidation gate, but notify after releasing it.
            // A newer header may have repaired this entry while its files were being checked.
            InvalidateFile(path, cancelPixels: true, expectedColor: remembered);
        }
    }

    /// <summary>Atomic: a temporary file moved over the previous one. Worker threads and shutdown only.</summary>
    private void SaveKnownColors()
    {
        lock (_colorsSaveGate)
        {
            if (Interlocked.Exchange(ref _colorsUnsaved, 0) == 0) return;
            Interlocked.Exchange(ref _colorsSavedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            var entries = _knownColors.ToArray().Where(pair => pair.Value.Verified).ToArray();
            var bytes = new byte[48 + entries.Length * ColorEntryBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, ColorsMagic);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), ColorsFormat);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
            var written = 0;
            foreach (var (sha, color) in entries)
            {
                var offset = 16 + written * ColorEntryBytes;
                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) continue;
                Convert.FromHexString(sha).CopyTo(bytes, offset);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 32), color.Color);
                Convert.FromHexString(color.Sources).CopyTo(bytes, offset + 36);
                written++;
            }
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), written);
            var payloadLength = 16 + written * ColorEntryBytes;
            SHA256.HashData(bytes.AsSpan(0, payloadLength)).CopyTo(bytes, payloadLength);
            var path = ColorsPath;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporary, bytes.AsSpan(0, payloadLength + 32).ToArray());
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
