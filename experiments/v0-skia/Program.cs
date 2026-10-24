using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SkiaSharp;

namespace V0Skia;

/// <summary>
/// V0 variant B runner. Opens one window on the secondary display and, on the first frame,
/// runs the requested job with Avalonia's own GPU context (the path Map uses), then exits.
///   V0Skia --shared &lt;dir&gt; --out &lt;dir&gt; --mode stills|video|perf|export|all [--relight 0.85]
/// </summary>
internal static class Program
{
    public static string Shared = "", Out = "", Mode = "all";

    [STAThread]
    public static int Main(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--shared": Shared = args[i + 1]; break;
                case "--out": Out = args[i + 1]; break;
                case "--mode": Mode = args[i + 1]; break;
                case "--relight": LightPass.Relight = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        Directory.CreateDirectory(Out);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return Runner.ExitCode;
    }
}

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new Window
            {
                Title = "Map V0 · B (SkiaSharp)",
                Width = 1280, Height = 720,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = Brushes.Black,
                Content = new RunnerControl(),
            };
            // Secondary display only (left of the owner's main screen).
            var target = w.Screens.All.FirstOrDefault(s => !s.IsPrimary);
            if (target is not null) w.Position = new PixelPoint(target.Bounds.X + 40, target.Bounds.Y + 40);
            desktop.MainWindow = w;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class RunnerControl : Control
{
    public RunnerControl() => ClipToBounds = true;

    public override void Render(DrawingContext context)
    {
        context.Custom(new Op(new Rect(Bounds.Size)));
        if (!Runner.Done) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    private sealed class Op(Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            Runner.Frame(lease.SkCanvas, lease.GrContext, (float)bounds.Width, (float)bounds.Height);
        }
    }
}

internal static class Runner
{
    public static bool Done;
    public static int ExitCode;
    private static SKImage? _shown;
    private static bool _started;

    public static void Frame(SKCanvas canvas, GRContext? gpu, float w, float h)
    {
        if (!_started)
        {
            _started = true;
            try { Run(gpu); }
            catch (Exception e) { File.WriteAllText(Path.Combine(Program.Out, "error.txt"), e.ToString()); ExitCode = 1; }
            Done = true;
            Dispatcher.UIThread.Post(() => (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(ExitCode));
        }
        if (_shown is { } img)
        {
            var scale = Math.Min(w / img.Width, h / img.Height);
            canvas.DrawImage(img, SKRect.Create(img.Width * scale, img.Height * scale), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
    }

    private static void Log(string s) => File.AppendAllText(Path.Combine(Program.Out, "log.txt"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n");

    private static void Run(GRContext? gpu)
    {
        Log($"start mode={Program.Mode} gpu={(gpu is null ? "none" : gpu.Backend.ToString())} relight={LightPass.Relight}");
        if (gpu is null) throw new InvalidOperationException("No GPU context: B must run on the GPU like Map's canvas.");
        var sw = Stopwatch.StartNew();
        using var scene = SceneInputs.Load(Program.Shared);
        Log($"inputs loaded {sw.ElapsedMilliseconds} ms");
        using var cache = new MaterialCache(scene, gpu);
        var frameRect = new SKRectI(0, 0, scene.Width, scene.Height);
        var buffers = cache.AssembleFrame(frameRect);
        gpu.Flush(true, true);
        Log($"material tiles built={cache.TileBuilds} assemblies={cache.FrameAssemblies} {sw.ElapsedMilliseconds} ms");
        using var gpuNoise = scene.Noise.ToTextureImage(gpu);
        using var target = cache.NewSurface(scene.Width, scene.Height);

        void Light(float t, float time)
        {
            var st = LightPass.At(scene, t, time);
            using var shadows = ShadowLayer.Draw(cache, scene, st, frameRect);
            LightPass.Draw(target.Canvas, buffers, shadows, gpuNoise, new SKPoint(0, 0), st, SKRect.Create(scene.Width, scene.Height));
        }

        var mode = Program.Mode;
        if (mode is "stills" or "all")
        {
            var dir = Directory.CreateDirectory(Path.Combine(Program.Out, "stills")).FullName;
            foreach (var (name, t) in new[] { ("day", 0f), ("evening", 0.5f), ("night", 1f), ("night_torch", 1f) })
            {
                Light(t, 1.0f);
                SavePng(target, Path.Combine(dir, name + ".png"));
            }
            var keep = LightPass.Relight;
            LightPass.Relight = 0;
            Light(0, 1.0f);
            SavePng(target, Path.Combine(dir, "day_normals_off.png"));
            LightPass.Relight = keep;
            Log("stills done");
        }

        if (mode is "video" or "all")
        {
            var dir = Directory.CreateDirectory(Path.Combine(Program.Out, "video")).FullName;
            using var small = cache.NewSurface(1280, 720);
            var hq = new SKSamplingOptions(SKCubicResampler.Mitchell);
            var frames = 15 * 30;
            for (var f = 0; f < frames; f++)
            {
                var time = f / 30f;
                var t = Math.Min(1f, time / 12f);
                Light(t, time);
                using var snap = target.Snapshot();
                small.Canvas.DrawImage(snap, SKRect.Create(1280, 720), hq);
                SavePng(small, Path.Combine(dir, $"f{f:0000}.png"));
                if (f % 30 == 0) { _shown?.Dispose(); _shown = small.Snapshot(); }
            }
            Log($"video done {frames} frames");
        }

        if (mode is "perf" or "all") Perf(gpu, scene, cache, target, gpuNoise, buffers);
        if (mode is "export" or "all") ExportCheck(gpu, scene, target, buffers, gpuNoise);

        _shown?.Dispose();
        _shown = target.Snapshot();
        buffers.Dispose();
        Log("done");
    }

    private static void Perf(GRContext gpu, SceneInputs scene, MaterialCache cache, SKSurface target, SKImage noise,
        FrameBuffers buffers)
    {
        var rect = SKRect.Create(scene.Width, scene.Height);
        // Empty submit cost: subtracted from nothing, reported for honesty.
        var idle = new List<double>();
        for (var i = 0; i < 60; i++) { var s0 = Stopwatch.GetTimestamp(); gpu.Flush(true, true); idle.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds); }

        var buildsBefore = cache.TileBuilds; var assembliesBefore = cache.FrameAssemblies;
        var light = new List<double>(); var cpuSubmit = new List<double>();
        for (var f = -120; f < 600; f++)
        {
            var t = Math.Clamp((f + 120) / 720f, 0f, 1f);
            var s0 = Stopwatch.GetTimestamp();
            var st = LightPass.At(scene, t, f / 60f);
            using (var shadows = ShadowLayer.Draw(cache, scene, st, new SKRectI(0, 0, scene.Width, scene.Height)))
                LightPass.Draw(target.Canvas, buffers, shadows, noise, new SKPoint(0, 0), st, rect);
            var cpu = Stopwatch.GetElapsedTime(s0).TotalMilliseconds;
            gpu.Flush(true, true);
            var total = Stopwatch.GetElapsedTime(s0).TotalMilliseconds;
            if (f >= 0) { light.Add(total); cpuSubmit.Add(cpu); }
        }
        var rebuilt = cache.TileBuilds - buildsBefore;
        var reassembled = cache.FrameAssemblies - assembliesBefore;

        // Pan: every frame re-assembles the visible frame from cached tiles, then lights it.
        var pan = new List<double>();
        for (var f = 0; f < 240; f++)
        {
            var dx = (int)(MathF.Sin(f / 240f * MathF.PI * 2) * 300);
            var s0 = Stopwatch.GetTimestamp();
            var fr = new SKRectI(dx, 0, dx + scene.Width, scene.Height);
            using var b = cache.AssembleFrame(fr);
            var st = LightPass.At(scene, 0.5f, f / 60f);
            using var shadows = ShadowLayer.Draw(cache, scene, st, fr);
            LightPass.Draw(target.Canvas, b, shadows, noise, new SKPoint(dx, 0), st, rect);
            gpu.Flush(true, true);
            pan.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds);
        }
        gpu.GetResourceCacheUsage(out var resources, out var gpuBytes);
        var proc = Process.GetCurrentProcess();
        var result = new
        {
            note = "Times are CPU wall clock around draw + GrContext.Flush(submit, sync) — they include the GPU work and the wait for it (Skia exposes no GPU timer queries here). Frame 2560x1440.",
            gpu = gpu.Backend.ToString(),
            idleSubmitMs = Stats(idle),
            lightChangeFrameMs = Stats(light),
            lightChangeCpuRecordMs = Stats(cpuSubmit),
            tilesRebuiltDuringLightChange = rebuilt,
            frameReassembliesDuringLightChange = reassembled,
            panFrameMs = Stats(pan),
            tilesBuiltTotal = cache.TileBuilds,
            gpuResourceCacheMiB = gpuBytes / 1048576.0,
            gpuResources = resources,
            privateMiB = proc.PrivateMemorySize64 / 1048576.0,
            workingSetMiB = proc.WorkingSet64 / 1048576.0,
        };
        File.WriteAllText(Path.Combine(Program.Out, "perf.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Log("perf done");
    }

    private static void ExportCheck(GRContext gpu, SceneInputs scene, SKSurface gpuTarget, FrameBuffers gpuBuf, SKImage gpuNoise)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Program.Out, "export")).FullName;
        var report = new List<object>();
        using var cpuCache = new MaterialCache(scene, null);
        var whole = new SKRectI(0, 0, scene.Width, scene.Height);
        using var cpuBuf = cpuCache.AssembleFrame(whole);
        foreach (var (name, t) in new[] { ("day", 0f), ("evening", 0.5f), ("night", 1f) })
        {
            var state = LightPass.At(scene, t, 1.0f);
            // Screen (GPU).
            using (var gpuCache = new MaterialCache(scene, gpu))
            using (var gs = ShadowLayer.Draw(gpuCache, scene, state, whole))
                LightPass.Draw(gpuTarget.Canvas, gpuBuf, gs, gpuNoise, new SKPoint(0, 0), state, SKRect.Create(scene.Width, scene.Height));
            var gpuPx = Read(gpuTarget, scene.Width, scene.Height);
            using var cs = ShadowLayer.Draw(cpuCache, scene, state, whole);
            // Export (CPU), whole frame.
            using var cpuSurf = SKSurface.Create(new SKImageInfo(scene.Width, scene.Height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
            var sw = Stopwatch.StartNew();
            LightPass.Draw(cpuSurf.Canvas, cpuBuf, cs, scene.Noise, new SKPoint(0, 0), state, SKRect.Create(scene.Width, scene.Height));
            var cpuMs = sw.Elapsed.TotalMilliseconds;
            var cpuPx = Read(cpuSurf, scene.Width, scene.Height);
            // Export (CPU) in 2x2 tiles with a margin, stitched.
            const int margin = 400;
            var tiled = new byte[cpuPx.Length];
            int hw = scene.Width / 2, hh = scene.Height / 2;
            for (var ty = 0; ty < 2; ty++)
            for (var tx = 0; tx < 2; tx++)
            {
                var r = new SKRectI(tx * hw - margin, ty * hh - margin, (tx + 1) * hw + margin, (ty + 1) * hh + margin);
                using var tb = cpuCache.AssembleFrame(r);
                using var ts = SKSurface.Create(new SKImageInfo(r.Width, r.Height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
                using var tsh = ShadowLayer.Draw(cpuCache, scene, state, r);
                LightPass.Draw(ts.Canvas, tb, tsh, scene.Noise, new SKPoint(r.Left, r.Top), state, SKRect.Create(r.Width, r.Height));
                var tp = Read(ts, r.Width, r.Height);
                for (var y = 0; y < hh; y++)
                    Buffer.BlockCopy(tp, ((y + margin) * r.Width + margin) * 4, tiled, ((ty * hh + y) * scene.Width + tx * hw) * 4, hw * 4);
            }
            // Same input buffers: the CPU-built material and CPU shadow layer uploaded to the GPU,
            // so only the light pass itself differs (the GO/NO-GO criterion).
            using var upA = cpuBuf.Albedo.ToTextureImage(gpu); using var upN = cpuBuf.Normal.ToTextureImage(gpu);
            using var upD = cpuBuf.Data.ToTextureImage(gpu); using var upS = cs.ToTextureImage(gpu);
            LightPass.Draw(gpuTarget.Canvas, new FrameBuffers(upA, upN, upD), upS, gpuNoise, new SKPoint(0, 0), state, SKRect.Create(scene.Width, scene.Height));
            var sameInPx = Read(gpuTarget, scene.Width, scene.Height);
            var (sMax, sOver2, sMean) = Diff(sameInPx, cpuPx);
            var (gMax, gOver2, gMean) = Diff(gpuPx, cpuPx);
            var (tMax, tOver2, _) = Diff(cpuPx, tiled);
            if (name == "night") SaveDiff(gpuPx, cpuPx, scene.Width, scene.Height, Path.Combine(dir, "diff_gpu_cpu_night.png"));
            report.Add(new { name, sameInputsGpuVsCpuMaxRgb = sMax, sameInputsPixelsOver2 = sOver2, sameInputsMeanAbs = sMean,
                separateMaterialGpuVsCpuMaxRgb = gMax, gpuVsCpuPixelsOver2 = gOver2, gpuVsCpuMeanAbs = gMean, cpuWholeVsTiledMaxRgb = tMax, cpuWholeVsTiledPixelsOver2 = tOver2, cpuLightPassMs = cpuMs });
        }
        File.WriteAllText(Path.Combine(Program.Out, "export.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Log("export check done");
    }

    private static (int Max, long Over2, double Mean) Diff(byte[] a, byte[] b)
    {
        int max = 0; long over = 0; double sum = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var m = 0;
            for (var c = 0; c < 3; c++) m = Math.Max(m, Math.Abs(a[i + c] - b[i + c]));
            max = Math.Max(max, m); if (m > 2) over++; sum += m;
        }
        return (max, over, sum / (a.Length / 4));
    }

    private static void SaveDiff(byte[] a, byte[] b, int w, int h, string path)
    {
        var px = new byte[a.Length];
        for (var i = 0; i < a.Length; i += 4)
        {
            var m = 0;
            for (var c = 0; c < 3; c++) m = Math.Max(m, Math.Abs(a[i + c] - b[i + c]));
            var v = (byte)Math.Min(255, m * 40);
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        using var img = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), px);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static byte[] Read(SKSurface s, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var px = new byte[w * h * 4];
        unsafe { fixed (byte* p = px) { if (!s.ReadPixels(info, (IntPtr)p, w * 4, 0, 0)) throw new InvalidOperationException("readback"); } }
        return px;
    }

    private static void SavePng(SKSurface s, string path)
    {
        using var img = s.Snapshot();
        using var raster = img.ToRasterImage();
        using var data = raster.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static object Stats(List<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        double P(double q) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)];
        return new { n = s.Length, median = Math.Round(P(0.5), 3), p95 = Math.Round(P(0.95), 3), p99 = Math.Round(P(0.99), 3), max = Math.Round(s[^1], 3) };
    }
}
