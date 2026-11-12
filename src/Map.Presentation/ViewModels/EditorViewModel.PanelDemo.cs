using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>What a row or button of the asset panel shows in its explanation card.</summary>
public enum PanelDemoKind
{
    ScopeSelected, ScopeNew,
    ModeOne, ModeSeries, ModeArea, ModePath, ModeFillArea, AfterPlacement,
    Scale, Rotation, Sublayer, Flip, Opacity,
    Spacing, Seed, AddNode, DeleteNode, PickObject, Detach, ApplyToPath, Bake, DeletePath,
    Reset,

    // The generation panels (variant 7, 2026-11-12): frames by GenerationDemos, not by PanelDemo. Order matters there.
    GenHexLand, GenHexRelief, GenHexClimate, GenHexRivers, GenHexSettlements, GenHexRoads, GenHexStates,
    GenHexAll, GenHexReroll, GenHexSeed,
    GenHexProtectBrush, GenHexProtectErase, GenHexProtectSelection, GenHexUnprotectSelection, GenHexSelectionOnly,
    GenApply, GenCancel, GenRecalculate,
    GenContinentHeights, GenContinentErosion, GenContinentClimate, GenContinentRivers, GenContinentSettlements,
    GenContinentAll, GenContinentSeed,
    GenAreaProtectBrush, GenAreaProtectErase, GenAreaProtectStop, GenAreaStart,
}

/// <summary>
/// What the program's renderer cannot know about a demo frame: the path line with its nodes, the selection frames, the
/// pointer and one short note. All in the demo map's units.
/// </summary>
public sealed record PanelDemoMarks(
    IReadOnlyList<VectorPoint> PathNodes,
    double PathShown,
    IReadOnlyList<WorldBounds> Selected,
    VectorPoint? Cursor,
    bool Click,
    string? Note);

/// <param name="View">The part of the demo map the card shows: all of it, or a close-up of one asset.</param>
public sealed record PanelDemoFrame(MapDocument Document, PanelDemoMarks Marks, WorldBounds View);

/// <summary>
/// The explanation cards of the asset panel (owner, 2026-11-11, variant 1 of
/// https://claude.ai/artifact/PNyW4CtTbatoHGn7wESxgn: «наведите на строку — рядом анимация, что она делает»). A frame is a
/// small map made the way the editor makes the real thing - the chosen asset, the panel's settings, the same placement
/// and path planners - on this map's page, ground, style and light. The view draws it with the program's renderer and
/// lays the marks over it; nothing here touches the map itself.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>The demo map in units of the asset's base width: wide enough for a row of six.</summary>
    private double DemoUnit => Math.Max(8, DefaultStampReferenceWidth());

    public double PanelDemoWidth => DemoUnit * 6;
    public double PanelDemoHeight => DemoUnit * 2.6;

    /// <summary>Changes when anything a demo is drawn with changes: the map's look, the asset and the panel's settings.</summary>
    public string PanelDemoSignature => string.Join('|', CreateDemoSignature, _selectedSetId, CurrentVariant?.Sha256,
        _stampToolSettings.GetHashCode(), SelectAfterPlacement, DemoUnit);

    /// <summary>Null when no asset is chosen: then there is nothing real to show.</summary>
    public PanelDemoFrame? PanelDemo(PanelDemoKind kind, int frame, int frames)
    {
        var settings = _stampToolSettings;
        if (!TryStampInputs(settings, out var request, out var sources, out _, out _)) return null;
        var t = frames <= 1 ? 1 : Math.Clamp(frame / (double)(frames - 1), 0, 1);
        var u = DemoUnit; double w = PanelDemoWidth, h = PanelDemoHeight;
        var ground = h * .72;
        var objects = new List<SceneObject>();
        var road = (Road?)null;
        IReadOnlyList<VectorPoint> nodes = [];
        double shown = 1;
        var selected = new List<WorldBounds>();
        VectorPoint? cursor = null; var click = false; string? note = null;
        VectorPoint[] Wave() => [new(u * .5, ground), new(u * 2, ground - u * .55), new(u * 3.6, ground + u * .1), new(u * 5.5, ground - u * .45)];

        // One stamp exactly as a click places it, with these settings.
        SceneObject One(double x, double y, StampToolSettings s, int seed)
        {
            var r = PanelRequest(s, request) with { CenterX = x, CenterY = y, AreaRadius = 0, Density = 0, Seed = seed };
            var item = StampPlacement.PlanArea(r, sources)[0];
            item = ApplyPlacementRotation([item], s.RotationDegrees)[0];
            return StampObject(item, "Пример", s, previewOpacityMultiplier: null);
        }
        // A path exactly as the path tool lays it.
        IReadOnlyList<SceneObject> Path(IReadOnlyList<VectorPoint> points, double spacing, int seed, StampToolSettings s)
        {
            var path = new PlacementPath
            {
                Name = "Пример", Variants = [.. sources], Request = PanelRequest(s, request), Seed = seed,
                Nodes = [.. points.Select(p => new PlacementPathNode(Guid.NewGuid(), p.X, p.Y))],
                Spacing = Math.Max(1, spacing), RotationDegrees = s.RotationDegrees, ScalePercent = s.ScalePercent,
                Sublayer = s.Sublayer, Opacity = s.ObjectOpacity, Style = s.Style ?? new StampStyle(sources[0].Sha256),
            };
            // Stable ids per node index keep the row's drawing still while only the asked thing changes.
            path = path with { Nodes = [.. path.Nodes.Select((n, i) => n with { Id = new Guid(i + 1, 0, 0, new byte[8]) })] };
            return PlacementPathPlanner.Preview(path, 64);
        }
        WorldBounds Box(SceneObject o) => new(o.X, o.Y, o.X + o.Width, o.Y + o.Height);
        WorldBounds BoxOf(IEnumerable<SceneObject> all)
        {
            var list = all.ToList();
            return list.Count == 0 ? new WorldBounds(0, 0, 0, 0)
                : new WorldBounds(list.Min(o => o.X), list.Min(o => o.Y), list.Max(o => o.X + o.Width), list.Max(o => o.Y + o.Height));
        }
        var plain = settings with { ScalePercent = 100, RotationDegrees = 0, Sublayer = 0, ObjectOpacity = 1, Style = settings.Style is { } st
            ? st with { FlipHorizontal = false, FlipVertical = false } : null };
        var step = u * Math.Max(.3, settings.SeriesSpacingPercent / 100);
        double Wobble(double k) => Math.Sin(Math.PI * k);

        switch (kind)
        {
            case PanelDemoKind.ScopeSelected:
            case PanelDemoKind.Spacing:
            {
                nodes = Wave();
                var spacing = u * (.7 + .7 * Wobble(t));
                objects.AddRange(Path(nodes, spacing, 7, settings));
                if (kind == PanelDemoKind.ScopeSelected) selected.Add(BoxOf(objects));
                note = kind == PanelDemoKind.Spacing ? $"шаг {Math.Round(spacing)}" : "меняется выделенный путь";
                break;
            }
            case PanelDemoKind.Seed:
                nodes = Wave();
                objects.AddRange(Path(nodes, step, 7 + frame / 2 * 13, settings));
                note = $"рисунок {7 + frame / 2 * 13}";
                break;
            case PanelDemoKind.ScopeNew:
            {
                objects.Add(One(u * .9, ground, plain, 1)); objects.Add(One(u * 1.9, ground - u * .2, plain, 2)); objects.Add(One(u * 2.8, ground + u * .1, plain, 3));
                Arrive(new VectorPoint(u * 4.6, ground - u * .2), .4);
                if (t >= .45) objects.Add(One(u * 4.6, ground, settings, 4));
                note = "стоящие не меняются";
                break;
            }
            case PanelDemoKind.ModeOne:
                Arrive(new VectorPoint(w / 2, ground - u * .2), .4);
                if (t >= .45) objects.Add(One(w / 2, ground, settings, 5));
                break;
            case PanelDemoKind.ModeSeries:
            {
                var x = u * .6 + (w - u * 1.2) * Math.Clamp(t / .85, 0, 1);
                for (var px = u * .6; px <= x + .01; px += step) objects.Add(One(px, ground, settings, (int)(px * 7)));
                cursor = new VectorPoint(x, ground - u * .2); click = t < .9;
                break;
            }
            case PanelDemoKind.ModeArea:
            {
                var r = PanelRequest(settings, request) with { CenterX = w / 2, CenterY = ground - u * .1, AreaRadius = u * 1.3, Seed = 11 };
                r = r with { Density = Math.Max(r.Density, 40) };
                var plan = ApplyPlacementRotation(StampPlacement.PlanAreaPreview(r, sources, 14), settings.RotationDegrees);
                var count = (int)Math.Round(plan.Count * Math.Clamp(t / .85, 0, 1));
                objects.AddRange(plan.Take(count).Select(i => StampObject(i, "Пример", settings, null)));
                var a = t * Math.Tau * 1.5;
                cursor = new VectorPoint(w / 2 + Math.Cos(a) * u * .8, ground - u * .2 + Math.Sin(a) * u * .4); click = t < .9;
                break;
            }
            case PanelDemoKind.ModePath:
            {
                var all = Wave();
                var placed = Math.Max(1, (int)Math.Ceiling(Math.Clamp(t / .5, 0, 1) * all.Length));
                nodes = all.Take(placed).ToArray();
                if (t <= .5) { cursor = nodes[^1]; click = true; }
                if (t > .5)
                {
                    var row = Path(all, step, 7, settings);
                    objects.AddRange(row.Take((int)Math.Round(row.Count * Math.Clamp((t - .5) / .4, 0, 1))));
                }
                break;
            }
            case PanelDemoKind.ModeFillArea:
            {
                double x0 = u * 1.1, y0 = h * .18, x1 = x0 + (w - u * 2.2) * Math.Clamp(t / .45, 0, 1), y1 = y0 + (h * .75) * Math.Clamp(t / .45, 0, 1);
                nodes = [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), new(x0, y0)];
                if (t <= .45) { cursor = new VectorPoint(x1, y1); click = true; }
                else
                {
                    var cells = new List<VectorPoint>();
                    for (var row = 0; row < 2; row++)
                        for (var col = 0; col < 5; col++)
                            cells.Add(new(x0 + u * .6 + col * (x1 - x0 - u * 1.2) / 4 + (row == 1 ? u * .3 : 0), y0 + u * .9 + row * (y1 - y0 - u * .9) * .6));
                    var k = (int)Math.Round(cells.Count * Math.Clamp((t - .5) / .4, 0, 1));
                    objects.AddRange(cells.Take(k).Select((p, i) => One(p.X, p.Y, settings with { ScalePercent = settings.ScalePercent * .7 }, 20 + i)));
                }
                break;
            }
            case PanelDemoKind.AfterPlacement:
                Arrive(new VectorPoint(w / 2, ground - u * .2), .35);
                if (t >= .4)
                {
                    objects.Add(One(w / 2, ground, settings, 6));
                    if (SelectAfterPlacement && t >= .5) selected.Add(Box(objects[^1]));
                }
                note = SelectAfterPlacement ? "сразу выделяется" : "не выделяется";
                break;
            case PanelDemoKind.Scale:
            {
                var k = .6 + .9 * Wobble(t);
                objects.Add(One(w / 2, ground, settings with { ScalePercent = settings.ScalePercent * k }, 8));
                note = $"{Math.Round(settings.ScalePercent * k)} %";
                break;
            }
            case PanelDemoKind.Rotation:
            {
                var turn = Math.Sin(t * Math.Tau) * 35;
                objects.Add(One(w / 2, ground, settings with { RotationDegrees = settings.RotationDegrees + turn }, 9));
                note = $"{Math.Round(((settings.RotationDegrees + turn) % 360 + 360) % 360)}°";
                break;
            }
            case PanelDemoKind.Sublayer:
            {
                var level = t < .5 ? -1 : 1;
                objects.Add(One(w / 2 - u * .45, ground, plain, 10));
                objects.Add(One(w / 2 + u * .35, ground + u * .05, settings with { Sublayer = level }, 11));
                note = $"правый: уровень {(level > 0 ? "+1" : "−1")}";
                break;
            }
            case PanelDemoKind.Flip:
            {
                var flipped = t >= .5;
                var style = (settings.Style ?? new StampStyle(sources[0].Sha256)) with { FlipHorizontal = flipped ^ (settings.Style?.FlipHorizontal ?? false) };
                objects.Add(One(w / 2, ground, settings with { Style = style }, 12));
                note = flipped ? "отражено" : "как в библиотеке";
                break;
            }
            case PanelDemoKind.Opacity:
            {
                var k = 1 - .75 * Wobble(t);
                road = RoadEditor.NewRoad([new(0, ground - u * .3), new(w * .4, ground - u * .55), new(w * .7, ground - u * .2), new(w, ground - u * .4)],
                    Math.Max(4, u * .14), RoadStyle.Painted(), "Дорога", seed: 7);
                objects.Add(One(w / 2, ground, settings with { ObjectOpacity = settings.ObjectOpacity * k }, 13));
                note = $"{Math.Round(settings.ObjectOpacity * k * 100)} %";
                break;
            }
            case PanelDemoKind.AddNode:
            {
                var lift = t < .3 ? 0 : Math.Clamp((t - .3) / .4, 0, 1) * u * .8;
                nodes = t < .3 ? [new(u * .5, ground), new(w - u * .5, ground)] : [new(u * .5, ground), new(w / 2, ground - lift), new(w - u * .5, ground)];
                objects.AddRange(Path(nodes, step, 7, settings));
                if (t >= .3 && t <= .75) { cursor = nodes[1]; click = true; }
                break;
            }
            case PanelDemoKind.DeleteNode:
            {
                var k = Math.Clamp((t - .45) / .3, 0, 1);
                nodes = t < .45 ? [new(u * .5, ground), new(w / 2, ground - u * .8), new(w - u * .5, ground)]
                    : [new(u * .5, ground), new(w / 2, ground - u * .8 * (1 - k)), new(w - u * .5, ground)];
                objects.AddRange(Path(nodes, step, 7, settings));
                if (t >= .45) nodes = [nodes[0], nodes[2]];
                if (t is > .25 and < .45) { cursor = new VectorPoint(w / 2, ground - u * .8); click = true; }
                break;
            }
            case PanelDemoKind.PickObject:
            case PanelDemoKind.Detach:
            {
                nodes = Wave();
                var row = Path(nodes, step, 7, settings).ToList();
                var index = Math.Min(2, row.Count - 1);
                if (index >= 0)
                {
                    if (kind == PanelDemoKind.Detach)
                    {
                        var k = Math.Clamp((t - .3) / .45, 0, 1);
                        row[index] = MultiSelectionGeometry.Translate([row[index]], u * .5 * k, -u * 1.1 * k)[0];
                        selected.Add(Box(row[index]));
                        if (k >= 1) note = "обычный объект";
                    }
                    else
                    {
                        var target = new VectorPoint(row[index].X + row[index].Width / 2, row[index].Y + row[index].Height * .4);
                        Arrive(target, .4);
                        if (t >= .45) selected.Add(Box(row[index]));
                    }
                }
                objects.AddRange(row);
                break;
            }
            case PanelDemoKind.ApplyToPath:
                nodes = Wave();
                objects.AddRange(Path(nodes, step, 7, t < .5 ? plain : settings));
                note = t < .5 ? "как было" : $"{Math.Round(settings.ScalePercent)} % · {Math.Round(settings.RotationDegrees)}°";
                break;
            case PanelDemoKind.Bake:
                nodes = t < .5 ? Wave() : [];
                objects.AddRange(Path(Wave(), step, 7, settings));
                if (t >= .6) selected.AddRange(objects.Select(Box));
                if (t >= .5) note = "линии нет, объекты отдельные";
                break;
            case PanelDemoKind.DeletePath:
                if (t < .5) { nodes = Wave(); objects.AddRange(Path(nodes, step, 7, settings)); }
                else note = "Ctrl+Z вернёт";
                break;
            case PanelDemoKind.Reset:
                objects.Add(One(w / 2, ground, t < .5 ? settings : plain, 14));
                note = t < .5 ? "изменено" : "исходные";
                break;
        }
        // One asset is shown close: at the row's scale it was a speck on the card.
        var close = kind is PanelDemoKind.ModeOne or PanelDemoKind.AfterPlacement or PanelDemoKind.Scale or PanelDemoKind.Rotation
            or PanelDemoKind.Sublayer or PanelDemoKind.Flip or PanelDemoKind.Opacity or PanelDemoKind.Reset;
        var view = new WorldBounds(0, 0, w, h);
        if (close)
        {
            // Framed by the asset's box at 100 %, standing on the card's lower edge: a grown asset rises upward.
            var probe = One(w / 2, ground, plain, 5);
            var viewHeight = probe.Height * 1.7;
            var bottom = probe.Y + probe.Height * 1.08;
            view = new WorldBounds(w / 2 - viewHeight * w / h / 2, bottom - viewHeight, w / 2 + viewHeight * w / h / 2, bottom);
        }
        return new PanelDemoFrame(DemoDocument(objects, road, w, h), new PanelDemoMarks(nodes, shown, selected, cursor, click, note), view);

        void Arrive(VectorPoint target, double at)
        {
            var k = Math.Clamp(t / at, 0, 1);
            k = k * k * (3 - 2 * k);
            cursor = new VectorPoint(w * .95 + (target.X - w * .95) * k, h * .1 + (target.Y - h * .1) * k);
            click = t >= at && t < at + .12;
        }
    }

    /// <summary>The request of the panel's settings: scale, jitters and variants as the tool reads them.</summary>
    private StampPlacementRequest PanelRequest(StampToolSettings s, StampPlacementRequest template) =>
        template with { BaseWidth = DefaultStampReferenceWidth() * s.ScalePercent / 100.0 };

    private MapDocument DemoDocument(IReadOnlyList<SceneObject> objects, Road? road, double w, double h)
    {
        var document = Session.Document;
        int width = (int)Math.Ceiling(w), height = (int)Math.Ceiling(h);
        var page = document.Layers.OfType<BaseColorLayer>().FirstOrDefault() is { } own
            ? own with { Id = LayerId.New() }
            : new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, document.Canvas.Background);
        var groundStyle = WithGroundTexture(LandLayer.CreateEmpty(LayerId.New(), "Земля").EdgeStyle, NewLandTextureId);
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects);
        if (road is not null) layer = layer with { Roads = [road] };
        return new MapDocument(DocumentId.New(), "Пример", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", document.Canvas.Background),
            [page, Land(groundStyle, static (_, _) => true, width, height), layer])
        { Style = document.Style, Lighting = document.Lighting };
    }
}
