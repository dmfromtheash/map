using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// Builds Map's own seamless fill textures. Nothing here is traced, sampled or derived from
/// anyone else's artwork: every tile is layered value noise on a warm or cool base, written by us.
/// <para>
/// The structure follows what hand-drawn cartography actually does, which is two scales of
/// variation at once. Parchment carries broad soft blotches of uneven staining plus a fine tooth;
/// meadow and stone carry the same two scales with a different colour and a different balance;
/// water carries a slow swell plus a short ripple. Values stay within a narrow band around the
/// base so the texture reads as material and never fights the map drawn on top of it.
/// </para>
/// <para>
/// The noise is tiled on a torus - every lattice coordinate is taken modulo the tile - so the
/// result repeats without a visible seam at any offset. The seed is fixed per texture, so the
/// same file is produced on every machine and a regenerated asset is byte-identical.
/// </para>
/// <para>Run: <c>--profile textures --output src/Map.Rendering/Textures</c>.</para>
/// </summary>
internal static class TextureForge
{
    public const int Edge = 512;

    internal sealed record TextureRecipe(
        string Id,
        string Title,
        SKColor Base,
        SKColor Stain,
        SKColor Highlight,
        double BlotchScale,
        double BlotchAmount,
        double GrainAmount,
        double StreakAmount,
        double StreakAspect,
        int Seed);

    /// <summary>The first set: four grounds and three waters.</summary>
    internal static readonly TextureRecipe[] Recipes =
    [
        // Paper keeps a visible fibre direction; water must not - a regular band reads as metal,
        // so its streak is weak and nearly isotropic and the broad mottling carries the surface.
        new("parchment", "Пергамент", new SKColor(230, 211, 175), new SKColor(184, 155, 108),
            new SKColor(246, 235, 211), 3.0, 0.55, 0.16, 0.20, 3.0, 0x9E37),
        new("meadow", "Луг", new SKColor(156, 175, 116), new SKColor(108, 129, 79),
            new SKColor(191, 204, 149), 4.5, 0.52, 0.26, 0.10, 2.0, 0x5BD1),
        new("sand", "Песок", new SKColor(226, 205, 160), new SKColor(194, 168, 120),
            new SKColor(243, 229, 199), 6.0, 0.36, 0.34, 0.08, 2.2, 0x94D0),
        new("stone", "Камень", new SKColor(178, 175, 166), new SKColor(124, 122, 116),
            new SKColor(208, 206, 199), 3.2, 0.66, 0.22, 0.24, 1.4, 0x27D4),
        new("water-still", "Тихая вода", new SKColor(98, 148, 178), new SKColor(72, 118, 150),
            new SKColor(150, 194, 216), 4.0, 0.60, 0.10, 0.20, 1.7, 0x1CE4),
        new("water-open", "Открытая вода", new SKColor(84, 132, 166), new SKColor(54, 98, 132),
            new SKColor(142, 186, 212), 2.4, 0.70, 0.09, 0.18, 1.5, 0x7F4A),
        new("water-shallow", "Отмель", new SKColor(152, 192, 202), new SKColor(114, 162, 178),
            new SKColor(200, 227, 231), 5.5, 0.52, 0.10, 0.16, 1.8, 0x3111),
    ];

    public static string[] Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var recipe in Recipes)
        {
            var path = Path.Combine(directory, recipe.Id + ".png");
            using var bitmap = Build(recipe);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
            written.Add(path);
        }
        return [.. written];
    }

    internal static SKBitmap Build(TextureRecipe recipe)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new byte[Edge * Edge * 4];
        for (var y = 0; y < Edge; y++)
        {
            for (var x = 0; x < Edge; x++)
            {
                // Broad staining: three octaves of tiling value noise at the recipe's scale.
                var blotch = Fractal(x, y, recipe.BlotchScale, recipe.BlotchScale, recipe.Seed, 3);
                // Fine tooth: two high-frequency octaves, so the material has a surface.
                var grain = Fractal(x, y, 48, 48, recipe.Seed ^ 0x51ED, 2);
                // Direction: fibres in paper, a long swell on water. The aspect stretches the
                // LATTICE, never the coordinates - stretching the coordinates would move the wrap
                // off the tile edge and leave a visible seam where the texture repeats.
                var streak = Fractal(x, y, 5 / recipe.StreakAspect, 5 * recipe.StreakAspect,
                    recipe.Seed ^ 0x2545, 2);

                var value = blotch * recipe.BlotchAmount + grain * recipe.GrainAmount + streak * recipe.StreakAmount;
                value = Math.Clamp(value, -1, 1);
                var colour = value >= 0
                    ? Mix(recipe.Base, recipe.Highlight, value)
                    : Mix(recipe.Base, recipe.Stain, -value);
                var offset = (y * Edge + x) * 4;
                pixels[offset] = colour.Red;
                pixels[offset + 1] = colour.Green;
                pixels[offset + 2] = colour.Blue;
                pixels[offset + 3] = 255;
            }
        }
        bitmap.Pixels = [.. Enumerable.Range(0, Edge * Edge).Select(i =>
            new SKColor(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], 255))];
        return bitmap;
    }

    private static SKColor Mix(SKColor from, SKColor to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Channel(byte a, byte b, double t) => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);
        return new SKColor(Channel(from.Red, to.Red, amount), Channel(from.Green, to.Green, amount),
            Channel(from.Blue, to.Blue, amount), 255);
    }

    /// <summary>Several octaves of tiling value noise, in -1..1.</summary>
    private static double Fractal(double x, double y, double cellsX, double cellsY, int seed, int octaves)
    {
        double total = 0, amplitude = 1, sum = 0;
        for (var octave = 0; octave < octaves; octave++)
        {
            var step = Math.Pow(2, octave);
            total += amplitude * Noise(x, y,
                (int)Math.Max(2, Math.Round(cellsX * step)), (int)Math.Max(2, Math.Round(cellsY * step)),
                seed + octave * 977);
            sum += amplitude;
            amplitude *= 0.5;
        }
        return total / Math.Max(1e-9, sum);
    }

    /// <summary>
    /// Value noise on a lattice of whole cells across the tile, wrapped on both axes so the tile
    /// meets itself without a seam. A whole number of cells per edge is what makes the wrap land
    /// exactly on the edge. Smoothstep interpolation keeps it soft.
    /// </summary>
    private static double Noise(double x, double y, int cellsX, int cellsY, int seed)
    {
        var fx = x * cellsX / Edge; var fy = y * cellsY / Edge;
        var x0 = (int)Math.Floor(fx); var y0 = (int)Math.Floor(fy);
        var tx = Smooth(fx - x0); var ty = Smooth(fy - y0);
        var a = Lattice(x0, y0, cellsX, cellsY, seed);
        var b = Lattice(x0 + 1, y0, cellsX, cellsY, seed);
        var c = Lattice(x0, y0 + 1, cellsX, cellsY, seed);
        var d = Lattice(x0 + 1, y0 + 1, cellsX, cellsY, seed);
        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static double Lattice(int x, int y, int cellsX, int cellsY, int seed)
    {
        var wx = ((x % cellsX) + cellsX) % cellsX;
        var wy = ((y % cellsY) + cellsY) % cellsY;
        unchecked
        {
            var h = (ulong)(uint)(wx * 374761393 + wy * 668265263 + seed * 1442695041);
            h ^= h >> 13; h *= 1274126177; h ^= h >> 16;
            return (h & 0xFFFF) / (double)0xFFFF * 2 - 1;
        }
    }
}
