namespace Map.Performance;

/// <summary>
/// The owner's own mountains (`content/Owner.Mountains.2026-10-18/source/1..6.png`) as 512 px test copies in
/// `tests/fixtures/assets`. Every smoke that needs a placed asset takes it from here: the windows a smoke opens are
/// seen by the owner, and he judges the interface with his own art (owner, 2026-10-30: the coloured diagnostic
/// stamps that stood here before were removed «отовсюду»).
/// </summary>
internal static class OwnerMountainFixtures
{
    /// <summary>The names the owner's library gives the six mountains, in file order.</summary>
    public static readonly string[] Names =
        ["Ледяной пик", "Двуглавая гора", "Гора с ледником", "Горная гряда", "Ледяные шпили", "Ледяной клык"];

    /// <summary>The test copy of mountain 1..6, under the given repository root (the working directory by default).</summary>
    public static string Path(int number, string? repositoryRoot = null)
    {
        if (number is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(number), number, "The owner pack has six mountains.");
        return System.IO.Path.Combine(repositoryRoot ?? Environment.CurrentDirectory,
            "tests", "fixtures", "assets", $"owner-mountain-{number:00}.png");
    }

    /// <summary>
    /// The owner's seamless snow texture, opaque everywhere: for a smoke that needs a picture as a brush or fill
    /// material, where a mountain's transparent corners would paint nothing.
    /// </summary>
    public static string TexturePath(string? repositoryRoot = null) => System.IO.Path.Combine(
        repositoryRoot ?? Environment.CurrentDirectory, "src", "Map.Rendering", "Textures", "owner-snow-drifts.png");

    /// <summary>The snow texture scaled to <paramref name="size"/> px (a brush material may be at most 1024 px).</summary>
    public static string WriteTexture(string destination, int size = 512)
    {
        using var source = SkiaSharp.SKBitmap.Decode(TexturePath())
            ?? throw new InvalidDataException("Owner texture could not be read.");
        using var scaled = source.Resize(new SkiaSharp.SKImageInfo(size, size), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell))
            ?? throw new InvalidDataException("Owner texture could not be scaled.");
        using var data = scaled.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(destination, data.ToArray());
        return destination;
    }

    /// <summary>
    /// A copy of mountain 1..6 with its own file hash, for a smoke that needs more distinct files than six (the
    /// catalog keys a one-file set by content). The picture is the same mountain, mirrored when asked; only the
    /// corner pixel, transparent in the original, becomes 1/255 opaque in a colour that carries
    /// <paramref name="marker"/> (a fully transparent pixel loses its colour when the PNG is written).
    /// </summary>
    public static string WriteDistinct(int number, string destination, byte marker, bool mirror = false)
    {
        using var source = SkiaSharp.SKBitmap.Decode(Path(number))
            ?? throw new InvalidDataException("Owner mountain could not be read: " + number);
        var info = new SkiaSharp.SKImageInfo(source.Width, source.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var copy = new SkiaSharp.SKBitmap(info);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                copy.SetPixel(mirror ? source.Width - 1 - x : x, y, source.GetPixel(x, y));
        if (copy.GetPixel(0, 0).Alpha != 0) throw new InvalidDataException("The corner of the mountain is not transparent.");
        copy.SetPixel(0, 0, new SkiaSharp.SKColor(marker, 0, 0, 1));
        using var data = copy.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(destination, data.ToArray());
        return destination;
    }
}
