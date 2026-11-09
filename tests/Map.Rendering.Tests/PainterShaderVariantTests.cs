using System.Reflection;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The painter program is compiled once per variant with the variant as a constant (A2 step 1, 2026-11-09). Each
/// specialised program must paint exactly the bytes of the old program that took the variant as a uniform.
/// </summary>
public sealed class PainterShaderVariantTests
{
    private static readonly Type Shader =
        typeof(Map.Rendering.Styles.StyleRenderer).Assembly.GetType("Map.Rendering.Styles.PainterShader")!;

    [Theory]
    [InlineData(MapStylePreset.WatercolorBigShapes)]
    [InlineData(MapStylePreset.UkiyoeSosakuHanga)]
    [InlineData(MapStylePreset.PrintLetterpress)]
    [InlineData(MapStylePreset.PrintTintedLithograph)]
    public void Each_variant_program_paints_the_same_bytes_as_the_program_with_a_variant_uniform(MapStylePreset preset)
    {
        using var mountain = OwnerMountain();
        using var generic = (SKRuntimeEffect)Shader.GetMethod("CompileWithVariantUniform",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var style = MapStyle.For(preset) with { Strength = .8 };
        // docPerPx 1 paints one sample per pixel; 2 takes the four-sample path of main().
        foreach (var docPerPx in new[] { 1f, 2f })
        {
            using var specialised = Paint(mountain, style, docPerPx, null);
            using var withUniform = Paint(mountain, style, docPerPx, generic);
            Assert.True(specialised.GetPixelSpan().SequenceEqual(withUniform.GetPixelSpan()), $"{preset}/{docPerPx}: bytes differ");
        }
    }

    private static SKBitmap Paint(SKImage mountain, MapStyle style, float docPerPx, SKRuntimeEffect? effect)
    {
        var build = Shader.GetMethod("Build", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        // Every child is the owner's mountain: alpha, colour and its edges reach every branch of the program.
        object[] args =
        [
            mountain, mountain, mountain, mountain, mountain, mountain, mountain, mountain, mountain, mountain,
            new SKPoint(-13.5f, 7.25f), new SKPoint(docPerPx, docPerPx), style, effect!,
        ];
        using var shader = (SKShader)build.Invoke(null, args)!;
        var bitmap = new SKBitmap(new SKImageInfo(mountain.Width, mountain.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, bitmap.Width, bitmap.Height, paint);
        return bitmap;
    }

    private static SKImage OwnerMountain()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Map.slnx"),
            "tests", "fixtures", "assets", "owner-mountain-01.png");
        return SKImage.FromEncodedData(path) ?? throw new InvalidDataException(path);
    }
}
