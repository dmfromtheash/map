using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// Audit 2026-11-01 (А1.1): style preparation took pictures from the canvas's stamp cache on the thread pool. The
/// cache holds 64 pictures without a lock, and its eviction disposed a picture another thread was still drawing.
/// Work off the render thread now leases its pictures; the canvas frame frees what others evict only after it ends.
/// </summary>
public sealed class StampImageLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-lease-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Without_a_lease_the_canvas_evicting_more_than_64_pictures_frees_the_one_in_use()
    {
        // The old route of PrepareAssets: a picture taken from the shared source, then the canvas draws 71 others.
        var (images, shas) = Source();
        using var _ = images;
        var inUse = images.TryGetImage(shas[0])!;
        using (images.BeginFrame()) foreach (var sha in shas.Skip(1)) Assert.NotNull(images.TryGetImage(sha));
        Assert.Equal(IntPtr.Zero, inUse.Handle);
    }

    [Fact]
    public void A_leased_picture_stays_valid_while_the_canvas_evicts_more_than_64_others_and_is_freed_after()
    {
        var (images, shas) = Source();
        using var _ = images;
        var lease = images.Lease();
        var inUse = lease.TryGetImage(shas[0])!;
        using (images.BeginFrame()) foreach (var sha in shas.Skip(1)) Assert.NotNull(images.TryGetImage(sha));

        Assert.NotEqual(IntPtr.Zero, inUse.Handle);
        Assert.True(Drawn(inUse));
        // The canvas finds the very picture the lease prepared from (form maps belong to it), not a copy.
        Assert.Same(inUse, images.TryGetImage(shas[0]));

        lease.Dispose();
        using (images.BeginFrame()) foreach (var sha in shas.Skip(1)) images.TryGetImage(sha);
        Assert.Equal(IntPtr.Zero, inUse.Handle);
    }

    [Fact]
    public void What_a_lease_evicts_during_a_frame_waits_until_the_frame_ends()
    {
        var (images, shas) = Source();
        using var _ = images;
        var frameThread = images.BeginFrame()!;
        var drawn = images.TryGetImage(shas[0])!;
        // Another thread fills the cache past its 64 pictures while this frame still draws «drawn». A thread of its
        // own: a waited Task.Run may run inline on this very thread, which is the frame's.
        var loaded = 0;
        var other = new Thread(() =>
        {
            foreach (var sha in shas.Skip(1))
            {
                using var lease = images.Lease();
                if (lease.TryGetImage(sha) is not null) loaded++;
            }
        });
        other.Start();
        other.Join();
        Assert.Equal(shas.Length - 1, loaded);

        Assert.NotEqual(IntPtr.Zero, drawn.Handle);
        Assert.True(Drawn(drawn));
        frameThread.Dispose();
        Assert.Equal(IntPtr.Zero, drawn.Handle);
    }

    [Fact]
    public async Task Leases_on_the_thread_pool_never_see_a_freed_picture_while_the_canvas_draws()
    {
        // The race itself: four workers draw leased pictures while frames on this thread ask for all 72.
        var (images, shas) = Source();
        using var _ = images;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var freed = 0; var draws = 0;
        var workers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
        {
            var random = new Random(seed);
            while (!stop.IsCancellationRequested)
            {
                using var lease = images.Lease();
                var held = Enumerable.Range(0, 6).Select(_ => lease.TryGetImageForPixelSize(shas[random.Next(shas.Length)], 48, 48)).ToArray();
                foreach (var image in held)
                {
                    if (image is null || image.Handle == IntPtr.Zero || !Drawn(image)) Interlocked.Increment(ref freed);
                    else Interlocked.Increment(ref draws);
                }
            }
        })).ToArray();
        var frames = 0;
        while (!stop.IsCancellationRequested)
        {
            using (images.BeginFrame())
                foreach (var sha in shas.OrderBy(_ => Random.Shared.Next()).Take(40))
                    if (images.TryGetImage(sha) is not { } image || !Drawn(image)) Interlocked.Increment(ref freed);
            frames++;
        }
        await Task.WhenAll(workers);

        Assert.Equal(0, freed);
        Assert.True(draws > 100 && frames > 10, $"draws {draws}, frames {frames}");
    }

    private static bool Drawn(SKImage image)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32))!;
        surface.Canvas.DrawImage(image, SKRect.Create(0, 0, 32, 32), new SKSamplingOptions(SKFilterMode.Linear));
        using var pixmap = surface.PeekPixels();
        return pixmap.GetPixelColor(16, 16).Alpha > 0 || pixmap.GetPixelColor(16, 24).Alpha > 0;
    }

    /// <summary>The owner's six mountains, each at twelve sizes: 72 different pictures embedded in one map.</summary>
    private (CatalogStampImageSource Images, string[] Shas) Source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var assets = new List<MapAssetReference>();
        for (var number = 1; number <= 6; number++)
        {
            using var mountain = SKBitmap.Decode(Path.Combine(root!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{number:00}.png"));
            for (var step = 0; step < 12; step++)
            {
                var width = 64 + step * 8;
                var height = Math.Max(1, (int)Math.Round(width * (double)mountain.Height / mountain.Width));
                using var scaled = mountain.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
                using var data = scaled.Encode(SKEncodedImageFormat.Png, 100);
                var bytes = data.ToArray();
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                assets.Add(new MapAssetReference(sha, $"Гора {number}·{width}", "image/png", "png", ProjectAssetStorage.Embedded,
                    DateTimeOffset.UnixEpoch, "test", "Map", bytes));
            }
        }
        var document = new MapDocument(DocumentId.New(), "Lease", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")], []) { Assets = [.. assets] };
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        return (new CatalogStampImageSource(catalog, () => document, () => null), assets.Select(a => a.Sha256).ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
