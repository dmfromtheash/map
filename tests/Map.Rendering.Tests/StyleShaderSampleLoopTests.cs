using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// A2 step 2 (2026-11-09): the zoomed-out 2×2 samples of the painter, charcoal, moonlight and blueprint programs run
/// through one call of paintPixel in a loop instead of four calls the compiler inlined four times. Each new program must
/// paint exactly the bytes of the former main() — with every uniform set and every child the owner's mountain.
/// </summary>
public sealed partial class StyleShaderSampleLoopTests
{
    private const string FormerMain = """
half4 main(float2 pixel) {
    if (max(docPerPx.x, docPerPx.y) > 1.001) {
        return (paintPixel(pixel + float2(-0.25, -0.25)) + paintPixel(pixel + float2(0.25, -0.25))
              + paintPixel(pixel + float2(-0.25, 0.25)) + paintPixel(pixel + float2(0.25, 0.25))) * 0.25;
    }
    return paintPixel(pixel);
}
""";

    [Theory]
    [InlineData("PainterShader", "1.0")]
    [InlineData("PainterShader", "2.0")]
    [InlineData("PainterShader", "3.0")]
    [InlineData("PainterShader", "4.0")]
    [InlineData("CharcoalShader", null)]
    [InlineData("MoonlightShader", null)]
    [InlineData("BlueprintShader", null)]
    public void The_sample_loop_paints_the_same_bytes_as_four_inlined_calls(string shader, string? variant)
    {
        var type = typeof(Map.Rendering.Styles.StyleRenderer).Assembly.GetType("Map.Rendering.Styles." + shader)!;
        var source = (string)type.GetField("Source", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (variant is not null) source = source.Replace("VARIANT", variant, StringComparison.Ordinal);
        var main = source.IndexOf("half4 main(float2 pixel)", StringComparison.Ordinal);
        Assert.True(main > 0, $"{shader}: main not found");
        var former = source[..main] + FormerMain;

        using var mountain = OwnerMountain();
        using var current = SKRuntimeEffect.CreateShader(source, out var e1) ?? throw new InvalidOperationException(e1);
        using var old = SKRuntimeEffect.CreateShader(former, out var e2) ?? throw new InvalidOperationException(e2);
        foreach (var docPerPx in new[] { 1f, 2f })
        {
            using var a = Paint(current, source, mountain, docPerPx);
            using var b = Paint(old, source, mountain, docPerPx);
            Assert.True(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), $"{shader}{variant}/{docPerPx}: bytes differ");
        }
    }

    private static SKBitmap Paint(SKRuntimeEffect effect, string source, SKImage mountain, float docPerPx)
    {
        var builder = new SKRuntimeShaderBuilder(effect);
        var children = new List<SKShader>();
        foreach (Match m in ChildPattern().Matches(source))
        {
            var child = mountain.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            children.Add(child);
            builder.Children[m.Groups[1].Value] = child;
        }

        var k = 0;
        foreach (Match m in UniformPattern().Matches(source))
        {
            var name = m.Groups[2].Value;
            var size = m.Groups[1].Value switch { "float2" => 2, "float3" => 3, "float4" => 4, _ => 1 };
            float[] value = name switch
            {
                "docPerPx" => [docPerPx, docPerPx],
                "origin" => [-13.5f, 7.25f],
                "variant" => [1f],
                // Every other control in the middle of its range, a little different each, so all of them take part.
                _ => Enumerable.Range(0, size).Select(i => 0.45f + 0.05f * ((k + i) % 7)).ToArray(),
            };
            k++;
            if (size == 1) builder.Uniforms[name] = value[0]; else builder.Uniforms[name] = value;
        }

        using var shader = builder.Build();
        foreach (var child in children) child.Dispose();
        var bitmap = new SKBitmap(new SKImageInfo(mountain.Width / 2, mountain.Height / 2, SKColorType.Rgba8888, SKAlphaType.Premul));
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

    [GeneratedRegex(@"uniform shader (\w+);")]
    private static partial Regex ChildPattern();

    [GeneratedRegex(@"uniform (float4|float3|float2|float|int) (\w+);")]
    private static partial Regex UniformPattern();
}
