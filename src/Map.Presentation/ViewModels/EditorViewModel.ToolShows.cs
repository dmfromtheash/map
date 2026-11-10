using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>What a «▶» show of a button plays: one tool or one section.</summary>
public enum ShowKind
{
    Select, Land, River, Road, Line, Shape, Text, Brush, Mask, Assets,
    SectionDraw, SectionObjects, SectionWorld, SectionAppearance,
}

/// <summary>
/// The «▶» shows of the buttons (plan V5, owner 2026-10-31: «анимация визуализации того что делает каждая кнопка. с
/// простым объяснением… у каждой функции всех кнопок и разделов»; «1 в 1 всё как в настоящем использовании»). Each show
/// is a short scene «до → действие → после»: frame 0 is the map before, the last frame the finished result. A frame is
/// a small map made with the same calls and the same settings for new things as the editor itself (the width of new
/// river nodes, the colours of new lines and shapes, the brush in hand, the owner's mountains from the catalog), on this
/// map's own page, sea, style and light. Nothing here touches the open map: the frames are separate documents.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>A show's map size in map units: its 316×178 DIP frame at twice the size.</summary>
    public const int ShowWidth = 632, ShowHeight = 356;

    /// <summary>Frames of growth in a show; the last one is the result.</summary>
    public const int ShowFrames = 8;

    /// <summary>The show's one line «зачем»: at most eight words, no ellipsis.</summary>
    public static string ShowWhy(ShowKind kind) => kind switch
    {
        ShowKind.Select => "Двигать, поворачивать и менять размер",
        ShowKind.Land => "Рисует сушу: острова и материки",
        ShowKind.River => "Река по точкам от истока к устью",
        ShowKind.Road => "Дорога по точкам, мосты у воды сами",
        ShowKind.Line => "Граница или тропа линией по точкам",
        ShowKind.Shape => "Фигура с заливкой: поле, болото, область",
        ShowKind.Text => "Подпись на карте: название места",
        ShowKind.Brush => "Краска поверх карты, как кистью",
        ShowKind.Mask => "Прячет часть слоя, не стирая её",
        ShowKind.Assets => "Ставит ваши горы щелчком по карте",
        ShowKind.SectionDraw => "Земля, вода, дороги и подписи",
        ShowKind.SectionObjects => "Ваши горы и рисунки на карте",
        ShowKind.SectionWorld => "Город, проверки, время и атлас",
        ShowKind.SectionAppearance => "Свет, стиль и море всей карты",
        _ => "",
    };

    /// <summary>The show of a rail tool; null for a tool without one yet.</summary>
    public static ShowKind? ShowOf(EditorTool tool) => tool switch
    {
        EditorTool.Select => ShowKind.Select,
        EditorTool.Land => ShowKind.Land,
        EditorTool.River => ShowKind.River,
        EditorTool.Road => ShowKind.Road,
        EditorTool.Line => ShowKind.Line,
        EditorTool.Shape => ShowKind.Shape,
        EditorTool.Text => ShowKind.Text,
        EditorTool.Brush => ShowKind.Brush,
        EditorTool.Mask => ShowKind.Mask,
        EditorTool.Stamp => ShowKind.Assets,
        _ => null,
    };

    /// <summary>The show of a section tab.</summary>
    public static ShowKind ShowOf(EditorWorkspace workspace) => workspace switch
    {
        EditorWorkspace.Objects => ShowKind.SectionObjects,
        EditorWorkspace.World => ShowKind.SectionWorld,
        EditorWorkspace.Appearance => ShowKind.SectionAppearance,
        _ => ShowKind.SectionDraw,
    };

    /// <summary>Changes when anything a show is drawn with changes, so its frames are drawn again only then.</summary>
    public string ShowSignature => string.Join('|', CreateDemoSignature, NewLineStrokeColor, NewLineStrokeWidth,
        NewShapeFillColor, NewShapeStrokeColor, NewShapeStrokeWidth, BrushSettings.GetHashCode(),
        string.Join(',', ShowMountains().Select(m => m.Sha256)));

    /// <summary>Frame <paramref name="frame"/> of <see cref="ShowFrames"/>: 0 is the map before, the last the result.</summary>
    public MapDocument ShowFrame(ShowKind kind, int frame)
    {
        var t = Math.Clamp(frame / (double)(ShowFrames - 1), 0, 1);
        var document = Session.Document;
        var page = document.Layers.OfType<BaseColorLayer>().FirstOrDefault() is { } own
            ? own with { Id = LayerId.New() }
            : new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, document.Canvas.Background);
        var ground = WithGroundTexture(LandLayer.CreateEmpty(LayerId.New(), "Земля").EdgeStyle, NewLandTextureId);
        var land = Land(ground, Coast(1), ShowWidth, ShowHeight);
        var layers = new List<MapLayer> { page };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var lighting = document.Lighting;
        var style = document.Style;

        // Two or three of the owner's mountains from the catalog: the land of every show is a real map, not a plate.
        var mountains = Mountains(ShowMountains(), count: 3, shown: 1);

        switch (kind)
        {
            case ShowKind.Land:
            case ShowKind.SectionDraw when t < .4:
                // The island grows out of the sea as a land stroke grows under the brush.
                var growth = kind == ShowKind.Land ? t : t / .4;
                if (growth > 0) layers.Add(Land(ground, Coast(growth), ShowWidth, ShowHeight));
                mountains = [];
                break;
            case ShowKind.River:
            case ShowKind.SectionDraw:
            {
                layers.Add(land);
                var flow = kind == ShowKind.River ? t : (t - .4) / .6;
                // Node by node from the source to the mouth, as the clicks place them; frame 0 has no river yet.
                VectorPoint[] path = [new(150, 120), new(230, 150), new(300, 210), new(390, 236), new(470, 228), new(560, 300), new(620, 330)];
                var nodes = (int)Math.Round(1 + (path.Length - 1) * flow);
                if (nodes >= 2)
                    objects = objects with
                    {
                        Rivers = [new River { Name = "Река", Channels = [RiverEditor.NewChannel(path[..nodes], NewRiverWidth)], Style = RiverStyle.Natural() }],
                    };
                break;
            }
            case ShowKind.Road:
            {
                layers.Add(land);
                VectorPoint[] path = [new(110, 250), new(200, 214), new(290, 222), new(380, 170), new(470, 150), new(540, 110)];
                var nodes = (int)Math.Round(1 + (path.Length - 1) * t);
                var roadStyle = Services.RegionalMapFactory.IsRegional(document) ? Services.RegionalMapFactory.RoadStyle : RoadStyle.Painted();
                if (nodes >= 2) objects = objects with { Roads = [RoadEditor.NewRoad(path[..nodes], NewRoadWidth, roadStyle, "Дорога", seed: 7)] };
                break;
            }
            case ShowKind.Line:
            {
                layers.Add(land);
                VectorPoint[] path = [new(120, 300), new(190, 240), new(260, 250), new(330, 180), new(420, 170), new(500, 110)];
                var nodes = (int)Math.Round(1 + (path.Length - 1) * t);
                if (nodes >= 2) objects = objects with { Objects = [VectorGeometry.CreateLine(ObjectId.New(), "Линия", path[..nodes], NewLineStroke())] };
                break;
            }
            case ShowKind.Shape:
            {
                layers.Add(land);
                // Free drawing, the tool's own mode for a new shape: the outline traced round and closed.
                var ring = Enumerable.Range(0, 16).Select(i =>
                {
                    var a = i * Math.Tau / 16;
                    var k = 1 + .12 * Math.Sin(3 * a);
                    return new VectorPoint(330 + Math.Cos(a) * 120 * k, 190 + Math.Sin(a) * 70 * k);
                }).ToArray();
                var points = (int)Math.Round(ring.Length * t);
                if (points >= 3)
                    objects = objects with { Objects = [VectorGeometry.CreateFreeShape(ObjectId.New(), "Фигура", ring[..points], NewShapeFill(), NewShapeStroke())] };
                break;
            }
            case ShowKind.Text:
            {
                layers.Add(land);
                const string name = "Северные горы";
                var letters = (int)Math.Round(name.Length * t);
                if (letters > 0)
                {
                    var style0 = new TextStyle { Content = name[..letters], FontSize = 40 };
                    objects = objects with
                    {
                        Objects = [new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Подпись", 170, 40, 320, 60, 0,
                            "#FF20242B", "#FFFFFFFF", 0, true, false) { Text = style0 }],
                    };
                }
                break;
            }
            case ShowKind.Brush:
            {
                layers.Add(land);
                VectorPoint[] path = [new(140, 280), new(220, 230), new(300, 250), new(380, 200), new(470, 210), new(530, 150)];
                var paint = BrushLayer.CreateEmpty(LayerId.New(), "Кисть");
                var reach = path.Length * t;
                if (reach >= 1)
                {
                    var session = new RasterStrokeSession(paint.Tiles, BrushSettings with { Seed = 7 });
                    foreach (var point in Along(path, reach)) session.AddPoint(point.X, point.Y);
                    var (_, after) = session.Commit();
                    var tiles = paint.Tiles;
                    foreach (var (key, bytes) in after) if (bytes is not null) tiles = tiles.With(key, bytes);
                    paint = paint with { Tiles = tiles };
                }
                // The paint lies over the map, its mountains included, as a brush layer added on top does.
                layers.Add(objects with { Objects = mountains });
                layers.Add(paint);
                return Show(layers, style, lighting);
            }
            case ShowKind.Mask:
            {
                // The brush of the mask hides a band of the land: the sea shows through, the land is still there.
                VectorPoint[] path = [new(200, 120), new(300, 170), new(400, 200), new(480, 260)];
                var reach = path.Length * t;
                if (reach >= 1)
                {
                    var session = new RasterStrokeSession(land.Mask?.Tiles ?? TileMap.Empty(1), BrushSettings with { Seed = 7, Radius = 34, Texture = null, Eraser = false });
                    foreach (var point in Along(path, reach)) session.AddPoint(point.X, point.Y);
                    var (_, after) = session.CommitCoverage(255, subtract: MaskMode == MaskPaintMode.Hide);
                    var tiles = TileMap.Empty(1);
                    foreach (var (key, bytes) in after) if (bytes is not null) tiles = tiles.With(key, bytes);
                    land = (LandLayer)land.WithMask(new LayerMask(tiles, enabled: true));
                }
                layers.Add(land);
                mountains = [];
                break;
            }
            case ShowKind.Assets:
            case ShowKind.SectionObjects:
                layers.Add(land);
                // One click, one mountain: they appear one after another where the clicks fall. The owner's stock
                // pictures as they are in the catalog, without the map's light or style (owner, 2026-11-10).
                mountains = Mountains(ShowMountains(), count: 5, t);
                lighting = null;
                style = null;
                break;
            case ShowKind.SectionAppearance:
                layers.Add(land);
                // The same map under the day, the evening and the night light (the cards of «Вид»).
                lighting = (document.Lighting ?? new MapLighting()) with
                {
                    // Frame 0 without light, then the day turns into the evening and the night (0 day, .5 evening, 1 night).
                    Enabled = frame > 0,
                    TimeOfDay = Math.Clamp((t - 1.0 / (ShowFrames - 1)) / (1 - 1.0 / (ShowFrames - 1)), 0, 1),
                };
                break;
            case ShowKind.SectionWorld:
            case ShowKind.Select:
            default:
                layers.Add(land);
                // The middle mountain is picked up and moved to its new place.
                if (kind == ShowKind.Select) mountains = Mountains(ShowMountains(), count: 3, shown: 1, moveSecond: (90 * t, -30 * t));
                break;
        }
        objects = objects with { Objects = [.. mountains, .. objects.Objects] };
        layers.Add(objects);
        return Show(layers, style, lighting);

        MapDocument Show(List<MapLayer> list, MapStyle? s, MapLighting? l) =>
            new(DocumentId.New(), "Показ", DateTimeOffset.UnixEpoch, new CanvasDefinition(ShowWidth, ShowHeight, "px", document.Canvas.Background), list)
            { Style = s, Lighting = l };
    }

    /// <summary>The coast of the shows' island, <paramref name="growth"/> of its full size.</summary>
    private static Func<int, int, bool> Coast(double growth) => (x, y) =>
    {
        double dx = (x - 316) / (280 * growth + 1), dy = (y - 186) / (150 * growth + 1);
        var wobble = 1 + .1 * Math.Sin(x / 41.0) + .07 * Math.Cos(y / 29.0);
        return dx * dx + dy * dy < wobble * wobble && growth > .05;
    };

    /// <summary>The points of a stroke drawn <paramref name="reach"/> path nodes far, interpolated as the pointer moves.</summary>
    private static IEnumerable<VectorPoint> Along(VectorPoint[] path, double reach)
    {
        var whole = (int)Math.Floor(reach);
        for (var i = 0; i < Math.Min(whole, path.Length); i++) yield return path[i];
        if (whole < path.Length && whole >= 1)
        {
            var f = reach - whole;
            var a = path[whole - 1]; var b = path[whole];
            yield return new VectorPoint(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }
    }

    /// <summary>
    /// The owner's mountains for the shows: the catalog's cards with the role «вершина» or «склон», else the pictures
    /// named as mountains, else the mountains already on this map.
    /// </summary>
    private IReadOnlyList<AssetRecord> ShowMountains()
    {
        if (_assetCatalog is not { } catalog) return [];
        var byRole = catalog.Sets
            .Where(s => !s.Archived && s.Placement?.Role is AssetRole.Peak or AssetRole.Slope)
            .SelectMany(s => s.VariantIds)
            .Select(id => catalog.FindVariant(id) is { } v ? catalog.Find(v.Sha256) : null)
            .OfType<AssetRecord>();
        var byName = catalog.Records.Where(r => r.DisplayName.Contains("гор", StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains("пик", StringComparison.OrdinalIgnoreCase));
        var onMap = Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Select(o => o.Stamp?.AssetSha256).OfType<string>().Distinct()
            .Select(catalog.Find).OfType<AssetRecord>();
        var pictures = catalog.Records.Where(r => r.HasAlpha);
        return [.. byRole.Concat(byName).Concat(onMap).Concat(pictures).Where(r => r.Width > 0 && r.Height > 0).DistinctBy(r => r.Sha256).Take(5)];
    }

    /// <summary>Up to <paramref name="count"/> mountains standing on the island, the first <paramref name="shown"/> share of them.</summary>
    private static SceneObject[] Mountains(IReadOnlyList<AssetRecord> records, int count, double shown, (double X, double Y) moveSecond = default)
    {
        if (records.Count == 0) return [];
        (double X, double Foot, double Width)[] spots = [(220, 200, 150), (330, 170, 170), (440, 215, 140), (270, 280, 120), (400, 290, 130)];
        var visible = (int)Math.Round(Math.Min(count, spots.Length) * Math.Clamp(shown, 0, 1));
        return [.. spots.Take(Math.Min(count, spots.Length)).Take(visible).Select((spot, i) =>
        {
            var record = records[i % records.Count];
            var height = spot.Width * record.Height / record.Width;
            var (dx, dy) = i == 1 ? moveSecond : default;
            return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName, spot.X - spot.Width / 2 + dx, spot.Foot - height + dy,
                spot.Width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(record.Sha256) { ReferenceWidth = spot.Width, ReferenceHeight = height } };
        })];
    }
}
