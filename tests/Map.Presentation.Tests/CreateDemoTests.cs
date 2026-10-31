using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// The create buttons' live pictures (owner, 2026-10-31: «1 в 1 всё как в настоящем использовании»): each frame is a
/// small map made the way the editor makes the real thing, and the renderer draws something new on it as it grows.
/// </summary>
public sealed class CreateDemoTests
{
    [Theory]
    [InlineData(CreateDemoKind.River)]
    [InlineData(CreateDemoKind.Lake)]
    [InlineData(CreateDemoKind.Road)]
    [InlineData(CreateDemoKind.Land)]
    public void Each_demo_grows_frame_by_frame_into_the_real_thing(CreateDemoKind kind)
    {
        var editor = new EditorViewModel(new MapDocument(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(1024, 768, "px", "#FF1E2430"),
                [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF1E2430") { Sea = SeaStyle.NewMapDefault }]),
            null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        var revision = editor.Session.Revision;

        using var first = Render(editor.CreateDemoFrame(kind, 0, 8));
        using var last = Render(editor.CreateDemoFrame(kind, 7, 8));
        var changed = Changed(first, last);
        Assert.True(changed > 0.02, $"{kind}: only {changed:P1} of the strip changed between the first and the last frame");
        Assert.True(Distinct(last) > 40, $"{kind}: the finished frame is nearly flat");
        // The pictures never touch the map itself.
        Assert.Equal(revision, editor.Session.Revision);
        Save(last, kind);
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(EditorViewModel.CreateDemoWidth, EditorViewModel.CreateDemoHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(new SKColor(0x1A, 0x14, 0x0E));
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            IsExport = true, ViewScale = 1, VisibleWorld = new WorldBounds(0, 0, info.Width, info.Height),
        });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static double Changed(SKBitmap a, SKBitmap b)
    {
        var changed = 0;
        for (var y = 0; y < a.Height; y += 2)
            for (var x = 0; x < a.Width; x += 2)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > 24) changed++;
            }
        return changed / (a.Width / 2.0 * (a.Height / 2.0));
    }

    private static int Distinct(SKBitmap bitmap)
    {
        var colours = new HashSet<int>();
        for (var y = 0; y < bitmap.Height; y += 3)
            for (var x = 0; x < bitmap.Width; x += 3)
            {
                var c = bitmap.GetPixel(x, y);
                colours.Add((c.Red >> 3) << 10 | (c.Green >> 3) << 5 | c.Blue >> 3);
            }
        return colours.Count;
    }

    /// <summary>For a look at the frames: set MAP_DEMO_SHOTS to a folder.</summary>
    private static void Save(SKBitmap bitmap, CreateDemoKind kind)
    {
        if (Environment.GetEnvironmentVariable("MAP_DEMO_SHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, $"demo-{kind}.png"));
        bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
    }
}
