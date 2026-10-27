using Map.Core.Documents;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;

namespace Map.Core.Editing;

/// <summary>What the city generator is asked for (R35).</summary>
public sealed record CitySettings
{
    public int Seed { get; init; } = 1;
    public string Name { get; init; } = "Город";
    public double Radius { get; init; } = 900;
    public int Radials { get; init; } = 6;
    public int Rings { get; init; } = 3;
    public bool Walls { get; init; } = true;
    public bool Port { get; init; }
    public int Temples { get; init; } = 2;
    public bool Guild { get; init; } = true;

    /// <summary>Directions (radians) the main streets should take - the roads coming in from the region.</summary>
    public IReadOnlyList<double> EntryDirections { get; init; } = [];

    /// <summary>Where there is land (R35): blocks, houses, walls and streets keep off the water, the port goes to the shore. Null - land everywhere.</summary>
    public Func<double, double, bool>? IsLand { get; init; }

    public bool IsValid => Radius is >= 150 and <= 20000 && Radials is >= 3 and <= 12 && Rings is >= 1 and <= 6 && Temples is >= 0 and <= 6;
}

/// <summary>A building of the city key (R34): its number, what it is and where.</summary>
public sealed record CityBuilding(int Number, string Kind, double X, double Y);

/// <summary>What the city generator made (R34/R35).</summary>
public sealed record CityPlan(
    IReadOnlyList<Road> Streets, IReadOnlyList<IReadOnlyList<VectorPoint>> Blocks, IReadOnlyList<SceneObject> Objects,
    IReadOnlyList<CityBuilding> Key, int Houses);

/// <summary>
/// R34/R35: a town from a point - main streets out from the square (along the roads coming in, if
/// any), ring streets around, the blocks between them (the faces of the street graph), lots cut
/// along each block's street front, a house on each lot turned to face its street, a wall with
/// towers and gates where the main streets leave, temples and a guild on the largest central
/// blocks, district names. Houses are plain roofs until the owner's art: placeholders by design.
/// The same settings give the same city.
/// </summary>
public static class CityBuilder
{
    private static readonly GeometryFactory Factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
    private static readonly string[] Roofs = ["#FFA4553A", "#FF8E4A35", "#FF6E6A66", "#FF7C5B42", "#FF9A6A48", "#FF5E5B57"];
    private static readonly string[] Districts = ["Старый город", "Рыночный квартал", "Ремесленный", "Верхний город", "Слобода", "Кожевники", "Храмовый", "Садовый"];

    public static CityPlan Generate(double cx, double cy, CitySettings settings)
    {
        if (!settings.IsValid) throw new ArgumentException("Неверные параметры города.");
        var random = new Random(settings.Seed);
        double Jitter(double amount) => (random.NextDouble() - .5) * 2 * amount;
        var radius = settings.Radius;
        var square = radius * .11;
        bool Land(double x, double y) => settings.IsLand?.Invoke(x, y) ?? true;
        // Only the parts of a street on land stay; a street cut by water becomes its pieces.
        IEnumerable<List<VectorPoint>> OnLand(IReadOnlyList<VectorPoint> line)
        {
            var run = new List<VectorPoint>();
            foreach (var point in line)
            {
                if (Land(point.X, point.Y)) { run.Add(point); continue; }
                if (run.Count >= 2) yield return run;
                run = [];
            }
            if (run.Count >= 2) yield return run;
        }

        // Main streets: along the incoming roads first, the rest spread evenly with a little wander.
        var angles = settings.EntryDirections.Take(settings.Radials).ToList();
        for (var k = angles.Count; k < settings.Radials; k++)
        {
            var best = 0.0; var bestGap = -1.0;
            for (var a = 0; a < 360; a += 5)
            {
                var angle = a * Math.PI / 180;
                var gap = angles.Count == 0 ? Math.PI : angles.Min(b => Math.Abs(Math.Atan2(Math.Sin(angle - b), Math.Cos(angle - b))));
                if (gap > bestGap) { bestGap = gap; best = angle + Jitter(.08); }
            }
            angles.Add(best);
        }
        angles.Sort();

        var streets = new List<Road>();
        var lines = new List<VectorPoint[]>();
        foreach (var (angle, index) in angles.Select((a, i) => (a, i)))
        {
            var points = new List<VectorPoint>();
            for (var t = 0; t <= 8; t++)
            {
                var r = square + ((radius * 1.18 - square) * t / 8.0);
                var bend = Math.Sin(t * .7 + index) * .05;
                points.Add(new VectorPoint(cx + (Math.Cos(angle + bend) * r), cy + (Math.Sin(angle + bend) * r)));
            }
            lines.Add([.. points]);
            foreach (var piece in OnLand(points).Take(1))
                streets.Add(RoadEditor.NewRoad(piece, 14, RoadStyle.Country(), $"Улица {StreetName(index)}", settings.Seed + index));
        }
        for (var ring = 1; ring <= settings.Rings; ring++)
        {
            var r = square + ((radius - square) * ring / (settings.Rings + .25));
            var points = new List<VectorPoint>();
            for (var s = 0; s <= 36; s++)
            {
                var a = s * Math.PI * 2 / 36;
                var rr = r * (1 + (Math.Sin((a * 3) + ring) * .04));
                points.Add(new VectorPoint(cx + (Math.Cos(a) * rr), cy + (Math.Sin(a) * rr)));
            }
            points[^1] = points[0];
            lines.Add([.. points]);
            var pieceIndex = 0;
            foreach (var piece in OnLand(points))
                streets.Add(RoadEditor.NewRoad(piece, ring == settings.Rings ? 11 : 9, RoadStyle.Country(), ring == settings.Rings ? "Кольцевая" : $"{ring}-я Кольцевая", settings.Seed + 100 + ring + (pieceIndex++ * 1000)));
        }
        // The square's edge and the city's edge close the faces.
        var squareRing = Circle(cx, cy, square, 24);
        var edge = Circle(cx, cy, radius, 64);
        lines.Add(squareRing);
        lines.Add(edge);

        // Blocks: the faces of the noded street graph, inside the edge and outside the square.
        var noded = Factory.CreateMultiLineString([.. lines.Select(l => Factory.CreateLineString([.. l.Select(p => new Coordinate(p.X, p.Y))]))]).Union();
        var polygonizer = new Polygonizer();
        polygonizer.Add(noded);
        var squarePolygon = Factory.CreatePolygon([.. squareRing.Select(p => new Coordinate(p.X, p.Y))]);
        var blocks = polygonizer.GetPolygons().OfType<Polygon>()
            .Where(p => p.Area > radius * radius * .002 && !squarePolygon.Contains(p.InteriorPoint) && Math.Sqrt(Math.Pow(p.Centroid.X - cx, 2) + Math.Pow(p.Centroid.Y - cy, 2)) < radius
                && Land(p.InteriorPoint.X, p.InteriorPoint.Y))
            .OrderBy(p => Math.Pow(p.Centroid.X - cx, 2) + Math.Pow(p.Centroid.Y - cy, 2)).ToList();

        var objects = new List<SceneObject>
        {
            VectorGeometry.CreateFreeShape(ObjectId.New(), "Площадь", squareRing[..^1], new ShapeFillStyle { Color = "#FFD9CBA8" }, new VectorStrokeStyle { Color = "#FF8C7A5C", Width = 2 }),
        };
        var key = new List<CityBuilding>();
        var houses = 0;
        var placed = new List<Envelope>();
        // Landmarks first, on the biggest blocks near the middle.
        var landmarks = new Queue<string>(Enumerable.Repeat("Храм", settings.Temples).Concat(settings.Guild ? ["Гильдия"] : []).Concat(["Ратуша"]));
        foreach (var block in blocks.Take(Math.Max(landmarks.Count * 2, 1)).OrderByDescending(b => b.Area))
        {
            if (landmarks.Count == 0) break;
            var kind = landmarks.Dequeue();
            var inner = block.Buffer(-radius * .02);
            if (inner.IsEmpty) continue;
            var c = inner.Centroid;
            var size = Math.Min(radius * .07, Math.Sqrt(inner.Area) * .45);
            var rect = Rectangle(c.X, c.Y, size * 1.3, size, Math.Atan2(c.Y - cy, c.X - cx));
            objects.Add(VectorGeometry.CreateFreeShape(ObjectId.New(), kind, rect,
                new ShapeFillStyle { Color = kind == "Храм" ? "#FFE8DFC8" : kind == "Гильдия" ? "#FFB07A3E" : "#FFC9A96B" }, new VectorStrokeStyle { Color = "#FF3A2E24", Width = 2.5 }));
            placed.Add(Envelope(rect));
            key.Add(new CityBuilding(key.Count + 1, kind, c.X, c.Y));
        }

        // Lots along each block's street front, a house on each, turned to its street; inner rows
        // behind them fill the block towards its yard, the middle stays open.
        foreach (var block in blocks)
        for (var row = 0; row < 3; row++)
        {
            var inner = block.Buffer(-8 - (row * 24)) as Polygon;
            if (inner is null || inner.IsEmpty || inner.Area < 900) break;
            var ring = inner.ExteriorRing.Coordinates;
            for (var i = 0; i + 1 < ring.Length; i++)
            {
                var (a, b) = (ring[i], ring[i + 1]);
                var length = a.Distance(b);
                var (ux, uy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
                // Inward normal: the side the block's centroid is on.
                var (nx, ny) = (-uy, ux);
                var mid = new Coordinate((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (((inner.Centroid.X - mid.X) * nx) + ((inner.Centroid.Y - mid.Y) * ny) < 0) (nx, ny) = (-nx, -ny);
                var along = 2.0;
                while (along < length - 10)
                {
                    var frontage = 12 + (random.NextDouble() * 10);
                    if (along + frontage > length - 2) break;
                    var depth = 10 + (random.NextDouble() * 9);
                    var (hx, hy) = (a.X + (ux * (along + (frontage / 2))) + (nx * (depth / 2 + 2)), a.Y + (uy * (along + (frontage / 2))) + (ny * (depth / 2 + 2)));
                    var rect = Rectangle(hx, hy, frontage - 2, depth, Math.Atan2(uy, ux));
                    var env = Envelope(rect);
                    if (inner.Contains(Factory.CreatePoint(new Coordinate(hx, hy))) && rect.All(c => Land(c.X, c.Y)) && !placed.Any(p => p.Intersects(env)))
                    {
                        objects.Add(VectorGeometry.CreateFreeShape(ObjectId.New(), "Дом", rect,
                            new ShapeFillStyle { Color = Roofs[random.Next(Roofs.Length)] }, new VectorStrokeStyle { Color = "#FF2E241C", Width = 1.2 }));
                        placed.Add(env);
                        houses++;
                    }
                    along += frontage;
                }
            }
        }

        // The wall: a ring at the city's edge, towers at its turns, gates where the main streets leave.
        if (settings.Walls)
        {
            var wall = Circle(cx, cy, radius * 1.02, 48);
            var gates = angles.Select(a => new VectorPoint(cx + (Math.Cos(a) * radius * 1.02), cy + (Math.Sin(a) * radius * 1.02))).ToList();
            var run = new List<VectorPoint>();
            void Flush() { if (run.Count >= 2) objects.Add(VectorGeometry.CreateLine(ObjectId.New(), "Стена", [.. run], new VectorStrokeStyle { Color = "#FF4A4038", Width = 9 })); run.Clear(); }
            foreach (var point in wall)
            {
                if (!Land(point.X, point.Y) || gates.Any(g => Math.Sqrt(Math.Pow(g.X - point.X, 2) + Math.Pow(g.Y - point.Y, 2)) < radius * .07)) { Flush(); continue; }
                run.Add(point);
            }
            Flush();
            for (var i = 0; i < wall.Length - 1; i += 4)
            {
                var p = wall[i];
                if (!Land(p.X, p.Y) || gates.Any(g => Math.Sqrt(Math.Pow(g.X - p.X, 2) + Math.Pow(g.Y - p.Y, 2)) < radius * .07)) continue;
                objects.Add(VectorGeometry.CreateShape(ObjectId.New(), "Башня", ShapePrimitiveKind.Ellipse, new WorldBounds(p.X - 11, p.Y - 11, p.X + 11, p.Y + 11),
                    new ShapeFillStyle { Color = "#FF6B6058" }, new VectorStrokeStyle { Color = "#FF2E2620", Width = 2 }));
            }
            foreach (var gate in gates.Where(g => Land(g.X, g.Y)))
                key.Add(new CityBuilding(key.Count + 1, "Ворота", gate.X, gate.Y));
        }

        // The port on a real shore: piers out into the water where the coast is nearest the middle.
        if (settings.Port && settings.IsLand is not null && Shore() is { } shore)
        {
            for (var p = -2; p <= 2; p++)
            {
                var a = shore.Angle + (p * 25 / shore.Distance);
                var coast = Coast(a) ?? shore.Distance;
                var (sx, sy) = (cx + (Math.Cos(a) * coast), cy + (Math.Sin(a) * coast));
                objects.Add(VectorGeometry.CreateFreeShape(ObjectId.New(), "Пирс", Rectangle(sx + (Math.Cos(a) * radius * .05), sy + (Math.Sin(a) * radius * .05), radius * .12, 10, a),
                    new ShapeFillStyle { Color = "#FF8B6A48" }, new VectorStrokeStyle { Color = "#FF3A2E24", Width = 1.5 }));
            }
            key.Add(new CityBuilding(key.Count + 1, "Порт", cx + (Math.Cos(shore.Angle) * shore.Distance), cy + (Math.Sin(shore.Angle) * shore.Distance)));
        }
        double? Coast(double angle)
        {
            for (var r = square; r <= radius * 1.8; r += radius * .01)
                if (!Land(cx + (Math.Cos(angle) * r), cy + (Math.Sin(angle) * r))) return r;
            return null;
        }
        (double Angle, double Distance)? Shore()
        {
            (double, double)? best = null;
            for (var d = 0; d < 360; d += 5)
            {
                var a = d * Math.PI / 180;
                if (Coast(a) is { } r && (best is null || r < best.Value.Item2)) best = (a, r);
            }
            return best;
        }

        // The port without a coast: water drawn beyond the side the last main street goes to, with piers.
        if (settings.Port && settings.IsLand is null)
        {
            var side = angles[^1];
            var (wx, wy) = (Math.Cos(side), Math.Sin(side));
            var water = new List<VectorPoint>();
            for (var s = -12; s <= 12; s++)
            {
                var a = side + (s * Math.PI / 30);
                water.Add(new VectorPoint(cx + (Math.Cos(a) * radius * 1.1), cy + (Math.Sin(a) * radius * 1.1)));
            }
            water.Add(new VectorPoint(cx + (wx * radius * 2.2) - (wy * radius * 1.2), cy + (wy * radius * 2.2) + (wx * radius * 1.2)));
            water.Add(new VectorPoint(cx + (wx * radius * 2.2) + (wy * radius * 1.2), cy + (wy * radius * 2.2) - (wx * radius * 1.2)));
            objects.Insert(0, VectorGeometry.CreateFreeShape(ObjectId.New(), "Гавань", water, new ShapeFillStyle { Color = "#FF7FA3B0" }, new VectorStrokeStyle { Color = "#FF4E6E7A", Width = 2 }));
            for (var p = -2; p <= 2; p++)
            {
                var a = side + (p * .09);
                var (sx, sy) = (cx + (Math.Cos(a) * radius * 1.1), cy + (Math.Sin(a) * radius * 1.1));
                objects.Add(VectorGeometry.CreateFreeShape(ObjectId.New(), "Пирс", Rectangle(sx + (Math.Cos(a) * radius * .07), sy + (Math.Sin(a) * radius * .07), radius * .14, 10, a),
                    new ShapeFillStyle { Color = "#FF8B6A48" }, new VectorStrokeStyle { Color = "#FF3A2E24", Width = 1.5 }));
            }
            key.Add(new CityBuilding(key.Count + 1, "Порт", cx + (wx * radius * 1.15), cy + (wy * radius * 1.15)));
        }

        // District names between the main streets in the outer ring.
        for (var i = 0; i < angles.Count; i++)
        {
            var a0 = angles[i];
            var a1 = angles[(i + 1) % angles.Count] + (i + 1 == angles.Count ? Math.PI * 2 : 0);
            var mid = (a0 + a1) / 2;
            var r = radius * .72;
            var name = i == 0 ? Districts[0] : Districts[(i + settings.Seed) % (Districts.Length - 1) + 1];
            if (!Land(cx + (Math.Cos(mid) * r), cy + (Math.Sin(mid) * r))) continue;
            objects.Add(Label(name, cx + (Math.Cos(mid) * r), cy + (Math.Sin(mid) * r), LabelPreset.Region, 3));
        }
        objects.Add(Label(settings.Name, cx, cy - (square * .2), LabelPreset.City, 5));

        return new CityPlan(streets, [.. blocks.Select(b => (IReadOnlyList<VectorPoint>)[.. b.ExteriorRing.Coordinates.Select(c => new VectorPoint(c.X, c.Y))])], objects, key, houses);
    }

    /// <summary>
    /// The city as a new map: paper, its streets and buildings, a note per building of the key. With
    /// <paramref name="coast"/> (R35) the paper is the sea of <paramref name="seaColor"/>, the land lies on it
    /// and the town keeps to the land.
    /// </summary>
    public static MapDocument NewCityMap(string title, int size, CitySettings settings, MapCategoryId category, DateTimeOffset createdUtc,
        LandLayer? coast = null, string? seaColor = null)
    {
        if (coast is not null)
            settings = settings with { IsLand = (x, y) => TerrainOrder.CoverageAt(coast, x, y) >= 128 };
        var plan = Generate(size / 2.0, size / 2.0, settings with { Radius = Math.Min(settings.Radius, size * .42) });
        var objects = new ObjectLayer(LayerId.New(), "Город", true, false, 1, plan.Objects) { Roads = [.. plan.Streets] };
        var notes = plan.Key.Select(KeyNote).ToArray();
        var ground = coast is null ? "#FFEDE3CC" : seaColor ?? "#FF8FB0BA";
        List<MapLayer> layers = [new BaseColorLayer(LayerId.New(), coast is null ? "Бумага" : "Море", true, false, 1, ground)];
        if (coast is not null) layers.Add(coast);
        layers.Add(objects);
        return new MapDocument(DocumentId.New(), title, createdUtc, new CanvasDefinition(size, size, "px", ground), layers, notes: notes, category: category);
    }

    /// <summary>The note of one building of the key, marked as the generator's (R35b) so a rebuild replaces only these.</summary>
    public static MapNote KeyNote(CityBuilding building) =>
        new(NoteId.New(), $"{building.Number}. {building.Kind}", "", NoteAnchorKind.World, building.X, building.Y) { Origin = NoteOrigin.CityKey };

    private static SceneObject Label(string text, double x, double y, LabelPreset preset, int importance)
    {
        var style = LabelPresets.Apply(new TextStyle { Content = text, Alignment = TextAlignment.Center }, preset) with { Importance = importance };
        var (w, h) = RegionLabels.Size(style);
        return new SceneObject(ObjectId.New(), SceneObjectKind.Text, text, x - (w / 2), y - (h / 2), w, h, 0, "#FF20242B", "#FFFFFFFF", 0, true, false) { Text = style };
    }

    private static VectorPoint[] Circle(double cx, double cy, double r, int segments) =>
        [.. Enumerable.Range(0, segments + 1).Select(s => new VectorPoint(cx + (Math.Cos(s * Math.PI * 2 / segments) * r), cy + (Math.Sin(s * Math.PI * 2 / segments) * r)))];

    private static VectorPoint[] Rectangle(double cx, double cy, double width, double height, double angle)
    {
        var (c, s) = (Math.Cos(angle), Math.Sin(angle));
        VectorPoint P(double dx, double dy) => new(cx + (dx * c) - (dy * s), cy + (dx * s) + (dy * c));
        return [P(-width / 2, -height / 2), P(width / 2, -height / 2), P(width / 2, height / 2), P(-width / 2, height / 2)];
    }

    private static Envelope Envelope(IEnumerable<VectorPoint> points)
    {
        var e = new Envelope();
        foreach (var p in points) e.ExpandToInclude(p.X, p.Y);
        e.ExpandBy(-1);
        return e;
    }

    private static string StreetName(int index) => index switch
    {
        0 => "Королевская", 1 => "Торговая", 2 => "Мельничная", 3 => "Кузнечная", 4 => "Речная", 5 => "Соборная", 6 => "Конная", 7 => "Каменная",
        _ => "Новая",
    };
}

/// <summary>The «Город» layer and the key notes replaced as one undo step (R34).</summary>
public sealed class ReplaceCityCommand(ObjectLayer? before, ObjectLayer after, IReadOnlyList<MapNote> notesBefore, IReadOnlyList<MapNote> notesAfter) : IEditCommand
{
    public string Name => "Перестроить город";

    public MapDocument Apply(MapDocument document)
    {
        var layers = before is null
            ? [.. document.Layers, after]
            : document.Layers.Select(l => l.Id == before.Id ? after : l).ToList();
        return document.WithLayers(layers).WithNotes(notesAfter);
    }

    public MapDocument Revert(MapDocument document)
    {
        var layers = before is null
            ? document.Layers.Where(l => l.Id != after.Id).ToList()
            : document.Layers.Select(l => l.Id == after.Id ? before : l).ToList();
        return document.WithLayers(layers).WithNotes(notesBefore);
    }
}
