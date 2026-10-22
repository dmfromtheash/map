using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace Map.Export;

/// <summary>Non-interlaced RGBA8 (or, for an opaque picture, RGB8) PNG with a single zlib stream
/// across bounded IDAT chunks. Rows are supplied in image order as RGBA; neither encoding nor
/// read-back owns a full image.</summary>
internal sealed class StreamingPngWriter : IDisposable
{
    /// <summary>An opaque writer met a pixel that is not fully opaque; the caller writes RGBA instead.</summary>
    internal sealed class NotOpaqueException() : InvalidOperationException("В картинке есть прозрачные пиксели.");

    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();
    private readonly Stream _destination;
    private readonly IdatStream _chunks;
    private readonly ZLibStream _zlib;
    private readonly byte[] _filtered;
    private readonly int _height;
    private readonly bool _opaque;
    private int _rows;
    private bool _complete;

    /// <param name="opaque">Write RGB: a quarter fewer bytes for a picture without transparency (R08 follow-up,
    /// the giant map is 100 % opaque). A row with a non-opaque pixel throws <see cref="NotOpaqueException"/>.</param>
    /// <param name="level">Compression level; it changes size and time only, never a pixel.</param>
    public StreamingPngWriter(Stream destination, int width, int height, bool opaque = false,
        CompressionLevel level = CompressionLevel.Fastest)
    {
        _destination = destination;
        _height = height;
        _opaque = opaque;
        _filtered = new byte[checked(width * (opaque ? 3 : 4) + 1)];
        destination.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = (byte)(opaque ? 2 : 6); // RGBA whenever a hidden/transparent base layer may leave alpha.
        WriteChunk(destination, "IHDR"u8, header);
        _chunks = new IdatStream(destination);
        _zlib = new ZLibStream(_chunks, level, leaveOpen: true);
    }

    public void WriteRow(ReadOnlySpan<byte> rgba)
    {
        if (_complete || _rows >= _height || rgba.Length != (_filtered.Length - 1) / (_opaque ? 3 : 4) * 4)
            throw new InvalidOperationException("Неверное число или размер строк PNG.");
        if (_opaque)
        {
            WriteOpaqueRow(rgba);
            return;
        }
        _filtered[0] = 1; // Sub; filter the complete row, including boundaries between render tiles.
        rgba[..4].CopyTo(_filtered.AsSpan(1));
        var i = 4;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= rgba.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                (new Vector<byte>(rgba[i..]) - new Vector<byte>(rgba[(i - 4)..]))
                    .CopyTo(_filtered.AsSpan(i + 1));
        }
        for (; i < rgba.Length; i++)
            _filtered[i + 1] = unchecked((byte)(rgba[i] - rgba[i - 4]));
        _zlib.Write(_filtered);
        _rows++;
    }

    private void WriteOpaqueRow(ReadOnlySpan<byte> rgba)
    {
        // Sub filter over RGB triplets; the first pixel is stored as is.
        _filtered[0] = 1;
        byte r0 = 0, g0 = 0, b0 = 0;
        for (int s = 0, d = 1; s < rgba.Length; s += 4, d += 3)
        {
            if (rgba[s + 3] != 255) throw new NotOpaqueException();
            byte r = rgba[s], g = rgba[s + 1], b = rgba[s + 2];
            _filtered[d] = unchecked((byte)(r - r0));
            _filtered[d + 1] = unchecked((byte)(g - g0));
            _filtered[d + 2] = unchecked((byte)(b - b0));
            r0 = r; g0 = g; b0 = b;
        }
        _zlib.Write(_filtered);
        _rows++;
    }

    public void Complete()
    {
        if (_complete || _rows != _height)
            throw new InvalidOperationException("PNG не содержит все строки результата.");
        _zlib.Dispose();
        _chunks.Flush();
        WriteChunk(_destination, "IEND"u8, []);
        _complete = true;
    }

    public void Dispose()
    {
        if (!_complete) _zlib.Dispose();
    }

    // Own-output integrity check, not a general image decoder. Standard decoders are exercised
    // in tests; here every chunk is checked without SKData or full-canvas allocation.
    public static void ValidateFile(string path, int width, int height, CancellationToken cancellationToken, bool opaque = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        if (!header.SequenceEqual(Signature)) throw new InvalidDataException("Неверная сигнатура PNG.");
        var buffer = new byte[65536];
        var first = true;
        var hasData = false;
        Span<byte> crc = stackalloc byte[4];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            var kind = header[4..];
            if (length < 0 || length > buffer.Length)
                throw new InvalidDataException("Неверная длина блока PNG.");
            var data = buffer.AsSpan(0, length);
            stream.ReadExactly(data);
            var expected = ~UpdateCrc(UpdateCrc(uint.MaxValue, kind), data);
            stream.ReadExactly(crc);
            if (BinaryPrimitives.ReadUInt32BigEndian(crc) != expected)
                throw new InvalidDataException("Контрольная сумма PNG не совпала после записи.");
            if (first)
            {
                if (!kind.SequenceEqual("IHDR"u8) || length != 13
                    || BinaryPrimitives.ReadInt32BigEndian(data) != width
                    || BinaryPrimitives.ReadInt32BigEndian(data[4..]) != height
                    || !data[8..].SequenceEqual(new byte[] { 8, (byte)(opaque ? 2 : 6), 0, 0, 0 }))
                    throw new InvalidDataException("Заголовок PNG не совпал с планом.");
                first = false;
            }
            else if (kind.SequenceEqual("IDAT"u8))
                hasData |= length > 0;
            else if (kind.SequenceEqual("IEND"u8) && length == 0 && hasData && stream.Position == stream.Length)
                return;
            else
                throw new InvalidDataException("Неверная последовательность блоков PNG.");
        }
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        kind.CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ~UpdateCrc(UpdateCrc(uint.MaxValue, kind), data));
        stream.Write(crc);
    }

    internal static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var value = n;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[n] = value;
        }
        return table;
    }

    private sealed class IdatStream(Stream destination) : Stream
    {
        private readonly byte[] _buffer = new byte[65536];
        private int _used;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var count = Math.Min(_buffer.Length - _used, bytes.Length);
                bytes[..count].CopyTo(_buffer.AsSpan(_used));
                _used += count;
                bytes = bytes[count..];
                if (_used == _buffer.Length) Flush();
            }
        }
        public override void Flush()
        {
            if (_used == 0) return;
            WriteChunk(destination, "IDAT"u8, _buffer.AsSpan(0, _used));
            _used = 0;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
