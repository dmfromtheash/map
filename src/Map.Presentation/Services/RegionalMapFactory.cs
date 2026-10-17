using System.Collections.Immutable;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.Services;

/// <summary>Original editable starter documents built from the same objects and commands as the editor.</summary>
public static class RegionalMapFactory
{
    public const string WaterColor = "#FF87A9AE";
    /// <summary>Deep blue-green water, lighter towards its banks.</summary>
    public const string RiverColor = "#FF3A7079";
    public const string LandColor = "#FFD6D9AF";
    /// <summary>Bright yellow-green grass, painted in soft washes.</summary>
    public const string GrassColor = "#FF8FB04E";

    // Watercolour grass with a quiet dark shore line; the sea side keeps a few calm surf lines.
    public static LandEdgeStyle LandStyle => new()
    {
        FillColor = GrassColor, OutlineColor = "#FF3B4A2E", OutlineWidth = 1.3,
        InnerShadowOpacity = .15, OuterShadowOpacity = .1, ShadowBlur = 8,
        RippleColor = "#70E6F2EE", RippleCount = 3, RippleSpacing = 8, RippleWidth = 1.1,
        FillTextureId = "watercolour-grass", FillTextureOpacity = .95, FillTextureScale = 420,
    };

    // A trodden earth path: warm body, long darker ruts, a firm dark edge and grey stones.
    public static RoadStyle RoadStyle => Map.Core.Documents.RoadStyle.Country() with
    { SurfaceColor = "#FFAD9573", ToneColor = "#FF97805F", ToneVariation = .5, EdgeColor = "#FF3E3228",
        EdgeWidth = 1.5, EdgeOpacity = .92, EdgeBreak = .04, RutColor = "#FF7C6448", RutOpacity = .6,
        DetailOpacity = .95, EdgeRoughness = .18, ShoulderColor = "#FF7E8A4A", ShoulderOpacity = .22 };

    // Painted river: deep water in the middle, a light foam rim at the banks, a muddy brown
    // bank strip with streaks and a dark ink edge on both sides.
    public static RiverStyle WaterStyle => RiverStyle.Illustrated() with
    { WaterColor = RiverColor, BankColor = "#FF514E3D", BankWidth = 1.1,
        BankReliefWidth = 10, BankReliefColor = "#FF9C8973", BankHatchOpacity = .26,
        BankShadowOpacity = .12, BankShadowBlur = 2, EdgeRoughness = .68,
        HighlightColor = "#FFD8ECE3", HighlightOpacity = .38, ShallowWidth = 9, ShallowColor = "#FFB4DACE",
        MouthFade = 36, TextureId = "watercolour-water", TextureOpacity = .85, TextureScale = 360 };

    /// <summary>The open sea: the page colour with the same painted water as the rivers.</summary>
    public static BaseColorLayer Sea(BaseColorLayer layer) =>
        layer with { Color = "#FF3E767E", TextureId = "watercolour-water", TextureOpacity = .85, TextureScale = 360 };

    /// <summary>Pale meadow matching the foot of the mountain drawings, and its tile size in world units.</summary>
    public const string FoothillTextureId = "watercolour-foothill";
    public const double FoothillTextureScale = 420;

    public const string ThemeNote = "Тихий атлас · оформление региона";
    public static bool IsRegional(MapDocument document) => document.Notes.Any(n => n.Title == ThemeNote);

    public static MapDocument Create(RegionalArtImport pack, AssetCatalog catalog, string title,
        int width = 4096, int height = 3072, bool illustrated = true)
    {
        var sx = width / 4096d; var sy = height / 3072d; var unit = Math.Min(sx, sy);
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля · долина Листвы") with { EdgeStyle = LandStyle };
        var terrain = new ObjectLayer(LayerId.New(), "Реки и дороги", true, false, 1, []);
        var forests = new ObjectLayer(LayerId.New(), "Леса · редактируемые области", true, false, 1, []);
        var mountains = new ObjectLayer(LayerId.New(), "Хребты · редактируемые пути", true, false, 1, []);
        var towns = new ObjectLayer(LayerId.New(), "Поселения и ориентиры", true, false, 1, []);
        var labels = new ObjectLayer(LayerId.New(), "Названия", true, false, 1, []);
        var doc = new MapDocument(DocumentId.New(), title, DateTimeOffset.UtcNow,
            new CanvasDefinition(width, height, "px", WaterColor),
            [Sea(new BaseColorLayer(LayerId.New(), "Тихое море", true, false, 1, WaterColor)), land, terrain, forests, mountains, towns, labels],
            pack.Records.Values.Select(r => Reference(r, catalog)).ToArray(),
            [new MapNote(NoteId.New(), ThemeNote,
                "Лес — область с устойчивыми вариантами; хребет — путь с узлами; поселение — отдельные дома вокруг дворов. " +
                "Выберите нужный режим в панели ассетов. Позднее выберите группу и передвиньте её узлы. " +
                "Названия, дороги, реки и все ассеты редактируются. Сохранение создаёт самостоятельную карту со встроенными изображениями.", NoteAnchorKind.World)]);
        if (!illustrated) return doc;

        LandPoint P(double x, double y) => new(x * sx, y * sy);
        var coast = new[] { P(-100,-100),P(4200,-100),P(4200,3160),P(3510,3120),P(3410,2980),P(3290,2860),
            P(3000,2810),P(2870,2670),P(2700,2720),P(2490,2610),P(2310,2470),P(2110,2500),P(1910,2380),
            P(1670,2450),P(1490,2350),P(1350,2160),P(1450,2020),P(1340,1910),P(1140,1950),P(1010,1800),
            P(870,1830),P(720,1690),P(560,1760),P(410,1630),P(260,1660),P(150,1510),P(-100,1490) };
        var geometry = LandRasterizer.ApplyShape(land.GeometryTiles,
            new LandShape { Kind = LandShapeKind.Polygon, Bounds = new(0,0,width,height), Points = SmoothCoast(coast) }, false);
        land = land with { GeometryTiles = land.GeometryTiles.WithMany(geometry.After) };
        doc = doc.WithLayers(doc.Layers.Select(l => l.Id == land.Id ? land : l).ToArray());

        RiverNode RN(double x,double y,double w) => new(Guid.NewGuid(),x*sx,y*sy,w*unit);
        var main = new RiverChannel { Name = "Листва", Nodes = [RN(2240,670,8),RN(2260,1000,15),RN(2150,1230,22),
            RN(2060,1440,26),RN(1870,1650,32),RN(1780,1880,36),RN(1600,2110,45),RN(1580,2490,58)] };
        var tributary = new RiverChannel { Name = "Малая Листва", Nodes = [RN(2880,1380,5),RN(2670,1500,13),RN(2410,1430,19),RN(2150,1440,22)],
            Mouth = new(main.Id,main.Nodes[3].Id,0) };
        var lake = new RiverChannel { Name = "Зеркальное озеро", Closed = true,
            Nodes = [RN(2590,1240,1),RN(2750,1140,1),RN(2940,1210,1),RN(3020,1390,1),RN(2860,1480,1),RN(2670,1420,1)] };
        var river = new River { Name = "Листва · озеро и приток", Channels = [main, lake, tributary], Style = WaterStyle };
        tributary = tributary with { Source = new(lake.Id,lake.Nodes[4].Id,0) };
        river = river with { Channels = [main,lake,tributary] };
        var roadNodes = new[] { (1150d,1480d),(1510d,1630d),(1820d,1760d),(2160d,1880d),(2630d,2130d),(3060d,2000d),
            (2290d,1630d),(2540d,1580d),(3100d,1620d),(3370d,1160d) }.Select(p => new RoadNode(Guid.NewGuid(),p.Item1*sx,p.Item2*sy)).ToImmutableArray();
        RoadBranch Branch(string name, params int[] indices) => new() { Name = name,
            Vertices = indices.Select(i => new RoadVertex(roadNodes[i].Id, 14*unit)).ToImmutableArray() };
        var road = new Road { Name = "Старый торговый тракт", Seed = 20260920, Nodes = roadNodes,
            Branches = [Branch("К морю",0,1,2,3,4,5),Branch("Озёрная ветвь",3,6,7,8,9)], Style = RoadStyle };
        terrain = terrain with { Rivers = [river], Roads = [road] };
        doc = doc.WithLayers(doc.Layers.Select(l => l.Id == terrain.Id ? terrain : l).ToArray());

        PlacementPathNode N(double x,double y) => new(Guid.NewGuid(),x*sx,y*sy);
        void Path(string name, double size, string[] ids, params (double X,double Y)[] points)
        {
            var set = pack.Sets["mountains"];
            // A ridge is built from peak-like drawings only; the arch stays a single catalogue mountain.
            var shas = ids.Select(id => pack.Records[id].Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var variants = Sources(set, catalog).Where(v => shas.Contains(v.Sha256)).ToImmutableArray();
            var path = new PlacementPath { Name = name, AssetSetId = set.Id, VariantId = set.CoverVariantId,
                Nodes = points.Select(p=>N(p.X,p.Y)).ToImmutableArray(), Variants = variants, Seed = 42019,
                Spacing = size * .42 * unit, Request = new() { BaseWidth = size * unit, RandomizeVariant = true },
                ScalePercent = size*unit/256*100, Style = new(variants[0].Sha256) { ShadowOpacity = 0 } };
            doc = new PlacementPathCommand(mountains.Id,path.Id,path,name).Apply(doc);
        }
        (double X,double Y)[] ridge=[(470,610),(920,470),(1350,610),(1670,870),(2040,650),(2540,520),(3000,680),(3540,600)];
        (double X,double Y)[] gate=[(3180,980),(3490,1110),(3700,1390)];
        // The painted mountains grow their own grass at the foot, so no pale foothill band is laid under them.
        Path("Седой кряж",310,["waterfall-peak","stone-spires","forested-mesa"],ridge);
        Path("Каменные врата",210,["waterfall-peak","lava-volcano","stone-spires"],gate);
        void Area(string name,string group,LayerId layer,double size,double density,PlacementAreaPattern pattern,
            params (double X,double Y)[] points)
        {
            var set = pack.Sets[group]; var variants = Sources(set,catalog);
            var area = new PlacementArea { Name=name, AssetSetId=set.Id, VariantId=set.CoverVariantId,
                Nodes=points.Select(p=>N(p.X,p.Y)).ToImmutableArray(), Variants=variants,
                LayoutVersion=4, Pattern=pattern, JoinEdges=group=="trees", Density=density,
                SizeVariationPercent=group=="trees"?28:12, Seed=93027+name.Length,
                Request=new(){BaseWidth=size*unit,RandomizeVariant=true}, ScalePercent=size*unit/256*100,
                Style=new(variants[0].Sha256){ShadowOpacity=0} };
            using var images = new ExportStampImageSource(doc,null,catalog);
            var measures = variants.ToDictionary(v=>v.Sha256,v=>StampContentMeasurer.Read(images.TryGetImage(v.Sha256)!));
            area=area with { ContentBounds=measures.ToImmutableDictionary(k=>k.Key,k=>k.Value.Bounds),
                ContentMasks=measures.Where(k=>k.Value.Mask is not null).ToImmutableDictionary(k=>k.Key,k=>k.Value.Mask!) };
            area=PlacementAreaPlanner.InitializeComposition(area);
            doc=new PlacementAreaCommand(layer,area.Id,area,name).Apply(doc);
        }
        Area("Серебряный лес","trees",forests.Id,46,74,PlacementAreaPattern.Masses,
            (430,740),(1040,790),(1580,1010),(1810,1260),(1650,1580),(1260,1370),(790,1420),(460,1220));
        Area("Озёрные рощи","trees",forests.Id,47,68,PlacementAreaPattern.Groups,
            (3030,1020),(3410,1250),(3790,1600),(3510,2100),(3090,2200),(2890,1870),(3060,1490));
        Area("Нижний бор","trees",forests.Id,48,64,PlacementAreaPattern.Masses,
            (2000,1980),(2230,2060),(2500,2280),(2540,2490),(2200,2370),(1950,2270));
        Area("Лиственный двор","buildings",towns.Id,60,90,PlacementAreaPattern.Clearings,
            (1000,1400),(1240,1380),(1430,1520),(1320,1740),(1050,1680),(970,1550));
        Area("Озёрный посад","buildings",towns.Id,62,90,PlacementAreaPattern.Clearings,
            (2420,1560),(2720,1550),(2840,1750),(2720,1910),(2450,1840),(2370,1660));
        var extras = new List<SceneObject>();
        foreach(var item in pack.Manifest.Assets.Where(a=>a.Group=="hills").Select((a,i)=>(a,i)))
            extras.Add(Stamp(pack.Records[item.a.Id],item.a.Name,(2960+item.i*230)*sx,(2550+item.i%2*170)*sy,210*unit));
        var specials = pack.Manifest.Assets.Where(a=>a.Group is "settlements" or "landmarks" or "groves").ToArray();
        var positions = new Dictionary<string,(double,double)> { ["red-grove"]=(700,1480),["fly-agarics"]=(1460,1880),["twin-palms"]=(1720,2330),
            ["flytrap-plant"]=(1960,1100),["berry-bush"]=(2400,1150),
            ["market-town"]=(3120,2020),["hill-keep"]=(3370,1160),["stone-bridge"]=(1810,1810),
            ["standing-stones"]=(2180,890),["ruined-gate"]=(840,1580),["coastal-beacon"]=(2810,2690) };
        for(var i=0;i<specials.Length;i++) { var a=specials[i];var p=positions[a.Id];
            extras.Add(Stamp(pack.Records[a.Id],a.Name,p.Item1*sx,p.Item2*sy,a.Width*unit)); }
        var currentTowns=doc.Layers.OfType<ObjectLayer>().Single(l=>l.Id==towns.Id);
        towns=currentTowns with { Objects=[..currentTowns.Objects, ..extras] };
        var text = new[] { Caption("ДОЛИНА ЛИСТВЫ",140*sx,135*sy,2000*sx,90*unit),
            Caption("Седой кряж",1350*sx,290*sy,800*sx,56*unit),
            Caption("Серебряный лес",480*sx,1630*sy,900*sx,52*unit),
            Caption("Лиственный двор",870*sx,1810*sy,950*sx,52*unit),
            Caption("Зеркальное озеро",2500*sx,1020*sy,1000*sx,50*unit),
            Caption("Озёрный посад",2410*sx,1940*sy,900*sx,52*unit),
            Caption("Каменные врата",2920*sx,800*sy,900*sx,50*unit),
            Caption("Нижний бор",1970*sx,2470*sy,900*sx,50*unit),
            Caption("Т И Х О Е   М О Р Е",300*sx,2370*sy,1650*sx,62*unit),
            Caption("Север",3650*sx,180*sy,300*sx,35*unit) };
        labels=labels with { Objects=text };
        return doc.WithLayers(doc.Layers.Select(l=>l.Id==towns.Id?towns:l.Id==labels.Id?labels:l).ToArray());
    }

    private static ImmutableArray<StampVariantSource> Sources(AssetSet set,AssetCatalog catalog) =>
        set.VariantIds.Select(catalog.RecordForVariant).OfType<AssetRecord>()
            .Select(r=>new StampVariantSource(r.Sha256,r.Width,r.Height)).ToImmutableArray();

    private static IReadOnlyList<LandPoint> SmoothCoast(IReadOnlyList<LandPoint> points)
    {
        var result=new List<LandPoint>();
        for(var i=0;i<points.Count;i++)
        {
            var a=points[(i+points.Count-1)%points.Count];var b=points[i];var c=points[(i+1)%points.Count];var d=points[(i+2)%points.Count];
            for(var j=0;j<10;j++) { var t=j/10d;var t2=t*t;var t3=t2*t;
                double Curve(double p,double q,double r,double s)=>.5*(2*q+(-p+r)*t+(2*p-5*q+4*r-s)*t2+(-p+3*q-3*r+s)*t3);
                result.Add(new(Curve(a.X,b.X,c.X,d.X),Curve(a.Y,b.Y,c.Y,d.Y))); }
        }
        return result;
    }

    public static MapDocument ArtStudy(RegionalArtImport pack, AssetCatalog catalog)
    {
        var objects = new List<SceneObject>();
        var mountain = pack.Records["waterfall-peak"];
        var tree = pack.Records["green-oak"];
        var house = pack.Records["gable-cottage"];
        objects.Add(Stamp(mountain, "Горная вершина", 340, 360, 340));
        objects.Add(Stamp(mountain, "Малый отрог", 195, 350, 190));
        for (var i = 0; i < 15; i++)
            objects.Add(Stamp(tree, "Пихта", 500 + i % 5 * 48 + (i / 5 % 2) * 19,
                280 + i / 5 * 56, 47 + i % 3 * 5));
        objects.Add(Stamp(house, "Дом", 820, 380, 125));
        objects.Add(Caption("ТИХИЙ АТЛАС", 58, 40, 850, 32));
        objects.Add(Caption("Серые вершины", 140, 460, 340, 22));
        objects.Add(Caption("Серебряная роща", 490, 460, 320, 22));
        objects.Add(Caption("Лесной двор", 810, 460, 240, 22));
        return new MapDocument(DocumentId.New(), "Тихий атлас · проверка рисунка", DateTimeOffset.UtcNow,
            new CanvasDefinition(1120, 640, "px", LandColor),
            [new BaseColorLayer(LayerId.New(), "Земля", true, false, 1, LandColor),
             new ObjectLayer(LayerId.New(), "Образцы", true, false, 1, objects)],
            pack.Records.Values.Select(record => Reference(record, catalog)).ToArray());
    }

    public static MapAssetReference Reference(AssetRecord record, AssetCatalog catalog)
    {
        var resolved = catalog.ReadVerified(record);
        if (resolved.Content is null) throw new InvalidDataException("Изображение недоступно: " + record.DisplayName);
        return new(record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, record.ImportedUtc, record.Provenance.Source,
            record.Provenance.LicenseId, resolved.Content);
    }

    public static SceneObject Stamp(AssetRecord record, string name, double x, double y, double width)
    {
        var height = width * record.Height / record.Width;
        return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, name, x - width / 2, y - height,
            width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(record.Sha256) { ReferenceWidth = width, ReferenceHeight = height, ShadowOpacity = 0 } };
    }

    public static SceneObject Caption(string text, double x, double y, double width, double size) =>
        new(ObjectId.New(), SceneObjectKind.Text, text, x, y, width, size * 1.8, 0,
            "#FF394B46", "#00000000", 0, true, false)
        { Text = new TextStyle { Content = text, FontSize = size, FillColor = "#FF394B46", RequestedFontFamily = "Georgia" } };
}
