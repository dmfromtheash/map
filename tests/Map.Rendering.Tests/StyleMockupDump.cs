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

    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }
}
