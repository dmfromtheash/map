using System.Text;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The seed of one placement gesture. It is deliberately not <see cref="HashCode"/>: that hash is
/// randomised per process for strings, so the same documented history would replay differently in
/// the next run. This mix (FNV-1a over the tuple's bytes) is fixed for good.
/// </summary>
public static class GestureSeed
{
    private const uint OffsetBasis = 2_166_136_261;
    private const uint Prime = 16_777_619;

    public static int From(long revision, string? setId, int variantIndex, int gestureCounter)
    {
        unchecked
        {
            var hash = MixLong(OffsetBasis, revision);
            hash = MixBytes(hash, Encoding.UTF8.GetBytes(setId ?? ""));

            // A zero word after the identifier keeps "ab" + 1 from mixing to the same value as
            // "a" + "b1" would: the parts stay separable however long the set id is.
            hash = MixInt(hash, 0);
            hash = MixInt(hash, variantIndex);
            hash = MixInt(hash, gestureCounter);
            return (int)hash;
        }
    }

    private static uint MixLong(uint hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return MixBytes(hash, bytes);
    }

    private static uint MixInt(uint hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return MixBytes(hash, bytes);
    }

    private static uint MixBytes(uint hash, ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            foreach (var value in bytes)
            {
                hash = (hash ^ value) * Prime;
            }

            return hash;
        }
    }
}
