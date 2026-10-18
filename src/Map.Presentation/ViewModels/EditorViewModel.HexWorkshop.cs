using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>One row of the hex key table: where, the printed hex number and the entry.</summary>
public sealed record HexKeyRow(HexCoord Hex, string Number, HexNote Note);

/// <summary>
/// The workshop tools of the hex category beyond painting tiles: regions, the fog of war, paths,
/// the hex key, the travel ruler, selection with copy / paste / turn, board rows and columns, and
/// levels of the world. Every change is one undoable command on the hex layer.
/// </summary>
public sealed partial class EditorViewModel
{
    private static readonly string[] RegionPalette =
    [
        "#FFC0392B", "#FF2E86C1", "#FF27AE60", "#FF8E44AD", "#FFD68910", "#FF16A085", "#FFCB4335", "#FF5D6D7E",
    ];

    private int _hexRegionId;
    private bool _hexRegionErase;
    private bool _hexFogReveal = true;
    private HexPathKind _hexPathKind = HexPathKind.River;
    private HexRouteStyle _hexRouteStyle = HexRouteStyle.Meander;
    private bool _hexPathAuto;
    private bool _hexPathErase;
    private List<HexCoord>? _hexPathHexes;
    private List<HexVertex>? _hexPathVertices;
    private HexCoord? _hexKeyHex;
    private HexCoord? _hexMeasureStart;
    private HexRoute? _hexRoute;
    private readonly HashSet<HexCoord> _hexSelection = [];
    private HexCoord? _hexSelectStart;
    private HashSet<HexCoord>? _hexSelectBase;
    private bool _hexSelectWand;
    private HexClip? _hexPaste;
    private HexCoord? _hexPasteAt;
    private bool _hexPasteMoves;
    private int _hexLevel;
    private bool _hexGhostSurface = true;

    // One clipboard for every hex map open in this process: copy here, paste in another map.
    private static HexClip? s_hexClipboard;

    /// <summary>Asks the window to make (or open) the child map of a hex; the handler sets the key's link.</summary>
    public event EventHandler<HexCoord>? HexChildMapRequested;

    /// <summary>Asks the window to open the players' view of this map.</summary>
    public event EventHandler? HexPlayerViewRequested;

    private bool CancelHexWorkshopGesture()
    {
        var any = _hexPathHexes is not null || _hexPathVertices is not null || _hexSelectStart is not null;
        _hexPathHexes = null;
        _hexPathVertices = null;
        _hexSelectStart = null;
        _hexSelectBase = null;
        if (any) Raise(nameof(HexOverlay));
        return any;
    }

    /// <summary>What the canvas shows over the hex layer for the running tool.</summary>
    public HexOverlay? HexOverlay
    {
        get
        {
            if (!IsHexWorkshop || ActiveHexLayer is not { } layer) return null;
            var overlay = new HexOverlay(layer.Id) { Focus = _hexMode == HexToolMode.Key && ActiveTool == EditorTool.Hex ? _hexKeyHex : null };
            if (ActiveTool != EditorTool.Hex) return overlay.Focus is null ? null : overlay;
            if (_hexMode == HexToolMode.Select)
            {
                overlay = overlay with { Selection = _hexSelection };
                if (_hexPaste is { } paste && _hexPasteAt is { } at)
                {
                    overlay = overlay with { Ghost = paste.Cells.ToDictionary(pair => pair.Key + at, pair => pair.Value) };
                }
            }

            if (_hexMode == HexToolMode.Path && PathDraft(layer) is { } draft) overlay = overlay with { PathPreview = draft };
            if (_hexMode == HexToolMode.Measure && _hexRoute is { } route)
            {
                overlay = overlay with { Route = route.Hexes, RouteLabel = route.Label };
            }

            return overlay;
        }
    }

    // ------------------------------------------------------------------ regions

    public IReadOnlyList<HexRegion> HexRegions => ActiveHexLayer?.Regions ?? [];

    /// <summary>The region the region brush paints.</summary>
    public int HexRegionId
    {
        get => _hexRegionId;
        set
        {
            if (_hexRegionId == value) return;
            _hexRegionId = value;
            _hexRegionErase = false;
            if (_hexMode != HexToolMode.Region) HexMode = HexToolMode.Region;
            Raise(nameof(HexRegionId));
            Raise(nameof(HexRegionErase));
        }
    }

    /// <summary>The region brush takes hexes out of their region instead.</summary>
    public bool HexRegionErase
    {
        get => _hexRegionErase;
        set
        {
            if (_hexRegionErase == value) return;
            _hexRegionErase = value;
            Raise(nameof(HexRegionErase));
            RefreshHexCursor();
        }
    }

    /// <summary>A new region with the next free id and colour; the brush switches to it.</summary>
    public void AddHexRegion(string? name = null)
    {
        if (ActiveHexLayer is not { } layer) return;
        var id = layer.Regions.IsEmpty ? 1 : layer.Regions.Max(region => region.Id) + 1;
        if (id > HexCell.MaxRegion)
        {
            StatusDetail = $"В слое уже {HexCell.MaxRegion} регионов.";
            return;
        }

        var region = new HexRegion(id, name ?? $"Регион {id}", RegionPalette[(id - 1) % RegionPalette.Length]);
        ExecuteHexLayerEdit("Новый регион", layer, layer with { Regions = layer.Regions.Add(region) });
        _hexRegionId = id;
        _hexRegionErase = false;
        HexMode = HexToolMode.Region;
        Raise(nameof(HexRegions));
        Raise(nameof(HexRegionId));
        StatusDetail = $"Регион «{region.Name}» создан. Проведите по гексам, чтобы включить их в него.";
    }

    public void RenameHexRegion(int id, string name)
    {
        if (ActiveHexLayer is not { } layer || layer.Region(id) is not { } region || string.IsNullOrWhiteSpace(name) || region.Name == name.Trim()) return;
        var renamed = new HexRegion(id, name, region.Color);
        ExecuteHexLayerEdit("Имя региона", layer, layer with { Regions = layer.Regions.Replace(region, renamed) });
        Raise(nameof(HexRegions));
    }

    public void SetHexRegionColor(int id, string color)
    {
        if (ActiveHexLayer is not { } layer || layer.Region(id) is not { } region || AppearanceColor.Normalize(color) is not { } normalized
            || region.Color == normalized) return;
        ExecuteHexLayerEdit("Цвет региона", layer, layer with { Regions = layer.Regions.Replace(region, region with { Color = normalized }) });
        Raise(nameof(HexRegions));
    }

    /// <summary>Removes a region; its hexes leave it (one undo step).</summary>
    public void RemoveHexRegion(int id)
    {
        if (ActiveHexLayer is not { } layer || layer.Region(id) is not { } region) return;
        var cleared = layer.Board.Cells.Where(pair => pair.Value.Region == id)
            .Select(pair => new KeyValuePair<HexCoord, HexCell?>(pair.Key, pair.Value.WithRegion(0))).ToList();
        ExecuteHexLayerEdit("Удалить регион", layer, layer with { Regions = layer.Regions.Remove(region), Board = layer.Board.With(cleared) });
        if (_hexRegionId == id) _hexRegionId = 0;
        Raise(nameof(HexRegions));
        Raise(nameof(HexRegionId));
        StatusDetail = $"Регион «{region.Name}» удалён, его гексов было {cleared.Count}.";
    }

    /// <summary>How many hexes each region holds, for the region list.</summary>
    public int HexRegionSize(int id) => ActiveHexLayer?.Board.Cells.Count(pair => pair.Value.Region == id) ?? 0;

    // ------------------------------------------------------------------ fog of war

    /// <summary>The fog of war of the hex layer: hexes the players have not seen are covered.</summary>
    public bool HexFogEnabled
    {
        get => ActiveHexLayer?.FogEnabled ?? false;
        set
        {
            if (ActiveHexLayer is not { } layer || layer.FogEnabled == value) return;
            ExecuteHexLayerEdit(value ? "Включить туман" : "Выключить туман", layer, layer with { FogEnabled = value });
            Raise(nameof(HexFogEnabled));
            StatusDetail = value
                ? "Туман включён: игроки видят только открытые гексы. Режим «Туман» открывает их кистью."
                : "Туман выключен.";
        }
    }

    /// <summary>The fog brush reveals (true) or covers again (false).</summary>
    public bool HexFogReveal
    {
        get => _hexFogReveal;
        set
        {
            if (_hexFogReveal == value) return;
            _hexFogReveal = value;
            Raise(nameof(HexFogReveal));
            RefreshHexCursor();
        }
    }

    /// <summary>Reveals or covers every painted hex at once.</summary>
    public void RevealAllHexes(bool reveal)
    {
        if (ActiveHexLayer is not { } layer) return;
        var changes = layer.Board.Cells.Where(pair => pair.Value.Revealed != reveal)
            .ToDictionary(pair => pair.Key, pair => (HexCell?)pair.Value.WithRevealed(reveal));
        if (HexPaintCommand.Create(reveal ? "Открыть все гексы" : "Скрыть все гексы", layer, changes) is not { } command) return;
        Session.Execute(command);
        AfterEdit();
        Raise(nameof(CanUndo));
        StatusDetail = reveal ? $"Открыто гексов: {command.HexCount}." : $"Скрыто гексов: {command.HexCount}.";
    }

    /// <summary>Opens the players' window: opaque fog, only what the players may read.</summary>
    public void OpenHexPlayerView() => HexPlayerViewRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ paths

    public HexPathKind HexPathKind
    {
        get => _hexPathKind;
        set
        {
            if (_hexPathKind == value) return;
            _hexPathKind = value;
            _hexRouteStyle = HexPath.DefaultStyle(value);
            _hexPathErase = false;
            if (_hexMode != HexToolMode.Path) HexMode = HexToolMode.Path;
            Raise(nameof(HexPathKind));
            Raise(nameof(HexRouteStyle));
            Raise(nameof(HexPathErase));
        }
    }

    /// <summary>Through hex centres, winding between edge middles, or along hex edges.</summary>
    public HexRouteStyle HexRouteStyle
    {
        get => _hexRouteStyle;
        set
        {
            if (_hexRouteStyle == value) return;
            _hexRouteStyle = value;
            Raise(nameof(HexRouteStyle));
        }
    }

    /// <summary>A drag only marks where the path starts and ends; the way between follows the land.</summary>
    public bool HexPathAuto
    {
        get => _hexPathAuto;
        set
        {
            if (_hexPathAuto == value) return;
            _hexPathAuto = value;
            Raise(nameof(HexPathAuto));
        }
    }

    /// <summary>A click removes the path under the pointer instead of drawing.</summary>
    public bool HexPathErase
    {
        get => _hexPathErase;
        set
        {
            if (_hexPathErase == value) return;
            _hexPathErase = value;
            Raise(nameof(HexPathErase));
        }
    }

    public IReadOnlyList<HexPath> HexPaths => ActiveHexLayer?.Paths ?? [];

    private bool BeginHexPath(HexLayer layer, double worldX, double worldY)
    {
        if (_hexPathErase)
        {
            RemoveHexPathAt(layer, worldX, worldY);
            return false;
        }

        if (_hexRouteStyle == HexRouteStyle.Edges)
        {
            _hexPathVertices = [layer.Board.Layout.NearestVertex(worldX, worldY)];
        }
        else
        {
            var hex = layer.Board.Layout.HexAt(worldX, worldY);
            if (!layer.Board.Contains(hex))
            {
                StatusDetail = "Путь начинается на поле.";
                return false;
            }

            _hexPathHexes = [hex];
        }

        Raise(nameof(HexOverlay));
        return true;
    }

    private void ContinueHexPath(double worldX, double worldY)
    {
        if (ActiveHexLayer is not { } layer) return;
        if (_hexPathVertices is { } vertices)
        {
            var next = layer.Board.Layout.NearestVertex(worldX, worldY);
            if (next == vertices[^1]) return;
            foreach (var vertex in Map.Core.Editing.HexPaths.EdgeWalk(layer.Board.Layout, vertices[^1], next).Skip(1))
            {
                // Dragging back along the line takes the last corners away again.
                if (vertices.Count >= 2 && vertices[^2] == vertex) vertices.RemoveAt(vertices.Count - 1);
                else vertices.Add(vertex);
            }
        }
        else if (_hexPathHexes is { } hexes)
        {
            var hex = layer.Board.Layout.HexAt(worldX, worldY);
            if (!layer.Board.Contains(hex) || hex == hexes[^1]) return;
            if (_hexPathAuto)
            {
                // Auto: keep only the ends; the route is found on release.
                if (hexes.Count > 1) hexes.RemoveRange(1, hexes.Count - 1);
                hexes.Add(hex);
            }
            else
            {
                var walked = Map.Core.Editing.HexPaths.Walk([.. hexes, hex]);
                hexes.Clear();
                hexes.AddRange(walked);
            }
        }

        Raise(nameof(HexOverlay));
    }

    private HexPath? PathDraft(HexLayer layer)
    {
        try
        {
            if (_hexPathVertices is { Count: >= 1 } vertices)
            {
                return new HexPath(HexPathId.New(), _hexPathKind, HexRouteStyle.Edges, [], [.. vertices]);
            }

            if (_hexPathHexes is { Count: >= 1 } hexes)
            {
                var run = _hexPathAuto && hexes.Count == 2 ? hexes : Map.Core.Editing.HexPaths.Walk(hexes);
                return new HexPath(HexPathId.New(), _hexPathKind, _hexRouteStyle == HexRouteStyle.Edges ? HexRouteStyle.Centers : _hexRouteStyle,
                    [.. _hexPathAuto && hexes.Count == 2 ? Map.Core.Editing.HexPaths.Walk(run) : run], []);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        _ = layer;
        return null;
    }

    private void EndHexPath()
    {
        if (ActiveHexLayer is not { } layer)
        {
            CancelHexWorkshopGesture();
            return;
        }

        HexPath? path = null;
        if (_hexPathVertices is { Count: >= 2 } vertices)
        {
            path = new HexPath(HexPathId.New(), _hexPathKind, HexRouteStyle.Edges, [], [.. vertices]);
        }
        else if (_hexPathHexes is { Count: >= 2 } hexes)
        {
            var run = hexes;
            if (_hexPathAuto)
            {
                var planner = new HexTravelPlanner(layer, TravelCostOf);
                // Rivers run anywhere water could; roads keep to land a party can walk.
                run = _hexPathKind == HexPathKind.River
                    ? HexRouting.FindRoute(hexes[0], hexes[^1], (_, next) => layer.Board.Contains(next) ? 1 : null) ?? hexes
                    : planner.Route(hexes[0], hexes[^1])?.Hexes.ToList() ?? Map.Core.Editing.HexPaths.Walk(hexes);
            }

            var style = _hexRouteStyle == HexRouteStyle.Edges ? HexRouteStyle.Centers : _hexRouteStyle;
            path = new HexPath(HexPathId.New(), _hexPathKind, style, [.. run], [], seed: Random.Shared.Next(1000));
        }

        CancelHexWorkshopGesture();
        if (path is null)
        {
            StatusDetail = "Протяните путь хотя бы через два гекса.";
            return;
        }

        ExecuteHexLayerEdit(PathKindName(path.Kind) + ": новый путь", layer, layer with { Paths = layer.Paths.Add(path) });
        Raise(nameof(HexPaths));
        StatusDetail = $"{PathKindName(path.Kind)}: {path.NodeCount} {(path.Style == HexRouteStyle.Edges ? "углов" : "гексов")}."
            + (path.Kind == HexPathKind.Road && layer.Paths.Any(p => p.Kind == HexPathKind.River && p.Hexes.Intersect(path.Hexes).Any())
                ? " Где дорога пересекает реку, стоит мост." : "");
    }

    /// <summary>Removes the path drawn nearest the point (within half a hex).</summary>
    private void RemoveHexPathAt(HexLayer layer, double worldX, double worldY)
    {
        HexPath? best = null;
        var bestDistance = layer.Board.Layout.Size * 0.6;
        foreach (var path in layer.Paths)
        {
            var points = path.Style == HexRouteStyle.Edges
                ? path.Vertices.Select(layer.Board.Layout.Position)
                : path.Hexes.Select(layer.Board.Layout.Center);
            foreach (var (x, y) in points)
            {
                var distance = Math.Sqrt((x - worldX) * (x - worldX) + (y - worldY) * (y - worldY));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = path;
                }
            }
        }

        if (best is null)
        {
            StatusDetail = "Здесь нет пути.";
            return;
        }

        RemoveHexPath(best.Id);
    }

    public void RemoveHexPath(HexPathId id)
    {
        if (ActiveHexLayer is not { } layer || layer.Path(id) is not { } path) return;
        ExecuteHexLayerEdit("Удалить путь", layer, layer with { Paths = layer.Paths.Remove(path) });
        Raise(nameof(HexPaths));
        StatusDetail = $"{PathKindName(path.Kind)} удалён(а).";
    }

    public static string PathKindName(HexPathKind kind) => kind switch
    {
        HexPathKind.River => "Река",
        HexPathKind.Road => "Дорога",
        HexPathKind.Trail => "Тропа",
        _ => "Граница",
    };

    private static double? TravelCostOf(string tileId) => HexStarterTiles.Find(tileId) is { } tile ? tile.TravelCost : 1;

    // ------------------------------------------------------------------ hex key

    /// <summary>The hex whose key is open in the panel.</summary>
    public HexCoord? HexKeyHex => _hexKeyHex;

    /// <summary>The key of the open hex (an empty one when it has none yet).</summary>
    public HexNote? HexKeyNote => _hexKeyHex is { } hex && ActiveHexLayer is { } layer
        ? layer.Notes.GetValueOrDefault(hex) ?? new HexNote("")
        : null;

    /// <summary>The printed number of the open hex ("0304").</summary>
    public string HexKeyNumber => _hexKeyHex is { } hex && ActiveHexLayer is { } layer
        ? HexLayerRenderer.NumberOf(layer.Board, layer.Board.Layout.ToOffset(hex))
        : "";

    public void OpenHexKey(HexCoord hex)
    {
        if (ActiveHexLayer is not { } layer || !layer.Board.Contains(hex)) return;
        _hexKeyHex = hex;
        if (_hexMode != HexToolMode.Key) HexMode = HexToolMode.Key;
        Raise(nameof(HexKeyHex));
        Raise(nameof(HexKeyNote));
        Raise(nameof(HexKeyNumber));
        Raise(nameof(HexOverlay));
        StatusDetail = $"Ключ гекса {HexKeyNumber}.";
    }

    /// <summary>Writes the key of the open hex; an empty key removes it. One undo step per change.</summary>
    public void SetHexKey(HexNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (_hexKeyHex is not { } hex || ActiveHexLayer is not { } layer) return;
        var before = layer.Notes.GetValueOrDefault(hex);
        if (before == note || (before is null && note.IsEmpty)) return;
        var notes = note.IsEmpty ? layer.Notes.Remove(hex) : layer.Notes.SetItem(hex, note);
        ExecuteHexLayerEdit(note.IsEmpty ? "Удалить ключ гекса" : "Ключ гекса", layer, layer with { Notes = notes });
        Raise(nameof(HexKeyNote));
        Raise(nameof(HexKeys));
    }

    /// <summary>Every key of the layer, in printed-number order: the hex key table.</summary>
    public IReadOnlyList<HexKeyRow> HexKeys => ActiveHexLayer is { } layer
        ? [.. layer.Notes
            .Select(pair => (Offset: layer.Board.Layout.ToOffset(pair.Key), pair.Key, pair.Value))
            .OrderBy(item => item.Offset.Column).ThenBy(item => item.Offset.Row)
            .Select(item => new HexKeyRow(item.Key, HexLayerRenderer.NumberOf(layer.Board, item.Offset), item.Value))]
        : [];

    /// <summary>Opens a key and brings its hex to the middle of the view.</summary>
    public void GoToHexKey(HexCoord hex)
    {
        if (ActiveHexLayer is not { } layer) return;
        OpenHexKey(hex);
        var (x, y) = layer.Board.Layout.Center(hex);
        var viewport = Session.Viewport;
        viewport.Set(x - viewport.ViewWidth / (2 * viewport.Scale), y - viewport.ViewHeight / (2 * viewport.Scale), viewport.Scale);
        Raise(nameof(ViewportStatus));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the window for the child map of the open hex (made on first use).</summary>
    public void OpenHexChildMap()
    {
        if (_hexKeyHex is { } hex) HexChildMapRequested?.Invoke(this, hex);
    }

    /// <summary>Remembers the child map file of a hex in its key (one undo step).</summary>
    public void LinkHexChildMap(HexCoord hex, string fileName)
    {
        if (ActiveHexLayer is not { } layer) return;
        var note = layer.Notes.GetValueOrDefault(hex) ?? new HexNote("");
        _hexKeyHex = hex;
        SetHexKey(note with { ChildMap = fileName });
    }

    /// <summary>The key table as Markdown: numbers, titles, categories, both texts (or the players' part only).</summary>
    public string HexKeyMarkdown(bool playersOnly)
    {
        if (ActiveHexLayer is not { } layer) return "";
        var text = new System.Text.StringBuilder();
        text.AppendLine($"# {Session.Document.Title} — ключ гексов");
        text.AppendLine();
        foreach (var row in HexKeys)
        {
            if (playersOnly && !HexLayerRenderer.PlayersSee(layer, row.Hex, row.Note)) continue;
            text.AppendLine($"## {row.Number}{(row.Note.Title.Length > 0 ? " — " + row.Note.Title : "")}");
            text.AppendLine($"*{HexNoteCategoryName(row.Note.Category)}*");
            text.AppendLine();
            if (!playersOnly && row.Note.GmText.Length > 0)
            {
                text.AppendLine("**Мастеру:** " + row.Note.GmText.Trim());
                text.AppendLine();
            }

            if (row.Note.PlayerText.Length > 0)
            {
                text.AppendLine((playersOnly ? "" : "**Игрокам:** ") + row.Note.PlayerText.Trim());
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    public static string HexNoteCategoryName(HexNoteCategory category) => category switch
    {
        HexNoteCategory.Encounter => "Встреча",
        HexNoteCategory.Mystery => "Тайна",
        HexNoteCategory.Landmark => "Ориентир",
        HexNoteCategory.Settlement => "Поселение",
        HexNoteCategory.Hook => "Зацепка",
        HexNoteCategory.Danger => "Опасность",
        HexNoteCategory.Treasure => "Сокровище",
        _ => "Прочее",
    };

    // ------------------------------------------------------------------ travel ruler

    public HexTravelScale HexTravel => ActiveHexLayer?.Travel ?? HexTravelScale.Default;

    /// <summary>The last measured way, if any.</summary>
    public HexRoute? HexRoute => _hexRoute;

    public void SetHexTravel(double distancePerHex, string unit, double distancePerDay)
    {
        if (ActiveHexLayer is not { } layer) return;
        HexTravelScale scale;
        try
        {
            scale = new HexTravelScale(distancePerHex, unit, distancePerDay);
        }
        catch (ArgumentException)
        {
            StatusDetail = "Расстояние на гекс и за день — числа больше нуля.";
            return;
        }

        if (scale == layer.Travel) return;
        ExecuteHexLayerEdit("Масштаб пути", layer, layer with { Travel = scale });
        Raise(nameof(HexTravel));
        if (_hexRoute is not null && _hexMeasureStart is { } start) Measure(layer, start, _hexRoute.Hexes[^1]);
    }

    private bool BeginHexMeasure(HexLayer layer, HexCoord hex)
    {
        _hexMeasureStart = hex;
        _hexRoute = new HexRoute([hex], 0, 0, 0, layer.Travel.Unit);
        Raise(nameof(HexOverlay));
        return true;
    }

    private void ContinueHexMeasure(double worldX, double worldY)
    {
        if (_hexMeasureStart is not { } start || ActiveHexLayer is not { } layer) return;
        var hex = layer.Board.Layout.HexAt(worldX, worldY);
        if (!layer.Board.Contains(hex) || _hexRoute?.Hexes[^1] == hex) return;
        Measure(layer, start, hex);
    }

    private void Measure(HexLayer layer, HexCoord start, HexCoord end)
    {
        _hexRoute = new HexTravelPlanner(layer, TravelCostOf).Route(start, end);
        StatusDetail = _hexRoute is { } route ? "Путь: " + route.Label : "Сюда пешком не пройти.";
        Raise(nameof(HexRoute));
        Raise(nameof(HexOverlay));
    }

    private void EndHexMeasure() => Raise(nameof(HexRoute));

    // ------------------------------------------------------------------ selection, clipboard

    public IReadOnlyCollection<HexCoord> HexSelection => _hexSelection;

    /// <summary>A click selects the connected hexes of the same terrain (magic wand).</summary>
    public bool HexSelectWand
    {
        get => _hexSelectWand;
        set
        {
            if (_hexSelectWand == value) return;
            _hexSelectWand = value;
            Raise(nameof(HexSelectWand));
        }
    }

    /// <summary>True while a copy follows the pointer, waiting for a click to put it down.</summary>
    public bool HasHexPaste => _hexPaste is not null;

    public bool HasHexClipboard => s_hexClipboard is not null;

    private bool BeginHexSelect(HexLayer layer, HexCoord hex, bool additive)
    {
        if (_hexSelectWand)
        {
            if (!additive) _hexSelection.Clear();
            _hexSelection.UnionWith(Map.Core.Editing.HexSelection.SameTerrain(layer.Board, hex));
            RaiseHexSelection();
            return false;
        }

        _hexSelectBase = additive ? [.. _hexSelection] : [];
        _hexSelectStart = hex;
        _hexSelection.Clear();
        _hexSelection.UnionWith(_hexSelectBase);
        _hexSelection.Add(hex);
        RaiseHexSelection();
        return true;
    }

    private void ContinueHexSelect(double worldX, double worldY)
    {
        if (_hexSelectStart is not { } start || ActiveHexLayer is not { } layer) return;
        var hex = layer.Board.Layout.HexAt(worldX, worldY);
        _hexSelection.Clear();
        if (_hexSelectBase is { } kept) _hexSelection.UnionWith(kept);
        _hexSelection.UnionWith(Map.Core.Editing.HexSelection.Rectangle(layer.Board, start, hex));
        RaiseHexSelection();
    }

    private void EndHexSelect()
    {
        _hexSelectStart = null;
        _hexSelectBase = null;
        RaiseHexSelection();
    }

    private void RaiseHexSelection()
    {
        Raise(nameof(HexSelection));
        Raise(nameof(HexSelectionSummary));
        Raise(nameof(HexOverlay));
    }

    public string HexSelectionSummary => _hexSelection.Count == 0
        ? "Ничего не выделено."
        : $"Выделено гексов: {_hexSelection.Count}.";

    public void SelectAllHexes()
    {
        if (ActiveHexLayer is not { } layer) return;
        _hexSelection.Clear();
        foreach (var (hex, _) in layer.Board.Cells) _hexSelection.Add(hex);
        HexMode = HexToolMode.Select;
        RaiseHexSelection();
    }

    public void ClearHexSelection()
    {
        if (_hexSelection.Count == 0) return;
        _hexSelection.Clear();
        RaiseHexSelection();
    }

    /// <summary>The anchor a copy is held by: the selected hex nearest the middle of the selection.</summary>
    private HexCoord SelectionAnchor(HexLayer layer)
    {
        var points = _hexSelection.Select(layer.Board.Layout.Center).ToList();
        var x = points.Average(p => p.X);
        var y = points.Average(p => p.Y);
        return _hexSelection.MinBy(hex =>
        {
            var (cx, cy) = layer.Board.Layout.Center(hex);
            return (cx - x) * (cx - x) + (cy - y) * (cy - y);
        });
    }

    public void CopyHexSelection()
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0)
        {
            StatusDetail = "Выделите гексы, чтобы скопировать.";
            return;
        }

        s_hexClipboard = HexClip.Capture(layer, _hexSelection, SelectionAnchor(layer));
        Raise(nameof(HasHexClipboard));
        StatusDetail = $"Скопировано гексов: {s_hexClipboard.Count}. Ctrl+V — вставить, [ и ] — повернуть.";
    }

    public void CutHexSelection()
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0) return;
        CopyHexSelection();
        DeleteHexSelection();
    }

    /// <summary>Picks the selection up: it follows the pointer and a click puts it down elsewhere (one undo step).</summary>
    public void MoveHexSelection()
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0) return;
        _hexPaste = HexClip.Capture(layer, _hexSelection, SelectionAnchor(layer));
        _hexPasteMoves = true;
        HexMode = HexToolMode.Select;
        Raise(nameof(HasHexPaste));
        Raise(nameof(HexOverlay));
        StatusDetail = "Щёлкните, куда перенести выделенные гексы. Esc — отмена.";
    }

    /// <summary>Arms the clipboard: the copy follows the pointer until a click puts it down.</summary>
    public void BeginHexPaste()
    {
        if (s_hexClipboard is null)
        {
            StatusDetail = "Буфер пуст: сначала скопируйте гексы.";
            return;
        }

        _hexPaste = s_hexClipboard;
        _hexPasteMoves = false;
        if (_hexMode != HexToolMode.Select)
        {
            _hexMode = HexToolMode.Select;
            Raise(nameof(HexMode));
        }

        if (ActiveTool != EditorTool.Hex) ActiveTool = EditorTool.Hex;
        if (_hexHover is { } hover && ActiveHexLayer is { } layer) _hexPasteAt = layer.Board.Layout.HexAt(hover.X, hover.Y);
        Raise(nameof(HasHexPaste));
        Raise(nameof(HexOverlay));
        StatusDetail = "Щёлкните, куда вставить. [ и ] — повернуть, M — отразить, Esc — отмена.";
    }

    public void RotateHexPaste(int steps)
    {
        if (_hexPaste is null) return;
        _hexPaste = _hexPaste.Rotated(steps);
        Raise(nameof(HexOverlay));
    }

    public void MirrorHexPaste()
    {
        if (_hexPaste is null) return;
        _hexPaste = _hexPaste.Mirrored();
        Raise(nameof(HexOverlay));
    }

    public bool CancelHexPaste()
    {
        if (_hexPaste is null) return false;
        _hexPaste = null;
        _hexPasteAt = null;
        _hexPasteMoves = false;
        Raise(nameof(HasHexPaste));
        Raise(nameof(HexOverlay));
        return true;
    }

    private void CommitHexPaste(HexLayer layer, HexCoord at)
    {
        if (_hexPaste is not { } paste) return;
        var source = layer;
        if (_hexPasteMoves)
        {
            // A move empties the old place first, in the same history entry.
            var cleared = _hexSelection.Select(hex => new KeyValuePair<HexCoord, HexCell?>(hex, null)).ToList();
            source = layer with
            {
                Board = layer.Board.With(cleared),
                Notes = layer.Notes.RemoveRange(_hexSelection),
                Paths = layer.Paths.RemoveAll(path => paste.Paths.Any(copy => copy.Id == path.Id)),
            };
        }

        var after = paste.PasteInto(source, at);
        ExecuteHexLayerEdit(_hexPasteMoves ? "Перенести гексы" : "Вставить гексы", layer, after,
            EditCost.BaseBytes + (long)paste.Count * 96);
        _hexSelection.Clear();
        foreach (var hex in paste.Cells.Keys)
        {
            if (layer.Board.Contains(hex + at)) _hexSelection.Add(hex + at);
        }

        var moved = _hexPasteMoves;
        CancelHexPaste();
        RaiseHexSelection();
        StatusDetail = moved ? $"Перенесено гексов: {_hexSelection.Count}." : $"Вставлено гексов: {_hexSelection.Count}. Щёлкните ещё раз — Ctrl+V.";
    }

    /// <summary>Clears the selected hexes (terrain, features, keys and the paths inside).</summary>
    public void DeleteHexSelection()
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0) return;
        var inside = _hexSelection;
        var after = layer with
        {
            Board = layer.Board.With(inside.Select(hex => new KeyValuePair<HexCoord, HexCell?>(hex, null))),
            Notes = layer.Notes.RemoveRange(inside),
            Paths = layer.Paths.RemoveAll(path => path.Style == HexRouteStyle.Edges
                ? path.Vertices.All(v => inside.Contains(v.Hex))
                : path.Hexes.All(inside.Contains)),
        };
        ExecuteHexLayerEdit("Очистить гексы", layer, after, EditCost.BaseBytes + (long)inside.Count * 64);
        StatusDetail = $"Очищено гексов: {inside.Count}.";
    }

    /// <summary>Puts the brush's tile on every selected hex.</summary>
    public void FillHexSelection()
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0) return;
        var choice = _hexMode is HexToolMode.Region or HexToolMode.Fog ? HexChoice
            : new HexTileChoice(HexTile.Id, HexTile.VariantCount, HexTile.Target, _hexRandomVariant, _hexFixedVariant, _hexSeed)
            {
                Mix = [.. _hexMix], Rotation = _hexRotation, RandomRotation = _hexRandomRotation, Rotatable = HexTile.Rotatable,
            };
        var changes = _hexSelection.ToDictionary(hex => hex, hex => HexEditing.Place(layer.Board[hex], hex, choice));
        if (HexPaintCommand.Create("Залить выделение", layer, changes) is not { } command) return;
        Session.Execute(command);
        AfterEdit();
        Raise(nameof(CanUndo));
        StatusDetail = $"Залито гексов: {command.HexCount}.";
    }

    /// <summary>Turns the selected hexes in place around their middle hex (60° steps).</summary>
    public void RotateHexSelection(int steps)
    {
        if (ActiveHexLayer is not { } layer || _hexSelection.Count == 0) return;
        var anchor = SelectionAnchor(layer);
        var clip = HexClip.Capture(layer, _hexSelection, anchor).Rotated(steps);
        var cleared = layer with
        {
            Board = layer.Board.With(_hexSelection.Select(hex => new KeyValuePair<HexCoord, HexCell?>(hex, null))),
            Notes = layer.Notes.RemoveRange(_hexSelection),
        };
        var after = clip.PasteInto(cleared with { Paths = cleared.Paths.RemoveAll(path => clip.Paths.Any(c => c.Id == path.Id)) }, anchor);
        ExecuteHexLayerEdit("Повернуть гексы", layer, after);
        _hexSelection.Clear();
        foreach (var hex in clip.Cells.Keys)
        {
            if (layer.Board.Contains(hex + anchor)) _hexSelection.Add(hex + anchor);
        }

        RaiseHexSelection();
    }

    // ------------------------------------------------------------------ board rows and columns

    /// <summary>Inserts (or with a negative count removes) columns before the first selected or hovered column.</summary>
    public void InsertHexColumns(int count) => ReshapeHexBoard(count > 0 ? "Вставить колонки" : "Удалить колонки",
        (layer, at) => HexBoardOps.InsertColumns(layer, at.Column, count));

    /// <summary>Inserts (or with a negative count removes) rows before the first selected or hovered row.</summary>
    public void InsertHexRows(int count) => ReshapeHexBoard(count > 0 ? "Вставить ряды" : "Удалить ряды",
        (layer, at) => HexBoardOps.InsertRows(layer, at.Row, count));

    /// <summary>Grows or cuts the board at its right and bottom edges.</summary>
    public void ResizeHexBoard(int columns, int rows) => ReshapeHexBoard("Размер поля",
        (layer, _) => HexBoardOps.Resize(layer, columns, rows));

    private void ReshapeHexBoard(string name, Func<HexLayer, HexOffset, HexLayer> reshape)
    {
        if (ActiveHexLayer is not { } layer) return;
        var at = _hexSelection.Count > 0
            ? new HexOffset(_hexSelection.Min(hex => layer.Board.Layout.ToOffset(hex).Column), _hexSelection.Min(hex => layer.Board.Layout.ToOffset(hex).Row))
            : _hexHover is { } hover ? layer.Board.Layout.ToOffset(layer.Board.Layout.HexAt(hover.X, hover.Y)) : new HexOffset(0, 0);
        at = new HexOffset(Math.Clamp(at.Column, 0, layer.Board.Columns), Math.Clamp(at.Row, 0, layer.Board.Rows));
        if (HexBoardShapeCommand.Create(name, Session.Document, layer, l => reshape(l, at)) is not { } command)
        {
            StatusDetail = "Поле уже такое.";
            return;
        }

        Session.Execute(command);
        _hexSelection.Clear();
        AfterEdit();
        RaiseHexSelection();
        Raise(nameof(CanUndo));
        Raise(nameof(HexBoardSummary));
        var board = ActiveHexLayer?.Board;
        StatusDetail = board is null ? "" : $"Поле: {board.Columns} × {board.Rows} гексов.";
    }

    // ------------------------------------------------------------------ levels

    /// <summary>The levels of the world on this map: 0 is the surface, -1 the first level under it.</summary>
    public IReadOnlyList<int> HexLevels =>
        [.. Session.Document.Layers.OfType<HexLayer>().Select(layer => layer.Level).Append(0).Distinct().OrderByDescending(level => level)];

    /// <summary>The level being edited.</summary>
    public int HexLevel
    {
        get => _hexLevel;
        set
        {
            if (_hexLevel == value || !HexLevels.Contains(value)) return;
            CancelHexGesture();
            CancelHexPaste();
            _hexLevel = value;
            _hexSelection.Clear();
            _hexKeyHex = null;
            if (ActiveHexLayer is { } layer) SelectLayer(layer.Id);
            Raise(nameof(HexLevel));
            Raise(nameof(HexBoardSummary));
            Raise(nameof(HexRegions));
            Raise(nameof(HexKeys));
            Raise(nameof(HexOverlay));
            SceneChanged?.Invoke(this, EventArgs.Empty);
            StatusDetail = $"Уровень: {HexLevelName(value)}.";
        }
    }

    /// <summary>Shows the surface faint under an underground level, to line up entrances.</summary>
    public bool HexGhostSurface
    {
        get => _hexGhostSurface;
        set
        {
            if (_hexGhostSurface == value) return;
            _hexGhostSurface = value;
            Raise(nameof(HexGhostSurface));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The level the canvas shows, or null when the map has only the surface.</summary>
    public int? HexLevelShown => IsHexWorkshop && HexLevels.Count > 1 ? _hexLevel : null;

    /// <summary>The level shown faint under the edited one.</summary>
    public int? HexGhostLevel => HexLevelShown is { } shown && shown != 0 && _hexGhostSurface ? 0 : null;

    public static string HexLevelName(int level) => level switch
    {
        0 => "Поверхность",
        < 0 => $"Подземелье {-level}",
        _ => $"Небо {level}",
    };

    /// <summary>A new level under the deepest one, laid out on the same grid as the surface.</summary>
    public void AddHexLevel(bool below = true)
    {
        var surface = Session.Document.Layers.OfType<HexLayer>().FirstOrDefault(layer => layer.Level == 0)
            ?? Session.Document.Layers.OfType<HexLayer>().FirstOrDefault();
        if (surface is null) return;
        var levels = HexLevels;
        var level = below ? Math.Min(-1, levels.Min() - 1) : Math.Max(1, levels.Max() + 1);
        var layer = new HexLayer(LayerId.New(), HexLevelName(level), true, false, 1,
            HexBoard.FromCells(surface.Board.Layout, surface.Board.Columns, surface.Board.Rows, []))
        {
            Grid = surface.Grid,
            Level = level,
            Travel = surface.Travel,
        };
        Session.Execute(new AddLayerCommand(layer, surface.Id));
        AfterEdit();
        Raise(nameof(HexLevels));
        HexLevel = level;
        SelectLayer(layer.Id);
        StatusDetail = $"Добавлен уровень «{HexLevelName(level)}» на той же сетке: вход в пещеру на поверхности стоит ровно над своим гексом внизу.";
    }

    // ------------------------------------------------------------------ helpers

    private void ExecuteHexLayerEdit(string name, HexLayer before, HexLayer after, long? workingBytes = null)
    {
        if (ReferenceEquals(before, after)) return;
        Session.Execute(new HexLayerEditCommand(name, before, after, workingBytes));
        AfterEdit();
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
        Raise(nameof(HexOverlay));
    }
}
