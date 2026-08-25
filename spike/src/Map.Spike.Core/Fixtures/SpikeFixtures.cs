using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Fixtures;

/// <summary>
/// Self-generated geometric fixtures. No third-party artwork is used anywhere in the spike
/// (AGENTS.md clean-implementation boundary).
/// </summary>
public static class SpikeFixtures
{
    public const ulong DefaultSeed = 0x5EED_0001_5EED_0001UL;
    public const int DefaultCanvasSize = 4096;

    /// <summary>Deterministic scene: same seed and count always produce the same objects and IDs.</summary>
    public static SpikeDocument CreateObjectScene(
        int objectCount,
        int canvasSize = DefaultCanvasSize,
        ulong seed = DefaultSeed,
        string title = "Spike scene")
    {
        var rng = new DeterministicRandom(seed);
        var document = new SpikeDocument(rng.NextGuid(), title, canvasSize, canvasSize)
        {
            CreatedUtc = DateTimeOffset.UnixEpoch,
        };

        var objects = new List<SpikeObject>(objectCount);
        for (var i = 0; i < objectCount; i++)
        {
            var size = rng.NextRange(12, 48);
            var center = new Vec2(
                rng.NextRange(size, canvasSize - size),
                rng.NextRange(size, canvasSize - size));

            objects.Add(new SpikeObject
            {
                Id = new ObjectId(rng.NextGuid()),
                Kind = (SpikeObjectKind)rng.NextInt(3),
                Center = center,
                Size = size,
                Rotation = rng.NextRange(0, Math.PI * 2),
                ColorArgb = PickColor(rng),
            });
        }

        document.Objects.AddRange(objects);
        document.Objects.RebuildIndex();
        return document;
    }

    /// <summary>
    /// Fills a few decor tiles with a flat colour. Used to prove the mask changes the brush layer
    /// only: the decor layer must look identical with the mask on and off.
    /// </summary>
    public static void FillDecorPatch(SpikeDocument document, int tilesX, int tilesY, uint premultipliedRgba)
    {
        var r = (byte)((premultipliedRgba >> 24) & 0xFF);
        var g = (byte)((premultipliedRgba >> 16) & 0xFF);
        var b = (byte)((premultipliedRgba >> 8) & 0xFF);
        var a = (byte)(premultipliedRgba & 0xFF);

        for (var ty = 0; ty < tilesY; ty++)
        {
            for (var tx = 0; tx < tilesX; tx++)
            {
                var tile = document.Decor.GetOrCreateTile(new TileKey(tx, ty));
                for (var i = 0; i < tile.Length; i += RasterLayer.BytesPerPixel)
                {
                    tile[i + 0] = r;
                    tile[i + 1] = g;
                    tile[i + 2] = b;
                    tile[i + 3] = a;
                }
            }
        }
    }

    private static uint PickColor(DeterministicRandom rng)
    {
        // Own palette, chosen numerically here; no external design tokens are referenced.
        ReadOnlySpan<uint> palette =
        [
            0xFF3E5C76, 0xFF748CAB, 0xFF5B8266, 0xFF8A6E4B,
            0xFF9E5A4A, 0xFF4C4A5A, 0xFF6F8F72, 0xFFB08968,
        ];

        return palette[rng.NextInt(palette.Length)];
    }
}
