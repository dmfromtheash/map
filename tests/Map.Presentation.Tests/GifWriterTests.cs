using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>R31: the GIF of a series is a valid looping animation that decodes back to its frames' colours.</summary>
public sealed class GifWriterTests
{
    [Fact]
    public void A_series_decodes_back_with_every_frame_and_its_colours()
    {
        var colours = new[] { new SKColor(0xC8, 0x45, 0x3B), new SKColor(0x3F, 0xA3, 0x4D), new SKColor(0x2B, 0x58, 0xA0) };
        var frames = colours.Select(c =>
        {
            var bitmap = new SKBitmap(97, 61);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = c };
            canvas.DrawRect(10, 10, 50, 30, paint);
            // A gradient strip, so the palette has more than a few colours and LZW codes grow past 9 bits.
            for (var x = 0; x < 97; x++) { paint.Color = new SKColor((byte)(x * 2), (byte)(255 - x), (byte)(x * 3 % 256)); canvas.DrawRect(x, 50, 1, 11, paint); }
            return bitmap;
        }).ToList();
        using var stream = new MemoryStream();
        GifWriter.Write(stream, frames, 40);
        foreach (var frame in frames) frame.Dispose();

        stream.Position = 0;
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Gif, codec.EncodedFormat);
        Assert.Equal(3, codec.FrameCount);
        Assert.Equal(-1, codec.RepetitionCount); // Skia: -1 = loops forever
        for (var i = 0; i < 3; i++)
        {
            using var decoded = new SKBitmap(new SKImageInfo(97, 61, SKColorType.Rgba8888, SKAlphaType.Premul));
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(decoded.Info, decoded.GetPixels(), new SKCodecOptions(i)));
            var pixel = decoded.GetPixel(30, 20);
            Assert.InRange(Math.Abs(pixel.Red - colours[i].Red) + Math.Abs(pixel.Green - colours[i].Green) + Math.Abs(pixel.Blue - colours[i].Blue), 0, 24);
            Assert.True(decoded.GetPixel(80, 5).Red > 240);
        }
    }
}
