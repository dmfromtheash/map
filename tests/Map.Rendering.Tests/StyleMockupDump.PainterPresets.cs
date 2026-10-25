using Map.Core.Documents;
using Map.Rendering.Styles;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed partial class StyleMockupDump
{
    /// <summary>
    /// Opt-in CPU-only references and timings. MAP_R09_PAINTER_BASELINE checks the saved
    /// program PNGs against a previous dump, byte-for-byte after decoding to unpremultiplied RGBA.
    /// Local assets are required; normal CI does not render or store these images.
    /// </summary>
    [Fact]
    public void Dump_four_painter_presets_and_measure_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_R09_PAINTER");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets,"*.png").Take(3).ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, f =>
        {
            using var decoded = SKBitmap.Decode(f)!;
            return SKImage.FromBitmap(decoded);
        });
        Assert.Equal(3,images.Count);
        var metrics = new List<string> { "preset,scene,scale,mean_rgb_difference_0_255" };
        var timings = new List<string> { "preset,backend,width,height,shader_ms,full_style_pass_ms,scene_ms,tensor_ms,soft_ms,wide_ms,painter_buffers_ms,composite_ms" };
        var equality = new List<string> { "image,rgba_bytes,different_bytes,sha256" };
        var baseline = Environment.GetEnvironmentVariable("MAP_R09_PAINTER_BASELINE");
        if (!string.IsNullOrEmpty(baseline))
            Assert.False(string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseline)),StringComparison.OrdinalIgnoreCase),
                "Output and baseline directories must be different.");
        MapStylePreset[] presets = [MapStylePreset.WatercolorBigShapes,MapStylePreset.UkiyoeSosakuHanga,
            MapStylePreset.PrintLetterpress,MapStylePreset.PrintTintedLithograph,
            MapStylePreset.OilAllaPrima,MapStylePreset.OilImpastoRhythm,
            MapStylePreset.CharcoalVineStump,MapStylePreset.CharcoalHatching];
        // MAP_R09_PAINTER_ONLY = a comma-separated list of preset names, to compare only those.
        if (Environment.GetEnvironmentVariable("MAP_R09_PAINTER_ONLY") is { Length: > 0 } only)
            presets = [.. presets.Where(p => only.Split(',').Contains(p.ToString()))];
        var oils = OilVariants().ToDictionary(v => v.Oil.Variant, v => v.Oil);
        var coals = CharcoalVariants().ToDictionary(v => v.Charcoal.Variant, v => v.Charcoal);
        var programOnly = Environment.GetEnvironmentVariable("MAP_R09_PAINTER_PROGRAM_ONLY") == "1";
        try
        {
            foreach (var crowd in new[] { false,true })
            {
                var tag = crowd ? "crowd" : "small";
                var (doc,labels) = crowd ? Crowd(images) : Scene(images);
                if (programOnly)
                {
                    // Pixel regression/performance runs need only program frames, not the
                    // independent visual prototypes and their expensive Kuwahara buffers.
                    foreach (var preset in presets)
                    foreach (var scale in new[] { 1f,.5f })
                    {
                        using var actual = ProgramFrame(doc,preset,scale);
                        SavePng(actual,$"{preset}-{tag}-{scale*100:0}-program.png");
                    }
                    continue;
                }
                using var mountains = RenderOnly(doc,doc.Layers.First(l => l.Name == "Горы").Id,images);
                using var land = RenderOnly(doc,doc.Layers.OfType<LandLayer>().First().Id,images);
                using var rivers = RenderOnly(doc,doc.Layers.First(l => l.Name == "Реки").Id,images);
                using var names = RenderOnly(doc,labels,images);
                using var geo = CoastField(land,rivers);
                using var riverSoft = Blurred(rivers,4);
                var ms = crowd ? .42f : 1f;
                using var soft = Blurred(mountains,4*ms);
                using var bigK = Kuwahara(soft,3.2f*ms);
                using var big = Blurred(bigK,1.5f*ms);
                using var wetBig = Blurred(bigK,2.5f*ms);
                using var facetSoft = Blurred(mountains,1.8f*ms);
                using var facetK = Kuwahara(facetSoft,2*ms);
                using var facets = Blurred(facetK,1*ms);
                using var wetFacets = Blurred(facetK,1.5f*ms);
                using var ca = Blurred(mountains,2.5f*ms);
                using var cb = Blurred(mountains,7*ms);
                using var wide = Blurred(mountains,14*ms);
                using var tensor = Tensor(big);
                using var wetTensor = Tensor(wetBig);
                using var flow = CoastFlow(geo);
                foreach (var preset in presets)
                {
                    using var referenceSurface = SKSurface.Create(new SKImageInfo(W,H))!;
                    using (var shader = preset switch
                    {
                        MapStylePreset.CharcoalVineStump => CharcoalShader(mountains,wetBig,facets,ca,cb,wide,wetTensor,geo,flow,coals[1],ms),
                        MapStylePreset.CharcoalHatching => CharcoalShader(mountains,wetBig,facets,ca,cb,wide,wetTensor,geo,flow,coals[2],ms),
                        MapStylePreset.OilAllaPrima =>OilShader(mountains,wetBig,wetFacets,ca,cb,wide,wetTensor,geo,flow,oils[1],ms),
                        MapStylePreset.OilImpastoRhythm => OilShader(mountains,wetBig,wetFacets,ca,cb,wide,wetTensor,geo,flow,oils[5],ms),
                        MapStylePreset.WatercolorBigShapes => WetShader(mountains,wetBig,wetFacets,ca,cb,wide,wetTensor,geo,riverSoft,new(1,"#f9f6ee"),ms),
                        MapStylePreset.UkiyoeSosakuHanga => PrintBlockShader(mountains,big,facets,ca,cb,wide,tensor,geo,rivers,new(5,"#ece2c8"),ms),
                        MapStylePreset.PrintLetterpress => TwoInkShader(mountains,big,facets,ca,cb,wide,tensor,geo,rivers,new(3,"#f1ece0"),ms),
                        _ => TwoInkShader(mountains,big,facets,ca,cb,wide,tensor,geo,rivers,new(4,"#f2ede2"),ms),
                    })
                    using (var paint = new SKPaint { Shader = shader }) referenceSurface.Canvas.DrawRect(0,0,W,H,paint);
                    referenceSurface.Canvas.DrawImage(names,0,0);
                    using var reference = referenceSurface.Snapshot();
                    foreach (var scale in new[] { 1f,.5f })
                    {
                        using var rs = SKSurface.Create(new SKImageInfo((int)(W*scale),(int)(H*scale)))!;
                        rs.Canvas.DrawImage(reference,SKRect.Create(W*scale,H*scale),new SKSamplingOptions(SKCubicResampler.Mitchell));
                        using var expected = rs.Snapshot();
                        using var actual = ProgramFrame(doc,preset,scale);
                        SavePng(expected,$"{preset}-{tag}-{scale*100:0}-mockup.png");
                        SavePng(actual,$"{preset}-{tag}-{scale*100:0}-program.png");
                        metrics.Add(FormattableString.Invariant($"{preset},{tag},{scale},{MeanRgb(actual,expected):F6}"));
                    }
                }
            }
            var (timingDoc,_) = Crowd(images);
            StyleDiagnostics.MeasureStages = true;
            void Measure()
            {
              foreach (var preset in new[] { MapStylePreset.InkWetBrush }.Concat(presets))
              {
                var measurements = new List<StyleFrameSample>();
                for (var sample = 0; sample < 4; sample++)
                {
                    while (StyleDiagnostics.Samples.TryDequeue(out _)) { }
                    var info = new SKImageInfo(2048,1095);
                    using var surface = SKSurface.Create(info)!;
                    surface.Canvas.Translate(-256,-172);
                    SceneRenderer.Render(surface.Canvas,timingDoc with { Style = MapStyle.For(preset) },
                        new RenderOptions { Stamps = new Images(images),IsExport = true });
                    var frame = StyleDiagnostics.Samples.Last();
                    if (sample > 0) measurements.Add(frame);
                    if (sample == 3)
                    {
                        using var image = surface.Snapshot();
                        SavePng(image,$"{preset}-timing-2048x1095-program.png");
                    }
                }
                timings.Add(FormattableString.Invariant($"{preset},CPU,2048,1095,{measurements.Average(s => s.ShaderMs):F3},{measurements.Average(s => s.PassMs):F3},{measurements.Average(s => s.SceneMs):F3},{measurements.Average(s => s.TensorMs):F3},{measurements.Average(s => s.SoftMs):F3},{measurements.Average(s => s.WideMs):F3},{measurements.Average(s => s.PrintMs):F3},{measurements.Average(s => s.CompositeMs):F3}"));
                File.WriteAllLines(Path.Combine(output,"timings.csv"),timings);
              }
            }
            // MAP_R09_PAINTER_NOTIME = 1 skips the timing frames and their PNGs.
            if (Environment.GetEnvironmentVariable("MAP_R09_PAINTER_NOTIME") != "1") Measure();
            File.WriteAllText(Path.Combine(output,"gpu.txt"),"NOT CHECKED: CPU-only dump; no GL interface, context or window created.");
            File.WriteAllLines(Path.Combine(output,"differences.csv"),metrics);
            File.WriteAllLines(Path.Combine(output,"timings.csv"),timings);
            File.WriteAllLines(Path.Combine(output,"rgba-equality.csv"),equality);
        }
        finally
        {
            StyleDiagnostics.MeasureStages = false;
            while (StyleDiagnostics.Samples.TryDequeue(out _)) { }
            foreach (var image in images.Values) image.Dispose();
        }
        SKImage ProgramFrame(MapDocument document,MapStylePreset preset,float scale)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)(W*scale),(int)(H*scale)))!;
            surface.Canvas.Scale(scale);
            var style = MapStyle.For(preset);
            // MAP_R09_PAINTER_OFF = comma-separated effect kinds to switch off in the program frame (diagnosis).
            foreach (var off in (Environment.GetEnvironmentVariable("MAP_R09_PAINTER_OFF") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                style = style.WithEffect(Enum.Parse<StyleEffectKind>(off), new StyleEffect(false, 0));
            SceneRenderer.Render(surface.Canvas,document with { Style = style },
                new RenderOptions { Stamps = new Images(images), IsExport = true,ViewScale = scale });
            return surface.Snapshot();
        }
        void SavePng(SKImage image,string filename)
        {
            using var data = image.Encode(SKEncodedImageFormat.Png,100);
            File.WriteAllBytes(Path.Combine(output,filename),data.ToArray());
            if (!filename.EndsWith("-program.png",StringComparison.Ordinal)) return;
            // Decode both PNGs to the same unpremultiplied RGBA layout: PNG encoding and
            // native BGRA/premultiplied surfaces cannot hide a changed channel (including alpha).
            var info = new SKImageInfo(image.Width,image.Height,SKColorType.Rgba8888,SKAlphaType.Unpremul);
            using var actual = SKBitmap.Decode(Path.Combine(output,filename),info)!;
            var bytes = actual.GetPixelSpan();
            var different = 0;
            if (!string.IsNullOrEmpty(baseline))
            {
                using var codec = SKCodec.Create(Path.Combine(baseline,filename));
                Assert.Equal(image.Width,codec.Info.Width);
                Assert.Equal(image.Height,codec.Info.Height);
                using var expected = SKBitmap.Decode(Path.Combine(baseline,filename),info)!;
                Assert.Equal(actual.Info,expected.Info);
                var referenceBytes = expected.GetPixelSpan();
                for (var i = 0; i < bytes.Length; i++)
                    if (bytes[i] != referenceBytes[i]) different++;
                Assert.True(different == 0,$"{filename}: {different} changed RGBA bytes");
            }
            equality.Add($"{filename},{bytes.Length},{different},{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}");
        }
    }

    private static double MeanRgb(SKImage actual,SKImage expected)
    {
        using var a = SKBitmap.FromImage(actual); using var b = SKBitmap.FromImage(expected);
        long sum = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var p = a.GetPixel(x,y); var q = b.GetPixel(x,y);
            sum += Math.Abs(p.Red-q.Red)+Math.Abs(p.Green-q.Green)+Math.Abs(p.Blue-q.Blue);
        }
        return sum/(3.0*a.Width*a.Height);
    }
}
