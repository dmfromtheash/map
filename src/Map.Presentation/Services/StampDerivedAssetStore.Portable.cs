using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Map.Assets;
using SkiaSharp;

namespace Map.Presentation.Services;

public sealed partial class StampDerivedAssetStore
{
    private static readonly byte[] PortableMagic = "MAPDRV01"u8.ToArray();
    private const int PortableLimit = 16 * 1024 * 1024;
    private sealed record PortableEntry(int Step, int Level, int Width, int Height, int Offset, int Length, string Hash);
    private sealed record PortableManifest(int Version, Header Header, FileStamp[] Sources, byte[] Alpha32, PortableEntry[] Entries, string[] SourceHashes);
    private sealed record PortablePixels(int Width, int Height, byte[] Pixels);
    private sealed record PortableTrust(FileStamp[] Sources, string[] Hashes);
    private readonly Dictionary<string, FileStamp?> _rejectedPortable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PortableTrust> _portableTrust = new(StringComparer.OrdinalIgnoreCase);

    private string PortablePath(AssetRecord record) => Path.Combine(_catalog.RootFor(record), "derived", "v" + Version, record.Sha256.ToLowerInvariant() + ".mapderived");

    private bool IsRejectedPortable(string sha, string path)
    {
        FileStamp? rejected;
        lock (_requestsGate)
            if (!_rejectedPortable.TryGetValue(sha, out rejected)) return false;
        return rejected == FileStamp.Read(path);
    }

    private void RejectPortable(Header header, Exception error)
    {
        if (IsRejectedPortable(header.Sha, header.PortablePath!)) return;
        var stamp = FileStamp.Read(header.PortablePath!);
        lock (_requestsGate)
        {
            while (_rejectedPortable.Count >= _metadataCapacity) _rejectedPortable.Remove(_rejectedPortable.Keys.First());
            _rejectedPortable[header.Sha] = stamp;
        }
        ReportFailure("portable pixels " + header.Sha, error);
        // Keep the running request alive so it can return the repaired raster. Future
        // requests must obtain a local header instead of repeatedly selecting this file.
        InvalidateFile(header.Original.Path, cancelPixels: false);
    }

    private static PixelKey[] PortableKeys(AssetRecord record)
    {
        var depth = AssetCatalog.DetailLadderDepth(record.Width, record.Height);
        var result = new List<PixelKey>();
        for (var step = 0; step <= depth; step++)
            for (var level = 0; level <= (step == depth
                ? Math.Min(15, (int)Math.Floor(Math.Log2(Math.Max(1, Math.Min(record.Width >> step, record.Height >> step))))) : 0); level++)
                if (Math.Max(Math.Max(1, record.Width >> step) >> level, Math.Max(1, record.Height >> step) >> level) <= 1024)
                    result.Add(new(record.Sha256.ToLowerInvariant(), step, level));
        return result.ToArray();
    }

    private static (PortableManifest Manifest, long DataStart) ReadPortableManifest(FileStream file)
    {
        if (file.Length is < 44 or > PortableLimit) throw new InvalidDataException("Portable asset size.");
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(PortableMagic)) throw new InvalidDataException("Portable asset magic.");
        var length = reader.ReadInt32();
        if (length is < 1 or > 32 * 1024 || 44L + length > file.Length) throw new InvalidDataException("Portable manifest size.");
        var hash = reader.ReadBytes(32);
        var json = reader.ReadBytes(length);
        if (!SHA256.HashData(json).AsSpan().SequenceEqual(hash)) throw new InvalidDataException("Portable manifest hash.");
        var manifest = JsonSerializer.Deserialize<PortableManifest>(json) ?? throw new InvalidDataException("Portable manifest.");
        if (manifest.Version != Version || manifest.Header is not { Version: Version, Sha.Length: 64, Width: > 0 and <= 32768, Height: > 0 and <= 32768,
                FirstMip: >= 0 and <= 15, MipCount: >= 0 and <= 16, PixelHash.Length: 64 }
            || manifest.Alpha32 is not { Length: 1024 } || manifest.Entries is not { Length: >= 1 and <= 32 }
            || manifest.Sources is not { Length: >= 1 and <= 16 }
            || manifest.SourceHashes is null || manifest.SourceHashes.Length != manifest.Sources.Length
            || manifest.SourceHashes.Any(hash => hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Portable manifest version or dimensions.");
        var dataStart = file.Position;
        long end = 0;
        var keys = new HashSet<(int, int)>();
        foreach (var entry in manifest.Entries)
        {
            if (entry is null || entry.Step is < 0 or > 15 || entry.Level is < 0 or > 15 || !keys.Add((entry.Step, entry.Level))
                || entry.Width is < 1 or > 1024 || entry.Height is < 1 or > 1024 || entry.Hash is not { Length: 64 }
                || entry.Offset != end || entry.Length is < 1 or > PortableLimit)
                throw new InvalidDataException("Portable pixel bounds.");
            end += entry.Length;
        }
        if (dataStart + end != file.Length) throw new InvalidDataException("Incomplete portable asset.");
        return (manifest, dataStart);
    }

    private Header? TryReadPortableHeader(AssetRecord record)
    {
        try
        {
            var path = PortablePath(record);
            if (!File.Exists(path) || IsRejectedPortable(record.Sha256, path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var (manifest, _) = ReadPortableManifest(file);
            var held = manifest.Header;
            var root = _catalog.RootFor(record);
            var original = FileStamp.Read(_catalog.SourcePath(record));
            var working = FileStamp.Read(_catalog.TryDetailLevelPath(record, 1, out var step) ? step : _catalog.SourcePath(record));
            if (original is null || working is null || !string.Equals(held.Sha, record.Sha256, StringComparison.OrdinalIgnoreCase)
                || !PortableStampMatches(held.Original, original, root) || !PortableStampMatches(held.Working, working, root)
                || !PortableSourcesMatch(record, manifest) || !VerifyPortableSources(record, manifest)) return null;
            foreach (var entry in manifest.Entries)
                if (entry.Width != Math.Max(1, Math.Max(1, record.Width >> entry.Step) >> entry.Level)
                    || entry.Height != Math.Max(1, Math.Max(1, record.Height >> entry.Step) >> entry.Level))
                    throw new InvalidDataException("Portable pixel dimensions differ from catalog.");
            return held with { Original = original, Working = working, PortablePath = path };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private static bool PortableStampMatches(FileStamp? stored, FileStamp current, string root)
        => stored is not null && stored.Length == current.Length
            && string.Equals(stored.Path, Path.GetRelativePath(root, current.Path).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private bool VerifyPortableSources(AssetRecord record, PortableManifest manifest)
    {
        var sources = PortableSources(record);
        var path = Stem(record.Sha256) + ".trust";
        PortableTrust? trust;
        lock (_requestsGate) _portableTrust.TryGetValue(record.Sha256, out trust);
        if (trust is null)
            try
            {
                if (new FileInfo(path) is { Exists: true, Length: <= 16 * 1024 })
                    trust = JsonSerializer.Deserialize<PortableTrust>(File.ReadAllBytes(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        if (trust?.Sources is not null && trust.Hashes is not null
            && trust.Sources.SequenceEqual(sources) && trust.Hashes.SequenceEqual(manifest.SourceHashes))
        {
            RememberPortableTrust(record.Sha256, trust);
            return true;
        }
        // A portable file is not a machine-local proof of the source identity. Verify
        // the master once, and each ladder's exact bytes, without decoding any image.
        if (_catalog.ReadVerified(record).Status != AssetResolutionStatus.Available) return false;
        for (var i = 0; i < sources.Length; i++)
        {
            var hash = i == 0 ? record.Sha256.ToLowerInvariant()
                : Convert.ToHexStringLower(SHA256.HashData(AssetImagePolicy.ReadEncodedFile(sources[i].Path)));
            if (!string.Equals(hash, manifest.SourceHashes[i], StringComparison.OrdinalIgnoreCase) || !sources[i].Matches()) return false;
        }
        trust = new(sources, manifest.SourceHashes);
        RememberPortableTrust(record.Sha256, trust);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var previousBytes = FileBytes(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(trust));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ReportFailure("portable trust persistence", error); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Interlocked.Add(ref _diskBytes, FileBytes(path) - previousBytes);
        if (DiskBytes > _diskBudget) TrimDisk();
        return true;
    }

    private void RememberPortableTrust(string sha, PortableTrust trust)
    {
        lock (_requestsGate)
        {
            if (!_portableTrust.ContainsKey(sha))
                while (_portableTrust.Count >= _metadataCapacity) _portableTrust.Remove(_portableTrust.Keys.First());
            _portableTrust[sha] = trust;
        }
    }

    private FileStamp[] PortableSources(AssetRecord record)
    {
        var sources = new List<FileStamp>();
        if (FileStamp.Read(_catalog.SourcePath(record)) is { } original) sources.Add(original);
        for (var step = 1; step <= AssetCatalog.DetailLadderDepth(record.Width, record.Height); step++)
            if (_catalog.TryDetailLevelPath(record, step, out var path) && FileStamp.Read(path) is { } stamp) sources.Add(stamp);
        return sources.ToArray();
    }

    private bool PortableSourcesMatch(AssetRecord record, PortableManifest manifest)
    {
        var sources = PortableSources(record);
        if (sources.Length != manifest.Sources.Length) return false;
        for (var i = 0; i < sources.Length; i++)
        {
            if (!PortableStampMatches(manifest.Sources[i], sources[i], _catalog.RootFor(record))) return false;
            Watch(record, sources[i].Path);
        }
        // Also watch absent ladder steps: their later creation changes which exact pixels are used.
        for (var step = 1; step <= AssetCatalog.DetailLadderDepth(record.Width, record.Height); step++)
            Watch(record, Path.Combine(_catalog.RootFor(record), "ladder", record.Sha256.ToLowerInvariant(), step + ".png"));
        return true;
    }

    private PortablePixels? TryReadPortablePixels(Header header, PixelKey key)
    {
        try
        {
            if (IsRejectedPortable(key.Sha, header.PortablePath!)) return null;
            if (!header.Original.Matches() || !header.Working.Matches()) throw new InvalidDataException("Portable source changed.");
            using var file = new FileStream(header.PortablePath!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var (manifest, start) = ReadPortableManifest(file);
            if (!string.Equals(manifest.Header.Sha, key.Sha, StringComparison.OrdinalIgnoreCase)
                || _catalog.Find(key.Sha) is not { } record || !PortableSourcesMatch(record, manifest)
                || !VerifyPortableSources(record, manifest)) throw new InvalidDataException("Portable source identity changed.");
            var entry = manifest.Entries.FirstOrDefault(entry => entry.Step == key.Step && entry.Level == key.Level);
            if (entry is null) return null;
            if (entry.Width != Math.Max(1, Math.Max(1, record.Width >> key.Step) >> key.Level)
                || entry.Height != Math.Max(1, Math.Max(1, record.Height >> key.Step) >> key.Level))
                throw new InvalidDataException("Portable pixel dimensions differ from catalog.");
            file.Position = start + entry.Offset;
            var compressed = new byte[entry.Length];
            file.ReadExactly(compressed);
            var pixels = new byte[checked(entry.Width * entry.Height * 4)];
            using var decoder = new BrotliDecoder();
            var status = decoder.Decompress(compressed, pixels, out var consumed, out var written);
            if (status != System.Buffers.OperationStatus.Done || consumed != compressed.Length || written != pixels.Length
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(pixels)), entry.Hash, StringComparison.Ordinal))
                throw new InvalidDataException("Portable pixels hash or size.");
            return new(entry.Width, entry.Height, pixels);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { RejectPortable(header, ex); return null; }
    }

    /// <summary>Explicit pipeline operation, on a separate store instance only.
    /// Normal rendering only reads this file; it never writes into a mounted library.</summary>
    public async Task<string> BuildPortableAsync(string sha, CancellationToken cancellation = default)
    {
        var record = _catalog.Find(sha) ?? throw new ArgumentException("Unknown asset.", nameof(sha));
        var root = _catalog.RootFor(record);
        var sources = PortableSources(record);
        var header = await Request(sha).WaitAsync(cancellation).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source is unavailable or changed.");
        var relativeOriginal = Path.GetRelativePath(root, header.Original.Path).Replace('\\', '/');
        var relativeWorking = Path.GetRelativePath(root, header.Working.Path).Replace('\\', '/');
        if (relativeOriginal.StartsWith("../", StringComparison.Ordinal) || relativeWorking.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeOriginal) || Path.IsPathRooted(relativeWorking))
            throw new InvalidDataException("Portable assets must be inside their catalog.");
        using var data = new MemoryStream();
        var entries = new List<PortableEntry>();
        foreach (var key in PortableKeys(record))
        {
            await RequestPixels(header, key.Step, key.Level).WaitAsync(cancellation).ConfigureAwait(false);
            if (!TryTakePixels(key, out var ready) || ready is null) throw new InvalidDataException("Pixel preparation did not finish.");
            using var image = ready.Image;
            byte[] pixels;
            var width = ready.Width; var height = ready.Height;
            if (image is not null)
            {
                using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) throw new InvalidDataException("Pixel read.");
                pixels = bitmap.Bytes;
            }
            else pixels = ready.Pixels ?? throw new InvalidDataException("No prepared pixels.");
            if (width > 1024 || height > 1024) throw new InvalidDataException("Portable raster exceeds 1024.");
            var offset = checked((int)data.Position);
            using (var compressed = new BrotliStream(data, CompressionLevel.Fastest, leaveOpen: true)) compressed.Write(pixels);
            entries.Add(new(key.Step, key.Level, width, height, offset, checked((int)data.Position - offset), Convert.ToHexStringLower(SHA256.HashData(pixels))));
        }
        // Auxiliary alpha preview only. It is not used for occlusion decisions: doing so
        // would require a separate conservative-mask pixel-equivalence proof.
        var alpha = new byte[1024];
        using (var source = ReadSource(record, 1, header, out _, out _))
        {
            if (source is null) throw new InvalidDataException("Alpha source unavailable.");
            using var bitmap = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var pixels = bitmap.PeekPixels();
            if (!source.ScalePixels(pixels, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) throw new InvalidDataException("Alpha preparation.");
            var rgba = bitmap.Bytes;
            for (var i = 0; i < alpha.Length; i++) alpha[i] = rgba[i * 4 + 3];
        }
        if (!header.Original.Matches() || !header.Working.Matches() || !sources.SequenceEqual(PortableSources(record)))
            throw new InvalidDataException("Source changed during preparation.");
        var portable = header with { Original = header.Original with { Path = relativeOriginal }, Working = header.Working with { Path = relativeWorking }, PortablePath = null };
        var relativeSources = sources.Select(stamp => stamp with { Path = Path.GetRelativePath(root, stamp.Path).Replace('\\', '/') }).ToArray();
        var hashes = sources.Select(stamp => Convert.ToHexStringLower(SHA256.HashData(AssetImagePolicy.ReadEncodedFile(stamp.Path)))).ToArray();
        if (!sources.All(stamp => stamp.Matches())) throw new InvalidDataException("Source changed during hashing.");
        var json = JsonSerializer.SerializeToUtf8Bytes(new PortableManifest(Version, portable, relativeSources, alpha, entries.ToArray(), hashes));
        if (44L + json.Length + data.Length > PortableLimit) throw new InvalidDataException("Portable size limit.");
        var destination = PortablePath(record);
        foreach (var directory in new[] { Path.Combine(root, "derived"), Path.GetDirectoryName(destination)! })
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Portable output must not follow a directory reparse point.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(PortableMagic); writer.Write(json.Length); writer.Write(SHA256.HashData(json)); writer.Write(json);
                data.Position = 0; data.CopyTo(file); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }
}
