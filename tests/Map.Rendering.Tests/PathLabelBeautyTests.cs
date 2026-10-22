using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R07 «Красота» of labels along a line: a name on a river leans in italic, opens its letters out
/// along a long line and sits on a soft halo of the paper; a bend never breaks it apart.
/// </summary>
public sealed class PathLabelBeautyTests
{
    private static readonly LayerId Labels = LayerId.New();

    [Fact]
    public void A_river_name_draws_along_a_bend_with_its_halo()
    {
        using var bitmap = Render(out var label);
        Assert.True(label.Text!.Path!.Baseline.Length >= 2);
        // The halo is the paper colour around the letters: light pixels where the dark water would be.
        var dark = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 70 && c.Green < 70 && c.Blue < 80) dark++;
            }
        Assert.True(dark > 150, $"ink of the name: {dark}");
    }

    /// <summary>MAP_LABEL_FRAMES=&lt;folder&gt;: a labelled bend for the eye.</summary>
    [Fact]
    public void Frames_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("MAP_LABEL_FRAMES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var bitmap = Render(out _, 2);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, "river-label-2x.png"), data.ToArray());
    }

    private static SKBitmap Render(out SceneObject label, double scale = 1)
    {
        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Natural() with { TextureId = null },
            Channels = [RiverEditor.NewChannel([new(40, 300), new(200, 250), new(330, 180), new(470, 200), new(600, 120)], 26)],
        };
        var water = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var draft = new MapDocument(DocumentId.New(), "labels", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(640, 400, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), water,
             new ObjectLayer(Labels, "Подписи", true, false, 1, [])]);
        var text = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Серебрянка", 0, 0, 1, 1, 0, "#FF1F2A36", "#FFE9E1CC", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Серебрянка", FontSize = 22, FillColor = "#FF1F2A36", OutlineColor = "#FFE9E1CC", OutlineWidth = 3,
                Path = new TextPathBinding { Target = TextPathTarget.River, TargetId = river.Id, PartId = river.Channels[0].Id, Side = TextPathSide.Above },
            },
        };
        var laid = TextOnPath.Layout(draft, text);
        label = laid;
        var document = draft.WithLayer(Labels, l => ((ObjectLayer)l) with { Objects = [laid] });
        var info = new SKImageInfo((int)(640 * scale), (int)(400 * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
