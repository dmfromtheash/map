using SkiaSharp;

namespace Map.Export;

/// <summary>
/// A small animated GIF89a writer (R31 «серия по годам»): one palette of up to 256 colours for all
/// frames, chosen by median cut over a sample of their pixels, each pixel mapped to its nearest
/// palette colour, frames compressed with the GIF variant of LZW, looping forever.
/// </summary>
public static class GifWriter
{
    public static void Write(Stream stream, IReadOnlyList<SKBitmap> frames, int delayHundredths = 50)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (frames.Count == 0) throw new ArgumentException("Нет кадров.", nameof(frames));
        var (width, height) = (frames[0].Width, frames[0].Height);
        if (frames.Any(f => f.Width != width || f.Height != height)) throw new ArgumentException("Кадры разного размера.", nameof(frames));
        if (width > ushort.MaxValue || height > ushort.MaxValue) throw new ArgumentException("Кадр больше 65535 пикселей.", nameof(frames));

        var palette = Palette(frames);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("GIF89a"u8);
        writer.Write((ushort)width); writer.Write((ushort)height);
        writer.Write((byte)0xF7); // global table, 8 bits, 256 entries
        writer.Write((byte)0); writer.Write((byte)0);
        for (var i = 0; i < 256; i++)
        {
            var c = i < palette.Length ? palette[i] : SKColors.Black;
            writer.Write(c.Red); writer.Write(c.Green); writer.Write(c.Blue);
        }
        // NETSCAPE2.0: loop forever.
        writer.Write(new byte[] { 0x21, 0xFF, 0x0B }); writer.Write("NETSCAPE2.0"u8); writer.Write(new byte[] { 3, 1, 0, 0, 0 });

        var lookup = new Dictionary<uint, byte>();
        foreach (var frame in frames)
        {
            writer.Write(new byte[] { 0x21, 0xF9, 4, 0 }); writer.Write((ushort)delayHundredths); writer.Write(new byte[] { 0, 0 });
            writer.Write((byte)0x2C); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)width); writer.Write((ushort)height); writer.Write((byte)0);
            var indices = new byte[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = frame.GetPixel(x, y);
                var key = (uint)((c.Red << 16) | (c.Green << 8) | c.Blue);
                if (!lookup.TryGetValue(key, out var index)) { index = Nearest(palette, c); lookup[key] = index; }
                indices[(y * width) + x] = index;
            }
            Lzw(writer, indices, 8);
        }
        writer.Write((byte)0x3B);
    }

    private static byte Nearest(SKColor[] palette, SKColor c)
    {
        var (best, bestDistance) = (0, int.MaxValue);
        for (var i = 0; i < palette.Length; i++)
        {
            var (dr, dg, db) = (palette[i].Red - c.Red, palette[i].Green - c.Green, palette[i].Blue - c.Blue);
            var distance = (dr * dr * 3) + (dg * dg * 4) + (db * db * 2);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return (byte)best;
    }

    /// <summary>Median cut over at most ~200 000 sampled pixels of all frames.</summary>
    private static SKColor[] Palette(IReadOnlyList<SKBitmap> frames)
    {
        var total = frames.Sum(f => (long)f.Width * f.Height);
        var step = Math.Max(1, (int)(total / 200_000));
        var samples = new List<(byte R, byte G, byte B)>();
        long n = 0;
        foreach (var frame in frames)
            for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                if (n++ % step == 0) { var c = frame.GetPixel(x, y); samples.Add((c.Red, c.Green, c.Blue)); }

        var boxes = new List<List<(byte R, byte G, byte B)>> { samples };
        while (boxes.Count < 256)
        {
            var (index, channel, range) = (-1, 0, 0);
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                foreach (var ch in new[] { 0, 1, 2 })
                {
                    int lo = 255, hi = 0;
                    foreach (var p in boxes[i]) { var v = ch == 0 ? p.R : ch == 1 ? p.G : p.B; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                    if (hi - lo > range) (index, channel, range) = (i, ch, hi - lo);
                }
            }
            if (index < 0 || range == 0) break;
            var box = boxes[index];
            box.Sort((a, b) => channel == 0 ? a.R.CompareTo(b.R) : channel == 1 ? a.G.CompareTo(b.G) : a.B.CompareTo(b.B));
            boxes[index] = box[..(box.Count / 2)];
            boxes.Add(box[(box.Count / 2)..]);
        }
        return [.. boxes.Where(b => b.Count > 0).Select(b => new SKColor(
            (byte)b.Average(p => p.R), (byte)b.Average(p => p.G), (byte)b.Average(p => p.B)))];
    }

    /// <summary>GIF LZW: variable code width from minimum+1 up to 12 bits, clear code on a full table, data in sub-blocks of 255.</summary>
    private static void Lzw(BinaryWriter writer, byte[] indices, int minimumCodeSize)
    {
        writer.Write((byte)minimumCodeSize);
        var clear = 1 << minimumCodeSize;
        var end = clear + 1;
        var output = new List<byte>();
        int bitBuffer = 0, bitCount = 0;
        var codeSize = minimumCodeSize + 1;
        void Emit(int code)
        {
            bitBuffer |= code << bitCount; bitCount += codeSize;
            while (bitCount >= 8) { output.Add((byte)(bitBuffer & 0xFF)); bitBuffer >>= 8; bitCount -= 8; }
        }

        var table = new Dictionary<int, int>();
        var next = end + 1;
        Emit(clear);
        var prefix = -1;
        foreach (var symbol in indices)
        {
            if (prefix < 0) { prefix = symbol; continue; }
            var key = (prefix << 8) | symbol;
            if (table.TryGetValue(key, out var code)) { prefix = code; continue; }
            Emit(prefix);
            table[key] = next++;
            if (next - 1 >= (1 << codeSize) && codeSize < 12) codeSize++;
            if (next - 1 == 4095)
            {
                // The table is full: start a new one, as decoders expect.
                Emit(clear);
                table.Clear(); next = end + 1; codeSize = minimumCodeSize + 1;
            }
            prefix = symbol;
        }
        if (prefix >= 0) Emit(prefix);
        Emit(end);
        if (bitCount > 0) output.Add((byte)(bitBuffer & 0xFF));

        for (var i = 0; i < output.Count; i += 255)
        {
            var size = Math.Min(255, output.Count - i);
            writer.Write((byte)size);
            for (var j = 0; j < size; j++) writer.Write(output[i + j]);
        }
        writer.Write((byte)0);
    }
}
