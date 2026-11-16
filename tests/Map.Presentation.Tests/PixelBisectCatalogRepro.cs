using System.Reflection;
using System.Security.Cryptography;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// Pixel-bisect harness with the production image source (not a normal test): runs only when MAP_PIXREPRO_OUT
/// is set. A catalog of distinct images made from the owner's mountain fixtures (kept between runs in
/// MAP_PIXREPRO_CATALOG), a fresh derived cache per run, the canvas's frame protocol (SetFrameReady, BeginFrame,
/// overview cache, background preparation when the build has it) until settled. Only a5a41b5 APIs or reflection.
/// </summary>
public sealed class PixelBisectCatalogRepro
{
    private const int Canvas = 32768;
    private const int ScreenW = 2048, ScreenH = 1095;
    private static int Objects => int.TryParse(Environment.GetEnvironmentVariable("MAP_PIXREPRO_OBJECTS"), out var n) ? n : 500_000;
    private static int AssetCount => int.TryParse(Environment.GetEnvironmentVariable("MAP_PIXREPRO_ASSETS"), out var n) ? n : 1800;
    // Exact (>= 4 screen px) objects at the fit view; 3 % (15 000) kept the exact-object pixel queue from converging.
    private static double BigShare => double.TryParse(Environment.GetEnvironmentVariable("MAP_PIXREPRO_BIG"),
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.003;
    private static string? Out => Environment.GetEnvironmentVariable("MAP_PIXREPRO_OUT");
    private static string Label => Environment.GetEnvironmentVariable("MAP_PIXREPRO_LABEL") ?? "run";

    [OptInFact("500k-stamp/1800-owner-image pixel-bisect dump; comparison is a separate manual step", "MAP_PIXREPRO_OUT")]
    public async Task Render_catalog_scenarios()
    {
        var outDir = Out ?? throw new InvalidOperationException("MAP_PIXREPRO_OUT is required.");
        Directory.CreateDirectory(outDir);
        var catalogRoot = Environment.GetEnvironmentVariable("MAP_PIXREPRO_CATALOG") ?? Path.Combine(outDir, "catalog");
        var derivedRoot = Path.Combine(outDir, "derived-" + Label);
        if (Directory.Exists(derivedRoot)) Directory.Delete(derivedRoot, true);
        var log = new List<string>();
        await using var catalog = new AssetCatalog(Path.Combine(catalogRoot, "catalog"));
        var records = EnsureAssets(catalog, catalogRoot);
        var document = BuildDocument(records);
        var current = document;
        var layerId = document.Layers.OfType<ObjectLayer>().Single().Id;
        var scale = ScreenH * 0.95 / Canvas;
        var offsetX = -(ScreenW / scale - Canvas) / 2;
        var offsetY = -(ScreenH / scale - Canvas) / 2;
        try
        {
            await using (var source = new CatalogStampImageSource(catalog, () => current, () => null, derivedRoot: derivedRoot))
            using (var frame = new FrameHarness(source, scale, offsetX, offsetY))
            {
                var (pixels, frames) = frame.Settle(document, null, null);
                Save(outDir, "fit", pixels, log, frames);
                Save(outDir, "fit-exact", ExactReference(document, source, scale, offsetX, offsetY), log, 1);
                var session = new EditingSession(document);
                var layer = (ObjectLayer)session.Document.FindLayer(layerId)!;
                var picked = Enumerable.Range(0, 25).Select(i => layer.Objects[(int)((i * 19_997L + 1234) % layer.Objects.Count)]).ToArray();
                frame.Settle(session.Document, picked[0].Id, null);
                var moved = picked.Select(o => SceneObjectClone.At(o, Math.Clamp(o.X + 3500, 0, Canvas - o.Width), Math.Clamp(o.Y - 2100, 0, Canvas - o.Height))).ToArray();
                frame.Frames(session.Document, picked[0].Id, moved, 3);
                session.Execute(new ReplaceObjectsCommand(picked, moved, "Drag"));
                current = session.Document; frame.Prepare(current);
                frame.Settle(current, picked[0].Id, null);
                var reference = session.Document.Assets[7];
                var placed = Enumerable.Range(0, 10).Select(i => Stamp(new Random(900 + i), reference.Sha256, 9000 + i * 900, 15000 + i * 300, 3)).ToArray();
                session.Execute(new PlaceAssetObjectsCommand(layerId, reference, placed));
                current = session.Document; frame.Prepare(current); frame.Settle(current, null, null);
                session.Undo(); current = session.Document; frame.Prepare(current); frame.Settle(current, null, null);
                session.Redo(); current = session.Document; frame.Prepare(current);
                (pixels, frames) = frame.Settle(current, null, null);
                Save(outDir, "edit", pixels, log, frames);
                Save(outDir, "edit-exact", ExactReference(current, source, scale, offsetX, offsetY), log, 1);
                log.Add(frame.Report);
            }
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, $"{Label}.txt"), log);
            try { Directory.Delete(derivedRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Compare_catalog_to_base()
    {
        if (Out is not { } outDir || Environment.GetEnvironmentVariable("MAP_PIXREPRO_BASE") is not { } baseLabel) return;
        var lines = new List<string>();
        var pairs = new[] { "fit", "edit" }.Select(n => (n, $"{baseLabel}-{n}", $"{Label}-{n}"))
            .Concat(new[] { "fit", "edit" }.Select(n => ($"{n} vs own exact", $"{Label}-{n}-exact", $"{Label}-{n}")))
            .Concat(new[] { "fit", "edit" }.Select(n => ($"{n} base vs base exact", $"{baseLabel}-{n}-exact", $"{baseLabel}-{n}")))
            .Concat(new[] { "fit", "edit" }.Select(n => ($"{n} exact vs exact", $"{baseLabel}-{n}-exact", $"{Label}-{n}-exact")));
        foreach (var (name, left, right) in pairs)
        {
            var a = Path.Combine(outDir, $"{left}.png");
            var b = Path.Combine(outDir, $"{right}.png");
            if (!File.Exists(a) || !File.Exists(b)) { lines.Add($"{name}: missing"); continue; }
            using var ba = SKBitmap.Decode(a);
            using var bb = SKBitmap.Decode(b);
            int max = 0, over2 = 0, differ = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            using var diff = new SKBitmap(ba.Width, ba.Height);
            for (var y = 0; y < ba.Height; y++)
                for (var x = 0; x < ba.Width; x++)
                {
                    var p = ba.GetPixel(x, y); var q = bb.GetPixel(x, y);
                    var d = Math.Max(Math.Max(Math.Abs(p.Red - q.Red), Math.Abs(p.Green - q.Green)),
                        Math.Max(Math.Abs(p.Blue - q.Blue), Math.Abs(p.Alpha - q.Alpha)));
                    if (d > 0) { differ++; minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
                    if (d > 2) over2++;
                    max = Math.Max(max, d);
                    var v = (byte)Math.Min(255, d * 8);
                    diff.SetPixel(x, y, d == 0 ? new SKColor(0, 0, 0) : new SKColor(255, (byte)(255 - v), 0));
                }
            using (var file = File.Create(Path.Combine(outDir, $"diff-{left}-vs-{right}.png")))
                diff.Encode(file, SKEncodedImageFormat.Png, 100);
            lines.Add($"{name}: max {max}/255, differing {differ}, >2/255 {over2}, bbox {(differ > 0 ? $"{minX},{minY}-{maxX},{maxY}" : "-")}");
        }
        File.WriteAllLines(Path.Combine(outDir, $"compare-{baseLabel}-vs-{Label}.txt"), lines);
    }

    private static AssetRecord[] EnsureAssets(AssetCatalog catalog, string root)
    {
        var list = Path.Combine(root, "records.txt");
        if (File.Exists(list))
        {
            var known = File.ReadAllLines(list).Select(sha => catalog.Find(sha)!).ToArray();
            if (known.Length == AssetCount && known.All(r => r is not null)) return known;
        }
        var fixtures = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixtures is not null && !File.Exists(Path.Combine(fixtures.FullName, "Map.slnx"))) fixtures = fixtures.Parent;
        var bases = Enumerable.Range(1, 6).Select(i => SKBitmap.Decode(Path.Combine(fixtures!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"))).ToArray();
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var random = new Random(4242);
        var records = new AssetRecord[AssetCount];
        for (var i = 0; i < AssetCount; i++)
        {
            var source = bases[i % bases.Length];
            var side = 220 + random.Next(420);
            var w = side; var h = Math.Max(16, (int)(side * source.Height / (double)source.Width));
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))!;
            surface.Canvas.Clear(SKColors.Transparent);
            var shift = (float)(random.NextDouble() * 0.5 - 0.25);
            var bright = (float)(random.NextDouble() * 0.3 - 0.15);
            using var filter = SKColorFilter.CreateColorMatrix([
                1 + shift, 0.1f * shift, 0, 0, bright,
                0, 1 - shift * 0.5f, 0.1f * shift, 0, bright,
                0.1f * shift, 0, 1 + shift * 0.3f, 0, bright,
                0, 0, 0, 1, 0]);
            using var paint = new SKPaint { ColorFilter = filter };
            using var image = SKImage.FromBitmap(source);
            surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            // A unique marker pixel keeps every file's content (and SHA) distinct.
            using var marker = new SKPaint { Color = new SKColor((byte)(i & 255), (byte)(i >> 8), 77, 255) };
            surface.Canvas.DrawPoint(0, 0, marker);
            using var snapshot = surface.Snapshot();
            var path = Path.Combine(staging, $"a{i:0000}.png");
            using (var file = File.Create(path)) snapshot.Encode(SKEncodedImageFormat.Png, 100).SaveTo(file);
            records[i] = catalog.Import(path, new(), DateTimeOffset.UnixEpoch);
            File.Delete(path);
        }
        foreach (var b in bases) b.Dispose();
        File.WriteAllLines(list, records.Select(r => r.Sha256));
        return records;
    }

    private static void Save(string dir, string name, byte[] png, List<string> log, int frames)
    {
        File.WriteAllBytes(Path.Combine(dir, $"{Label}-{name}.png"), png);
        using var bitmap = SKBitmap.Decode(png);
        var hash = Convert.ToHexString(SHA256.HashData(bitmap.Bytes))[..16];
        log.Add($"{name}: pixels-sha256 {hash} frames {frames}");
    }

    /// <summary>
    /// Every image resolved at its exact size: a synchronous overview render (fresh cache, no refinement) off the
    /// frame thread through a lease, which decodes what it asks for instead of drawing a resident fallback.
    /// </summary>
    private static byte[] ExactReference(MapDocument document, CatalogStampImageSource source, double scale, double ox, double oy) =>
        Task.Run(() =>
        {
            // The exact lease draws only the pixels a settled frame draws; wait until none is preparing.
            for (var attempt = 0; ; attempt++)
            {
                var png = ExactReferenceOnce(document, source, scale, ox, oy, out var preparing);
                if (preparing == 0 || attempt >= 600) return png;
                Thread.Sleep(100);
            }
        }).GetAwaiter().GetResult();

    private static byte[] ExactReferenceOnce(MapDocument document, CatalogStampImageSource source, double scale, double ox, double oy,
        out int preparing)
    {
            using var lease = source.ExactLease();
            using var cache = new ObjectOverviewCache(1024, 256L * 1024 * 1024);
            using var surface = SKSurface.Create(new SKImageInfo(ScreenW, ScreenH, SKColorType.Rgba8888, SKAlphaType.Premul))!;
            surface.Canvas.Clear(SKColors.Black);
            surface.Canvas.Scale((float)scale);
            surface.Canvas.Translate((float)-ox, (float)-oy);
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = lease, ObjectCache = cache, ViewScale = scale, LodTier = RenderLodTier.Overview,
                    VisibleWorld = new(ox, oy, ox + ScreenW / scale, oy + ScreenH / scale),
                });
            }
            finally { cache.EndFrame(); }
            preparing = lease.PreparingLookups;
            using var image = surface.Snapshot();
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed class FrameHarness : IDisposable
    {
        private readonly CatalogStampImageSource _source;
        private readonly double _scale, _ox, _oy;
        private readonly ObjectOverviewCache _cache = new(1024, 256L * 1024 * 1024);
        private readonly object? _preparation;
        private static readonly Type? PreparationType = typeof(SceneRenderer).GetNestedType("OverviewPreparation", BindingFlags.Public);
        private static readonly PropertyInfo? PreparationOption = typeof(RenderOptions).GetProperty("OverviewPreparation");
        private volatile bool _requested;
        private int _frames, _unresolvedFrames;
        public string Report => $"frames {_frames}, frames with unresolved images {_unresolvedFrames}";

        public FrameHarness(CatalogStampImageSource source, double scale, double ox, double oy)
        {
            _source = source; _scale = scale; _ox = ox; _oy = oy;
            if (PreparationType is not null && Environment.GetEnvironmentVariable("MAP_PIXREPRO_NOPREP") != "1")
                // Newer builds take an optional interaction probe: pass its default explicitly.
                _preparation = PreparationType.GetConstructor(Type.EmptyTypes) is not null
                    ? Activator.CreateInstance(PreparationType)
                    : Activator.CreateInstance(PreparationType, [null]);
        }

        public void Prepare(MapDocument document) =>
            PreparationType?.GetMethod("UpdateDocument")?.Invoke(_preparation, [document, _source, (Action)(() => _requested = true)]);

        private bool Idle()
        {
            if (_preparation is null) return true;
            var stats = PreparationType!.GetProperty("Statistics")!.GetValue(_preparation)!;
            var fields = stats.GetType().GetFields();
            return (int)fields[2].GetValue(stats)! == 0 && (bool)fields[4].GetValue(stats)!;
        }

        public (byte[] Png, int Frames) Settle(MapDocument document, ObjectId? selected, IReadOnlyList<SceneObject>? overrides)
        {
            Prepare(document);
            var quiet = 0;
            var deadline = DateTime.UtcNow.AddMinutes(25);
            for (var frame = 1; DateTime.UtcNow < deadline; frame++)
            {
                _requested = false;
                var (png, stats) = Draw(document, selected, overrides, encode: quiet >= 2);
                var settled = stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !_source.ImageRequestsPending && Idle();
                if (stats.UnresolvedImages > 0) _unresolvedFrames++;
                if (frame % 100 == 1 && Out is { } progress)
                    File.AppendAllText(Path.Combine(progress, $"{Label}-progress.txt"),
                        $"{DateTime.Now:HH:mm:ss} frame {frame} pending {stats.DetailTilesPending} unresolved {stats.UnresolvedImages} requests {_source.ImageRequestsPending} idle {Idle()} chunks {stats.OverviewChunks} exact {stats.ExactObjects} missing {stats.MissingAssets} mips {_source.DerivedStatistics.PendingMips} cache {_source.CacheStatistics.Entries}/{_source.CacheStatistics.Evictions}{Environment.NewLine}");
                quiet = settled ? quiet + 1 : 0;
                if (quiet >= 3) return (png, frame);
                Thread.Sleep(settled ? 1 : 30);
            }
            throw new InvalidOperationException("Overview did not settle in 25 minutes.");
        }

        public void Frames(MapDocument document, ObjectId? selected, IReadOnlyList<SceneObject>? overrides, int count)
        {
            for (var i = 0; i < count; i++) Draw(document, selected, overrides);
        }

        private (byte[] Png, RenderStats Stats) Draw(MapDocument document, ObjectId? selected, IReadOnlyList<SceneObject>? overrides,
            bool encode = false)
        {
            _frames++;
            _source.SetFrameReady(() => _requested = true);
            using var stampFrame = _source.BeginFrame();
            using var surface = SKSurface.Create(new SKImageInfo(ScreenW, ScreenH, SKColorType.Rgba8888, SKAlphaType.Premul))!;
            surface.Canvas.Clear(SKColors.Black);
            surface.Canvas.Scale((float)_scale);
            surface.Canvas.Translate((float)-_ox, (float)-_oy);
            var stats = new RenderStats();
            _cache.BeginFrame();
            try
            {
                var options = new RenderOptions
                {
                    Stamps = _source, ObjectCache = _cache, Stats = stats, ViewScale = _scale,
                    LodTier = RenderLodTier.Overview, SelectedObjectId = selected, ObjectOverrides = overrides,
                    VisibleWorld = new(_ox, _oy, _ox + ScreenW / _scale, _oy + ScreenH / _scale),
                    RequestRefinedFrame = () => _requested = true,
                };
                if (_preparation is not null) PreparationOption!.SetValue(options, _preparation);
                SceneRenderer.Render(surface.Canvas, document, options);
            }
            finally { _cache.EndFrame(); }
            if (!encode) return ([], stats);
            using var image = surface.Snapshot();
            return (image.Encode(SKEncodedImageFormat.Png, 100).ToArray(), stats);
        }

        public void Dispose()
        {
            (_preparation as IDisposable)?.Dispose();
            _cache.Dispose();
        }
    }

    private static MapDocument BuildDocument(AssetRecord[] records)
    {
        var random = new Random(20261114);
        var objects = new SceneObject[Objects];
        for (var i = 0; i < Objects; i++)
            objects[i] = Stamp(random, records[random.Next(records.Length)].Sha256, random.NextDouble() * Canvas, random.NextDouble() * Canvas, random.Next(4));
        var assets = records.Select(r => new MapAssetReference(r.Sha256, r.DisplayName, r.MediaType, r.Extension,
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: r.Sha256)).ToArray();
        return new MapDocument(new DocumentId(new Guid(Bytes(new Random(1)))), "Pixel bisect", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Canvas, Canvas, "px", "#FFF3EBDD"),
            [new BaseColorLayer(new LayerId(new Guid(Bytes(new Random(2)))), "Page", true, false, 1, "#FFF3EBDD"),
             new ObjectLayer(new LayerId(new Guid(Bytes(new Random(3)))), "Горы", true, false, 1, objects)],
            assets: assets);
    }

    private static byte[] Bytes(Random random) { var b = new byte[16]; random.NextBytes(b); return b; }

    private static SceneObject Stamp(Random random, string sha, double x, double y, int sublayer)
    {
        var big = random.NextDouble() < BigShare;
        var w = big ? 130 + random.NextDouble() * 270 : 20 + random.NextDouble() * 90;
        var h = w * (0.6 + random.NextDouble() * 0.6);
        var kind = random.NextDouble();
        var style = new StampStyle(sha);
        if (kind < 0.25) style = style with { ShadowOpacity = 0.45, ShadowBlur = 6, ShadowOffsetX = 4, ShadowOffsetY = 3 };
        else if (kind < 0.33) style = style with { Hue = 12, Brightness = -0.1 };
        else if (kind < 0.36) style = style with { Blur = 1.5 };
        else if (kind < 0.40) style = style with { Tint = "#FFC8B090" };
        var opacity = random.NextDouble() < 0.7 ? 1.0 : 0.35 + random.NextDouble() * 0.6;
        var rotation = random.NextDouble() < 0.2 ? random.NextDouble() * 360 : 0;
        var flip = random.NextDouble() < 0.3;
        return new SceneObject(new ObjectId(new Guid(Bytes(random))), SceneObjectKind.Stamp, "Гора",
            Math.Min(Canvas - w, x), Math.Min(Canvas - h, y), w, h, rotation, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = style with { FlipHorizontal = flip }, Sublayer = sublayer, ObjectOpacity = opacity,
        };
    }
}
