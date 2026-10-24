using System.Text.Json;
using SkiaSharp;

namespace V0Skia;

/// <summary>Shared V0 scene (scene.json + layers) turned into source images for the material cache.</summary>
internal sealed class SceneInputs : IDisposable
{
    public const float HeightUnitPx = 512f; // data.G = relief height in px / 512
    public const float GroundReliefPx = 77f; // ground_height.png 0..1 -> 0..0.35 * 220 px
    public const float MountainReliefPx = 0.30f * 1024f; // maps: full height = 0.30 of the sprite width

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required SKImage GroundAlbedo { get; init; }
    public required SKImage GroundNormal { get; init; }
    public required SKImage GroundData { get; init; }
    public required List<Sprite> Mountains { get; init; }
    public required List<LightSpec> Lights { get; init; }
    public required List<TimeKey> Keys { get; init; }
    public required SKImage Noise { get; init; }

    public sealed record Sprite(string Name, float X, float Y, float Scale, SKImage Albedo, SKImage Normal, SKImage Data, float BaseScreenY);
    public sealed record LightSpec(string Id, float X, float Y, SKColor Color, float Intensity, float Radius, float Height, float FlickerAmp, float[] FlickerHz, int Seed);
    public sealed record TimeKey(float T, string Name, float[] SunDir, SKColor SunColor, float SunIntensity, SKColor AmbientColor,
        float AmbientIntensity, SKColor FogColor, float FogDensity, float LightsIntensity, float ShadowLength, float Exposure, float Saturation,
        float[] WhiteBalance);

    public static SceneInputs Load(string shared)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(shared, "scene", "scene.json")));
        var root = doc.RootElement;
        var canvas = root.GetProperty("canvas");
        int w = canvas[0].GetInt32(), h = canvas[1].GetInt32();

        var gColor = Load8(Path.Combine(shared, "scene", "ground_color.png"));
        var gNormal = Load8(Path.Combine(shared, "scene", "ground_normal.png"));
        var gHeight = Load8(Path.Combine(shared, "scene", "ground_height.png"));
        var gWater = Load8(Path.Combine(shared, "scene", "water_mask.png"));
        var gShore = Load8(Path.Combine(shared, "scene", "shore_dist.png"));

        var groundData = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var heightPx = gHeight.Px[i * 4] / 255f * GroundReliefPx;
            var water = gWater.Px[i * 4] > 127;
            groundData[i * 4 + 0] = 255; // ground fully lit by its normals (they are gentle)
            groundData[i * 4 + 1] = (byte)Math.Clamp(MathF.Round(heightPx / HeightUnitPx * 255f), 0, 255);
            groundData[i * 4 + 2] = water ? (byte)(128 + gShore.Px[i * 4] / 2) : (byte)0;
            groundData[i * 4 + 3] = 255;
        }

        var mountains = new List<Sprite>();
        foreach (var m in root.GetProperty("mountains").EnumerateArray())
        {
            var name = m.GetProperty("name").GetString()!;
            var dir = Path.Combine(shared, "maps", name);
            var color = Load8(Path.Combine(dir, "color.png"));
            var normal = Load8(Path.Combine(dir, "normal.png"));
            var height = Load8(Path.Combine(dir, "height.png"));
            var mask = Load8(Path.Combine(dir, "lightmask.png"));
            var scale = m.GetProperty("scale").GetSingle();
            var groundH = m.GetProperty("groundHeight").GetSingle();
            int mw = color.W, mh = color.H, n = mw * mh;
            var nrm = new byte[n * 4];
            var data = new byte[n * 4];
            for (var i = 0; i < n; i++)
            {
                var a = color.Px[i * 4 + 3];
                nrm[i * 4 + 0] = normal.Px[i * 4 + 0];
                nrm[i * 4 + 1] = normal.Px[i * 4 + 1];
                nrm[i * 4 + 2] = normal.Px[i * 4 + 2];
                nrm[i * 4 + 3] = a;
                var hpx = groundH / 0.35f * GroundReliefPx + height.Px[i * 4] / 255f * MountainReliefPx * scale;
                data[i * 4 + 0] = mask.Px[i * 4];
                data[i * 4 + 1] = (byte)Math.Clamp(MathF.Round(hpx / HeightUnitPx * 255f), 0, 255);
                data[i * 4 + 2] = 0;
                data[i * 4 + 3] = a;
            }
            mountains.Add(new Sprite(name, m.GetProperty("x").GetSingle(), m.GetProperty("y").GetSingle(), scale,
                MakeImage(color.Px, mw, mh, SKAlphaType.Unpremul), MakeImage(nrm, mw, mh, SKAlphaType.Unpremul),
                MakeImage(data, mw, mh, SKAlphaType.Unpremul), m.GetProperty("baseScreenY").GetSingle()));
        }

        var lights = new List<LightSpec>();
        foreach (var l in root.GetProperty("lights").EnumerateArray())
        {
            float amp = 0; float[] hz = []; var seed = 0;
            if (l.TryGetProperty("flicker", out var f))
            {
                amp = f.GetProperty("amplitude").GetSingle();
                hz = f.GetProperty("hz").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                seed = f.GetProperty("seed").GetInt32();
            }
            lights.Add(new LightSpec(l.GetProperty("id").GetString()!, l.GetProperty("x").GetSingle(), l.GetProperty("y").GetSingle(),
                SKColor.Parse(l.GetProperty("color").GetString()!),
                l.TryGetProperty("intensity", out var li) ? li.GetSingle() : 1f,
                l.GetProperty("radius").GetSingle(), l.GetProperty("height").GetSingle(), amp, hz, seed));
        }

        var keys = new List<TimeKey>();
        foreach (var k in root.GetProperty("timeOfDay").GetProperty("keys").EnumerateArray())
        {
            var grade = k.GetProperty("grade");
            keys.Add(new TimeKey(k.GetProperty("t").GetSingle(), k.GetProperty("name").GetString()!,
                k.GetProperty("sunDir").EnumerateArray().Select(x => x.GetSingle()).ToArray(),
                SKColor.Parse(k.GetProperty("sunColor").GetString()!), k.GetProperty("sunIntensity").GetSingle(),
                SKColor.Parse(k.GetProperty("ambientColor").GetString()!), k.GetProperty("ambientIntensity").GetSingle(),
                SKColor.Parse(k.GetProperty("fogColor").GetString()!), k.GetProperty("fogDensity").GetSingle(),
                k.GetProperty("lightsIntensity").GetSingle(), k.GetProperty("shadowLength").GetSingle(),
                grade.GetProperty("exposure").GetSingle(), grade.GetProperty("saturation").GetSingle(),
                grade.TryGetProperty("whiteBalance", out var wbal) ? wbal.EnumerateArray().Select(x => x.GetSingle()).ToArray() : [1f, 1f, 1f]));
        }

        return new SceneInputs
        {
            Width = w, Height = h,
            GroundAlbedo = MakeImage(gColor.Px, w, h, SKAlphaType.Opaque),
            GroundNormal = MakeImage(gNormal.Px, w, h, SKAlphaType.Opaque),
            GroundData = MakeImage(groundData, w, h, SKAlphaType.Opaque),
            Mountains = mountains, Lights = lights, Keys = keys,
            Noise = MakeNoise(256),
        };
    }

    /// <summary>Tileable smooth value noise (fixed seed), read identically by the CPU and GPU passes.</summary>
    private static SKImage MakeNoise(int size)
    {
        var rng = new Random(20261024);
        var px = new byte[size * size * 4];
        for (var c = 0; c < 4; c++)
        {
            // r, g, a: smooth value noise; b: per-texel white noise (shadow-march offsets).
            var cell = c switch { 0 => 32, 1 => 16, 2 => 1, _ => 64 };
            int g = size / cell;
            var grid = new float[g * g];
            for (var i = 0; i < grid.Length; i++) grid[i] = (float)rng.NextDouble();
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float fx = (float)x / cell, fy = (float)y / cell;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = fx - x0, ty = fy - y0;
                tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                float v00 = grid[(y0 % g) * g + x0 % g], v10 = grid[(y0 % g) * g + (x0 + 1) % g];
                float v01 = grid[((y0 + 1) % g) * g + x0 % g], v11 = grid[((y0 + 1) % g) * g + (x0 + 1) % g];
                var v = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                px[(y * size + x) * 4 + c] = (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
            }
        }
        return MakeImage(px, size, size, SKAlphaType.Opaque);
    }

    private sealed record Raw(byte[] Px, int W, int H);

    private static Raw Load8(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new FileNotFoundException(path);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var r = codec.GetPixels(info, bmp.GetPixels());
        if (r != SKCodecResult.Success && r != SKCodecResult.IncompleteInput) throw new InvalidOperationException($"{path}: {r}");
        return new Raw(bmp.Bytes, info.Width, info.Height);
    }

    private static SKImage MakeImage(byte[] px, int w, int h, SKAlphaType alpha)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, alpha);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        bmp.SetImmutable();
        return SKImage.FromBitmap(bmp);
    }

    public void Dispose()
    {
        GroundAlbedo.Dispose(); GroundNormal.Dispose(); GroundData.Dispose(); Noise.Dispose();
        foreach (var m in Mountains) { m.Albedo.Dispose(); m.Normal.Dispose(); m.Data.Dispose(); }
    }
}
