using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R07: a bound label is drawn along its line, not as a horizontal block.</summary>
public sealed class TextOnPathRenderingTests
{
    [Fact]
    public void A_label_on_a_diagonal_river_lies_along_the_diagonal()
    {
        var river = new River
        {
            Name = "Река", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new(30, 370), new(370, 30)], 6)],
        };
        var water = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var draft = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Река", 0, 0, 1, 1, 0, "#FF000000", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "ЖЖЖЖЖЖЖЖ", FontSize = 28, FontWeight = 700, FillColor = "#FFFF0000", OutlineWidth = 0, ShadowOpacity = 0,
                Path = new TextPathBinding { Target = TextPathTarget.River, TargetId = river.Id, PartId = river.Channels[0].Id, Side = TextPathSide.On },
            },
        };
        var labels = new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "label", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 400, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), water, labels]);
        var label = TextOnPath.Layout(document, draft);
        document = document.WithLayer(labels.Id, l => ((ObjectLayer)l) with { Objects = [label] });

        using var bitmap = Render(document);
        var red = new List<(int X, int Y)>();
        for (var y = 0; y < 400; y++)
        for (var x = 0; x < 400; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red > 200 && c.Green < 80 && c.Blue < 80) red.Add((x, y));
        }

        Assert.True(red.Count > 400, $"red pixels: {red.Count}");
        // Every glyph pixel stays close to the line x + y = 400 …
        var off = red.Max(p => Math.Abs(p.X + p.Y - 400) / Math.Sqrt(2));
        Assert.True(off < 30, $"farthest red pixel from the river: {off:0.#}");
        // … and the run stretches along it, which a horizontal block of the same text could not do.
        var along = red.Max(p => (p.X - p.Y) / Math.Sqrt(2)) - red.Min(p => (p.X - p.Y) / Math.Sqrt(2));
        Assert.True(along > 120, $"extent along the river: {along:0.#}");
        Assert.True(red.Max(p => p.Y) - red.Min(p => p.Y) > 80, "a diagonal label spans many rows");
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(400, 400);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = 1 });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
