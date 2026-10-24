using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Mock-ups of the R09 styles, map variants and handouts (Claude, for the owner's choice; not product
/// code): MAP_STYLE_MOCKUPS = output folder, MAP_LIGHT_ASSETS = folder of the owner's mountain PNGs
/// named by SHA-256 (a COPY). One test scene (island, river with its name, the owner's three mountains,
/// lights) is rendered through Map; a prototype finishing pass (colour grade, paper, grain, vignette,
/// watercolour edges, torn or burnt edge, folds) and vector decorations (rhumb lines, dotted route, seal)
/// make every variant. Skipped otherwise.
/// </summary>
public sealed class StyleMockupDump
{
    private const int W = 2560, H = 1440;

    [Fact]
    public void Dump_style_mockups_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_MOCKUPS");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var (document, labels) = Scene(images);
            var day = new MapLighting { TimeOfDay = 0.2, Sources = [.. Lights()] };
            var evening = day with { TimeOfDay = 0.5 };
            var night = day with { TimeOfDay = 1 };

            using var dayFrame = Render(document with { Lighting = day }, images);
            using var eveningFrame = Render(document with { Lighting = evening }, images);
            using var nightFrame = Render(document with { Lighting = night }, images);
            using var plainFrame = Render(document, images);
            using var politicalFrame = Render(Political(document, labels) with { Lighting = null }, images);

            if (Environment.GetEnvironmentVariable("MAP_STYLE_MOCKUPS_V2") == "1")
            {
                // Names stay sharp: the map is drawn without them, stylized, and the names go on top.
                var unlabelled = document.WithLayer(labels, l => l with { Visible = false });
                using var dayBare = Render(unlabelled with { Lighting = day }, images);
                using var nightBare = Render(unlabelled with { Lighting = night }, images);
                using var plainBare = Render(unlabelled, images);
                var politicalDoc = Political(document, labels);
                using var politicalBare = Render(politicalDoc.WithLayer(labels, l => l with { Visible = false }), images);
                using var names = RenderOnly(document, labels, images);
                using var politicalNames = RenderOnly(politicalDoc, labels, images);
                foreach (var (name, frame, look) in StrongLooks(dayBare, nightBare, plainBare, politicalBare))
                {
                    using var stylizedBare = ApplyStylize(frame, look.Stylize);
                    using var stylized = Overlay(stylizedBare, ReferenceEquals(frame, politicalBare) ? politicalNames : names);
                    using var finished = Apply(stylized, look.Finish);
                    using var data = finished.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
                }
                return;
            }
            var styles = new (string Name, SKImage Frame, Finish Finish)[]
            {
                ("Стиль · Мастерская (как сейчас)", dayFrame, Finish.None),
                ("Стиль · Тушь", dayFrame, Finish.Ink),
                ("Стиль · Пергамент", dayFrame, Finish.Parchment),
                ("Стиль · Акварель", dayFrame, Finish.Watercolor),
                ("Стиль · Атлас", dayFrame, Finish.Atlas),
                ("Стиль · Зимний", dayFrame, Finish.Winter),
                ("Стиль · Выцветший", dayFrame, Finish.Faded),
                ("Стиль · Свечи", nightFrame, Finish.Candles),
                ("Вариант · День", dayFrame, Finish.None),
                ("Вариант · Вечер", eveningFrame, Finish.None),
                ("Вариант · Ночь", nightFrame, Finish.None),
                ("Вариант · Зима", dayFrame, Finish.Winter),
                ("Вариант · Политическая", politicalFrame, Finish.Atlas),
                ("Хендаут · Старый свиток", plainFrame, Finish.Scroll),
                ("Хендаут · Пиратская карта", plainFrame, Finish.Pirate),
                ("Хендаут · Письмо гонца", plainFrame, Finish.Letter),
            };
            foreach (var (name, frame, finish) in styles)
            {
                using var finished = Apply(frame, finish);
                using var data = finished.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    // ------------------------------------------------------------------ the scene

    private static readonly LayerId LabelsId = LayerId.New();

    private static (MapDocument Document, LayerId Labels) Scene(Dictionary<string, SKImage> images)
    {
        var island = Land("Остров", (1280, 760, 1120, 560, 0.08, 7));
        var river = new River
        {
            Name = "Серебрянка", Style = RiverStyle.Natural() with { TextureId = null }, Level = 1,
            Channels = [RiverEditor.NewChannel([new(1300, 900), new(1180, 1010), new(1010, 1080), new(820, 1170), new(600, 1260)], 30)],
        };
        var water = new ObjectLayer(LayerId.New(), "Реки", true, false, 1, []) { Rivers = [river] };
        var objects = new List<SceneObject>();
        var placements = new[] { (760.0, 820.0, 0.62), (1320.0, 760.0, 0.66), (1860.0, 840.0, 0.60) };
        var i = 0;
        foreach (var (sha, image) in images)
        {
            var (cx, baseY, scale) = placements[i++ % placements.Length];
            double w = 1024 * scale, h = 1024.0 * image.Height / image.Width * scale;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", cx - w / 2, baseY - h, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        var sea = "#FF7F9FAE";
        var draft = new MapDocument(DocumentId.New(), "Макет стилей", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(W, H, "px", sea),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, sea), island, water,
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects.OrderBy(o => o.Y + o.Height)]),
             new ObjectLayer(LabelsId, "Подписи", true, false, 1, [])]);
        var riverName = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Серебрянка", 0, 0, 1, 1, 0, "#FF1F2A36", "#FFE9E1CC", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = "Серебрянка", FontSize = 34, FillColor = "#FF1F2A36", OutlineColor = "#FFE9E1CC", OutlineWidth = 3,
                Path = new TextPathBinding { Target = TextPathTarget.River, TargetId = river.Id, PartId = river.Channels[0].Id, Side = TextPathSide.Above },
            },
        };
        var title = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ледяной хребет", 1060, 230, 520, 70, 0, "#FF1F2A36", "#FFE9E1CC", 0, true, false)
        {
            Text = new TextStyle { Content = "ЛЕДЯНОЙ ХРЕБЕТ", FontSize = 46, FillColor = "#FF1F2A36", OutlineColor = "#FFE9E1CC", OutlineWidth = 3 },
        };
        var laid = TextOnPath.Layout(draft, riverName);
        return (draft.WithLayer(LabelsId, l => ((ObjectLayer)l) with { Objects = [laid, title] }), LabelsId);
    }

    /// <summary>The political variant: the mountains layer hidden, the names kept, a realm named.</summary>
    private static MapDocument Political(MapDocument document, LayerId labels)
    {
        var mountains = document.Layers.OfType<ObjectLayer>().First(l => l.Name == "Горы");
        var realm = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Королевство Север", 980, 560, 600, 70, 0, "#FF7A2A2A", "#FFE9E1CC", 0, true, false)
        {
            Text = new TextStyle { Content = "КОРОЛЕВСТВО СЕВЕР", FontSize = 44, FillColor = "#FF7A2A2A", OutlineColor = "#FFE9E1CC", OutlineWidth = 3 },
        };
        return document.WithLayer(mountains.Id, l => l with { Visible = false })
            .WithLayer(labels, l => ((ObjectLayer)l) with { Objects = [.. ((ObjectLayer)l).Objects, realm] });
    }

    private static IEnumerable<MapLightSource> Lights() =>
    [
        new(Guid.NewGuid(), 1010, 1010, "#ff9a42", 260, 1.15, true),
        new(Guid.NewGuid(), 1560, 930, "#ffb866", 210, 1.25, false),
        new(Guid.NewGuid(), 1500, 960, "#ffc27a", 140, 0.85, false),
        new(Guid.NewGuid(), 700, 1150, "#ff8a3c", 330, 1.3, true),
    ];

    private static LandLayer Land(string name, (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes) s)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty * TileMap.TileSize < H; ty++)
            for (var tx = 0; tx * TileMap.TileSize < W; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = tx * TileMap.TileSize + x, wy = ty * TileMap.TileSize + y;
                        var angle = Math.Atan2(wy - s.Cy, wx - s.Cx);
                        var wobble = 1 + s.Wobble * Math.Sin(angle * s.Lobes) + s.Wobble * .6 * Math.Sin(angle * (s.Lobes * 2 + 3) + 1.3);
                        var distance = Math.Sqrt(Math.Pow((wx - s.Cx) / s.Rx, 2) + Math.Pow((wy - s.Cy) / s.Ry, 2)) / wobble;
                        var b = (byte)Math.Clamp((1 - distance) * Math.Max(s.Rx, s.Ry) / 2 * 255, 0, 255);
                        bytes[y * TileMap.TileSize + x] = b; any |= b > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = 1 };
    }

    /// <summary>Only one layer, on transparency (the names over a stylized map).</summary>
    private static SKImage RenderOnly(MapDocument document, LayerId keep, Dictionary<string, SKImage> images)
    {
        var only = document.WithLayers([.. document.Layers.Select(l => l is BaseColorLayer || l.Id == keep ? l : l with { Visible = false })]) with { Lighting = null };
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        surface.Canvas.Clear(SKColors.Transparent);
        SceneRenderer.Render(surface.Canvas, only, new RenderOptions { Stamps = new Images(images), IsExport = true, IncludeBaseColor = false });
        return surface.Snapshot();
    }

    private static SKImage Overlay(SKImage under, SKImage over)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        surface.Canvas.DrawImage(under, 0, 0);
        surface.Canvas.DrawImage(over, 0, 0);
        return surface.Snapshot();
    }

    private static SKImage Render(MapDocument document, Dictionary<string, SKImage> images)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = new Images(images), IsExport = true });
        return surface.Snapshot();
    }

    // ------------------------------------------------------------------ the finishing pass (prototype)

    private sealed record Finish(
        float Exposure, float Saturation, float Contrast, float Lift, float[] ShadowTint, float[] LightTint, float Split,
        float[] Paper, float PaperAmount, float Vignette, float[] VignetteColor, float Grain, float Wash, float Edge, float Burnt, float Folds,
        bool Rhumbs = false, bool Route = false, bool Seal = false)
    {
        private static readonly float[] Cream = [0.95f, 0.91f, 0.82f];
        private static readonly float[] Black = [0, 0, 0];

        public static Finish None => new(0, 1, 1, 0, Black, Black, 0, Cream, 0, 0, Black, 0, 0, 0, 0, 0);

        public static Finish Ink => new(0.05f, 0.25f, 1.18f, 0.02f, [0.20f, 0.18f, 0.16f], [0.98f, 0.95f, 0.88f], 0.35f,
            [0.96f, 0.93f, 0.85f], 0.55f, 0.18f, [0.25f, 0.20f, 0.14f], 0.035f, 0, 0, 0, 0);

        public static Finish Parchment => new(0.02f, 0.55f, 1.05f, 0.04f, [0.30f, 0.20f, 0.11f], [0.97f, 0.88f, 0.70f], 0.55f,
            [0.93f, 0.84f, 0.66f], 0.75f, 0.32f, [0.32f, 0.20f, 0.08f], 0.05f, 0, 0, 0, 0);

        public static Finish Watercolor => new(0.08f, 1.05f, 0.96f, 0.03f, [0.30f, 0.36f, 0.48f], [1.00f, 0.97f, 0.92f], 0.25f,
            [0.97f, 0.96f, 0.93f], 0.45f, 0.10f, [0.20f, 0.24f, 0.30f], 0.03f, 0.9f, 0, 0, 0);

        public static Finish Atlas => new(0.03f, 0.85f, 1.08f, 0.01f, [0.16f, 0.24f, 0.30f], [1.00f, 0.95f, 0.86f], 0.25f,
            [0.97f, 0.95f, 0.90f], 0.25f, 0.14f, [0.12f, 0.15f, 0.18f], 0.02f, 0, 0, 0, 0);

        public static Finish Winter => new(0.12f, 0.62f, 1.02f, 0.05f, [0.32f, 0.40f, 0.55f], [0.94f, 0.97f, 1.00f], 0.45f,
            [0.95f, 0.97f, 1.00f], 0.30f, 0.12f, [0.20f, 0.26f, 0.38f], 0.02f, 0, 0, 0, 0);

        public static Finish Faded => new(0.10f, 0.45f, 0.86f, 0.12f, [0.38f, 0.30f, 0.22f], [0.98f, 0.92f, 0.78f], 0.40f,
            [0.94f, 0.88f, 0.74f], 0.55f, 0.22f, [0.36f, 0.26f, 0.16f], 0.05f, 0, 0, 0, 0);

        public static Finish Candles => new(0.05f, 0.95f, 1.05f, 0.0f, [0.10f, 0.08f, 0.14f], [1.00f, 0.86f, 0.62f], 0.35f,
            [0.93f, 0.86f, 0.72f], 0.25f, 0.38f, [0.10f, 0.05f, 0.02f], 0.04f, 0, 0, 0, 0);

        public static Finish Scroll => Parchment with { Edge = 1, Folds = 1, PaperAmount = 0.85f, Vignette = 0.38f };

        public static Finish Pirate => Parchment with
        {
            Saturation = 0.45f, Burnt = 1, Edge = 1, PaperAmount = 0.9f, Vignette = 0.42f, Rhumbs = true, Route = true, Seal = true,
        };

        public static Finish Letter => Faded with { Folds = 1, Seal = true, PaperAmount = 0.8f };
    }

    private const string FinishSource = """
uniform shader map;
uniform shader soft;
uniform float2 size;
uniform float exposure;
uniform float saturation;
uniform float contrast;
uniform float lift;
uniform float3 shadowTint;
uniform float3 lightTint;
uniform float split;
uniform float3 paper;
uniform float paperAmount;
uniform float vignette;
uniform float3 vignetteColor;
uniform float grain;
uniform float wash;
uniform float edge;
uniform float burnt;
uniform float folds;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }

half4 main(float2 p) {
    float3 c = map.eval(p).rgb;
    // Watercolour: pigment gathers at the edges of colour areas, granulation in the washes.
    if (wash > 0.0) {
        float3 s = soft.eval(p).rgb;
        float e = clamp(length(c - s) * 3.0, 0.0, 1.0);
        float g = fbm(p / 9.0);
        c *= 1.0 - wash * (0.22 * e * (0.6 + 0.8 * fbm(p / 60.0)) + 0.06 * (g - 0.5));
        c = mix(c, s, 0.25 * wash);
    }
    c *= exp2(exposure);
    c = lift + c * (1.0 - lift);
    c = (c - 0.5) * contrast + 0.5;
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    c = mix(float3(l), c, saturation);
    // Split tone: shadows toward one ink, highlights toward the paper light.
    float3 toned = mix(shadowTint, lightTint, smoothstep(0.0, 1.0, l));
    c = mix(c, c * toned * 1.6, split * 0.6);
    // Paper: low stains and fibres, multiplied.
    float stain = fbm(p / 260.0);
    float fibre = noise(float2(p.x / 2.0, p.y / 40.0));
    float3 sheet = paper * (0.9 + 0.12 * stain - 0.04 * fibre);
    c = mix(c, c * sheet * 1.08, paperAmount);
    // Folds: a light and a dark band each side of the creases.
    if (folds > 0.0) {
        float fx = (p.x - size.x * 0.5) / 6.0; float fy = (p.y - size.y * 0.5) / 6.0;
        float crease = exp(-fx * fx) * 0.5 + exp(-fy * fy) * 0.5;
        float side = (exp(-(fx - 3.0) * (fx - 3.0) / 9.0) + exp(-(fy - 3.0) * (fy - 3.0) / 9.0)) * 0.5;
        c *= 1.0 - folds * (0.16 * crease - 0.05 * side);
        c *= 1.0 - folds * 0.05 * fbm(p / 30.0) * crease * 6.0;
    }
    // Vignette from the centre of the map.
    float2 q = (p - size * 0.5) / size;
    float v = smoothstep(0.25, 0.75, length(q * float2(1.0, 0.85)) * 1.25);
    c = mix(c, c * vignetteColor * 2.2, vignette * v);
    // Grain.
    c += grain * (hash(floor(p)) - 0.5) * (1.0 - 0.6 * l);
    float alpha = 1.0;
    // Torn or burnt edge: a noisy border; burnt adds a charred rim.
    if (edge > 0.0) {
        float d = min(min(p.x, size.x - p.x), min(p.y, size.y - p.y));
        float rough = 40.0 + 70.0 * fbm(p / 70.0) + 25.0 * fbm(p / 12.0);
        float k = d - rough;
        alpha = smoothstep(0.0, 2.5, k);
        if (burnt > 0.0) {
            float rim = 1.0 - smoothstep(0.0, 30.0, k);
            c = mix(c, float3(0.16, 0.08, 0.03), burnt * rim * 0.85);
            c = mix(c, c * float3(0.75, 0.55, 0.35), burnt * (1.0 - smoothstep(0.0, 120.0, k)) * 0.5);
        } else {
            float rim = 1.0 - smoothstep(0.0, 16.0, k);
            c = mix(c, c * float3(0.78, 0.68, 0.52), rim * 0.6);
        }
    }
    c = clamp(c, 0.0, 1.0);
    return half4(half3(c * alpha), half(alpha));
}
""";

    private static readonly Lazy<SKRuntimeEffect> FinishEffect = new(() =>
        SKRuntimeEffect.CreateShader(FinishSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKImage Apply(SKImage frame, Finish f)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        var canvas = surface.Canvas;
        // The table under a handout: a dark wood so the torn edge shows.
        canvas.Clear(f.Edge > 0 ? new SKColor(0x3a, 0x2c, 0x22) : SKColors.Transparent);
        using var softSurface = SKSurface.Create(new SKImageInfo(W, H))!;
        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(6, 6) }) softSurface.Canvas.DrawImage(frame, 0, 0, blur);
        using var soft = softSurface.Snapshot();
        var builder = new SKRuntimeShaderBuilder(FinishEffect.Value);
        using var mapShader = frame.ToShader();
        using var softShader = soft.ToShader();
        builder.Children["map"] = mapShader;
        builder.Children["soft"] = softShader;
        builder.Uniforms["size"] = new float[] { W, H };
        builder.Uniforms["exposure"] = f.Exposure; builder.Uniforms["saturation"] = f.Saturation;
        builder.Uniforms["contrast"] = f.Contrast; builder.Uniforms["lift"] = f.Lift;
        builder.Uniforms["shadowTint"] = f.ShadowTint; builder.Uniforms["lightTint"] = f.LightTint; builder.Uniforms["split"] = f.Split;
        builder.Uniforms["paper"] = f.Paper; builder.Uniforms["paperAmount"] = f.PaperAmount;
        builder.Uniforms["vignette"] = f.Vignette; builder.Uniforms["vignetteColor"] = f.VignetteColor;
        builder.Uniforms["grain"] = f.Grain; builder.Uniforms["wash"] = f.Wash;
        builder.Uniforms["edge"] = f.Edge; builder.Uniforms["burnt"] = f.Burnt; builder.Uniforms["folds"] = f.Folds;
        using var shader = builder.Build();
        using (var paint = new SKPaint { Shader = shader }) canvas.DrawRect(0, 0, W, H, paint);
        // Ink lies on the sheet only, not on the table around a torn edge.
        canvas.Save();
        if (f.Edge > 0) canvas.ClipRect(SKRect.Create(120, 120, W - 240, H - 240));
        if (f.Rhumbs) DrawRhumbs(canvas);
        if (f.Route) DrawRoute(canvas);
        canvas.Restore();
        if (f.Seal) DrawSeal(canvas, f.Edge > 0 ? new SKPoint(2250, 1180) : new SKPoint(2230, 1170));
        return surface.Snapshot();
    }

    /// <summary>Portolan rhumb lines from a hidden centre: 16 directions, black, green and red by rank.</summary>
    private static void DrawRhumbs(SKCanvas canvas)
    {
        var centre = new SKPoint(1880, 330);
        using var ink = new SKPaint { IsAntialias = true, StrokeWidth = 1.6f, Style = SKPaintStyle.Stroke };
        for (var i = 0; i < 32; i++)
        {
            var a = i * Math.PI / 16;
            ink.Color = (i % 4 == 0 ? new SKColor(0x2a, 0x1e, 0x14) : i % 2 == 0 ? new SKColor(0x3d, 0x5a, 0x34) : new SKColor(0x8a, 0x2d, 0x24)).WithAlpha(120);
            canvas.DrawLine(centre, new SKPoint(centre.X + (float)Math.Cos(a) * 3200, centre.Y + (float)Math.Sin(a) * 3200), ink);
        }
        using var ring = new SKPaint { IsAntialias = true, StrokeWidth = 3, Style = SKPaintStyle.Stroke, Color = new SKColor(0x2a, 0x1e, 0x14, 200) };
        canvas.DrawCircle(centre, 70, ring);
        canvas.DrawCircle(centre, 54, ring);
    }

    /// <summary>A dotted route to X: hand-wobbled dashes in red-brown ink and a cross at the end.</summary>
    private static void DrawRoute(SKCanvas canvas)
    {
        using var path = new SKPath();
        path.MoveTo(420, 1180);
        path.CubicTo(700, 1060, 760, 980, 980, 1000);
        path.CubicTo(1180, 1020, 1240, 1180, 1460, 1120);
        path.CubicTo(1620, 1080, 1640, 980, 1720, 960);
        using var dash = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round,
            Color = new SKColor(0x7c, 0x22, 0x18, 230), PathEffect = SKPathEffect.CreateDash([2, 22], 0),
        };
        canvas.DrawPath(path, dash);
        using var cross = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 11, StrokeCap = SKStrokeCap.Round, Color = new SKColor(0x7c, 0x22, 0x18, 240) };
        canvas.DrawLine(1690, 930, 1750, 990, cross);
        canvas.DrawLine(1750, 930, 1690, 990, cross);
    }

    /// <summary>A wax seal: a wobbly red blob, an embossed ring and a highlight from the upper left.</summary>
    private static void DrawSeal(SKCanvas canvas, SKPoint at)
    {
        using var blob = new SKPath();
        var random = new Random(7);
        for (var i = 0; i <= 48; i++)
        {
            var a = i * Math.PI * 2 / 48;
            var r = 92 + random.NextDouble() * 12 + (i % 7 == 0 ? 10 : 0);
            var pt = new SKPoint(at.X + (float)(Math.Cos(a) * r), at.Y + (float)(Math.Sin(a) * r));
            if (i == 0) blob.MoveTo(pt); else blob.LineTo(pt);
        }
        blob.Close();
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90), ImageFilter = SKImageFilter.CreateBlur(10, 10) };
        canvas.Save(); canvas.Translate(8, 10); canvas.DrawPath(blob, shadow); canvas.Restore();
        using var wax = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(at.X - 30, at.Y - 34), 150,
                [new SKColor(0xc4, 0x3b, 0x2f), new SKColor(0x8e, 0x1f, 0x1a), new SKColor(0x5a, 0x10, 0x0e)], [0, 0.55f, 1], SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(blob, wax);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = new SKColor(0x4a, 0x0c, 0x0a, 200) };
        canvas.DrawCircle(at, 58, ring);
        ring.Color = new SKColor(0xe0, 0x7a, 0x60, 120); ring.StrokeWidth = 2;
        canvas.DrawCircle(at.X - 2, at.Y - 2, 58, ring);
        using var font = new SKFont(SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold), 58);
        using var letter = new SKPaint { IsAntialias = true, Color = new SKColor(0x4a, 0x0c, 0x0a, 220) };
        canvas.DrawText("М", at.X - 21, at.Y + 20, font, letter);
        letter.Color = new SKColor(0xe8, 0x8a, 0x70, 110);
        canvas.DrawText("М", at.X - 23, at.Y + 18, font, letter);
    }

    // ------------------------------------------------------------------ strong looks (second round)

    /// <summary>The non-photoreal stage: painterly smoothing, palette, ink lines, hatching, print dots, wobble, dither.</summary>
    private sealed record Stylize(
        float Kuwahara, float Levels, float[][] Gradient, float GradientAmount, float Saturation,
        float EdgeInk, float[] InkColor, float EdgeWidth, float Hatch, float HatchSpacing,
        float Halftone, float Cell, float Wobble, float Dither, float Invert = 0, float Grid = 0,
        float InkKeep = 0, float Duotone = 0, float[]? Ink2 = null, float Charcoal = 0, float Pixel = 0)
    {
        public static float[] Hex(string hex)
        {
            var v = Convert.ToInt32(hex.TrimStart('#'), 16);
            return [((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f];
        }

        public static float[][] Ramp(params string[] stops) => [.. stops.Select(Hex)];

        public static Stylize Off => new(0, 0, Ramp("#000000", "#555555", "#aaaaaa", "#ffffff"), 0, 1, 0, Hex("#000000"), 1.5f, 0, 7, 0, 7, 0, 0);
    }

    private sealed record Look(Stylize Stylize, Finish Finish);

    private static IEnumerable<(string Name, SKImage Frame, Look Look)> StrongLooks(SKImage day, SKImage night, SKImage plain, SKImage political)
    {
        // Every style brings the owner's ink lines back over itself (Astra's guard), unless it says otherwise.
        var o = Stylize.Off with { InkKeep = 0.85f };
        var ink = Stylize.Hex("#1b1712");
        var clean = Finish.None;
        yield return ("Стиль · Мастерская (как сейчас)", day, new(Stylize.Off, clean));
        // Sumi ink wash: grey-brown ink in four tones, flattened brush areas, strong contour.
        yield return ("Стиль · Тушь", day, new(o with
        {
            Kuwahara = 3, Gradient = Stylize.Ramp("#16130f", "#4d473f", "#a8a090", "#f3eddf"), GradientAmount = 0.95f,
            EdgeInk = 0.95f, InkColor = ink, EdgeWidth = 1.4f,
        }, Finish.Ink with { Saturation = 1, Split = 0, Contrast = 1.05f, PaperAmount = 0.7f, Vignette = 0.2f }));
        // Old map: everything in sepia inks, brown contour, burnt-in edges, foxing.
        yield return ("Стиль · Старая карта", day, new(o with
        {
            Kuwahara = 2, Gradient = Stylize.Ramp("#2a180b", "#714a26", "#c99d63", "#f3e1b6"), GradientAmount = 0.92f,
            EdgeInk = 0.8f, InkColor = Stylize.Hex("#3a2212"), EdgeWidth = 1.3f,
        }, Finish.Parchment with { Saturation = 1, Split = 0, PaperAmount = 0.9f, Vignette = 0.55f, Grain = 0.06f }));
        // Watercolour: wobbly flattened washes, saturated pigment, dark edges, no ink line.
        yield return ("Стиль · Акварель", day, new(o with
        {
            Kuwahara = 4, Wobble = 3, Saturation = 1.9f, Gradient = Stylize.Ramp("#22324f", "#3f6f9a", "#9fc3dc", "#fbf6ec"),
            GradientAmount = 0.35f, EdgeInk = 0.35f, InkColor = Stylize.Hex("#24344d"), EdgeWidth = 2.2f,
        }, Finish.Watercolor with { Wash = 2.6f, PaperAmount = 0.7f, Paper = [0.98f, 0.95f, 0.88f], Contrast = 1.12f, Grain = 0.05f }));
        // Engraving: two-tone ink with hatching by tone, the look of a copper-plate atlas.
        yield return ("Стиль · Гравюра", day, new(o with
        {
            Gradient = Stylize.Ramp("#1a2230", "#55606c", "#c9c3b2", "#f2ecdc"), GradientAmount = 1, Hatch = 1, HatchSpacing = 6,
            EdgeInk = 0.9f, InkColor = Stylize.Hex("#141a24"), EdgeWidth = 1.2f,
        }, Finish.Ink with { Saturation = 1, Split = 0, PaperAmount = 0.6f, Vignette = 0.15f, Grain = 0.02f }));
        // Two-colour print: deep blue and terracotta dot screens on cream, the owner's ink solid.
        yield return ("Стиль · Двухкрасочная печать", day, new(o with
        {
            Gradient = Stylize.Ramp("#000000", "#000000", "#000000", "#f1e7cf"), Duotone = 1, Cell = 6,
            InkColor = Stylize.Hex("#1f3550"), Ink2 = Stylize.Hex("#c0583a"), InkKeep = 1, EdgeWidth = 1.3f,
        }, Finish.None with { Paper = [0.96f, 0.92f, 0.82f], PaperAmount = 0.4f, Grain = 0.03f }));
        // Oil over the drawing: broad painterly masses, rich colour, the ink kept.
        yield return ("Стиль · Масло", day, new(o with
        {
            Kuwahara = 7, Saturation = 1.35f, InkKeep = 0.9f, InkColor = Stylize.Hex("#1d1915"), EdgeWidth = 1.4f,
        }, Finish.Atlas with { Contrast = 1.15f, Split = 0.3f, ShadowTint = [0.22f, 0.20f, 0.30f], LightTint = [1.0f, 0.93f, 0.80f], PaperAmount = 0.15f, Vignette = 0.25f }));
        // Charcoal sheet: no colour, velvety streaked shadows, the line kept.
        yield return ("Стиль · Уголь", day, new(o with
        {
            Gradient = Stylize.Ramp("#1a1816", "#1a1816", "#efebe2", "#efebe2"), Charcoal = 1, InkKeep = 1, InkColor = Stylize.Hex("#141210"), EdgeWidth = 1.3f,
        }, Finish.None with { Paper = [0.95f, 0.94f, 0.90f], PaperAmount = 0.6f, Grain = 0.05f, Vignette = 0.2f, VignetteColor = [0.2f, 0.2f, 0.2f] }));
        // Ukiyo-e: flat colour fields, bold contour, warm paper.
        yield return ("Стиль · Укиё-э", day, new(o with
        {
            Kuwahara = 5, Gradient = Stylize.Ramp("#1c2440", "#2f5f86", "#d8bf8e", "#f4ead3"), GradientAmount = 0.9f,
            EdgeInk = 1, InkColor = Stylize.Hex("#1e1a22"), EdgeWidth = 1.8f,
        }, Finish.Atlas with { Paper = [0.96f, 0.91f, 0.80f], PaperAmount = 0.6f, Split = 0, Saturation = 1.1f, Vignette = 0.12f }));
        // Blueprint: the drawing as white lines on deep blue, with a grid.
        yield return ("Стиль · Синька", day, new(o with
        {
            Gradient = Stylize.Ramp("#0b2347", "#123766", "#1d4f8c", "#2f66a8"), GradientAmount = 1, Invert = 0,
            EdgeInk = 1, InkColor = Stylize.Hex("#eaf3ff"), EdgeWidth = 1.3f, Grid = 1,
        }, clean with { Grain = 0.02f, Vignette = 0.25f, VignetteColor = [0.02f, 0.06f, 0.15f] }));
        // Moonlight: night in blue-silver tones, lights kept warm.
        yield return ("Стиль · Лунная ночь", night, new(o with
        {
            Kuwahara = 2, Gradient = Stylize.Ramp("#05070f", "#1b2a46", "#5d7aa8", "#d9e6ff"), GradientAmount = 0.55f, Saturation = 0.8f,
            EdgeInk = 0.5f, InkColor = Stylize.Hex("#04060c"), EdgeWidth = 1.4f,
        }, Finish.Candles with { Vignette = 0.5f }));
        // Pocket pixel atlas: coarse blocks, a small palette, ordered dither in the half-tones.
        yield return ("Стиль · Пиксельный атлас", day, new(o with
        {
            Pixel = 4, Gradient = Stylize.Ramp("#1d2433", "#4a6d8c", "#c8b98e", "#f4efe1"), GradientAmount = 0.55f, Levels = 4, Saturation = 1.3f,
        }, clean));
        // Frost: icy blue palette, white highlights, blue contour.
        yield return ("Стиль · Зимний", day, new(o with
        {
            Kuwahara = 2, Gradient = Stylize.Ramp("#16253a", "#4f7299", "#b8d2ea", "#f7fbff"), GradientAmount = 0.8f,
            EdgeInk = 0.6f, InkColor = Stylize.Hex("#1a2c45"), EdgeWidth = 1.4f,
        }, Finish.Winter with { Saturation = 1, Split = 0, PaperAmount = 0.2f }));
        yield return ("Вариант · Политическая", political, new(o with
        {
            Levels = 5, Saturation = 1.2f, EdgeInk = 0.6f, InkColor = ink, EdgeWidth = 1.3f,
        }, Finish.Atlas));
        yield return ("Хендаут · Старый свиток", plain, new(o with
        {
            Kuwahara = 2, Gradient = Stylize.Ramp("#2a180b", "#714a26", "#c99d63", "#f3e1b6"), GradientAmount = 0.92f,
            EdgeInk = 0.8f, InkColor = Stylize.Hex("#3a2212"), EdgeWidth = 1.3f,
        }, Finish.Scroll with { Saturation = 1, Split = 0 }));
        yield return ("Хендаут · Пиратская карта", plain, new(o with
        {
            Kuwahara = 2, Gradient = Stylize.Ramp("#24140a", "#6a4020", "#c08a50", "#ecd2a0"), GradientAmount = 0.95f,
            EdgeInk = 0.9f, InkColor = Stylize.Hex("#2c170a"), EdgeWidth = 1.5f,
        }, Finish.Pirate with { Saturation = 1, Split = 0 }));
        yield return ("Хендаут · Письмо гонца", plain, new(o with
        {
            Gradient = Stylize.Ramp("#3b2a1a", "#7a6248", "#cdb894", "#f2e6cc"), GradientAmount = 0.85f, Hatch = 0.6f, HatchSpacing = 7,
            EdgeInk = 0.7f, InkColor = Stylize.Hex("#3b2a1a"), EdgeWidth = 1.2f,
        }, Finish.Letter with { Saturation = 1, Split = 0 }));
    }

    private const string StylizeSource = """
uniform shader map;
uniform float2 size;
uniform float kuwahara;
uniform float levels;
uniform float3 g0; uniform float3 g1; uniform float3 g2; uniform float3 g3;
uniform float gradient;
uniform float saturation;
uniform float edgeInk;
uniform float3 inkColor;
uniform float edgeWidth;
uniform float hatch;
uniform float hatchSpacing;
uniform float halftone;
uniform float cell;
uniform float wobble;
uniform float dither;
uniform float grid;
uniform float inkKeep;
uniform float duotone;
uniform float3 ink2;
uniform float charcoal;
uniform float pixel;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

// Kuwahara: of four square windows around the pixel, the mean of the calmest one.
float3 kuwa(float2 p, float r) {
    float3 best = map.eval(p).rgb; float bestVar = 1e9;
    for (int q = 0; q < 4; q++) {
        float2 dir = float2(q == 0 || q == 2 ? -1.0 : 1.0, q < 2 ? -1.0 : 1.0);
        float3 sum = float3(0); float3 sq = float3(0);
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) {
            float3 c = map.eval(p + dir * float2(float(x), float(y)) * (r / 3.0)).rgb;
            sum += c; sq += c * c;
        }
        float3 m = sum / 16.0; float3 v = sq / 16.0 - m * m;
        float vv = v.r + v.g + v.b;
        if (vv < bestVar) { bestVar = vv; best = m; }
    }
    return best;
}

float3 ramp(float l) {
    l = clamp(l, 0.0, 1.0) * 3.0;
    return l < 1.0 ? mix(g0, g1, l) : l < 2.0 ? mix(g1, g2, l - 1.0) : mix(g2, g3, l - 2.0);
}

half4 main(float2 p) {
    float a = map.eval(p).a;
    float3 original = map.eval(p).rgb;
    float2 w = float2(noise(p / 40.0), noise(p / 40.0 + 17.3)) - 0.5;
    float2 q = p + wobble * 2.0 * w;
    // Pocket pixel atlas: sample the centre of a coarse block.
    if (pixel > 0.0) q = (floor(q / pixel) + 0.5) * pixel;
    float3 c = kuwahara > 0.0 ? kuwa(q, kuwahara) : map.eval(q).rgb;
    float l = lum(c);
    c = mix(float3(l), c, saturation);
    if (levels > 0.0) { c = floor(c * levels + 0.5) / levels; l = lum(c); }
    c = mix(c, ramp(l), gradient);
    // Hatching: three line families darken by tone.
    if (hatch > 0.0) {
        float d = 1.0 - l;
        float s = hatchSpacing;
        float h1 = smoothstep(0.55, 0.85, abs(fract((p.x + p.y) / s) - 0.5) * 2.0);
        float h2 = smoothstep(0.55, 0.85, abs(fract((p.x - p.y) / s) - 0.5) * 2.0);
        float h3 = smoothstep(0.6, 0.9, abs(fract(p.y / (s * 0.7)) - 0.5) * 2.0);
        float lines = h1 * step(0.25, d) + h2 * step(0.5, d) + h3 * step(0.72, d);
        c = mix(c, c * 0.25, hatch * clamp(lines, 0.0, 1.0));
    }
    // Halftone: rotated dot screen, dot size by darkness.
    if (halftone > 0.0) {
        float2 r = float2(p.x * 0.866 - p.y * 0.5, p.x * 0.5 + p.y * 0.866) / cell;
        float2 f = fract(r) - 0.5;
        float spot = smoothstep(0.0, 0.08, sqrt((1.0 - l) * 0.5) - length(f));
        c = mix(c, c * (1.0 - 0.55 * spot) + 0.12 * (1.0 - spot), halftone);
    }
    // Ink contour: Sobel on luminance.
    if (edgeInk > 0.0) {
        float e = edgeWidth;
        float tl = lum(map.eval(p + float2(-e, -e)).rgb), t = lum(map.eval(p + float2(0, -e)).rgb), tr = lum(map.eval(p + float2(e, -e)).rgb);
        float ml = lum(map.eval(p + float2(-e, 0)).rgb), mr = lum(map.eval(p + float2(e, 0)).rgb);
        float bl = lum(map.eval(p + float2(-e, e)).rgb), b = lum(map.eval(p + float2(0, e)).rgb), br = lum(map.eval(p + float2(e, e)).rgb);
        float gx = tr + 2.0 * mr + br - tl - 2.0 * ml - bl;
        float gy = bl + 2.0 * b + br - tl - 2.0 * t - tr;
        float g = sqrt(gx * gx + gy * gy);
        c = mix(c, inkColor, edgeInk * smoothstep(0.18, 0.55, g));
    }
    // Two-colour print: a dark ink by tone and a warm ink by warmth, each its own dot screen.
    if (duotone > 0.0) {
        float d1 = clamp((1.0 - l) * 1.15, 0.0, 1.0);
        float d2 = clamp((original.r - original.b) * 2.2 + 0.25 * (1.0 - l), 0.0, 1.0);
        float2 r1 = float2(p.x * 0.966 - p.y * 0.259, p.x * 0.259 + p.y * 0.966) / cell;
        float2 r2 = float2(p.x * 0.707 + p.y * 0.707, -p.x * 0.707 + p.y * 0.707) / cell;
        float s1 = smoothstep(0.0, 0.06, sqrt(d1) * 0.62 - length(fract(r1) - 0.5));
        float s2 = smoothstep(0.0, 0.06, sqrt(d2) * 0.6 - length(fract(r2) - 0.5));
        float3 printed = g3;
        printed *= mix(float3(1), ink2, s2 * 0.85);
        printed *= mix(float3(1), inkColor, s1 * 0.92);
        c = mix(c, printed, duotone);
    }
    // Charcoal: monochrome, streaked grain by darkness, smudged shadows.
    if (charcoal > 0.0) {
        float2 sp = float2(p.x * 0.8 + p.y * 0.6, -p.x * 0.6 + p.y * 0.8);
        float streak = noise(float2(sp.x / 1.6, sp.y / 14.0)) * 0.6 + noise(p / 3.0) * 0.4;
        float tone = smoothstep(0.15, 0.95, l);
        float dark = 1.0 - tone;
        float cover = smoothstep(streak - 0.25, streak + 0.25, dark * 1.15);
        c = mix(c, mix(g3, g0, cover), charcoal);
    }
    // Ordered 4x4 dither to two colours.
    if (dither > 0.0) {
        int ix = int(mod(p.x, 4.0)); int iy = int(mod(p.y, 4.0));
        float m = 0.0;
        // Bayer 4x4
        float b0 = (ix == 0 ? (iy == 0 ? 0.0 : iy == 1 ? 12.0 : iy == 2 ? 3.0 : 15.0)
                 : ix == 1 ? (iy == 0 ? 8.0 : iy == 1 ? 4.0 : iy == 2 ? 11.0 : 7.0)
                 : ix == 2 ? (iy == 0 ? 2.0 : iy == 1 ? 14.0 : iy == 2 ? 1.0 : 13.0)
                 : (iy == 0 ? 10.0 : iy == 1 ? 6.0 : iy == 2 ? 9.0 : 5.0));
        m = (b0 + 0.5) / 16.0;
        float lit = lum(mix(c, ramp(l), 0.0));
        c = mix(c, lit > m ? g3 : g0, dither);
    }
    // Keep the owner's ink: dark thin strokes of the original come back over the style.
    if (inkKeep > 0.0) {
        float lo = lum(original);
        float around = 0.25 * (lum(map.eval(p + float2(2.5, 0)).rgb) + lum(map.eval(p - float2(2.5, 0)).rgb)
            + lum(map.eval(p + float2(0, 2.5)).rgb) + lum(map.eval(p - float2(0, 2.5)).rgb));
        float line = smoothstep(0.42, 0.18, lo) * smoothstep(0.03, 0.14, around - lo);
        c = mix(c, inkColor * (0.6 + 0.8 * lo), inkKeep * line);
    }
    if (grid > 0.0) {
        float gl = max(1.0 - smoothstep(0.0, 1.2, abs(mod(p.x, 128.0) - 64.0) - 63.0), 1.0 - smoothstep(0.0, 1.2, abs(mod(p.y, 128.0) - 64.0) - 63.0));
        c = mix(c, inkColor, grid * 0.25 * gl);
    }
    return half4(half3(clamp(c, 0.0, 1.0) * a), half(a));
}
""";

    private static readonly Lazy<SKRuntimeEffect> StylizeEffect = new(() =>
        SKRuntimeEffect.CreateShader(StylizeSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKImage ApplyStylize(SKImage frame, Stylize s)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        var builder = new SKRuntimeShaderBuilder(StylizeEffect.Value);
        using var mapShader = frame.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        builder.Children["map"] = mapShader;
        builder.Uniforms["size"] = new float[] { W, H };
        builder.Uniforms["kuwahara"] = s.Kuwahara; builder.Uniforms["levels"] = s.Levels;
        builder.Uniforms["g0"] = s.Gradient[0]; builder.Uniforms["g1"] = s.Gradient[1];
        builder.Uniforms["g2"] = s.Gradient[2]; builder.Uniforms["g3"] = s.Gradient[3];
        builder.Uniforms["gradient"] = s.GradientAmount; builder.Uniforms["saturation"] = s.Saturation;
        builder.Uniforms["edgeInk"] = s.EdgeInk; builder.Uniforms["inkColor"] = s.InkColor; builder.Uniforms["edgeWidth"] = s.EdgeWidth;
        builder.Uniforms["hatch"] = s.Hatch; builder.Uniforms["hatchSpacing"] = s.HatchSpacing;
        builder.Uniforms["halftone"] = s.Halftone; builder.Uniforms["cell"] = s.Cell;
        builder.Uniforms["wobble"] = s.Wobble; builder.Uniforms["dither"] = s.Dither; builder.Uniforms["grid"] = s.Grid;
        builder.Uniforms["inkKeep"] = s.InkKeep; builder.Uniforms["duotone"] = s.Duotone; builder.Uniforms["ink2"] = s.Ink2 ?? s.InkColor;
        builder.Uniforms["charcoal"] = s.Charcoal; builder.Uniforms["pixel"] = s.Pixel;
        using var shader = builder.Build();
        using (var paint = new SKPaint { Shader = shader }) surface.Canvas.DrawRect(0, 0, W, H, paint);
        return surface.Snapshot();
    }

    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }
}
