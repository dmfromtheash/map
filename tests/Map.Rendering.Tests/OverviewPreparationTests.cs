using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class OverviewPreparationTests
{
    private const double Scale = 1d / 64;
    private static readonly string Sha = new('a', 64);

    [Theory]
    [InlineData(false, Scale)]
    [InlineData(true, Scale)]
    // Between zoom buckets: the chunk raster resolution comes from the bucket, the view scales it.
    [InlineData(false, 1d / 50)]
    [InlineData(true, 1d / 50)]
    public void Background_final_pixels_equal_synchronous_owner_mountains_including_edit_and_zoom(bool painted, double scale)
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var document = Document(Enumerable.Range(0, 180).Select(i => Stamp(i, painted))
            .Append(Stamp(2, painted, size: 512)).ToArray());
        prep.UpdateDocument(document, source, source.Ready);
        var final = Settle(document, source, cache, prep, scale);
        using (var reference = new ObjectOverviewCache()) Assert.Equal(Draw(document, source, reference, scale: scale).Pixels, final.Pixels);
        Assert.True(source.Notifications > 0);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, source.ColorThreads);

        var layer = document.Layers.OfType<ObjectLayer>().Single();
        document = new AddObjectCommand(layer.Id, Stamp(0, painted, offset: 2048)).Apply(document);
        prep.UpdateDocument(document, source, source.Ready);
        final = Settle(document, source, cache, prep, scale / Math.Sqrt(2));
        using (var reference = new ObjectOverviewCache())
            Assert.Equal(Draw(document, source, reference, scale: scale / Math.Sqrt(2)).Pixels, final.Pixels);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void A_pan_keeps_prepared_rasters_and_settles_to_the_synchronous_pixels()
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var document = Document(Enumerable.Range(0, 256).Select(i => Stamp(i, true)).ToArray(), 131072);
        prep.UpdateDocument(document, source, source.Ready);
        Settle(document, source, cache, prep, origin: 0);
        // Half a view to the right: the overlapping chunks are reused, the new ones are prepared.
        var panned = Settle(document, source, cache, prep, origin: 16384);
        using var reference = new ObjectOverviewCache();
        Assert.Equal(Draw(document, source, reference, origin: 16384).Pixels, panned.Pixels);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void A_legacy_source_without_a_colour_revision_settles_instead_of_polling()
    {
        using var source = new Mountains { Revision = null };
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var document = Document([Stamp(0, false), Stamp(1, true)]);
        prep.UpdateDocument(document, source, source.Ready);
        var settled = Settle(document, source, cache, prep);
        Wait(() => prep.Statistics.Idle);
        var lookups = source.ColorThreads.Count;
        for (var frame = 0; frame < 3; frame++)
        {
            Thread.Sleep(300); // longer than the legacy re-sample interval
            var again = Draw(document, source, cache, prep);
            Assert.Equal(0, again.Stats.DetailTilesPending);
            Assert.Equal(settled.Pixels, again.Pixels);
        }
        Wait(() => prep.Statistics.Idle);
        Assert.Equal(lookups, source.ColorThreads.Count);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void Blocked_background_palette_never_blocks_a_cold_frame()
    {
        using var source = new Mountains { Block = true };
        using var prep = new SceneRenderer.OverviewPreparation();
        var document = Document([Stamp(0, false)]);
        prep.UpdateDocument(document, source, source.Ready);
        try
        {
            Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(10)));
            var draw = Task.Run(() =>
            {
                using var cache = new ObjectOverviewCache();
                return Draw(document, source, cache, prep);
            });
            Assert.True(draw.Wait(TimeSpan.FromSeconds(2)), "a frame waited for the blocked palette worker");
            Assert.Equal(0, draw.Result.Stats.OverviewChunks);
            Assert.Equal(0, draw.Result.Stats.OverviewVariantBuilds);
            Assert.True(draw.Result.Stats.DetailTilesPending > 0);
        }
        finally { source.Release.Set(); Wait(() => prep.Statistics.Idle); }
    }

    [Fact]
    public void Pending_revision_keeps_previous_picture_and_then_replaces_it()
    {
        using var source = new Mountains();
        using var blocked = new Mountains { Block = true };
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var document = Document([Stamp(0, true)]);
        var before = Settle(document, source, cache, prep);
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        document = new AddObjectCommand(layer.Id, Stamp(1, true)).Apply(document);
        prep.UpdateDocument(document, blocked, blocked.Ready);
        try
        {
            Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(10)));
            var pending = Draw(document, blocked, cache, prep);
            Assert.Equal(before.Pixels, pending.Pixels);
            Assert.True(pending.Stats.DetailTilesPending > 0);
        }
        finally { blocked.Release.Set(); }
        var final = Settle(document, blocked, cache, prep);
        using var reference = new ObjectOverviewCache();
        Assert.Equal(Draw(document, blocked, reference).Pixels, final.Pixels);
        Assert.NotEqual(before.Pixels, final.Pixels);
    }

    [Fact]
    public void Revision_cancellation_drops_obsolete_publication_and_hidden_layers_do_no_work()
    {
        using var source = new Mountains { Block = true };
        using var prep = new SceneRenderer.OverviewPreparation();
        var document = Document([Stamp(0, false)]);
        prep.UpdateDocument(document, source, source.Ready);
        try
        {
            Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(10)));
            var layer = document.Layers.OfType<ObjectLayer>().Single();
            var hidden = document with { Layers = [document.Layers[0], layer with { Visible = false }] };
            prep.UpdateDocument(hidden, source, source.Ready);
        }
        finally { source.Release.Set(); Wait(() => prep.Statistics.Idle); }
        Assert.True(prep.Statistics.Cancelled > 0);
        Assert.Equal(0, prep.Statistics.Queued);
        Assert.Equal(0, prep.Statistics.ReadyImages);
        var lookups = source.ColorThreads.Count;
        prep.UpdateDocument(document with { Layers = [document.Layers[0]] }, source, source.Ready);
        Wait(() => prep.Statistics.Idle);
        Assert.Equal(lookups, source.ColorThreads.Count);
        Assert.Equal(0, source.Notifications);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void Cancelled_palette_does_not_poison_the_next_opening_of_the_same_layer()
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var secondSha = new string('b', 64);
        var document = Document([Stamp(0, false), Stamp(1, false) with { Stamp = new StampStyle(secondSha) }]);
        var before = Settle(document, source, cache, prep);
        document = document.WithAssets([document.Assets[0], new MapAssetReference(secondSha, "owner mountain", "image/png", "png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: "owner-mountain-01")]);
        source.Block = true;
        prep.UpdateDocument(document, source, source.Ready);
        try
        {
            Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(10)));
            prep.Reset();
        }
        finally { source.Block = false; source.Release.Set(); Wait(() => prep.Statistics.Idle); }
        var final = Settle(document, source, cache, prep);
        using var freshSource = new Mountains();
        using var reference = new ObjectOverviewCache();
        Assert.Equal(Draw(document, freshSource, reference).Pixels, final.Pixels);
        Assert.NotEqual(before.Pixels, final.Pixels);
        Assert.True(prep.Statistics.Cancelled > 0);
    }

    [Fact]
    public void Settled_view_prefetches_both_neighbour_buckets_off_the_frame()
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var document = Document([Stamp(0, false)]);
        Settle(document, source, cache, prep);
        Wait(() => prep.Statistics.Prefetched >= 2 && prep.Statistics.Idle);
        Assert.Equal(0, prep.Statistics.ReadyImages); // prefetch retains CPU variants, not unrequested rasters
        var next = Settle(document, source, cache, prep, Scale * Math.Pow(2, .25));
        Assert.Equal(0, next.Stats.OverviewVariantBuilds);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void Queue_and_raster_handoff_stay_bounded_and_reset_releases_them()
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        var document = Document(Enumerable.Range(0, 256).Select(i => Stamp(i, false)).ToArray(), 131072);
        prep.UpdateDocument(document, source, source.Ready);
        Wait(() => prep.Statistics.Idle);
        Draw(document, source, cache, prep, extent: 131072);
        Wait(() => prep.Statistics.Idle);
        Assert.InRange(prep.Statistics.Queued, 1, 64);
        Assert.InRange(prep.Statistics.ReadyImages, 1, 16);
        prep.Reset();
        Wait(() => prep.Statistics.Idle);
        Assert.Equal(0, prep.Statistics.Queued);
        Assert.Equal(0, prep.Statistics.ReadyImages);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void A_moving_frame_takes_only_its_gesture_budget_of_ready_rasters_and_settles_to_the_synchronous_pixels()
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation(static () => false);
        using var cache = new ObjectOverviewCache { GestureAdoptionsPerFrame = 1 };
        var document = Document(Enumerable.Range(0, 256).Select(i => Stamp(i, true)).ToArray(), 131072);
        const double scale = 1d / 32;
        prep.UpdateDocument(document, source, source.Ready);
        Wait(() => prep.Statistics.Idle && prep.Statistics.Plans > 0);
        // The first moving frame only requests the visible chunks; the worker prepares all of them.
        var first = Draw(document, source, cache, prep, scale, origin: 0, moving: true);
        Assert.Equal(0, first.Stats.OverviewAdoptions);
        Wait(() => prep.Statistics.Idle);
        var ready = prep.Statistics.ReadyImages;
        Assert.True(ready >= 3, $"only {ready} rasters were ready");
        var moving = Draw(document, source, cache, prep, scale, origin: 0, moving: true);
        Assert.Equal(1, moving.Stats.OverviewAdoptions);
        Assert.True(moving.Stats.OverviewAdoptionsDeferred >= ready - 1);
        Assert.Equal(cache.GestureAdoptedPixelsPerFrame, moving.Stats.OverviewAdoptionBudgetPixels);
        Assert.True(moving.Stats.DetailTilesPending > 0);
        Assert.Equal(ready - 1, prep.Statistics.ReadyImages); // the rest wait in the handoff, not rebuilt
        // The settled frame has the larger budget and converges to the synchronous picture.
        var settled = Settle(document, source, cache, prep, scale, origin: 0);
        Assert.True(settled.Stats.OverviewAdoptionBudgetPixels == 0 || settled.Stats.OverviewAdoptionBudgetPixels == 8L * 1024 * 1024);
        using var reference = new ObjectOverviewCache();
        Assert.Equal(Draw(document, source, reference, scale: scale, origin: 0).Pixels, settled.Pixels);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void Neighbour_prefetch_waits_while_the_view_moves_and_follows_the_settled_frame()
    {
        using var source = new Mountains();
        var interacting = true;
        using var prep = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref interacting));
        using var cache = new ObjectOverviewCache();
        var document = Document([Stamp(0, false)]);
        prep.UpdateDocument(document, source, source.Ready);
        Wait(() =>
        {
            var frame = Draw(document, source, cache, prep, moving: true);
            Assert.Null(prep.Statistics.Error);
            return frame.Stats.DetailTilesPending == 0;
        });
        // The 150 ms prefetch timer fired at least twice (a busy thread pool may delay it) and found the gesture.
        Wait(() => prep.GestureStatistics.PrefetchDeferred >= 2);
        Assert.Equal(0, prep.Statistics.Prefetched);
        Assert.True(prep.GestureStatistics.GestureActive);
        Volatile.Write(ref interacting, false);
        Draw(document, source, cache, prep); // the settled frame
        Wait(() => prep.Statistics.Prefetched >= 2 && prep.Statistics.Idle);
        Assert.False(prep.GestureStatistics.GestureActive);
        Assert.Null(prep.Statistics.Error);
    }

    [Fact]
    public void Gesture_budget_off_restores_the_settled_adoption_budget()
    {
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation(static () => false);
        using var cache = new ObjectOverviewCache { GestureAdoptedPixelsPerFrame = 0, GestureAdoptionsPerFrame = 1 };
        var document = Document(Enumerable.Range(0, 256).Select(i => Stamp(i, true)).ToArray(), 131072);
        prep.UpdateDocument(document, source, source.Ready);
        Wait(() => prep.Statistics.Idle && prep.Statistics.Plans > 0);
        Draw(document, source, cache, prep, 1d / 32, origin: 0, moving: true);
        Wait(() => prep.Statistics.Idle);
        var ready = prep.Statistics.ReadyImages;
        var moving = Draw(document, source, cache, prep, 1d / 32, origin: 0, moving: true);
        // Only the settled limits apply (8 Mpx and the 4 ms build budget), not the one-picture gesture limit.
        Assert.True(ready > 1 && moving.Stats.OverviewAdoptions > 1, $"{moving.Stats.OverviewAdoptions} of {ready}");
        Assert.Equal(8L * 1024 * 1024, moving.Stats.OverviewAdoptionBudgetPixels);
    }

    private static (byte[] Pixels, RenderStats Stats) Settle(MapDocument document, Mountains source,
        ObjectOverviewCache cache, SceneRenderer.OverviewPreparation prep, double scale = Scale, double? origin = null)
    {
        (byte[] Pixels, RenderStats Stats) result = default;
        Wait(() =>
        {
            result = Draw(document, source, cache, prep, scale, origin: origin);
            Assert.Null(prep.Statistics.Error);
            return result.Stats.DetailTilesPending == 0;
        });
        Assert.Equal(0, result.Stats!.OverviewVariantBuilds); // no CPU preparation on the frame
        return result;
    }

    private static void Wait(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(15), "background overview did not progress");
            Thread.Sleep(5);
        }
    }

    private static (byte[] Pixels, RenderStats Stats) Draw(MapDocument document, Mountains source,
        ObjectOverviewCache cache, SceneRenderer.OverviewPreparation? prep = null, double scale = Scale, int extent = 32768,
        double? origin = null, bool moving = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Scale((float)scale);
        // A 512-pixel view that starts at origin; without one the whole page is the visible world.
        WorldBounds visible = origin is { } left ? new(left, 0, left + 512 / scale, 512 / scale) : new(0, 0, extent - 1, extent - 1);
        if (origin is { } shift) surface.Canvas.Translate((float)-shift, 0);
        var stats = new RenderStats();
        cache.BeginFrame();
        try
        {
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                ObjectCache = cache, OverviewPreparation = prep, Stamps = source, Stats = stats,
                ViewScale = scale, LodTier = RenderLodTier.Overview, VisibleWorld = visible,
                RequestRefinedFrame = prep is null ? null : source.Ready, CameraMoving = moving,
            });
            using var snapshot = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(snapshot);
            return (bitmap.Bytes, stats);
        }
        finally { cache.EndFrame(); }
    }

    internal static SceneObject Stamp(int i, bool painted, double size = 32, double offset = 0) => new(ObjectId.New(), SceneObjectKind.Stamp,
        "owner mountain", (i % 16) * 8192 + 2048 + offset, (i / 16) * 8192 + 2048 + offset,
        size, size, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(Sha) { ShadowOpacity = painted ? .7 : 0, ShadowBlur = painted ? 6 : 0 } };

    internal static MapDocument Document(SceneObject[] objects, int extent = 32768) => new(DocumentId.New(), "overview prep",
        DateTimeOffset.UnixEpoch, new CanvasDefinition(extent, extent, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
         new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
        [new MapAssetReference(Sha, "owner mountain", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: "owner-mountain-01")]);

    internal sealed class Mountains : IStampImageSource, IDisposable
    {
        private readonly SKImage _image;
        public bool Block { get; set; }
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public readonly System.Collections.Concurrent.ConcurrentBag<int> ColorThreads = [];
        private int _notifications;
        public int Notifications => Volatile.Read(ref _notifications);
        public void Ready() => Interlocked.Increment(ref _notifications);
        public long? Revision { get; init; } = 0;
        public long? OverviewColorRevision => Revision;
        public SKImage? TryGetImage(string sha256) => _image;
        public SKColor? TryGetOverviewColor(string sha256)
        {
            ColorThreads.Add(Environment.CurrentManagedThreadId);
            if (Block) { Entered.Set(); Assert.True(Release.Wait(TimeSpan.FromSeconds(20))); }
            return SceneRenderer.StampOverviewColor(_image);
        }
        public Mountains()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            _image = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"))!;
        }
        public void Dispose() { _image.Dispose(); Entered.Dispose(); Release.Dispose(); }
    }
}
