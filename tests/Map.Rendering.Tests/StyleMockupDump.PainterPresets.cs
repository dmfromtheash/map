using Map.Core.Documents;
using Map.Rendering.Styles;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed partial class StyleMockupDump
{
    /// <summary>Opt-in, offscreen only: exact original references, program frames, RGB errors and style timings.</summary>
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
        var timings = new List<string> { "preset,backend,width,height,shader_ms,full_style_pass_ms" };
        MapStylePreset[] presets = [MapStylePreset.WatercolorBigShapes,MapStylePreset.UkiyoeSosakuHanga,
            MapStylePreset.PrintLetterpress,MapStylePreset.PrintTintedLithograph,
            MapStylePreset.OilAllaPrima,MapStylePreset.OilImpastoRhythm];
        // MAP_R09_PAINTER_ONLY = a comma-separated list of preset names, to compare only those.
        if (Environment.GetEnvironmentVariable("MAP_R09_PAINTER_ONLY") is { Length: > 0 } only)
            presets = [.. presets.Where(p => only.Split(',').Contains(p.ToString()))];
        var oils = OilVariants().ToDictionary(v => v.Oil.Variant, v => v.Oil);
        try
        {
            foreach (var crowd in new[] { false,true })
            {
                var tag = crowd ? "crowd" : "small";
                var (doc,labels) = crowd ? Crowd(images) : Scene(images);
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
                        MapStylePreset.OilAllaPrima => OilShader(mountains,wetBig,wetFacets,ca,cb,wide,wetTensor,geo,flow,oils[1],ms),
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
                        using var ps = SKSurface.Create(new SKImageInfo((int)(W*scale),(int)(H*scale)))!;
                        ps.Canvas.Scale(scale);
                        SceneRenderer.Render(ps.Canvas,doc with { Style = MapStyle.For(preset) },
                            new RenderOptions { Stamps = new Images(images), IsExport = true,ViewScale = scale });
                        using var actual = ps.Snapshot();
                        SavePng(expected,$"{preset}-{tag}-{scale*100:0}-mockup.png");
                        SavePng(actual,$"{preset}-{tag}-{scale*100:0}-program.png");
                        metrics.Add(FormattableString.Invariant($"{preset},{tag},{scale},{MeanRgb(actual,expected):F6}"));
                    }
                }
            }
            var (timingDoc,_) = Crowd(images);
            StyleDiagnostics.MeasureStages = true;
            void Measure(GRContext? context)
            {
              foreach (var preset in new[] { MapStylePreset.InkWetBrush }.Concat(presets))
              {
                var measurements = new List<StyleFrameSample>();
                for (var sample = 0; sample < 4; sample++)
                {
                    while (StyleDiagnostics.Samples.TryDequeue(out _)) { }
                    var info = new SKImageInfo(2048,1095);
                    using var surface = context is null ? SKSurface.Create(info)! : SKSurface.Create(context,false,info)!;
                    surface.Canvas.Translate(-256,-172);
                    SceneRenderer.Render(surface.Canvas,timingDoc with { Style = MapStyle.For(preset) },
                        new RenderOptions { Stamps = new Images(images),IsExport = context is null,GpuContext = context });
                    var frame = StyleDiagnostics.Samples.Last();
                    if (sample > 0) measurements.Add(frame);
                }
                timings.Add(FormattableString.Invariant($"{preset},{(context is null ? "CPU" : "GPU")},2048,1095,{measurements.Average(s => s.ShaderMs):F3},{measurements.Average(s => s.PassMs):F3}"));
              }
            }
            // MAP_R09_PAINTER_NOTIME = 1 skips the timings (the CPU pass of the oil presets takes about half a minute a frame).
            if (Environment.GetEnvironmentVariable("MAP_R09_PAINTER_NOTIME") != "1") Measure(null);
            // No window is created to acquire a GL context. This succeeds only if a windowless
            // current context was provided by the host; normal VSTest has none.
            using var gl = GRGlInterface.Create();
            using var gpu = gl is null ? null : GRContext.CreateGl(gl);
            if (gpu is not null) Measure(gpu);
            File.WriteAllText(Path.Combine(output,"gpu.txt"),gpu is null
                ? "NOT CHECKED: no current windowless GL context in VSTest; no window opened."
                : "FACT: GPU measured using the host's current windowless GL context.");
            File.WriteAllLines(Path.Combine(output,"differences.csv"),metrics);
            File.WriteAllLines(Path.Combine(output,"timings.csv"),timings);
        }
        finally
        {
            StyleDiagnostics.MeasureStages = false;
            while (StyleDiagnostics.Samples.TryDequeue(out _)) { }
            foreach (var image in images.Values) image.Dispose();
        }
        void SavePng(SKImage image,string filename)
        {
            using var data = image.Encode(SKEncodedImageFormat.Png,100);
            File.WriteAllBytes(Path.Combine(output,filename),data.ToArray());
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
