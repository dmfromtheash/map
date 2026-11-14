using System.Diagnostics;
using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering.Styles;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// Explicit headless probe of the art styles on 100 000 and 1 000 000 owner mountains (STYLE_SCALE.md). It runs only with
/// MAP_STYLE_SCALE=1 (minutes of CPU); otherwise it returns at once. For each style path it times the first styled frame,
/// a frame after a pan past the follow gutter, after a zoom and after one edit, with the style's own CPU stages, and
/// writes the SHA-256 of every frame's pixels so two builds can be compared byte for byte (MAP_STYLE_SCALE_OUT=file).
/// </summary>
public sealed class StyleScaleBenchmark(ITestOutputHelper output)
{
    private const int DeviceWidth = 768, DeviceHeight = 432, Columns = 1000;
    private const double Spacing = 130;

    [Fact]
    public void Styles_on_100k_and_1M_owner_mountains()
    {
        if (Environment.GetEnvironmentVariable("MAP_STYLE_SCALE") != "1") return;
        var counts = (Environment.GetEnvironmentVariable("MAP_STYLE_SCALE_COUNTS") ?? "100000,1000000")
            .Split(',').Select(int.Parse).ToArray();
        var presets = (Environment.GetEnvironmentVariable("MAP_STYLE_SCALE_PRESETS")
            ?? "InkWetBrush,OldMapBlaeu,WatercolorBigShapes,OilAllaPrima,PixelFantasy16").Split(',')
            .Select(Enum.Parse<MapStylePreset>).ToArray();
        var mixed = Environment.GetEnvironmentVariable("MAP_STYLE_SCALE_MIXED") != "0";
        var lines = new List<string>();
        void Log(string line) { output.WriteLine(line); lines.Add(line); }
        var images = OwnerMountains();
        var stamps = new Images(images);
        try
        {
            foreach (var count in counts)
            {
                var document = Document(count, images, mixed);
                // The editor has drawn the map and indexed it before a style is chosen; the form maps are built when
                // «Оформление» opens (StyleRenderer.PrepareAssets). Neither is part of a style frame.
                DocumentRevisionIndex.For(document);
                // The pictures' form maps from a small map with the same six pictures; MAP_STYLE_SCALE_NOPREP=1 leaves out
                // the preparation of the large map itself (the first styled frame then meets its layers cold).
                StyleRenderer.PrepareAssets(Document(12, images, false), stamps, CancellationToken.None);
                if (Environment.GetEnvironmentVariable("MAP_STYLE_SCALE_NOPREP") != "1")
                    StyleRenderer.PrepareAssets(document, stamps, CancellationToken.None);
                var view = new WorldBounds(60000, 4000, 60000 + DeviceWidth / .5, 4000 + DeviceHeight / .5);
                Frame(document, null, view, .5, stamps, $"{count} warm-up");
                foreach (var preset in presets.Select(p => (MapStylePreset?)p).Prepend(null))
                {
                    // Fresh layer records (same object lists): the style meets layers it has not split before, as after any edit.
                    var styled = preset is null ? document : document.WithLayers([.. document.Layers.Select(Renewed)]);
                    DocumentRevisionIndex.For(styled);
                    var current = styled;
                    foreach (var (scenario, frameView, scale) in Views(view))
                    {
                        if (scenario.StartsWith("edit", StringComparison.Ordinal)) current = Edited(current, frameView);
                        Log(Frame(current, preset, frameView, scale, stamps, $"{count} {preset?.ToString() ?? "off"} {scenario}"));
                    }
                }
            }
        }
        finally
        {
            StyleDiagnostics.MeasureStages = false;
            StyleDiagnostics.Samples.Clear();
            StyleDiagnostics.ResetStages();
            foreach (var image in images.Values) image.Dispose();
        }
        if (Environment.GetEnvironmentVariable("MAP_STYLE_SCALE_OUT") is { Length: > 0 } path) File.WriteAllLines(path, lines);
    }

    private static MapLayer Renewed(MapLayer layer) => layer is ObjectLayer o ? o with { Name = o.Name + " " } : layer;

    private static IEnumerable<(string Scenario, WorldBounds View, double Scale)> Views(WorldBounds view)
    {
        yield return ("cold", view, .5);
        // Past the 512-pixel follow gutter: a moving camera would rebuild here.
        var pan = Shift(view, 3000, 700);
        yield return ("pan", pan, .5);
        // A settled zoom outside the follow range rebuilds at the new sampling grid.
        var zoom = new WorldBounds(pan.Left, pan.Top, pan.Left + DeviceWidth / .65, pan.Top + DeviceHeight / .65);
        yield return ("zoom", zoom, .65);
        yield return ("edit", zoom, .65);
        yield return ("edit2", zoom, .65);
    }

    private static WorldBounds Shift(WorldBounds v, double dx, double dy) => new(v.Left + dx, v.Top + dy, v.Right + dx, v.Bottom + dy);

    /// <summary>One mountain in the view moves by 8 px, as a drag that ends does (the editor's replacement path).</summary>
    private static MapDocument Edited(MapDocument document, WorldBounds view)
    {
        var layer = document.Layers.OfType<ObjectLayer>().First();
        var item = ObjectSpatialIndex.For(layer).Query(view).First(o => o.Kind == SceneObjectKind.Stamp);
        var edited = DocumentEditor.ReplaceObjects(document, [SceneObjectClone.At(item, item.X + 8, item.Y)]);
        DocumentRevisionIndex.For(edited);
        return edited;
    }

    private string Frame(MapDocument document, MapStylePreset? preset, WorldBounds view, double scale, Images stamps, string name)
    {
        var target = document with { Style = preset is { } p ? MapStyle.For(p) : null };
        var info = new SKImageInfo(DeviceWidth, DeviceHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        StyleDiagnostics.MeasureStages = true;
        StyleDiagnostics.Samples.Clear();
        StyleDiagnostics.ResetStages();
        var stats = new RenderStats();
        var started = Stopwatch.GetTimestamp();
        SceneRenderer.Render(surface.Canvas, target, new RenderOptions
        {
            Stamps = stamps, VisibleWorld = view, ViewScale = scale, Stats = stats,
        });
        surface.Canvas.Flush();
        var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        StyleDiagnostics.MeasureStages = false;
        var sample = StyleDiagnostics.Samples.LastOrDefault();
        // The measured frame runs the paint pass a second time (ShaderRepeatMs); it is not part of a real frame.
        total -= sample.ShaderRepeatMs;
        var stages = string.Join(' ', StyleDiagnostics.StageMs.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value:0.0}"));
        using var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        var hash = Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()))[..16];
        return $"{name}: total={total:0.0} scene={sample.SceneMs:0.0} tensor={sample.TensorMs:0.0} soft={sample.SoftMs:0.0} " +
            $"wide={sample.WideMs:0.0} buffers={sample.PrintMs:0.0} shade={sample.ShaderMs:0.0} composite={sample.CompositeMs:0.0} " +
            $"{stages} hash={hash}";
    }

    private static MapDocument Document(int count, IReadOnlyDictionary<string, SKImage> images, bool mixed)
    {
        var shas = images.Keys.Order(StringComparer.Ordinal).ToArray();
        var objects = new SceneObject[count];
        for (var i = 0; i < count; i++)
        {
            var x = i % Columns * Spacing + i * 7919 % 37;
            var y = i / Columns * Spacing + i * 104729 % 29;
            var size = 90 + i * 37 % 90;
            if (mixed && i % 997 == 500)
                objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Имя", x, y, 180, 40, 0,
                    "#FF202020", "#00000000", 0, true, false) { Text = new TextStyle { Content = "Имя " + i, FontSize = 24 } };
            else
                objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", x, y, size, size * .8, i % 13 == 0 ? 7 : 0,
                    "#FFFFFFFF", "#00000000", 0, true, false)
                    { Stamp = new StampStyle(shas[i % shas.Length]) { FlipHorizontal = i % 5 == 0 } };
        }
        return new MapDocument(DocumentId.New(), "style scale", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(131072, 131072, "px", "#FFB6AE86"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFB6AE86"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects)]);
    }

    private static Dictionary<string, SKImage> OwnerMountains()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "assets"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var result = new Dictionary<string, SKImage>(StringComparer.Ordinal);
        for (var i = 1; i <= 6; i++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{i:D2}.png"));
            result[Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()] = SKImage.FromEncodedData(bytes).ToRasterImage();
        }
        return result;
    }

    private sealed class Images(IReadOnlyDictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }
}
