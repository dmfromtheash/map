using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class SceneRenderer
{
    /// <summary>
    /// Bump when the chunk raster changes for the same inputs in a way the assembly identities below cannot see
    /// (they already change with any rebuilt Map.Rendering code and any other Skia).
    /// </summary>
    internal const int OverviewChunkKeyFormat = 1;

    /// <summary>
    /// The drawing code a kept chunk raster was made by: the key format, the Map.Rendering module (a deterministic
    /// build gives the same id for the same code, so any change of the drawing code makes new keys) and Skia, managed
    /// and native. A map whose program was updated rasters its chunks once more; never an old picture.
    /// </summary>
    internal static readonly string OverviewChunkRenderVersion = string.Join('|', OverviewChunkKeyFormat,
        typeof(SceneRenderer).Module.ModuleVersionId.ToString("N"), typeof(SKImage).Assembly.GetName().Version,
        SkiaSharpVersion.Native);

    private static readonly ConditionalWeakTable<StampStyle, byte[]> StampStyleDigests = [];

    /// <summary>
    /// The disk key of a chunk raster: SHA-256 over everything <see cref="OverviewVariant.BuildImage"/> draws from -
    /// the render version, the zoom bucket and its scale, the chunk bounds and pixel size, and every draw step in
    /// order: each dot run's colour (the palette tint or the object's own colour, with its opacity) and points, and
    /// for each painted stamp its geometry, rotation, opacity, its whole style and the identity of the asset's
    /// pixels. Null: the raster is not to be kept on disk - a painted asset whose pixel identity the source does not
    /// know, or a clip-masked stamp over a non-empty foreground (its pixels depend on the foreground paint).
    /// </summary>
    internal static byte[]? OverviewChunkDiskKey(OverviewVariant variant, int zoomBucket, StampForegroundSurface? foreground,
        IStampImageSource source, string? renderVersion = null)
    {
        var writer = new ArrayBufferWriter<byte>(256 + variant.Steps.Length * 24);
        WriteString(writer, "map-overview-chunk");
        WriteString(writer, renderVersion ?? OverviewChunkRenderVersion);
        WriteInt(writer, zoomBucket);
        WriteDouble(writer, variant.BucketScale);
        var bounds = variant.Bounds;
        WriteFloats(writer, [bounds.Left, bounds.Top, bounds.Right, bounds.Bottom]);
        var (width, height) = variant.PixelSize;
        WriteInt(writer, width);
        WriteInt(writer, height);
        WriteByte(writer, variant.Painted ? (byte)1 : (byte)0);
        var steps = variant.Steps;
        WriteInt(writer, steps.Length);
        // The pixel identity of each painted asset, written once after the steps (the style digest names the asset).
        Dictionary<string, string>? identities = null;
        StampStyle? lastStyle = null;
        byte[] lastDigest = [];
        foreach (ref readonly var step in steps)
        {
            if (step.Mountain is { } mountain)
            {
                if (mountain.Stamp is not { } stamp) return null;
                if (stamp.ClipMaskEnabled && stamp.MaskAssetSha256 is not null && foreground is { IsEmpty: false }) return null;
                identities ??= new(StringComparer.Ordinal);
                if (!identities.ContainsKey(stamp.AssetSha256))
                {
                    if (source.PixelIdentity(stamp.AssetSha256) is not { } identity) return null;
                    identities[stamp.AssetSha256] = identity;
                }
                if (!ReferenceEquals(stamp, lastStyle))
                {
                    lastStyle = stamp;
                    lastDigest = StampStyleDigests.GetValue(stamp, static style =>
                        SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(style)));
                }
                const int size = 1 + 4 + 1 + 6 * 8 + 32;
                var span = writer.GetSpan(size);
                span[0] = 3;
                BinaryPrimitives.WriteInt32LittleEndian(span[1..], (int)mountain.Kind);
                span[5] = mountain.Visible ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteDoubleLittleEndian(span[6..], mountain.X);
                BinaryPrimitives.WriteDoubleLittleEndian(span[14..], mountain.Y);
                BinaryPrimitives.WriteDoubleLittleEndian(span[22..], mountain.Width);
                BinaryPrimitives.WriteDoubleLittleEndian(span[30..], mountain.Height);
                BinaryPrimitives.WriteDoubleLittleEndian(span[38..], mountain.RotationDegrees);
                BinaryPrimitives.WriteDoubleLittleEndian(span[46..], mountain.ObjectOpacity);
                lastDigest.CopyTo(span[54..]);
                writer.Advance(size);
                continue;
            }
            if (step.Points is { } points)
            {
                WriteByte(writer, 2);
                WriteInt(writer, (int)(uint)step.Color);
                WriteInt(writer, points.Length);
                writer.Write(MemoryMarshal.AsBytes(points.AsSpan()));
            }
            else
            {
                var span = writer.GetSpan(13);
                span[0] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(span[1..], (uint)step.Color);
                BinaryPrimitives.WriteSingleLittleEndian(span[5..], step.Point.X);
                BinaryPrimitives.WriteSingleLittleEndian(span[9..], step.Point.Y);
                writer.Advance(13);
            }
        }
        if (identities is not null)
            foreach (var (sha, identity) in identities.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteString(writer, sha);
                WriteString(writer, identity);
            }
        return SHA256.HashData(writer.WrittenSpan);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt(ArrayBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    private static void WriteDouble(ArrayBufferWriter<byte> writer, double value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), BitConverter.DoubleToInt64Bits(value));
        writer.Advance(8);
    }

    private static void WriteFloats(ArrayBufferWriter<byte> writer, ReadOnlySpan<float> values)
    {
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), BitConverter.SingleToInt32Bits(value));
            writer.Advance(4);
        }
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt(writer, bytes.Length);
        writer.Write(bytes);
    }
}
