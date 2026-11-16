using System.Diagnostics;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

[Collection("Editor refresh")]
public sealed class LargeSelectionClickTests
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void Prepared_click_and_frame_visit_only_local_objects(int count)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var art = File.ReadAllBytes(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        var sha = Convert.ToHexStringLower(SHA256.HashData(art));
        var style = new StampStyle(sha);
        var items = Enumerable.Range(0, count).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "mountain " + i, i == count - 1 ? 65504 : i < 240 ? 65000 + i % 20 * 70 : i % 1000 * 128,
            i == count - 1 ? 65512 : i < 240 ? 65000 + i / 20 * 65 : i / 1000 * 32, 64, 48, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = style }).ToArray();
        var document = new MapDocument(DocumentId.New(), "click", DateTimeOffset.UnixEpoch,
            new(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "mountains", true, false, 1, items)],
            [new MapAssetReference(sha, "owner mountain", "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "owner", "owner", embeddedContent: art)]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
        editor.DerivedIndexPreparation.GetAwaiter().GetResult();
        editor.ActiveTool = EditorTool.Select;
        using var mountain = SKImage.FromEncodedData(art);
        var images = new Images(mountain);
        using var surface = SKSurface.Create(new SKImageInfo(1536, 864))!;
        using var layers = new LayerImageCache();
        using var objects = new ObjectOverviewCache();
        var stats = new RenderStats();
        void Frame()
        {
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.SetMatrix(SKMatrix.CreateTranslation(-65000, -65000));
            SceneRenderer.Render(surface.Canvas, editor.RenderDocument, new RenderOptions
            {
                Stamps = images, Stats = stats, LayerImages = layers, ObjectCache = objects,
                ViewScale = 1, VisibleWorld = new(65000, 65000, 66536, 65864),
                SelectedObjectId = editor.Session.SelectedObjectId,
            });
        }
        Frame(); Frame();
        for (var step = 0; step < 10; step++)
        {
            editor.DeselectObject();
            var visited = SceneObjectList.EnumeratedOnThisThread;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var hit = SceneGeometry.HitTest(document, 65536, 65536);
            var hitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(65536, 65536, 1));
            editor.EndDrag();
            var selectMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Frame();
            var frameMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine(FormattableString.Invariant($"CLICK count={count} step={step} hitMs={hitMs:F4} selectMs={selectMs:F4} frameMs={frameMs:F4} bytes={GC.GetAllocatedBytesForCurrentThread() - bytes} enumerated={SceneObjectList.EnumeratedOnThisThread - visited} drawn={stats.ObjectsDrawn} kept={stats.LayerImagesKept}"));
            Assert.Equal(items[^1].Id, hit!.Id);
            Assert.Same(document, editor.Session.Document);
            Assert.Equal(0, editor.Session.Revision);
            Assert.Equal(0, editor.Session.UndoDepth);
            Assert.Equal(0, editor.Session.RedoDepth);
            Assert.False(editor.Session.IsDirty);
            Assert.InRange(SceneObjectList.EnumeratedOnThisThread - visited, 0, 256);
        }
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        Console.WriteLine($"SELECT-PIXELS count={count} sha256={Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()))}");
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
