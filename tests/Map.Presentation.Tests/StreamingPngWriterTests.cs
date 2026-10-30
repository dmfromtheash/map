using CsCheck;
using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>
/// The PNG writer chooses a filter per row and filters inside parallel compression blocks
/// (owner, 2026-10-30: "smaller, the pixels must not change"): any picture, opaque or not, written
/// by one worker or several, decodes to exactly the pixels it was given.
/// </summary>
public sealed class StreamingPngWriterTests
{
    private static readonly Gen<(int Width, int Height, bool Opaque, int Workers, int Seed, int Kind)> Pictures =
        Gen.Select(Gen.Int[1, 300], Gen.Int[1, 120], Gen.Bool, Gen.Int[0, 2].Select(i => 1 << i), Gen.Int, Gen.Int[0, 3]);

    [Fact]
    public void Every_picture_decodes_to_the_pixels_it_was_written_with()
    {
        Pictures.Sample(p =>
        {
            var rgba = Picture(p.Width, p.Height, p.Opaque, p.Seed, p.Kind);
            var decoded = Decode(Write(rgba, p.Width, p.Height, p.Opaque, p.Workers, blockRows: 7));
            var first = decoded.AsSpan().CommonPrefixLength(rgba);
            Assert.True(first == rgba.Length, $"{p}: first difference at byte {first} of {rgba.Length}: {(first < decoded.Length ? decoded[first] : -1)} vs {rgba[Math.Min(first, rgba.Length - 1)]}");
        }, iter: 300);
    }

    [Fact]
    public void One_worker_and_several_write_the_same_rows()
    {
        // Filter choice depends on the pixels only, so the unpacked rows are byte-identical.
        var rgba = Picture(257, 300, true, 11, 3);
        var serial = Write(rgba, 257, 300, true, 1, blockRows: 0);
        var parallel = Write(rgba, 257, 300, true, 4, blockRows: 0);
        Assert.Equal(Decode(serial), Decode(parallel));
        Assert.Equal(Inflate(serial), Inflate(parallel));
        Assert.Contains(Inflate(serial).Chunk(257 * 3 + 1), row => row[0] != 1); // not Sub alone any more
    }

    /// <summary>Noise, gradients, flat areas and stripes: each favours a different filter.</summary>
    private static byte[] Picture(int width, int height, bool opaque, int seed, int kind)
    {
        var random = new Random(seed);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            for (var c = 0; c < 3; c++)
                rgba[i + c] = kind switch
                {
                    0 => (byte)random.Next(256),
                    1 => (byte)(x * (c + 1) + y * 3),
                    2 => (byte)(x / 17 * 40 + c * 60),
                    _ => (byte)((y % 5 == 0 ? 200 : 30) + random.Next(4)),
                };
            rgba[i + 3] = opaque ? (byte)255 : (byte)random.Next(256);
        }
        return rgba;
    }

    private static byte[] Write(byte[] rgba, int width, int height, bool opaque, int workers, int blockRows)
    {
        using var stream = new MemoryStream();
        using (var png = new StreamingPngWriter(stream, width, height, opaque,
            System.IO.Compression.CompressionLevel.Optimal, workers, blockRows))
        {
            for (var y = 0; y < height; y++) png.WriteRow(rgba.AsSpan(y * width * 4, width * 4));
            png.Complete();
        }
        return stream.ToArray();
    }

    /// <summary>Straight (unpremultiplied) RGBA, so colour under partial transparency is compared too.</summary>
    private static byte[] Decode(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png));
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));
        return bitmap.GetPixelSpan().ToArray();
    }

    private static byte[] Inflate(byte[] png)
    {
        using var idat = new MemoryStream();
        for (var at = 8; at < png.Length;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8)) idat.Write(png, at + 8, length);
            at += length + 12;
        }
        idat.Position = 0;
        using var rows = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        using var all = new MemoryStream();
        rows.CopyTo(all);
        return all.ToArray();
    }
}
