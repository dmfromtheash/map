using System.Collections.Concurrent;
using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>What the renderer cannot know about a frame of a generation card: protection, selection, the pointer, a note.</summary>
public sealed class GenerationDemoMarks
{
    public List<HexCoord> Protected { get; } = [];
    public List<HexCoord> Selected { get; } = [];
    /// <summary>The board's layout when the marks are hexes.</summary>
    public HexLayout? Layout { get; set; }
    public VectorPoint? Cursor { get; set; }
    public bool Click { get; set; }
    public double BrushRadius { get; set; }
    public string? Note { get; set; }
    /// <summary>The dashed frame of a preview not yet applied.</summary>
    public bool Preview { get; set; }
    /// <summary>The calculation's bar along the lower edge, 0..1; negative — no bar.</summary>
    public double Progress { get; set; } = -1;
    /// <summary>Boxes in world units: protected objects of an area.</summary>
    public List<WorldBounds> Frames { get; } = [];
    /// <summary>The next state laid over the frame with this opacity (a step whose change is pixels, not hexes).</summary>
    public MapDocument? FadeTo { get; set; }
    public double Fade { get; set; }
    /// <summary>The outline of a placement area, world units.</summary>
    public IReadOnlyList<VectorPoint>? Outline { get; set; }
}

public sealed record GenerationDemoFrame(MapDocument Document, WorldBounds View, GenerationDemoMarks Marks);

/// <summary>
/// The animations of the generation panels' explanation cards (owner, 2026-11-12, variant 7 of
/// https://claude.ai/artifact/2JRg4noKtdLyhPGW4N5Xki): ten frames of the real thing - the hex world generator
/// (<see cref="ControlledHexGeneration"/>), the continent generator (<see cref="ContinentGenerator"/>) and the area generator
/// (<see cref="ControlledAreaGeneration"/>) - on a small map; the view draws them with the program's renderer and lays the
/// marks over. Pure: no map of the editor is touched, so the frames are made off the UI thread.
/// </summary>
public static class GenerationDemos
{
    public const int Frames = 10;

    public static bool IsGeneration(this PanelDemoKind kind) => kind >= PanelDemoKind.GenHexLand;
    public static bool IsArea(this PanelDemoKind kind) => kind is >= PanelDemoKind.GenAreaProtectBrush and <= PanelDemoKind.GenAreaStart;
    public static bool IsContinent(this PanelDemoKind kind) => kind is >= PanelDemoKind.GenContinentHeights and <= PanelDemoKind.GenContinentSeed;

    // ------------------------------------------------------------------ hex world

    /// <summary>A board of 40 × 15 flat hexes, about the card's proportions, and a seed whose world shows every step.</summary>
    public static readonly HexGeneratorSettings HexSettings = new()
    {
        Seed = 1, RiverModel = HexGeneratorSettings.FlowRivers, LandShare = .55, SettlementsPerThousand = 50, RiversPerThousand = 40, Relief = .3,
    };

    private sealed record HexWorlds(HexLayer Empty, HexLayer[] States, HexLayer RerollA, HexLayer RerollB, HexLayer Seed7, HexLayer Seed1203,
        List<HexCoord> Patch, HexLayer Protected, HexLayer Regenerated, List<HexCoord> Selection, ImmutableHashSet<HexCoord> Only, HexLayer ReliefOnly,
        HexLayer Recalculated, MapDocument Template);

    private static readonly ConcurrentDictionary<string, Lazy<HexWorlds>> HexCache = new();

    private static HexWorlds MakeHexWorlds(string background, MapCategoryId category)
    {
        var document = HexMapFactory.Create("Генератор мира", DateTimeOffset.UnixEpoch, category, HexOrientation.Flat, 40, 40, 15, background);
        var empty = document.Layers.OfType<HexLayer>().Single();
        var s = HexSettings;
        var states = new List<HexLayer> { empty };
        foreach (var step in Enum.GetValues<HexGenerationStep>()) states.Add(Step(states[^1], step, s));
        var world = states[^1];
        var patch = Patch(world, 9, 5, 3);
        var protectedWorld = world with { GenerationProtection = [.. patch] };
        var only = Patch(states[1], 16, 6, 4).ToImmutableHashSet();
        var edited = states[4] with { GenerationProtection = [.. patch] };
        return new HexWorlds(empty, [.. states],
            Step(states[1], HexGenerationStep.Relief, s with { Seed = s.Seed + 1 }), Step(states[1], HexGenerationStep.Relief, s with { Seed = s.Seed + 2 }),
            All(empty, s with { Seed = 7 }), All(empty, s with { Seed = 1203 }),
            patch, protectedWorld, All(protectedWorld, s with { Seed = 7 }), Patch(world, 22, 6, 3), only,
            Step(states[1], HexGenerationStep.Relief, s, only),
            ControlledHexGeneration.Run(edited, new HexGenerationRecipe { Settings = s, Steps = [HexGenerationStep.Settlements] }), document);

        static HexLayer Step(HexLayer layer, HexGenerationStep step, HexGeneratorSettings settings, ImmutableHashSet<HexCoord>? only = null) =>
            ControlledHexGeneration.Run(layer, new HexGenerationRecipe { Settings = settings, Steps = [step], Selection = only ?? [] });
        static HexLayer All(HexLayer layer, HexGeneratorSettings settings) =>
            ControlledHexGeneration.Run(layer, new HexGenerationRecipe { Settings = settings, Steps = [.. Enum.GetValues<HexGenerationStep>()] });
    }

    /// <summary>The frames of a hex world card on a page of <paramref name="background"/>.</summary>
    public static IReadOnlyList<GenerationDemoFrame> Hex(PanelDemoKind kind, string background, MapCategoryId category)
    {
        var w = HexCache.GetOrAdd(background, b => new Lazy<HexWorlds>(() => MakeHexWorlds(b, category))).Value;
        var document = w.Template;
        var layout = w.Empty.Board.Layout;
        var view = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
        var steps = Enum.GetValues<HexGenerationStep>();
        var s = HexSettings;
        var frames = new List<GenerationDemoFrame>(Frames);
        for (var i = 0; i < Frames; i++)
        {
            var (layer, m) = HexFrame(i);
            m.Layout = layout;
            frames.Add(new(document with { Layers = [.. document.Layers.Select(l => l is HexLayer ? layer : l)] }, view, m));
        }
        return frames;

        (HexLayer, GenerationDemoMarks) HexFrame(int i)
        {
            var m = new GenerationDemoMarks();
            VectorPoint At(HexCoord h) { var (x, y) = layout.Center(h); return new(x, y); }
            switch (kind)
            {
                case >= PanelDemoKind.GenHexLand and <= PanelDemoKind.GenHexStates:
                    var k = kind - PanelDemoKind.GenHexLand;
                    m.Note = i == 0 ? "до шага" : i >= 8 ? "шаг готов" : null;
                    return (Between(w.States[k], w.States[k + 1], Grow(i)), m);
                case PanelDemoKind.GenHexAll:
                    var n = Math.Min(steps.Length, i * steps.Length / 8);
                    m.Note = n == 0 ? "пустая доска" : $"{n}. {EditorViewModel.HexGenerationStepName(steps[n - 1])}";
                    return (w.States[n], m);
                case PanelDemoKind.GenHexReroll:
                    m.Note = $"зерно {s.Seed + (i < 4 ? 0 : i < 7 ? 1 : 2)}";
                    return (i < 4 ? w.States[2] : i < 7 ? w.RerollA : w.RerollB, m);
                case PanelDemoKind.GenHexSeed:
                    var pick = Math.Min(2, i / 4);
                    m.Note = $"зерно {new[] { s.Seed, 7, 1203 }[pick]}";
                    return (new[] { w.States[^1], w.Seed7, w.Seed1203 }[pick], m);
                case PanelDemoKind.GenHexProtectBrush:
                    var painted = i < 6 ? w.Patch.Take((int)Math.Ceiling(w.Patch.Count * Math.Min(1, (i + 1) / 5.0))).ToList() : w.Patch;
                    m.Protected.AddRange(painted);
                    if (i < 6) { m.Cursor = At(painted[^1]); m.Click = true; m.BrushRadius = 40; }
                    m.Note = i < 6 ? "защита кистью" : "пересчёт: защищённое осталось";
                    return (i < 6 ? w.Protected : w.Regenerated, m);
                case PanelDemoKind.GenHexProtectErase:
                    var left = w.Patch.Skip((int)Math.Round(w.Patch.Count * Math.Clamp((i - 1) / 7.0, 0, 1))).ToList();
                    m.Protected.AddRange(left);
                    if (i is >= 1 and <= 8 && left.Count > 0) { m.Cursor = At(left[0]); m.Click = true; m.BrushRadius = 40; }
                    m.Note = left.Count == 0 ? "защиты нет" : null;
                    return (w.Protected, m);
                case PanelDemoKind.GenHexProtectSelection:
                    if (i < 4) m.Selected.AddRange(w.Selection); else m.Protected.AddRange(w.Selection);
                    if (i is >= 2 and < 4) m.Note = "щелчок по кнопке"; else if (i >= 4) m.Note = $"защищено {w.Selection.Count} гекс.";
                    return (w.States[^1], m);
                case PanelDemoKind.GenHexUnprotectSelection:
                    m.Protected.AddRange(w.Patch);
                    if (i < 4) { m.Protected.AddRange(w.Selection); m.Selected.AddRange(w.Selection); }
                    m.Note = i < 4 ? $"защищено {w.Patch.Count + w.Selection.Count}" : $"защищено {w.Patch.Count}";
                    return (w.States[^1], m);
                case PanelDemoKind.GenHexSelectionOnly:
                    m.Selected.AddRange(w.Only);
                    m.Note = i == 0 ? "выделено" : i >= 8 ? "горы только в выделенном" : null;
                    return (Between(w.States[1], w.ReliefOnly, Grow(i)), m);
                case PanelDemoKind.GenApply:
                    m.Preview = i < 6; m.Note = i < 6 ? "предпросмотр: карта не изменена" : "применено · Ctrl+Z вернёт";
                    if (i is >= 4 and < 6) { m.Cursor = new(document.Canvas.Width * .9, document.Canvas.Height * .9); m.Click = i == 5; }
                    return (w.States[5], m);
                case PanelDemoKind.GenCancel:
                    var busy = i < 4;
                    m.Preview = i < 6; m.Progress = busy ? i / 4.0 * .6 : -1;
                    m.Note = busy ? "расчёт в фоне" : i < 6 ? "предпросмотр" : "отменено · карта как была";
                    return (busy ? Between(w.States[4], w.States[5], i / 6.0) : i < 6 ? w.States[5] : w.States[4], m);
                default: // GenRecalculate
                    if (i < 3)
                    {
                        m.Progress = i / 3.0 * .5; m.Note = "расчёт в фоне"; m.Protected.AddRange(w.Patch.Take((i + 1) * w.Patch.Count / 3));
                        m.Cursor = At(w.Patch[Math.Min(w.Patch.Count - 1, (i + 1) * w.Patch.Count / 3 - 1)]); m.Click = true; m.BrushRadius = 40;
                        return (w.States[4], m);
                    }
                    m.Protected.AddRange(w.Patch);
                    if (i < 5) { m.Note = "карта изменилась — не применено"; return (w.States[4], m); }
                    m.Preview = true; m.Progress = i < 8 ? (i - 5) / 3.0 : -1;
                    m.Note = i < 8 ? "пересчёт по текущей карте" : "новый предпросмотр";
                    return (i < 8 ? Between(w.States[4], w.Recalculated, (i - 5) / 3.0) : w.Recalculated, m);
            }
        }
    }

    /// <summary>0 for the first frame, 1 from the ninth: the step's changes appear between them.</summary>
    private static double Grow(int frame) => Math.Clamp((frame - 0.5) / 7.5, 0, 1);

    /// <summary>A round patch of hexes around an offset position, in the order a brush would cover them.</summary>
    private static List<HexCoord> Patch(HexLayer layer, int column, int row, int radius)
    {
        var centre = layer.Board.Layout.FromOffset(new HexOffset(column, row));
        return [.. Enumerable.Range(-radius, radius * 2 + 1).SelectMany(dq => Enumerable.Range(-radius, radius * 2 + 1).Select(dr => new HexCoord(centre.Q + dq, centre.R + dr)))
            .Where(h => HexCoord.Distance(h, centre) <= radius && layer.Board.Contains(h))
            .OrderBy(h => layer.Board.Layout.Center(h).X).ThenBy(h => layer.Board.Layout.Center(h).Y)];
    }

    /// <summary>
    /// The board part way from <paramref name="before"/> to <paramref name="after"/>: changed hexes in a fixed scattered
    /// order (the generator works the whole board at once), new paths drawn from their start.
    /// </summary>
    private static HexLayer Between(HexLayer before, HexLayer after, double t)
    {
        if (t <= 0) return before;
        if (t >= 1) return after;
        var coords = before.Board.Cells.Select(c => c.Key).Union(after.Board.Cells.Select(c => c.Key)).ToList();
        var changes = coords.Where(h => !Equals(before.Board[h], after.Board[h])).OrderBy(h => HexEditing.Noise(h, 977)).ToList();
        var take = (int)Math.Round(changes.Count * t);
        var board = before.Board.With(changes.Take(take).Select(h => new KeyValuePair<HexCoord, HexCell?>(h, after.Board[h])));
        var old = before.Paths.Select(p => p.Id).ToHashSet();
        var paths = after.Paths.Select(p =>
        {
            if (old.Contains(p.Id)) return p;
            var n = p.Style == HexRouteStyle.Edges ? p.Vertices.Length : p.Hexes.Length;
            var keep = Math.Max(0, (int)Math.Round(n * t));
            if (keep < 2) return null;
            return p.Style == HexRouteStyle.Edges ? p with { Vertices = p.Vertices[..keep] } : p with { Hexes = p.Hexes[..keep] };
        }).OfType<HexPath>().ToImmutableList();
        return before with { Board = board, Paths = paths, Regions = t >= .5 ? after.Regions : before.Regions };
    }

    // ------------------------------------------------------------------ continent

    public const int ContinentWidth = 1600, ContinentHeight = 1000;
    public static readonly ContinentSettings ContinentDemoSettings = new() { Seed = 23, RiversPerMillion = 10, TownsPerMillion = 10 };

    /// <summary>The demo continent: a big island, a small one and a bay, as a hand would draw the land.</summary>
    public static double ContinentShape(double x, double y)
    {
        (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] shapes = [(800, 500, 640, 290, .12, 5), (330, 330, 150, 90, .14, 4)];
        var value = shapes.Max(s => Shape(s, x, y));
        return Math.Min(value, 1 - Shape((1010, 700, 120, 70, .1, 3), x, y));

        static double Shape((double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes) s, double x, double y)
        {
            var angle = Math.Atan2(y - s.Cy, x - s.Cx);
            var wobble = 1 + (s.Wobble * Math.Sin(angle * s.Lobes)) + (s.Wobble * .6 * Math.Sin((angle * ((s.Lobes * 2) + 3)) + 1.3));
            var distance = Math.Sqrt(Math.Pow((x - s.Cx) / s.Rx, 2) + Math.Pow((y - s.Cy) / s.Ry, 2)) / wobble;
            return Math.Clamp((1 - distance) * Math.Max(s.Rx, s.Ry) / 2, 0, 1);
        }
    }

    private static readonly ConcurrentDictionary<string, Lazy<(MapDocument[] States, MapDocument Other)>> ContinentCache = new();

    /// <summary>The frames of a continent card; <paramref name="start"/> holds the drawn land <paramref name="land"/>.</summary>
    public static IReadOnlyList<GenerationDemoFrame> Continent(PanelDemoKind kind, MapDocument start, LayerId land, string cacheKey)
    {
        var steps = Enum.GetValues<ContinentStep>();
        var (states, other) = ContinentCache.GetOrAdd(cacheKey, _ => new(() =>
        {
            var list = new List<MapDocument> { start };
            foreach (var step in steps) list.Add(ContinentGenerator.Run(list[^1], land, step, ContinentDemoSettings));
            var second = start;
            foreach (var step in steps) second = ContinentGenerator.Run(second, land, step, ContinentDemoSettings with { Seed = 9001 });
            return ([.. list], second);
        })).Value;
        var view = new WorldBounds(0, 154, ContinentWidth, 846);
        var frames = new List<GenerationDemoFrame>(Frames);
        for (var i = 0; i < Frames; i++)
        {
            var m = new GenerationDemoMarks();
            MapDocument d;
            if (kind == PanelDemoKind.GenContinentAll)
            {
                var k = Math.Min(steps.Length, i * steps.Length / 7);
                d = states[k]; m.Note = k == 0 ? "нарисованная суша" : $"{k}. {EditorViewModel.ContinentStepName(steps[k - 1])}";
            }
            else if (kind == PanelDemoKind.GenContinentSeed)
            {
                d = states[^1];
                if (i < 5) m.Note = "зерно 23"; else { m.FadeTo = other; m.Fade = Math.Clamp((i - 4) / 3.0, 0, 1); m.Note = "зерно 9001"; }
            }
            else
            {
                var k = kind - PanelDemoKind.GenContinentHeights;
                d = states[k]; m.FadeTo = states[k + 1]; m.Fade = Grow(i); m.Note = i == 0 ? "до шага" : i >= 8 ? "шаг готов" : null;
            }
            frames.Add(new(d, view, m));
        }
        return frames;
    }

    // ------------------------------------------------------------------ areas

    public const int AreaWidth = 1000, AreaHeight = 433;

    /// <summary>
    /// The frames of an area card: <paramref name="start"/> is a page with land and the object layer <paramref name="layer"/>
    /// holding one area of the chosen assets (<see cref="AreaStart"/>).
    /// </summary>
    public static IReadOnlyList<GenerationDemoFrame> Area(PanelDemoKind kind, MapDocument start, LayerId layer)
    {
        var view = new WorldBounds(0, 0, AreaWidth, AreaHeight);
        PlacementArea AreaOf(MapDocument d) => ((ObjectLayer)d.FindLayer(layer)!).PlacementAreas.Single();
        IReadOnlyList<SceneObject> Objects(MapDocument d) => ((ObjectLayer)d.FindLayer(layer)!).Objects;
        static WorldBounds Box(SceneObject o) => new(o.X, o.Y, o.X + o.Width, o.Y + o.Height);
        var area = AreaOf(start);
        MapDocument Generate(MapDocument d, PlacementArea parameters) =>
            ControlledAreaGeneration.Create(d, layer, area.Id, CancellationToken.None, parameters: parameters).Apply(d);
        var outline = area.Nodes.Select(n => new VectorPoint(n.X, n.Y)).ToList();

        // The brush passes left to right through the middle; the members it touches become protected.
        var placed = area.Members.Select(m => (m.Slot, Object: Objects(start).Single(o => o.Id == m.ObjectId))).ToList();
        static VectorPoint Brush(double t) => new(180 + 330 * t, 200 + 40 * Math.Sin(t * Math.PI * 2));
        static bool Under(VectorPoint b, SceneObject o) => Math.Abs(o.X + o.Width / 2 - b.X) < 70 && Math.Abs(o.Y + o.Height * .6 - b.Y) < 70;
        List<(PlacementAreaSlot Slot, SceneObject Object)> Touched(double upTo) =>
            [.. placed.Where(p => Enumerable.Range(0, 21).Select(k => Brush(upTo * k / 20)).Any(b => Under(b, p.Object)))];
        var allTouched = Touched(1);
        // As the editor's brush leaves it: the touched members and the space under the stroke (density 0 there).
        const int cell = 8, cols = AreaWidth / cell + 1, rows = AreaHeight / cell + 1;
        var values = Enumerable.Repeat(PlacementAreaDensityMask.Neutral, cols * rows).ToArray();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                if (Enumerable.Range(0, 41).Select(k => Brush(k / 40.0)).Any(b => double.Hypot((c + .5) * cell - b.X, (r + .5) * cell - b.Y) <= 70))
                    values[r * cols + c] = 0;
        var protectedArea = area with { GenerationProtection = [.. allTouched.Select(p => p.Slot)],
            GenerationProtectionMask = PlacementAreaDensityBrush.Finish(0, 0, cell, cols, rows, values) };
        var protectedDoc = start.WithLayer(layer, l => ((ObjectLayer)l) with { PlacementAreas = [protectedArea] });

        var frames = new List<GenerationDemoFrame>(Frames);
        MapDocument? regenerated = null, denser = null;
        for (var i = 0; i < Frames; i++)
        {
            var m = new GenerationDemoMarks { Outline = outline };
            MapDocument d;
            switch (kind)
            {
                case PanelDemoKind.GenAreaProtectBrush:
                    if (i < 6)
                    {
                        var t = (i + 1) / 6.0;
                        m.Frames.AddRange(Touched(t).Select(p => Box(p.Object))); m.Cursor = Brush(t); m.BrushRadius = 45; m.Note = "защита кистью";
                        d = start; break;
                    }
                    // A new seed with protected members fails in Core (found 2026-11-12, «Другой рисунок»); density is a real recalculation.
                    regenerated ??= Generate(protectedDoc, protectedArea with { Density = 80 });
                    m.Frames.AddRange(allTouched.Select(p => Box(p.Object))); m.Note = "пересчёт: защищённые на месте";
                    if (i == 6) { m.FadeTo = regenerated; m.Fade = .5; d = protectedDoc; } else d = regenerated;
                    break;
                case PanelDemoKind.GenAreaProtectErase:
                    var cleared = Touched(Math.Clamp((i - 1) / 7.0, 0, 1)).Select(p => p.Slot).ToHashSet();
                    m.Frames.AddRange(allTouched.Where(p => i == 0 || !cleared.Contains(p.Slot)).Select(p => Box(p.Object)));
                    if (i is >= 1 and <= 8) { m.Cursor = Brush(Math.Clamp((i - 1) / 7.0, 0, 1)); m.BrushRadius = 45; }
                    m.Note = i >= 9 ? "защиты нет" : null;
                    d = protectedDoc; break;
                case PanelDemoKind.GenAreaProtectStop:
                    m.Frames.AddRange(allTouched.Select(p => Box(p.Object)));
                    if (i < 5) { m.Cursor = Brush(.5 + i * .05); m.BrushRadius = 45; m.Note = "кисть защиты"; }
                    else { m.Cursor = new(760, 230); m.Click = i is 6 or 7; m.Note = "кисть выключена"; }
                    d = protectedDoc; break;
                default: // GenAreaStart
                    denser ??= Generate(start, area with { Density = 80 });
                    m.Progress = i is >= 1 and < 5 ? i / 5.0 : -1;
                    if (i < 5) m.Note = i == 0 ? "плотность 45 → 80" : "расчёт в фоне";
                    else { m.Preview = true; m.FadeTo = denser; m.Fade = Math.Clamp((i - 4) / 3.0, 0, 1); m.Note = "предпросмотр"; }
                    d = start; break;
            }
            frames.Add(new(d, view, m));
        }
        return frames;
    }

    /// <summary>The area of the demo: four corners, density 45, the given assets at a base width of 70.</summary>
    public static PlacementArea AreaStart(IReadOnlyList<StampVariantSource> variants) => PlacementAreaPlanner.InitializeComposition(new PlacementArea
    {
        LayoutVersion = 4, Density = 45, Seed = 5,
        Nodes = [new(Guid.NewGuid(), 60, 50), new(Guid.NewGuid(), 940, 40), new(Guid.NewGuid(), 950, 400), new(Guid.NewGuid(), 50, 395)],
        Variants = [.. variants], Style = new(variants[0].Sha256), Request = new() { BaseWidth = 70 },
    });
}
