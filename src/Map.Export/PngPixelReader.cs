using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Map.Export;

/// <summary>Sequential RGBA8 PNG reader for Map exports. No full-image allocation.</summary>
internal sealed class PngPixelReader : IDisposable
{
    internal delegate void RowConsumer(ReadOnlySpan<byte> rgba);
    private readonly FileStream _file;
    private readonly int _channels;
    public int Width { get; }
    public int Height { get; }
    /// <summary>RGB file: every pixel is opaque.</summary>
    public bool Opaque => _channels == 3;

    public PngPixelReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try
        {
            Span<byte> header = stackalloc byte[33];
            _file.ReadExactly(header);
            if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || BinaryPrimitives.ReadInt32BigEndian(header[8..]) != 13 || !header[12..16].SequenceEqual("IHDR"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(header[29..]) != ~StreamingPngWriter.UpdateCrc(uint.MaxValue, header[12..29]))
                throw new InvalidDataException("Заголовок PNG повреждён.");
            Width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
            Height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
            if (Width <= 0 || Height <= 0 || Width > ExportEngineSettings.DefaultMaxOutputEdge
                || Height > ExportEngineSettings.DefaultMaxOutputEdge || header[24] != 8 || header[25] is not (2 or 6)
                || !header[26..29].SequenceEqual(new byte[] { 0, 0, 0 }))
                throw new InvalidDataException("Этот просмотр поддерживает PNG RGB/RGBA 8 bit без чересстрочной записи, создаваемый экспортом Map.");
            _channels = header[25] == 6 ? 4 : 3;
        }
        catch { _file.Dispose(); throw; }
    }

    public void ReadRows(RowConsumer consume, CancellationToken token)
    {
        using var chunks = new ImageDataStream(_file, token);
        using var zlib = new ZLibStream(chunks, CompressionMode.Decompress, leaveOpen: true);
        var filtered = new byte[checked(Width * _channels + 1)];
        var previous = new byte[Width * _channels];
        var expanded = _channels == 3 ? new byte[Width * 4] : [];
        for (var y = 0; y < Height; y++)
        {
            token.ThrowIfCancellationRequested();
            zlib.ReadExactly(filtered);
            var row = filtered.AsSpan(1);
            Unfilter(filtered[0], row, previous, _channels);
            if (_channels == 4) consume(row);
            else
            {
                for (var x = 0; x < Width; x++)
                { row.Slice(x * 3, 3).CopyTo(expanded.AsSpan(x * 4)); expanded[x * 4 + 3] = 255; }
                consume(expanded);
            }
            row.CopyTo(previous);
        }
        if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG содержит лишние строки.");
        Span<byte> remaining = stackalloc byte[1024];
        while (chunks.Read(remaining) != 0) { }
    }

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int channels)
    {
        if (filter == 0) return;
        if (filter == 1 && channels == 4 && BitConverter.IsLittleEndian)
        {
            var pixels = MemoryMarshal.Cast<byte, uint>(row);
            uint left = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                var delta = pixels[i];
                left = (((delta & 0x00ff00ff) + (left & 0x00ff00ff)) & 0x00ff00ff)
                    | (((((delta >> 8) & 0x00ff00ff) + ((left >> 8) & 0x00ff00ff)) & 0x00ff00ff) << 8);
                pixels[i] = left;
            }
            return;
        }
        if (filter > 4) throw new InvalidDataException("Неизвестный фильтр PNG.");
        for (var i = 0; i < row.Length; i++)
        {
            var left = i >= channels ? row[i - channels] : 0;
            var up = previous[i];
            var corner = i >= channels ? previous[i - channels] : 0;
            var predictor = filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                _ => Paeth(left, up, corner),
            };
            row[i] = unchecked((byte)(row[i] + predictor));
        }
    }

    private static int Paeth(int left, int up, int corner)
    {
        var p = left + up - corner;
        var a = Math.Abs(p - left); var b = Math.Abs(p - up); var c = Math.Abs(p - corner);
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    public void Dispose() => _file.Dispose();

    private sealed class ImageDataStream(FileStream source, CancellationToken token) : Stream
    {
        private int _remaining;
        private uint _crc;
        private bool _inChunk, _ended;
        private readonly byte[] _skip = new byte[65536];
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _ended) return 0;
            token.ThrowIfCancellationRequested();
            Span<byte> header = stackalloc byte[8];
            Span<byte> crc = stackalloc byte[4];
            while (_remaining == 0)
            {
                if (_inChunk)
                {
                    source.ReadExactly(crc);
                    if (BinaryPrimitives.ReadUInt32BigEndian(crc) != ~_crc)
                        throw new InvalidDataException("Контрольная сумма PNG не совпала.");
                    _inChunk = false;
                }
                source.ReadExactly(header);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Недопустимая длина блока PNG.");
                _crc = StreamingPngWriter.UpdateCrc(uint.MaxValue, header[4..]);
                if (header[4..].SequenceEqual("IEND"u8) && length == 0)
                {
                    source.ReadExactly(crc);
                    if (BinaryPrimitives.ReadUInt32BigEndian(crc) != ~_crc || source.Position != source.Length)
                        throw new InvalidDataException("Окончание PNG повреждено.");
                    _ended = true;
                    return 0;
                }
                if ((header[4] & 32) != 0)
                {
                    if (header[4..].SequenceEqual("tRNS"u8) || header[4..].SequenceEqual("acTL"u8))
                        throw new InvalidDataException("PNG с ключевой прозрачностью или анимацией пока не поддерживается просмотром Map.");
                    // Ancillary metadata is not pixel data. Validate it without retaining it.
                    for (var left = length; left > 0;)
                    {
                        token.ThrowIfCancellationRequested();
                        var skipCount = Math.Min(left, _skip.Length);
                        source.ReadExactly(_skip.AsSpan(0, skipCount));
                        _crc = StreamingPngWriter.UpdateCrc(_crc, _skip.AsSpan(0, skipCount));
                        left -= skipCount;
                    }
                    source.ReadExactly(crc);
                    if (BinaryPrimitives.ReadUInt32BigEndian(crc) != ~_crc)
                        throw new InvalidDataException("Контрольная сумма метаданных PNG не совпала.");
                    continue;
                }
                if (!header[4..].SequenceEqual("IDAT"u8))
                    throw new InvalidDataException("Неподдерживаемая последовательность блоков PNG Map.");
                _inChunk = true;
                _remaining = length;
            }
            var count = Math.Min(buffer.Length, _remaining);
            source.ReadExactly(buffer[..count]);
            _crc = StreamingPngWriter.UpdateCrc(_crc, buffer[..count]);
            _remaining -= count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
