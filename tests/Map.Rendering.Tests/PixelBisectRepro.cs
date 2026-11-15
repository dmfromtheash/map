using System.Reflection;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Pixel-bisect harness (not a normal test): runs only when MAP_PIXREPRO_OUT is set. Builds a deterministic
/// 32 768 px map with 500 000 stamps of 1 800 synthetic images, renders the fit view the way the canvas does
/// (overview tier, render-context cache, background preparation when the build has it, refined-frame loop
/// until settled) and the same after an edit route. Uses only APIs that exist at a5a41b5 (reflection otherwise).
/// </summary>
public sealed class PixelBisectRepro
{
    private const int Canvas = 32768;
    private const int Objects = 500_000;
    private const int Assets = 1800;
    private const int ScreenW = 2048, ScreenH = 1095;
    private static string? Out => Environment.GetEnvironmentVariable("MAP_PIXREPRO_OUT");
    private static string Label => Environment.GetEnvironmentVariable("MAP_PIXREPRO_LABEL") ?? "run";

    [Fact]
    public void Small_fit_and_edit_pixels_match_the_synchronous_overview()
    {
        using var source = new SyntheticSource(8);
        var document = BuildDocument(source, objectCount: 600, assetCount: 8);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var original = SceneObjectClone.At(layer.Objects[0], 8000, 8000, 512, 512);
        document = DocumentEditor.ReplaceObject(document, original);
        const double scale = 1d / 32;
        using var frame = new FrameHarness(source, scale, 0, 0);
        var first = frame.Settle(document, null, null).Png;
        Check(document, first);
        var session = new EditingSession(document);
        session.Execute(new ReplaceObjectsCommand([original], [SceneObjectClone.At(original, 14000, 14000)], "move"));
        var changed = frame.Settle(session.Document, null, null).Png;
        Check(session.Document, changed);
        using (var a = SKBitmap.Decode(first))
        using (var b = SKBitmap.Decode(changed))
            Assert.False(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), "the edit never changed the fit frame");
        session.Undo(); Check(session.Document, frame.Settle(session.Document, null, null).Png);
        session.Redo(); Check(session.Document, frame.Settle(session.Document, null, null).Png);

        void Check(MapDocument current, byte[] png)
        {
            using var actual = SKBitmap.Decode(png);
            using var expected = SKBitmap.Decode(Reference(current, source, scale, 0, 0));
            Assert.True(actual.Pixels.Distinct().Take(8).Count() == 8, "the fit frame is empty");
            Assert.Equal(expected.Bytes, actual.Bytes);
        }
    }

    [OptInProbe("MAP_PIXREPRO_OUT", "PixelBisectRepro.Small_fit_and_edit_pixels_match_the_synchronous_overview")]
    public void Render_scenarios()
    {
        var outDir = Out!;
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        using var source = new SyntheticSource(Assets);
        var document = BuildDocument(source);
        var layerId = document.Layers.OfType<ObjectLayer>().Single().Id;
        var scale = ScreenH * 0.95 / Canvas;
        var offsetX = -(ScreenW / scale - Canvas) / 2;
        var offsetY = -(ScreenH / scale - Canvas) / 2;

        // Scenario 1: fit view.
        using (var frame = new FrameHarness(source, scale, offsetX, offsetY))
        {
            var (pixels, frames) = frame.Settle(document, null, null);
            Save(outDir, "fit", pixels, log, frames);
        }
        Save(outDir, "fit-ref", Reference(document, source, scale, offsetX, offsetY), log, 1);

        // Scenario 2: edit route on a session (select, drag 25, place 10, undo, redo).
        var session = new EditingSession(document);
        using (var frame = new FrameHarness(source, scale, offsetX, offsetY))
        {
            frame.Settle(session.Document, null, null);
            var layer = (ObjectLayer)session.Document.FindLayer(layerId)!;
            var picked = Enumerable.Range(0, 25).Select(i => layer.Objects[(int)((i * 19_997L + 1234) % layer.Objects.Count)]).ToArray();
            frame.Settle(session.Document, picked[0].Id, null);
            var moved = picked.Select(o => SceneObjectClone.At(o, Math.Clamp(o.X + 3500, 0, Canvas - o.Width), Math.Clamp(o.Y - 2100, 0, Canvas - o.Height))).ToArray();
            frame.Frames(session.Document, picked[0].Id, moved, 3);
            session.Execute(new ReplaceObjectsCommand(picked, moved, "Drag"));
            frame.Prepare(session.Document);
            frame.Settle(session.Document, picked[0].Id, null);
            var reference = session.Document.Assets[7];
            var placed = Enumerable.Range(0, 10).Select(i => Stamp(new Random(900 + i), reference.Sha256, 9000 + i * 900, 15000 + i * 300, 3)).ToArray();
            session.Execute(new PlaceAssetObjectsCommand(layerId, reference, placed));
            frame.Prepare(session.Document);
            frame.Settle(session.Document, null, null);
            session.Undo(); frame.Prepare(session.Document); frame.Settle(session.Document, null, null);
            session.Redo(); frame.Prepare(session.Document);
            var (pixels, frames) = frame.Settle(session.Document, null, null);
            Save(outDir, "edit", pixels, log, frames);
        }
        Save(outDir, "edit-ref", Reference(session.Document, source, scale, offsetX, offsetY), log, 1);
        File.WriteAllLines(Path.Combine(outDir, $"{Label}.txt"), log);
    }

    [Fact]
    public void Compare_to_base()
    {
        if (Out is not { } outDir || Environment.GetEnvironmentVariable("MAP_PIXREPRO_BASE") is not { } baseLabel) return;
        var lines = new List<string>();
        foreach (var name in new[] { "fit", "fit-ref", "edit", "edit-ref" })
        {
            var a = Path.Combine(outDir, $"{baseLabel}-{name}.png");
            var b = Path.Combine(outDir, $"{Label}-{name}.png");
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
            using (var file = File.Create(Path.Combine(outDir, $"diff-{baseLabel}-vs-{Label}-{name}.png")))
                diff.Encode(file, SKEncodedImageFormat.Png, 100);
            lines.Add($"{name}: max {max}/255, differing {differ}, >2/255 {over2}, bbox {(differ > 0 ? $"{minX},{minY}-{maxX},{maxY}" : "-")}");
        }
        File.WriteAllLines(Path.Combine(outDir, $"compare-{baseLabel}-vs-{Label}.txt"), lines);
    }

    private static void Save(string dir, string name, byte[] png, List<string> log, int frames)
    {
        File.WriteAllBytes(Path.Combine(dir, $"{Label}-{name}.png"), png);
        using var bitmap = SKBitmap.Decode(png);
        var hash = Convert.ToHexString(SHA256.HashData(bitmap.Bytes))[..16];
        log.Add($"{name}: pixels-sha256 {hash} frames {frames}");
    }

    private static byte[] Reference(MapDocument document, SyntheticSource source, double scale, double ox, double oy)
    {
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
                Stamps = source, ObjectCache = cache, ViewScale = scale, LodTier = RenderLodTier.Overview,
                VisibleWorld = new(ox, oy, ox + ScreenW / scale, oy + ScreenH / scale),
            });
        }
        finally { cache.EndFrame(); }
        using var image = surface.Snapshot();
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed class FrameHarness : IDisposable
    {
        private readonly SyntheticSource _source;
        private readonly double _scale, _ox, _oy;
        private readonly ObjectOverviewCache _cache = new(1024, 256L * 1024 * 1024);
        private readonly object? _preparation;
        private static readonly Type? PreparationType = typeof(SceneRenderer).GetNestedType("OverviewPreparation", BindingFlags.Public);
        private static readonly PropertyInfo? PreparationOption = typeof(RenderOptions).GetProperty("OverviewPreparation");
        private volatile bool _requested;

        public FrameHarness(SyntheticSource source, double scale, double ox, double oy)
        {
            _source = source; _scale = scale; _ox = ox; _oy = oy;
            if (PreparationType is not null && Environment.GetEnvironmentVariable("MAP_PIXREPRO_NOPREP") != "1")
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
            // (Plans, Cancelled, Queued, ReadyImages, Idle, ...) - nested tuples beyond 7 live in Rest.
            return (int)fields[2].GetValue(stats)! == 0 && (bool)fields[4].GetValue(stats)!;
        }

        public (byte[] Png, int Frames) Settle(MapDocument document, ObjectId? selected, IReadOnlyList<SceneObject>? overrides)
        {
            Prepare(document);
            byte[] last = [];
            var quiet = 0;
            for (var frame = 1; frame <= 4000; frame++)
            {
                _requested = false;
                var (png, stats) = Draw(document, selected, overrides);
                last = png;
                var settled = stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && Idle();
                quiet = settled ? quiet + 1 : 0;
                if (quiet >= 3) return (last, frame);
                if (!settled) Thread.Sleep(5);
            }
            throw new InvalidOperationException("Overview did not settle in 4000 frames.");
        }

        public void Frames(MapDocument document, ObjectId? selected, IReadOnlyList<SceneObject>? overrides, int count)
        {
            for (var i = 0; i < count; i++) Draw(document, selected, overrides);
        }

        private (byte[] Png, RenderStats Stats) Draw(MapDocument document, ObjectId? selected, IReadOnlyList<SceneObject>? overrides)
        {
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
            using var image = surface.Snapshot();
            return (image.Encode(SKEncodedImageFormat.Png, 100).ToArray(), stats);
        }

        public void Dispose()
        {
            (_preparation as IDisposable)?.Dispose();
            _cache.Dispose();
        }
    }

    private static MapDocument BuildDocument(SyntheticSource source, int objectCount = Objects, int assetCount = Assets)
    {
        var random = new Random(20261114);
        var objects = new SceneObject[objectCount];
        for (var i = 0; i < objectCount; i++)
            objects[i] = Stamp(random, source.Sha(random.Next(assetCount)), random.NextDouble() * Canvas, random.NextDouble() * Canvas, random.Next(4));
        var assets = Enumerable.Range(0, assetCount).Select(i => new MapAssetReference(source.Sha(i), $"Синтетика {i}", "image/png", "png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "synthetic", "user-owned", catalogId: source.Sha(i))).ToArray();
        return new MapDocument(new DocumentId(new Guid(Bytes(new Random(1)))), "Pixel bisect", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Canvas, Canvas, "px", "#FFF3EBDD"),
            [new BaseColorLayer(new LayerId(new Guid(Bytes(new Random(2)))), "Page", true, false, 1, "#FFF3EBDD"),
             new ObjectLayer(new LayerId(new Guid(Bytes(new Random(3)))), "Горы", true, false, 1, objects)],
            assets: assets);
    }

    private static byte[] Bytes(Random random) { var b = new byte[16]; random.NextBytes(b); return b; }

    private static SceneObject Stamp(Random random, string sha, double x, double y, int sublayer)
    {
        var big = random.NextDouble() < 0.03;
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

    internal sealed class SyntheticSource : IStampImageSource, IDisposable
    {
        private readonly Dictionary<string, SKImage> _images = new(StringComparer.Ordinal);
        private readonly string[] _shas;
        public long? OverviewColorRevision => 1;

        public SyntheticSource(int count)
        {
            _shas = new string[count];
            var random = new Random(77);
            for (var i = 0; i < count; i++)
            {
                _shas[i] = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i))).ToLowerInvariant();
                var w = 40 + random.Next(40);
                var h = 40 + random.Next(40);
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))!;
                surface.Canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { IsAntialias = true, Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)) };
                using var path = new SKPath();
                path.MoveTo(w * 0.05f, h * 0.95f);
                path.LineTo(w * (0.3f + (float)random.NextDouble() * 0.4f), h * 0.05f);
                path.LineTo(w * 0.95f, h * 0.95f);
                path.Close();
                surface.Canvas.DrawPath(path, paint);
                paint.Color = paint.Color.WithAlpha(140);
                surface.Canvas.DrawCircle(w * 0.5f, h * 0.7f, w * 0.25f, paint);
                _images[_shas[i]] = surface.Snapshot();
            }
        }

        public string Sha(int index) => _shas[index];
        public SKImage? TryGetImage(string sha256) => _images.GetValueOrDefault(sha256);
        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }
    }
}
