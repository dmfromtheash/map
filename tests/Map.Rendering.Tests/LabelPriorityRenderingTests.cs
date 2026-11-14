using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R07b: far away the less important labels stop being drawn first; the selected one always shows.</summary>
public sealed class LabelPriorityRenderingTests
{
    private const string Ink = "#FFC00000";

    [Fact]
    public async Task Cold_interactive_frame_is_pending_and_refines_to_the_exact_pixels()
    {
        var first = Label("Первый", -280, 80, 3);
        var second = Label("Второй", -100, 80, 3);
        var third = Label("Третий", 80, 80, 3);
        var distant = Enumerable.Range(0, 997).Select(i => Label("Вдали", 10000 + i * 512, 10000, 3)).ToArray();
        var document = Document([first, second, third, .. distant]) with
        { Labels = new LabelSettings { ResolveOverlaps = true } };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stats = new RenderStats();
        using (var pending = Render(document, 1, null, () => ready.TrySetResult(), stats))
            Assert.True(stats.DetailTilesPending > 0);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(LabelPriority.IsPrepared(document));
        using var settled = Render(document, 1, null, () => { });
        using var expected = Render(Document([first, third, .. distant]), 1, null);
        Assert.Equal(expected.Bytes, settled.Bytes);
    }

    [Fact]
    public void Overlap_chain_and_label_delta_match_explicit_visibility_pixel_for_pixel()
    {
        var first = Label("Первый", -280, 80, 3);
        var second = Label("Второй", -100, 80, 3);
        var third = Label("Третий", 80, 80, 3);
        var document = Document(first, second, third) with { Labels = new LabelSettings { ResolveOverlaps = true } };
        using (var actual = Render(document, 1, null))
        using (var expected = Render(Document(first, third), 1, null))
            Assert.Equal(expected.Bytes, actual.Bytes);

        // The offscreen first label still hides the second. Moving it away changes that
        // dependency: now the second stays and the third hides, with the same font sizes.
        var layer = (ObjectLayer)document.Layers[1];
        var moved = SceneObjectClone.At(first, -1000, first.Y);
        var revised = layer with { Objects = layer.ObjectStorage.ReplaceAt(0, moved) };
        ObjectLayerChanges.RegisterDerived(layer, revised, [0], [0]);
        var snapshot = document with { Layers = [document.Layers[0], revised] };
        using (var actual = Render(snapshot, 1, null))
        using (var expected = Render(Document(moved, second), 1, null))
            Assert.Equal(expected.Bytes, actual.Bytes);

        // Selection still exposes a hidden label.
        using var selected = Render(snapshot, 1, third.Id);
        using var all = Render(Document(moved, second, third), 1, third.Id);
        Assert.Equal(all.Bytes, selected.Bytes);
    }

    [Fact]
    public void Far_away_importance_one_hides_before_importance_five()
    {
        var minor = Label("Хутор", 100, 200, 1);
        var major = Label("Империя", 900, 200, 5);
        var document = Document(minor, major) with { Labels = new LabelSettings { ByScale = true } };

        using (var near = Render(document, 1, null))
            Assert.True(Inked(near, minor, 1) > 50);

        // At a quarter (below importance 1's 0.6 and 2's 0.35, above 5's 0) only the empire stays.
        using (var far = Render(document, .25, null))
        {
            Assert.Equal(0, Inked(far, minor, .25));
            Assert.True(Inked(far, major, .25) > 10);
        }

        // Selected, the hidden label shows: the user sees what they edit.
        using (var selected = Render(document, .25, minor.Id))
            Assert.True(Inked(selected, minor, .25) > 10);

        // Without «Подписи по масштабу» nothing hides.
        using var plain = Render(Document(minor, major), .25, null);
        Assert.True(Inked(plain, minor, .25) > 10);
    }

    private static SceneObject Label(string content, double x, double y, int importance) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Text, content, x, y, 260, 90, 0, Ink, Ink, 0, true, false)
        { Text = new TextStyle { Content = content, FontSize = 64, FontWeight = 700, FillColor = Ink, Importance = importance } };

    private static MapDocument Document(params SceneObject[] labels) =>
        new(DocumentId.New(), "Labels", DateTimeOffset.UnixEpoch, new CanvasDefinition(1600, 1600, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, labels)]);

    private static SKBitmap Render(MapDocument document, double scale, ObjectId? selected,
        Action? ready = null, RenderStats? stats = null)
    {
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            ViewScale = scale, SelectedObjectId = selected, VisibleWorld = new(0, 0, 400 / scale, 400 / scale),
            RequestRefinedFrame = ready, Stats = stats,
        });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    /// <summary>Red pixels inside the label's box on screen.</summary>
    private static int Inked(SKBitmap bitmap, SceneObject label, double scale)
    {
        var count = 0;
        for (var y = (int)(label.Y * scale); y < Math.Min(bitmap.Height, (label.Y + label.Height) * scale); y++)
        for (var x = (int)(label.X * scale); x < Math.Min(bitmap.Width, (label.X + label.Width) * scale); x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red > 150 && c.Green < 110 && c.Blue < 110) count++;
        }
        return count;
    }
}
