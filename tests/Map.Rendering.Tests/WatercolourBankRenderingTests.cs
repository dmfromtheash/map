using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class WatercolourBankRenderingTests
{
    private static River MakeRiver() => new()
    {
        Channels = [RiverEditor.NewChannel([new(24, 94), new(170, 128), new(310, 94)], 42)
            with { Id = Guid.Parse("9304c7db-c3bc-4eca-9702-08b53df14592") }],
        Style = RiverStyle.Illustrated() with
        {
            TextureId = "watercolour-water", TextureOpacity = 0,
            BankReliefWidth = 12, BankReliefColor = "#FF9C8973", BankHatchOpacity = 0,
            BankWidth = 0, ShallowWidth = 0, HighlightOpacity = 0, BankShadowOpacity = 0,
            CutsLand = false, ClipToLand = false, TaperSource = false,
        },
    };

    private static SKBitmap Draw(River river, bool split = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(340, 220))!;
        surface.Canvas.Clear(SKColors.Transparent);
        if (split)
        {
            foreach (var rectangle in new[] { new SKRect(0, 0, 170, 220), new SKRect(170, 0, 340, 220) })
            {
                surface.Canvas.Save(); surface.Canvas.ClipRect(rectangle, antialias: false);
                RiverRenderer.Draw(surface.Canvas, river, 1, false);
                surface.Canvas.Restore();
            }
        }
        else RiverRenderer.Draw(surface.Canvas, river, 1, false);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Painted_bank_reuses_cache_without_amplifying_clip_edge_variation(double shallowWidth)
    {
        var river = MakeRiver();
        river = river with { Style = river.Style with { ShallowWidth = shallowWidth } };
        using var whole = Draw(river);
        using var repeated = Draw(river);
        using var split = Draw(river, split: true);
        Assert.Equal(whole.Bytes, repeated.Bytes);
        using var plainWhole = Draw(river with { Style = river.Style with { TextureId = null } });
        using var plainSplit = Draw(river with { Style = river.Style with { TextureId = null } }, split: true);
        var paintedDifference = whole.Bytes.Zip(split.Bytes, (x, y) => Math.Abs(x-y)).ToArray();
        var plainDifference = plainWhole.Bytes.Zip(plainSplit.Bytes, (x, y) => Math.Abs(x-y)).ToArray();
        // Skia retessellates a few antialiased outline pixels when a path is clipped,
        // including near its caps. The new irregular fringe has different edge pixels,
        // so compare the magnitude and sparse extent, not their old exact locations.
        Assert.True(paintedDifference.Max() <= plainDifference.Max());
        Assert.True(paintedDifference.Select((difference, i) => (difference, pixel: i / 4))
            .Where(x => x.difference != 0).Select(x => x.pixel).Distinct().Count() < whole.Height / 10);
        Assert.True(paintedDifference.Sum() < whole.Width * whole.Height * .005,
            "Clipped rendering changed more than sparse edge coverage.");
    }

    [Fact]
    public void Pigment_adds_bank_volume_without_repainting_the_water_core()
    {
        var river = MakeRiver();
        using var painted = Draw(river);
        using var flat = Draw(river with { Style = river.Style with { TextureId = null } });
        Assert.Equal(flat.GetPixel(170, 128), painted.GetPixel(170, 128));
        var changedOpaqueBankPixels = 0;
        for (var y = 0; y < painted.Height; y++)
            for (var x = 0; x < painted.Width; x++)
            {
                var before = flat.GetPixel(x, y); var after = painted.GetPixel(x, y);
                if (before.Alpha == 255 && after.Alpha == 255 && before != after) changedOpaqueBankPixels++;
            }
        Assert.True(changedOpaqueBankPixels > 300, $"Only {changedOpaqueBankPixels} bank pixels received pigment.");
    }
}
