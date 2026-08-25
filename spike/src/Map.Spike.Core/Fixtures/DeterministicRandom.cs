namespace Map.Spike.Core.Fixtures;

/// <summary>
/// SplitMix64. Written out rather than using System.Random because the BCL generator's exact
/// sequence is not contractually stable across runtime versions, and the benchmark scene must be
/// byte-identical between runs and machines.
/// </summary>
public sealed class DeterministicRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0,1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    public double NextRange(double min, double max) => min + (NextDouble() * (max - min));

    public int NextInt(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>Deterministic RFC-4122-shaped GUID so saved fixtures compare byte for byte.</summary>
    public Guid NextGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..8], NextUInt64());
        BitConverter.TryWriteBytes(bytes[8..], NextUInt64());
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        return new Guid(bytes);
    }
}
