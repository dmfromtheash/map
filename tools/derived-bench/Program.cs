// Derived-image cache storage benchmark: (A) one file per entry vs (B) one SQLite database.
// Usage: DerivedBench --n 2000 --out <dir> --mode files|sqlite|all --sample-seed 1
//        [--library H:\MapLibrary] [--large-format png|webp] [--startup-runs 5] [--batch 100] [--keep-stores]
// Each measured phase runs in a fresh child process (BelowNormal priority).
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace DerivedBench;

sealed class Opts
{
    public int N = 100;
    public string Out = "";
    public string Mode = "all";
    public int Seed = 1;
    public string Library = @"H:\MapLibrary";
    public string LargeFormat = "png";
    public int StartupRuns = 5;
    public int Batch = 100;
    public bool KeepStores;
    public string? Child;
    public string Store = "";
    public int Run;

    public static Opts Parse(string[] a)
    {
        var o = new Opts();
        for (int i = 0; i < a.Length; i++)
        {
            string v() => a[++i];
            switch (a[i])
            {
                case "--n": o.N = int.Parse(v()); break;
                case "--out": o.Out = Path.GetFullPath(v()); break;
                case "--mode": o.Mode = v(); break;
                case "--sample-seed": o.Seed = int.Parse(v()); break;
                case "--library": o.Library = v(); break;
                case "--large-format": o.LargeFormat = v(); break;
                case "--startup-runs": o.StartupRuns = int.Parse(v()); break;
                case "--batch": o.Batch = int.Parse(v()); break;
                case "--keep-stores": o.KeepStores = true; break;
                case "--child": o.Child = v(); break;
                case "--store": o.Store = v(); break;
                case "--run": o.Run = int.Parse(v()); break;
                default: throw new ArgumentException("unknown argument " + a[i]);
            }
        }
        if (o.Out == "") throw new ArgumentException("--out is required");
        if (o.Mode is not ("files" or "sqlite" or "all")) throw new ArgumentException("--mode files|sqlite|all");
        if (o.LargeFormat is not ("png" or "webp")) throw new ArgumentException("--large-format png|webp");
        return o;
    }

    public string PreparedDir => Path.Combine(Out, "prepared", $"n{N}-s{Seed}-{LargeFormat}");
    public string StoresDir => Path.Combine(Out, "stores");
    public string ResultsDir => Path.Combine(Out, "results");
}

sealed record Asset(string Sha, byte[] Header, byte[] Small, List<(int Side, byte[] Bytes)> Large);

sealed record ManifestItem(string Sha, string Src, int W, int H, int[] Large, int SmallBytes, long LargeBytes);

static class Program
{
    static int Main(string[] args)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var o = Opts.Parse(args);
        if (o.Child != null) return Child.Run(o);
        return Orchestrator.Run(o);
    }
}

// ---------------------------------------------------------------- header layout
static class Hdr
{
    // [0]magic "MDA1" [4]version [8]w [12]h [16]firstSide [20]levels [24]smallLevels [28]color
    // [32]rawLen [36]compLen [40..72)pixelSha256, then source stamps (path, length, mtime) x2.
    public static readonly byte[] Magic = "MDA1"u8.ToArray();

    public static byte[] Build(int w, int h, int firstSide, int levels, int smallLevels, uint color,
        int rawLen, int compLen, byte[] pixelSha, string src, long len, long mtime)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        bw.Write(Magic); bw.Write(1); bw.Write(w); bw.Write(h); bw.Write(firstSide); bw.Write(levels);
        bw.Write(smallLevels); bw.Write(color); bw.Write(rawLen); bw.Write(compLen); bw.Write(pixelSha);
        for (int i = 0; i < 2; i++) { bw.Write(src); bw.Write(len); bw.Write(mtime); } // original + working stamp
        bw.Flush();
        return ms.ToArray();
    }

    public static bool Valid(ReadOnlySpan<byte> h) => h.Length >= 72 && h[..4].SequenceEqual(Magic);
    public static int RawLen(ReadOnlySpan<byte> h) => BinaryPrimitives.ReadInt32LittleEndian(h[32..]);
    public static ReadOnlySpan<byte> PixelSha(ReadOnlySpan<byte> h) => h.Slice(40, 32);

    // Decompress + verify the small-mip payload, exactly the work a real read does after IO.
    public static bool DecodeSmall(byte[] header, ReadOnlySpan<byte> payload)
    {
        if (!Valid(header)) return false;
        var raw = new byte[RawLen(header)];
        if (!BrotliDecoder.TryDecompress(payload, raw, out int n) || n != raw.Length) return false;
        Span<byte> sha = stackalloc byte[32];
        SHA256.HashData(raw, sha);
        return sha.SequenceEqual(PixelSha(header));
    }
}

// ---------------------------------------------------------------- preparation
static class Prepare
{
    static readonly string[] Exts = [".png", ".jpg", ".jpeg", ".webp"];

    public static void Ensure(Opts o)
    {
        var dir = o.PreparedDir;
        if (File.Exists(Path.Combine(dir, "manifest.json")) && File.Exists(Path.Combine(dir, "pack.bin")))
        {
            Console.WriteLine($"prepared payloads reused: {dir}");
            return;
        }
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        var all = Directory.EnumerateDirectories(o.Library)
            .Select(d => Path.Combine(d, "originals")).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d))
            .Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        new Random(o.Seed).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all));
        Console.WriteLine($"library: {all.Count} originals; sampling {o.N} (seed {o.Seed})");

        var manifest = new List<ManifestItem>();
        var seen = new HashSet<string>();
        var tmpPack = Path.Combine(dir, "pack.bin.tmp");
        using (var fs = new FileStream(tmpPack, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var bw = new BinaryWriter(fs))
        {
            var format = o.LargeFormat == "webp" ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png;
            foreach (var path in all)
            {
                if (manifest.Count >= o.N) break;
                try
                {
                    var asset = Build(o.Library, path, format, out var item);
                    if (asset == null || !seen.Add(asset.Sha)) continue;
                    bw.Write(asset.Sha);
                    bw.Write(asset.Header.Length); bw.Write(asset.Header);
                    bw.Write(asset.Small.Length); bw.Write(asset.Small);
                    bw.Write(asset.Large.Count);
                    foreach (var (side, bytes) in asset.Large) { bw.Write(side); bw.Write(bytes.Length); bw.Write(bytes); }
                    manifest.Add(item!);
                    if (manifest.Count % 25 == 0) Console.WriteLine($"  prepared {manifest.Count}/{o.N} ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                catch (Exception e) { Console.WriteLine($"  skip {Path.GetFileName(path)}: {e.GetType().Name}"); }
            }
        }
        if (manifest.Count < o.N) throw new InvalidOperationException($"only {manifest.Count} originals decodable");
        File.Move(tmpPack, Path.Combine(dir, "pack.bin"), true);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest));
        Console.WriteLine($"prepared {manifest.Count} assets in {sw.Elapsed.TotalSeconds:F1}s -> {dir}");
    }

    static SKBitmap Scale(SKBitmap src, int w, int h, bool cubic)
    {
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var sampling = cubic ? new SKSamplingOptions(SKCubicResampler.Mitchell) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        if (!src.ScalePixels(dst, sampling)) throw new InvalidOperationException("scale failed");
        return dst;
    }

    static Asset? Build(string library, string path, SKEncodedImageFormat format, out ManifestItem? item)
    {
        item = null;
        var bytes = File.ReadAllBytes(path); // read-only access to the library
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var original = SKBitmap.Decode(bytes);
        if (original == null) return null;
        int w = original.Width, h = original.Height, k = 0;
        while (Math.Max(w >> k, h >> k) > 1024) k++;
        var cur = Scale(original, Math.Max(1, w >> k), Math.Max(1, h >> k), cubic: true);
        int firstSide = Math.Max(cur.Width, cur.Height);
        var large = new List<(int, byte[])>();
        using var raw = new MemoryStream();
        int levels = 0, smallLevels = 0;
        uint color = 0;
        while (true)
        {
            levels++;
            int side = Math.Max(cur.Width, cur.Height);
            if (side > 256)
            {
                using var img = SKImage.FromBitmap(cur);
                using var data = img.Encode(format, format == SKEncodedImageFormat.Webp ? 90 : 100);
                large.Add((side, data.ToArray()));
            }
            else
            {
                smallLevels++;
                raw.Write(cur.GetPixelSpan());
            }
            if (cur.Width == 1 && cur.Height == 1)
            {
                var px = cur.GetPixelSpan();
                color = BinaryPrimitives.ReadUInt32LittleEndian(px);
                cur.Dispose();
                break;
            }
            var next = Scale(cur, Math.Max(1, cur.Width / 2), Math.Max(1, cur.Height / 2), cubic: false);
            cur.Dispose();
            cur = next;
        }
        var rawBytes = raw.ToArray();
        var comp = new byte[BrotliEncoder.GetMaxCompressedLength(rawBytes.Length)];
        if (!BrotliEncoder.TryCompress(rawBytes, comp, out int compLen, 4, 22)) throw new InvalidOperationException("brotli");
        var small = comp.AsSpan(0, compLen).ToArray();
        var fi = new FileInfo(path);
        var rel = Path.GetRelativePath(library, path);
        var header = Hdr.Build(w, h, firstSide, levels, smallLevels, color, rawBytes.Length, compLen,
            SHA256.HashData(rawBytes), rel, fi.Length, fi.LastWriteTimeUtc.Ticks);
        item = new ManifestItem(sha, rel, w, h, large.Select(l => l.Item1).ToArray(), small.Length + header.Length,
            large.Sum(l => (long)l.Item2.Length));
        return new Asset(sha, header, small, large);
    }

    public static List<ManifestItem> LoadManifest(Opts o) =>
        JsonSerializer.Deserialize<List<ManifestItem>>(File.ReadAllText(Path.Combine(o.PreparedDir, "manifest.json")))!;

    public static IEnumerable<Asset> ReadPack(Opts o)
    {
        using var fs = new FileStream(Path.Combine(o.PreparedDir, "pack.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var br = new BinaryReader(fs);
        while (fs.Position < fs.Length)
        {
            var sha = br.ReadString();
            var header = br.ReadBytes(br.ReadInt32());
            var small = br.ReadBytes(br.ReadInt32());
            int lc = br.ReadInt32();
            var large = new List<(int, byte[])>(lc);
            for (int i = 0; i < lc; i++) { int side = br.ReadInt32(); large.Add((side, br.ReadBytes(br.ReadInt32()))); }
            yield return new Asset(sha, header, small, large);
        }
    }
}

// ---------------------------------------------------------------- stores
interface IStore : IDisposable
{
    void Put(Asset a, long atime);
    void Flush();
    HashSet<string> EnumerateKeys();
    byte[]? ReadHeader(string sha);
    (byte[] Header, byte[] Payload)? ReadSmall(string sha);
    byte[]? ReadLarge(string sha, int side);
    void ApplyAtimes(IReadOnlyList<(string Sha, long Atime)> atimes);
    int Evict(int assets);
    JsonObject Footprint();
    int Retries { get; }
}

static class Keys
{
    public static string Small(string sha) => sha + ".s";
    public static string Large(string sha, int side) => sha + ".L" + side + ".img";
}

sealed class FileStore(string root) : IStore
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(root, "v1")).FullName;
    int _retries;
    public int Retries => _retries;

    string P(string key) => Path.Combine(_dir, key);

    void WriteAtomic(string key, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var path = P(key);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
        {
            fs.Write(prefix); fs.Write(a); fs.Write(b);
        }
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 200)
            {
                Interlocked.Increment(ref _retries); // a reader holds the target open
                Thread.Sleep(1);
            }
        }
    }

    public void Put(Asset a, long atime)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, a.Header.Length);
        WriteAtomic(Keys.Small(a.Sha), len, a.Header, a.Small);
        foreach (var (side, bytes) in a.Large) WriteAtomic(Keys.Large(a.Sha, side), default, bytes, default);
    }

    public void Flush() { }

    public HashSet<string> EnumerateKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_dir))
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".tmp", StringComparison.Ordinal)) { try { File.Delete(f); } catch { } continue; }
            keys.Add(name);
        }
        return keys;
    }

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);

    public byte[]? ReadHeader(string sha)
    {
        try
        {
            using var fs = OpenRead(P(Keys.Small(sha)));
            Span<byte> len = stackalloc byte[4];
            fs.ReadExactly(len);
            var h = new byte[BinaryPrimitives.ReadInt32LittleEndian(len)];
            fs.ReadExactly(h);
            return h;
        }
        catch (FileNotFoundException) { return null; }
    }

    static byte[] ReadAll(FileStream fs)
    {
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    public (byte[] Header, byte[] Payload)? ReadSmall(string sha)
    {
        try
        {
            using var fs = OpenRead(P(Keys.Small(sha)));
            var all = ReadAll(fs);
            int hl = BinaryPrimitives.ReadInt32LittleEndian(all);
            return (all.AsSpan(4, hl).ToArray(), all.AsSpan(4 + hl).ToArray());
        }
        catch (FileNotFoundException) { return null; }
    }

    public byte[]? ReadLarge(string sha, int side)
    {
        try { using var fs = OpenRead(P(Keys.Large(sha, side))); return ReadAll(fs); }
        catch (FileNotFoundException) { return null; }
    }

    // Files keep recency in LastWriteTime (NTFS last-access updates are usually off).
    public void ApplyAtimes(IReadOnlyList<(string Sha, long Atime)> atimes)
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (sha, t) in atimes) File.SetLastWriteTimeUtc(P(Keys.Small(sha)), baseTime.AddSeconds(t));
    }

    public int Evict(int assets)
    {
        var groups = new DirectoryInfo(_dir).EnumerateFiles()
            .GroupBy(f => f.Name[..f.Name.IndexOf('.')])
            .Select(g => (Files: g.ToList(), T: g.FirstOrDefault(f => f.Name.EndsWith(".s"))?.LastWriteTimeUtc ?? DateTime.MinValue))
            .OrderBy(g => g.T).Take(assets).ToList();
        foreach (var g in groups) foreach (var f in g.Files) f.Delete();
        return groups.Count;
    }

    public JsonObject Footprint()
    {
        long cluster = Native.ClusterSize(_dir), bytes = 0, alloc = 0;
        int count = 0;
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles())
        {
            count++; bytes += f.Length; alloc += (f.Length + cluster - 1) / cluster * cluster;
        }
        return new JsonObject { ["files"] = count, ["bytes"] = bytes, ["allocatedBytes"] = alloc, ["clusterSize"] = cluster };
    }

    public void Dispose() { }
}

sealed class SqliteStore : IStore
{
    readonly string _path;
    readonly int _batch;
    readonly SqliteConnection _c;
    readonly SqliteCommand _put, _hdr, _small, _large;
    SqliteTransaction? _tx;
    int _inTx;
    public int Retries => 0;
    public double OpenMs { get; }

    public SqliteStore(string dir, int batch)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "derived.db");
        _batch = batch;
        _c = new SqliteConnection($"Data Source={_path};Pooling=False");
        _c.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS entries(
              key TEXT PRIMARY KEY, sha TEXT NOT NULL, kind INTEGER NOT NULL, atime INTEGER NOT NULL,
              header BLOB, payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_sha ON entries(sha);
            CREATE INDEX IF NOT EXISTS ix_atime ON entries(atime) WHERE kind = 0;
            """);
        _put = Cmd("INSERT OR REPLACE INTO entries(key, sha, kind, atime, header, payload) VALUES($k, $s, $kind, $t, $h, $p)",
            "$k", "$s", "$kind", "$t", "$h", "$p");
        _hdr = Cmd("SELECT header FROM entries WHERE key = $k", "$k");
        _small = Cmd("SELECT header, payload FROM entries WHERE key = $k", "$k");
        _large = Cmd("SELECT payload FROM entries WHERE key = $k", "$k");
        OpenMs = sw.Elapsed.TotalMilliseconds;
    }

    public string DbPath => _path;

    void Exec(string sql) { using var c = _c.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }

    SqliteCommand Cmd(string sql, params string[] ps)
    {
        var c = _c.CreateCommand();
        c.CommandText = sql;
        foreach (var p in ps) c.Parameters.Add(new SqliteParameter(p, null));
        c.Prepare();
        return c;
    }

    void Insert(string key, string sha, int kind, long t, byte[]? h, byte[] p)
    {
        _put.Transaction = _tx;
        _put.Parameters["$k"].Value = key; _put.Parameters["$s"].Value = sha; _put.Parameters["$kind"].Value = kind;
        _put.Parameters["$t"].Value = t; _put.Parameters["$h"].Value = (object?)h ?? DBNull.Value; _put.Parameters["$p"].Value = p;
        _put.ExecuteNonQuery();
    }

    public void Put(Asset a, long atime)
    {
        _tx ??= _c.BeginTransaction();
        Insert(Keys.Small(a.Sha), a.Sha, 0, atime, a.Header, a.Small);
        foreach (var (side, bytes) in a.Large) Insert(Keys.Large(a.Sha, side), a.Sha, 1, atime, null, bytes);
        if (++_inTx >= _batch) Flush();
    }

    public void Flush()
    {
        if (_tx == null) return;
        _tx.Commit(); _tx.Dispose(); _tx = null; _inTx = 0;
    }

    public HashSet<string> EnumerateKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var c = _c.CreateCommand();
        c.CommandText = "SELECT key FROM entries";
        using var r = c.ExecuteReader();
        while (r.Read()) keys.Add(r.GetString(0));
        return keys;
    }

    static byte[]? One(SqliteCommand c, string key)
    {
        c.Parameters["$k"].Value = key;
        using var r = c.ExecuteReader();
        return r.Read() ? r.GetFieldValue<byte[]>(0) : null;
    }

    public byte[]? ReadHeader(string sha) => One(_hdr, Keys.Small(sha));

    public (byte[] Header, byte[] Payload)? ReadSmall(string sha)
    {
        _small.Parameters["$k"].Value = Keys.Small(sha);
        using var r = _small.ExecuteReader();
        return r.Read() ? (r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1)) : null;
    }

    public byte[]? ReadLarge(string sha, int side) => One(_large, Keys.Large(sha, side));

    public void ApplyAtimes(IReadOnlyList<(string Sha, long Atime)> atimes) { } // atime is written with the row

    public int Evict(int assets)
    {
        using var tx = _c.BeginTransaction();
        var shas = new List<string>();
        using (var q = _c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT sha FROM entries WHERE kind = 0 ORDER BY atime LIMIT $n";
            q.Parameters.AddWithValue("$n", assets);
            using var r = q.ExecuteReader();
            while (r.Read()) shas.Add(r.GetString(0));
        }
        using (var d = _c.CreateCommand())
        {
            d.Transaction = tx;
            d.CommandText = "DELETE FROM entries WHERE sha = $s";
            var p = d.Parameters.Add(new SqliteParameter("$s", null));
            foreach (var s in shas) { p.Value = s; d.ExecuteNonQuery(); }
        }
        tx.Commit();
        return shas.Count;
    }

    public double Checkpoint() { var sw = Stopwatch.StartNew(); Exec("PRAGMA wal_checkpoint(TRUNCATE);"); return sw.Elapsed.TotalMilliseconds; }
    public double Vacuum() { var sw = Stopwatch.StartNew(); Exec("VACUUM;"); return sw.Elapsed.TotalMilliseconds; }

    long Scalar(string sql) { using var c = _c.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }

    public JsonObject Footprint()
    {
        long cluster = Native.ClusterSize(Path.GetDirectoryName(_path)!), bytes = 0, alloc = 0;
        int count = 0;
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }.Where(File.Exists).Select(f => new FileInfo(f)))
        {
            count++; bytes += f.Length; alloc += (f.Length + cluster - 1) / cluster * cluster;
        }
        long page = Scalar("PRAGMA page_size");
        return new JsonObject
        {
            ["files"] = count,
            ["bytes"] = bytes,
            ["allocatedBytes"] = alloc,
            ["clusterSize"] = cluster,
            ["freelistBytes"] = Scalar("PRAGMA freelist_count") * page,
            ["pageSize"] = page,
        };
    }

    public void Dispose()
    {
        Flush();
        _put.Dispose(); _hdr.Dispose(); _small.Dispose(); _large.Dispose();
        _c.Dispose();
    }
}

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint free, out uint total);

    public static long ClusterSize(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        return GetDiskFreeSpaceW(root, out var spc, out var bps, out _, out _) ? (long)spc * bps : 4096;
    }
}

// ---------------------------------------------------------------- stats
static class Stats
{
    public static JsonObject Of(List<double> ms, long bytes = -1)
    {
        var s = ms.OrderBy(x => x).ToArray();
        var o = new JsonObject { ["count"] = s.Length };
        if (s.Length == 0) return o;
        double total = s.Sum();
        o["medianMs"] = R(s[s.Length / 2]);
        o["p99Ms"] = R(s[Math.Max(0, (int)Math.Ceiling(0.99 * s.Length) - 1)]);
        o["maxMs"] = R(s[^1]);
        o["meanMs"] = R(total / s.Length);
        o["sumMs"] = R(total);
        if (bytes >= 0) { o["bytes"] = bytes; o["mbPerSec"] = R(bytes / 1048576.0 / Math.Max(total / 1000, 1e-9)); }
        return o;
    }

    public static double R(double v) => Math.Round(v, 3);
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

// ---------------------------------------------------------------- child phases
static class Child
{
    static IStore Open(Opts o, string store) => store switch
    {
        "files" => new FileStore(Path.Combine(o.StoresDir, "files")),
        "sqlite" => new SqliteStore(Path.Combine(o.StoresDir, "sqlite"), 1),
        "sqlite-batch" => new SqliteStore(Path.Combine(o.StoresDir, "sqlite-batch"), o.Batch),
        _ => throw new ArgumentException(store),
    };

    public static int Run(Opts o)
    {
        var result = new JsonObject { ["phase"] = o.Child, ["store"] = o.Store, ["run"] = o.Run };
        var total = Stopwatch.StartNew();
        switch (o.Child)
        {
            case "build": Build(o, result); break;
            case "startup": Startup(o, result); break;
            case "read": Read(o, result); break;
            case "concurrent": Concurrent(o, result); break;
            case "evict": Evict(o, result); break;
            default: throw new ArgumentException(o.Child);
        }
        result["phaseWallMs"] = Stats.R(total.Elapsed.TotalMilliseconds);
        Directory.CreateDirectory(o.ResultsDir);
        File.WriteAllText(Path.Combine(o.ResultsDir, $"{o.Child}-{o.Store}-{o.Run}.json"), result.ToJsonString());
        return 0;
    }

    static List<(string, long)> Atimes(List<ManifestItem> m, int seed)
    {
        var perm = Enumerable.Range(0, m.Count).ToArray();
        new Random(seed + 7).Shuffle(perm);
        return m.Select((x, i) => (x.Sha, (long)perm[i])).ToList();
    }

    static void Build(Opts o, JsonObject res)
    {
        var dir = Path.Combine(o.StoresDir, o.Store);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var m = Prepare.LoadManifest(o);
        var atimes = Atimes(m, o.Seed).ToDictionary(x => x.Item1, x => x.Item2);
        var lat = new List<double>();
        long bytes = 0, entries = 0;
        var wall = Stopwatch.StartNew();
        long pureTicks = 0;
        using (var s = Open(o, o.Store))
        {
            foreach (var a in Prepare.ReadPack(o)) // pack read is excluded from per-asset latency
            {
                long t0 = Stopwatch.GetTimestamp();
                s.Put(a, atimes[a.Sha]);
                long dt = Stopwatch.GetTimestamp() - t0;
                pureTicks += dt;
                lat.Add(Stats.Ms(dt));
                bytes += 4 + a.Header.Length + a.Small.Length + a.Large.Sum(l => l.Bytes.Length);
                entries += 1 + a.Large.Count;
            }
            long f0 = Stopwatch.GetTimestamp();
            s.Flush();
            long fdt = Stopwatch.GetTimestamp() - f0;
            pureTicks += fdt;
            res["finalCommitMs"] = Stats.R(Stats.Ms(fdt));
            s.ApplyAtimes(atimes.Select(kv => (kv.Key, kv.Value)).ToList()); // untimed setup for LRU
            res["footprint"] = s.Footprint();
        }
        res["perAsset"] = Stats.Of(lat, bytes);
        res["entries"] = entries;
        res["writeSeconds"] = Stats.R(Stats.Ms(pureTicks) / 1000);
        res["mbPerSec"] = Stats.R(bytes / 1048576.0 / (Stats.Ms(pureTicks) / 1000));
        res["wallSecondsInclPackRead"] = Stats.R(wall.Elapsed.TotalSeconds);
    }

    static void Startup(Opts o, JsonObject res)
    {
        var m = Prepare.LoadManifest(o); // untimed
        var sw = Stopwatch.StartNew();
        using var s = Open(o, o.Store);
        double openMs = sw.Elapsed.TotalMilliseconds;
        var keys = s.EnumerateKeys();
        double enumMs = sw.Elapsed.TotalMilliseconds - openMs;
        int missing = 0;
        foreach (var it in m)
        {
            if (!keys.Contains(Keys.Small(it.Sha))) missing++;
            foreach (var side in it.Large) if (!keys.Contains(Keys.Large(it.Sha, side))) missing++;
        }
        res["openMs"] = Stats.R(openMs);
        res["enumerateMs"] = Stats.R(enumMs);
        res["totalMs"] = Stats.R(sw.Elapsed.TotalMilliseconds);
        res["keys"] = keys.Count;
        res["missing"] = missing;
    }

    static void Read(Opts o, JsonObject res)
    {
        var m = Prepare.LoadManifest(o);
        var rnd = new Random(o.Seed + 1);
        using var s = Open(o, o.Store);
        var order = m.ToArray(); rnd.Shuffle(order);
        var lat = new List<double>(); long bytes = 0; int bad = 0;
        foreach (var it in order)
        {
            long t0 = Stopwatch.GetTimestamp();
            var h = s.ReadHeader(it.Sha);
            lat.Add(Stats.Ms(Stopwatch.GetTimestamp() - t0));
            if (h == null || !Hdr.Valid(h)) bad++; else bytes += h.Length;
        }
        res["headers"] = Stats.Of(lat, bytes);

        rnd.Shuffle(order);
        var io = new List<double>(); var full = new List<double>(); bytes = 0;
        foreach (var it in order.Take(Math.Max(1, m.Count * 30 / 100)))
        {
            long t0 = Stopwatch.GetTimestamp();
            var r = s.ReadSmall(it.Sha);
            long t1 = Stopwatch.GetTimestamp();
            bool ok = r != null && Hdr.DecodeSmall(r.Value.Header, r.Value.Payload);
            long t2 = Stopwatch.GetTimestamp();
            io.Add(Stats.Ms(t1 - t0)); full.Add(Stats.Ms(t2 - t0));
            if (!ok) bad++; else bytes += r!.Value.Header.Length + r.Value.Payload.Length;
        }
        res["smallIo"] = Stats.Of(io, bytes);
        res["smallIoBrotliSha"] = Stats.Of(full, bytes);

        rnd.Shuffle(order);
        lat = new List<double>(); bytes = 0;
        foreach (var it in order.Where(x => x.Large.Length > 0).Take(Math.Max(1, m.Count * 5 / 100)))
        {
            int side = it.Large[rnd.Next(it.Large.Length)];
            long t0 = Stopwatch.GetTimestamp();
            var b = s.ReadLarge(it.Sha, side);
            lat.Add(Stats.Ms(Stopwatch.GetTimestamp() - t0));
            if (b == null) bad++; else bytes += b.Length;
        }
        res["large"] = Stats.Of(lat, bytes);
        res["errors"] = bad;
    }

    static void Concurrent(Opts o, JsonObject res)
    {
        var m = Prepare.LoadManifest(o);
        int w = Math.Clamp(m.Count / 10, 10, 200);
        var rewrite = Prepare.ReadPack(o).Take(w).ToList(); // rewrites existing keys with identical bytes
        var atimes = Atimes(m, o.Seed).ToDictionary(x => x.Item1, x => x.Item2);
        using var writerStore = Open(o, o.Store);
        using var readerStore = Open(o, o.Store);
        var wl = new List<double>(); var hl = new List<double>(); var sl = new List<double>();
        int bad = 0; long readBytes = 0;
        using var go = new ManualResetEventSlim();
        bool done = false;
        var writer = new Thread(() =>
        {
            go.Wait();
            foreach (var a in rewrite)
            {
                long t0 = Stopwatch.GetTimestamp();
                writerStore.Put(a, atimes[a.Sha]);
                wl.Add(Stats.Ms(Stopwatch.GetTimestamp() - t0));
            }
            writerStore.Flush();
            Volatile.Write(ref done, true);
        });
        var reader = new Thread(() =>
        {
            var rnd = new Random(o.Seed + 3);
            // Half the reads target the assets being rewritten, half the rest of the store.
            go.Wait();
            while (!Volatile.Read(ref done))
            {
                var sha = rnd.Next(2) == 0 ? rewrite[rnd.Next(rewrite.Count)].Sha : m[rnd.Next(m.Count)].Sha;
                long t0 = Stopwatch.GetTimestamp();
                var h = readerStore.ReadHeader(sha);
                long t1 = Stopwatch.GetTimestamp();
                var r = readerStore.ReadSmall(sha);
                bool ok = h != null && r != null && Hdr.DecodeSmall(r.Value.Header, r.Value.Payload);
                long t2 = Stopwatch.GetTimestamp();
                hl.Add(Stats.Ms(t1 - t0)); sl.Add(Stats.Ms(t2 - t1));
                if (!ok) bad++; else readBytes += r!.Value.Payload.Length;
            }
        });
        writer.Start(); reader.Start(); go.Set();
        writer.Join(); reader.Join();
        res["rewrittenAssets"] = w;
        res["writerPerAsset"] = Stats.Of(wl);
        res["readerHeader"] = Stats.Of(hl);
        res["readerSmallIoBrotliSha"] = Stats.Of(sl, readBytes);
        res["readerErrors"] = bad;
        res["writerRenameRetries"] = writerStore.Retries;
    }

    static void Evict(Opts o, JsonObject res)
    {
        var m = Prepare.LoadManifest(o);
        int k = Math.Max(1, m.Count * 20 / 100);
        using var s = Open(o, o.Store);
        res["footprintBefore"] = s.Footprint();
        var sw = Stopwatch.StartNew();
        int evicted = s.Evict(k);
        res["evictMs"] = Stats.R(sw.Elapsed.TotalMilliseconds);
        res["evictedAssets"] = evicted;
        if (s is SqliteStore q)
        {
            res["footprintAfterDelete"] = q.Footprint();
            res["checkpointMs"] = Stats.R(q.Checkpoint());
            res["footprintAfterCheckpoint"] = q.Footprint();
            res["vacuumMs"] = Stats.R(q.Vacuum());
            q.Checkpoint();
            res["footprintAfterVacuum"] = q.Footprint();
        }
        else res["footprintAfterDelete"] = s.Footprint();
    }
}

// ---------------------------------------------------------------- orchestration
static class Orchestrator
{
    public static int Run(Opts o)
    {
        Directory.CreateDirectory(o.Out);
        Prepare.Ensure(o);
        if (Directory.Exists(o.ResultsDir)) Directory.Delete(o.ResultsDir, true);
        var stores = o.Mode switch
        {
            "files" => new[] { "files" },
            "sqlite" => ["sqlite", "sqlite-batch"],
            _ => ["files", "sqlite", "sqlite-batch"],
        };
        var readStores = stores.Where(s => s != "sqlite-batch").ToArray();
        var wall = new Dictionary<string, List<double>>();

        foreach (var s in stores) Spawn(o, "build", s, 0, wall);
        foreach (var s in readStores)
            for (int i = 0; i < o.StartupRuns; i++) Spawn(o, "startup", s, i, wall);
        foreach (var s in readStores) Spawn(o, "read", s, 0, wall);
        foreach (var s in readStores) Spawn(o, "concurrent", s, 0, wall);
        foreach (var s in readStores) Spawn(o, "evict", s, 0, wall);

        var all = new JsonObject
        {
            ["n"] = o.N,
            ["seed"] = o.Seed,
            ["largeFormat"] = o.LargeFormat,
            ["batch"] = o.Batch,
            ["date"] = DateTime.Now.ToString("s"),
            ["processorCount"] = Environment.ProcessorCount,
        };
        var results = new JsonObject();
        foreach (var f in Directory.EnumerateFiles(o.ResultsDir, "*.json").OrderBy(x => x))
            results[Path.GetFileNameWithoutExtension(f)] = JsonNode.Parse(File.ReadAllText(f));
        all["results"] = results;
        all["childWallMs"] = new JsonObject(wall.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Stats.Of(kv.Value))));
        var jsonPath = Path.Combine(o.Out, "results.json");
        File.WriteAllText(jsonPath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Table(o, readStores, stores, results, wall);
        Console.WriteLine($"\nJSON: {jsonPath}");

        if (!o.KeepStores && Directory.Exists(o.StoresDir)) Directory.Delete(o.StoresDir, true);
        return 0;
    }

    static void Spawn(Opts o, string phase, string store, int run, Dictionary<string, List<double>> wall)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var a in new[] { "--child", phase, "--store", store, "--run", run.ToString(), "--n", o.N.ToString(),
                     "--out", o.Out, "--sample-seed", o.Seed.ToString(), "--large-format", o.LargeFormat,
                     "--batch", o.Batch.ToString(), "--library", o.Library })
            psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{phase}/{store} failed with {p.ExitCode}");
        var key = phase + "-" + store;
        if (!wall.TryGetValue(key, out var l)) wall[key] = l = [];
        l.Add(sw.Elapsed.TotalMilliseconds);
        Console.WriteLine($"  {phase,-10} {store,-13} run {run}: {sw.Elapsed.TotalMilliseconds,8:F0} ms (child wall)");
    }

    static void Table(Opts o, string[] readStores, string[] stores, JsonObject r, Dictionary<string, List<double>> wall)
    {
        JsonNode? G(string file, params string[] path)
        {
            JsonNode? n = r[file];
            foreach (var p in path) n = n?[p];
            return n;
        }
        string Lat(JsonNode? s) => s == null ? "-" : $"{s["medianMs"]} / {s["p99Ms"]} / {s["maxMs"]}";
        string Mb(JsonNode? s) => s?["mbPerSec"]?.ToString() ?? "-";
        string MiB(JsonNode? v) => v == null ? "-" : $"{v.GetValue<long>() / 1048576.0:F1}";
        var rows = new List<(string, Func<string, string>)>
        {
            ("build per asset ms med/p99/max", s => Lat(G($"build-{s}-0", "perAsset"))),
            ("build MB/s", s => G($"build-{s}-0", "mbPerSec")?.ToString() ?? "-"),
            ("build footprint MiB alloc / files", s => $"{MiB(G($"build-{s}-0", "footprint", "allocatedBytes"))} / {G($"build-{s}-0", "footprint", "files")}"),
            ("startup open+enum ms med/max", s =>
            {
                var t = Enumerable.Range(0, o.StartupRuns).Select(i => G($"startup-{s}-{i}", "totalMs")?.GetValue<double>()).Where(x => x != null).Select(x => x!.Value).OrderBy(x => x).ToArray();
                return t.Length == 0 ? "-" : $"{t[t.Length / 2]:F1} / {t[^1]:F1}";
            }),
            ("startup child process wall ms med", s => wall.TryGetValue("startup-" + s, out var l) ? $"{l.OrderBy(x => x).ElementAt(l.Count / 2):F0}" : "-"),
            ("headers (all N) ms med/p99/max", s => Lat(G($"read-{s}-0", "headers"))),
            ("headers sum ms", s => G($"read-{s}-0", "headers", "sumMs")?.ToString() ?? "-"),
            ("small 30% io ms med/p99/max", s => Lat(G($"read-{s}-0", "smallIo"))),
            ("small io MB/s", s => Mb(G($"read-{s}-0", "smallIo"))),
            ("small io+brotli+sha ms med/p99/max", s => Lat(G($"read-{s}-0", "smallIoBrotliSha"))),
            ("large 5% ms med/p99/max", s => Lat(G($"read-{s}-0", "large"))),
            ("large MB/s", s => Mb(G($"read-{s}-0", "large"))),
            ("concurrent: reader header med/p99/max", s => Lat(G($"concurrent-{s}-0", "readerHeader"))),
            ("concurrent: reader small med/p99/max", s => Lat(G($"concurrent-{s}-0", "readerSmallIoBrotliSha"))),
            ("concurrent: writer asset med/p99/max", s => Lat(G($"concurrent-{s}-0", "writerPerAsset"))),
            ("concurrent: errors / rename retries", s => $"{G($"concurrent-{s}-0", "readerErrors")} / {G($"concurrent-{s}-0", "writerRenameRetries")}"),
            ("evict 20% LRU ms", s => G($"evict-{s}-0", "evictMs")?.ToString() ?? "-"),
            ("after evict MiB alloc / files", s => $"{MiB(G($"evict-{s}-0", "footprintAfterDelete", "allocatedBytes"))} / {G($"evict-{s}-0", "footprintAfterDelete", "files")}"),
            ("sqlite: checkpoint / vacuum ms", s => s == "files" ? "-" : $"{G($"evict-{s}-0", "checkpointMs")} / {G($"evict-{s}-0", "vacuumMs")}"),
            ("sqlite: MiB after checkpoint / vacuum", s => s == "files" ? "-" : $"{MiB(G($"evict-{s}-0", "footprintAfterCheckpoint", "allocatedBytes"))} / {MiB(G($"evict-{s}-0", "footprintAfterVacuum", "allocatedBytes"))}"),
        };
        Console.WriteLine();
        Console.WriteLine($"{"metric (N=" + o.N + ")",-40}" + string.Concat(stores.Select(s => $"{s,-26}")));
        foreach (var (name, f) in rows)
            Console.WriteLine($"{name,-40}" + string.Concat(stores.Select(s => $"{(readStores.Contains(s) || name.StartsWith("build") ? f(s) : "-"),-26}")));
    }
}
