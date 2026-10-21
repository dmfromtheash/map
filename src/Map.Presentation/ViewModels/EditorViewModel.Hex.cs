using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>What a press of the hex tool does.</summary>
public enum HexToolMode
{
    /// <summary>Puts the chosen tile on the hexes under the footprint; a drag lays a line of them.</summary>
    Place,

    /// <summary>Puts the chosen tile on the whole connected region of the hex that was pressed.</summary>
    Fill,

    /// <summary>Takes the top of each hex away: the feature first, then the terrain.</summary>
    Erase,

    /// <summary>Takes the tile of the pressed hex as the chosen tile.</summary>
    Pick,

    /// <summary>Paints hexes into the chosen region (a kingdom, a province); frontiers follow.</summary>
    Region,

    /// <summary>Reveals hexes to the players, or covers them again.</summary>
    Fog,

    /// <summary>Draws a river, road, trail or border through the hexes.</summary>
    Path,

    /// <summary>Opens the key of a hex: title, notes for the game master and for the players.</summary>
    Key,

    /// <summary>Measures the quickest way between two hexes in hexes, distance and days.</summary>
    Measure,

    /// <summary>Selects hexes to copy, move, turn, fill or clear.</summary>
    Select,
}

/// <summary>
/// The hex workshop: the tool of the hex category. A map of this category is built only from
/// ready painted hexes - no brush strokes - so the creative part is choosing tiles and putting
/// them on hexes, and then everything that lives on a hex map: regions, rivers and roads, the
/// hex key, the fog of war, travel times, levels. Everything else (canvas, layers, labels,
/// notes, history, saving, export) is the shared core of the editor.
/// </summary>
public sealed partial class EditorViewModel
{
    private string _hexTileId = HexStarterTiles.Prefix + "grass";
    private HexToolMode _hexMode = HexToolMode.Place;
    private int _hexRadius;
    private bool _hexRandomVariant = true;
    private int _hexFixedVariant;
    private int _hexSeed;
    private int _hexRotation;
    private bool _hexRandomRotation;
    private readonly List<HexMixEntry> _hexMix = [];
    private HexCursorPreview? _hexCursor;
    private (double X, double Y)? _hexHover;

    // The running gesture: the layer it writes, the last hex the pointer was on, every hex it
    // already wrote and how many history entries it made, folded into one on release.
    private LayerId? _hexGestureLayer;
    private HexCoord? _hexGestureLast;
    private readonly HashSet<HexCoord> _hexGestureTouched = [];
    private int _hexGestureEntries;

    /// <summary>True when this map belongs to the hex constructor.</summary>
    public bool IsHexWorkshop => MapCategories.IsHex(Session.Document.Category);

    /// <summary>The tiles the palette offers, in palette order.</summary>
    public IReadOnlyList<HexTileDefinition> HexTiles => HexStarterTiles.All;

    /// <summary>The tile the hex tool puts down.</summary>
    public string HexTileId
    {
        get => _hexTileId;
        set
        {
            if (HexStarterTiles.Find(value) is null || _hexTileId == value) return;
            _hexTileId = value;
            _hexFixedVariant = 0;
            if (_hexMode is not (HexToolMode.Place or HexToolMode.Fill)) _hexMode = HexToolMode.Place;
            // Choosing one tile ends a mix unless the tile belongs to it.
            if (_hexMix.Count > 0 && _hexMix.All(entry => entry.TileId != value)) _hexMix.Clear();
            Raise(nameof(HexTileId));
            Raise(nameof(HexTile));
            Raise(nameof(HexMode));
            Raise(nameof(HexFixedVariant));
            Raise(nameof(HexMix));
            RefreshHexCursor();
        }
    }

    public HexTileDefinition HexTile => HexStarterTiles.Find(_hexTileId) ?? HexStarterTiles.All[0];

    public HexToolMode HexMode
    {
        get => _hexMode;
        set
        {
            if (_hexMode == value) return;
            CancelHexGesture();
            if (value != HexToolMode.Select) CancelHexPaste();
            if (value != HexToolMode.Measure) _hexRoute = null;
            _hexMode = value;
            Raise(nameof(HexMode));
            Raise(nameof(HexOverlay));
            RefreshHexCursor();
        }
    }

    /// <summary>Footprint radius in hexes: 0 is one hex, 1 is seven, 2 is nineteen.</summary>
    public int HexRadius
    {
        get => _hexRadius;
        set
        {
            value = Math.Clamp(value, 0, HexEditing.MaxRadius);
            if (_hexRadius == value) return;
            _hexRadius = value;
            Raise(nameof(HexRadius));
            Raise(nameof(HexFootprintText));
            RefreshHexCursor();
        }
    }

    /// <summary>How many hexes the footprint covers, said plainly: "1 гекс", "7 гексов".</summary>
    public string HexFootprintText
    {
        get
        {
            var count = 3 * _hexRadius * (_hexRadius + 1) + 1;
            return count == 1 ? "1 гекс" : $"{count} гексов";
        }
    }

    /// <summary>Every hex gets its own picture of the tile, so a forest never repeats one tree.</summary>
    public bool HexRandomVariant
    {
        get => _hexRandomVariant;
        set
        {
            if (_hexRandomVariant == value) return;
            _hexRandomVariant = value;
            Raise(nameof(HexRandomVariant));
        }
    }

    /// <summary>The picture used when <see cref="HexRandomVariant"/> is off.</summary>
    public int HexFixedVariant
    {
        get => _hexFixedVariant;
        set
        {
            value = Math.Clamp(value, 0, HexTile.VariantCount - 1);
            if (_hexFixedVariant == value) return;
            _hexFixedVariant = value;
            Raise(nameof(HexFixedVariant));
        }
    }

    /// <summary>The turn (60° steps clockwise) the next terrain hexes get; tiles that cannot turn ignore it.</summary>
    public int HexRotation
    {
        get => _hexRotation;
        set
        {
            value = ((value % 6) + 6) % 6;
            if (_hexRotation == value) return;
            _hexRotation = value;
            Raise(nameof(HexRotation));
        }
    }

    /// <summary>Every hex gets its own turn of the picture.</summary>
    public bool HexRandomRotation
    {
        get => _hexRandomRotation;
        set
        {
            if (_hexRandomRotation == value) return;
            _hexRandomRotation = value;
            Raise(nameof(HexRandomRotation));
        }
    }

    /// <summary>Turns the brush (keys [ and ]); with a paste armed, turns the paste.</summary>
    public void RotateHexBrush(int steps)
    {
        if (_hexPaste is not null)
        {
            RotateHexPaste(steps);
            return;
        }

        HexRotation += steps;
        StatusDetail = HexTile.Rotatable
            ? $"Поворот гекса: {HexRotation * 60}°."
            : $"«{HexTile.Name}» не поворачивается (у тайла есть верх).";
    }

    /// <summary>The tiles of a mixed brush with their weights; empty when the brush is one tile.</summary>
    public IReadOnlyList<HexMixEntry> HexMix => _hexMix;

    /// <summary>
    /// Adds a tile to the mixed brush or takes it out (Ctrl+click in the palette): the brush then
    /// puts one of the mixed tiles on every hex, chosen by weight (Hex Kit multi-select, Hexxmap sampler).
    /// </summary>
    public void ToggleHexMix(string tileId)
    {
        if (HexStarterTiles.Find(tileId) is not { } tile) return;
        if (_hexMix.Count == 0 && HexTile.Target == tile.Target && tile.Id != HexTile.Id)
        {
            _hexMix.Add(new HexMixEntry(HexTile.Id, HexTile.VariantCount));
        }

        var index = _hexMix.FindIndex(entry => entry.TileId == tileId);
        if (index >= 0) _hexMix.RemoveAt(index);
        else if (_hexMix.Count == 0 || HexStarterTiles.Find(_hexMix[0].TileId)?.Target == tile.Target)
        {
            _hexMix.Add(new HexMixEntry(tile.Id, tile.VariantCount));
        }
        else
        {
            StatusDetail = "В смесь идут тайлы одного вида: местность с местностью, надстройки с надстройками.";
            return;
        }

        if (_hexMix.Count == 1)
        {
            _hexTileId = _hexMix[0].TileId;
            _hexMix.Clear();
        }
        else if (_hexMix.Count > 0 && _hexMix.All(entry => entry.TileId != _hexTileId))
        {
            _hexTileId = _hexMix[0].TileId;
        }

        if (_hexMode is not (HexToolMode.Place or HexToolMode.Fill)) _hexMode = HexToolMode.Place;
        StatusDetail = _hexMix.Count == 0
            ? $"Кисть: «{HexTile.Name}»."
            : $"Смесь из {_hexMix.Count} тайлов: каждый гекс получит один из них по весу.";
        Raise(nameof(HexMix));
        Raise(nameof(HexTileId));
        Raise(nameof(HexTile));
        Raise(nameof(HexMode));
    }

    /// <summary>How often a mixed tile comes up relative to the others (0 leaves it out).</summary>
    public void SetHexMixWeight(string tileId, double weight)
    {
        var index = _hexMix.FindIndex(entry => entry.TileId == tileId);
        if (index < 0) return;
        _hexMix[index] = _hexMix[index] with { Weight = Math.Clamp(weight, 0, 100) };
        Raise(nameof(HexMix));
    }

    public void ClearHexMix()
    {
        if (_hexMix.Count == 0) return;
        _hexMix.Clear();
        Raise(nameof(HexMix));
    }

    /// <summary>Deals the random pictures again for the next gesture (the painted hexes keep theirs).</summary>
    public void ShuffleHexVariants()
    {
        _hexSeed++;
        StatusDetail = "Случайные варианты перемешаны для следующих гексов.";
    }

    /// <summary>
    /// The hex layer the tool writes: the active one when it is a hex layer of the shown level,
    /// otherwise the top visible unlocked hex layer of that level.
    /// </summary>
    public HexLayer? ActiveHexLayer =>
        ActiveLayer is HexLayer active && active.Level == _hexLevel ? active
        : Session.Document.Layers.OfType<HexLayer>().LastOrDefault(layer => layer.Visible && !layer.Locked && layer.Level == _hexLevel);

    /// <summary>The footprint under the pointer, outlined on the canvas while the hex tool is active.</summary>
    public HexCursorPreview? HexCursor => _hexCursor;

    /// <summary>What the canvas draws for the hover: <see cref="HexCursor"/> eased by <see cref="StepHexHover"/>.</summary>
    public HexCursorPreview? HexCursorShown => _hexHoverShown ?? _hexCursor;

    private HexTileChoice HexChoice => _hexMode switch
    {
        HexToolMode.Region => new HexTileChoice(HexTile.Id, 1, HexPaintTarget.Region) { Region = _hexRegionErase ? 0 : _hexRegionId },
        HexToolMode.Fog => new HexTileChoice(HexTile.Id, 1, HexPaintTarget.Fog) { Reveal = _hexFogReveal },
        _ => new HexTileChoice(HexTile.Id, HexTile.VariantCount, HexTile.Target, _hexRandomVariant, _hexFixedVariant, _hexSeed)
        {
            Mix = [.. _hexMix],
            Rotation = _hexRotation,
            RandomRotation = _hexRandomRotation,
            // A mix turns only when every tile in it may turn.
            Rotatable = _hexMix.Count == 0 ? HexTile.Rotatable : _hexMix.All(entry => HexStarterTiles.Find(entry.TileId)?.Rotatable == true),
        },
    };

    /// <summary>The pointer moved over the canvas (null when it left); true when something under it changed.</summary>
    public bool UpdateHexHover(double? worldX, double? worldY)
    {
        _hexHover = worldX is { } x && worldY is { } y ? (x, y) : null;
        var before = _hexCursor;
        RefreshHexCursor();
        // The eased outline follows the pointer inside a hex too, so any move over the board redraws.
        var changed = _hexCursor is not null || before is not null;
        if (_hexPaste is not null && _hexHover is { } hover && ActiveHexLayer is { } layer)
        {
            var at = layer.Board.Layout.HexAt(hover.X, hover.Y);
            if (_hexPasteAt != at)
            {
                _hexPasteAt = at;
                changed = true;
            }
        }

        return changed;
    }

    private void RefreshHexCursor()
    {
        if (ActiveTool != EditorTool.Hex || _hexHover is not { } hover || ActiveHexLayer is not { } layer
            || _hexMode is HexToolMode.Path or HexToolMode.Measure or HexToolMode.Select)
        {
            _hexCursor = null;
            return;
        }

        var center = layer.Board.Layout.HexAt(hover.X, hover.Y);
        if (!layer.Board.Contains(center))
        {
            _hexCursor = null;
            return;
        }

        var hexes = _hexMode is HexToolMode.Place or HexToolMode.Erase or HexToolMode.Region or HexToolMode.Fog
            ? HexEditing.Footprint(layer.Board, center, _hexRadius).ToList()
            : [center];
        _hexCursor = new HexCursorPreview(layer.Id, hexes, _hexMode == HexToolMode.Erase
            || (_hexMode == HexToolMode.Region && _hexRegionErase) || (_hexMode == HexToolMode.Fog && !_hexFogReveal));
    }

    /// <summary>True while a hex drag is running.</summary>
    public bool HasHexGesture => _hexGestureLayer is not null;

    /// <summary>A press of the hex tool. Returns true when a drag follows.</summary>
    public bool BeginHexGesture(double worldX, double worldY, bool additive = false)
    {
        CancelHexGesture();
        if (ActiveHexLayer is not { } layer)
        {
            StatusDetail = Session.Document.Layers.OfType<HexLayer>().Any()
                ? "Слой гексов скрыт или закреплён. Откройте его в панели слоёв."
                : "На карте нет слоя гексов.";
            return false;
        }

        var hex = layer.Board.Layout.HexAt(worldX, worldY);
        if (_hexMode == HexToolMode.Select && _hexPaste is not null)
        {
            CommitHexPaste(layer, hex);
            return false;
        }

        if (!layer.Board.Contains(hex) && _hexMode != HexToolMode.Path)
        {
            StatusDetail = "Этот гекс за краем поля.";
            return false;
        }

        switch (_hexMode)
        {
            case HexToolMode.Pick:
                PickHexTile(layer.Board[hex]);
                return false;
            case HexToolMode.Fill:
                FillHexRegion(layer, hex);
                return false;
            case HexToolMode.Key:
                OpenHexKey(hex);
                return false;
            case HexToolMode.Path:
                return BeginHexPath(layer, worldX, worldY);
            case HexToolMode.Measure:
                return BeginHexMeasure(layer, hex);
            case HexToolMode.Select:
                return BeginHexSelect(layer, hex, additive);
        }

        if (_hexMode == HexToolMode.Region && !_hexRegionErase && layer.Region(_hexRegionId) is null)
        {
            StatusDetail = "Сначала создайте регион кнопкой «Новый регион».";
            return false;
        }

        _hexGestureLayer = layer.Id;
        _hexGestureLast = hex;
        _hexGestureTouched.Clear();
        _hexGestureEntries = 0;
        WriteHexFootprints(layer, [hex]);
        return true;
    }

    /// <summary>The drag reached a new point.</summary>
    public void ContinueHexGesture(double worldX, double worldY)
    {
        switch (_hexMode)
        {
            case HexToolMode.Path:
                ContinueHexPath(worldX, worldY);
                return;
            case HexToolMode.Measure:
                ContinueHexMeasure(worldX, worldY);
                return;
            case HexToolMode.Select:
                ContinueHexSelect(worldX, worldY);
                return;
        }

        if (_hexGestureLayer is not { } layerId || Session.Document.FindLayer(layerId) is not HexLayer layer) return;
        var hex = layer.Board.Layout.HexAt(worldX, worldY);
        if (_hexGestureLast == hex) return;
        var line = _hexGestureLast is { } last ? HexCoord.Line(last, hex).Skip(1).ToList() : [hex];
        _hexGestureLast = hex;
        WriteHexFootprints(layer, line);
        _hexHover = (worldX, worldY);
        RefreshHexCursor();
    }

    /// <summary>Release: the whole drag becomes one history entry.</summary>
    public void EndHexGesture()
    {
        switch (_hexMode)
        {
            case HexToolMode.Path:
                EndHexPath();
                return;
            case HexToolMode.Measure:
                EndHexMeasure();
                return;
            case HexToolMode.Select:
                EndHexSelect();
                return;
        }

        if (_hexGestureLayer is null) return;
        var name = HexGestureName;
        if (_hexGestureEntries > 1)
        {
            Session.MergeLastEntries(_hexGestureEntries, name);
        }

        var count = _hexGestureTouched.Count;
        ResetHexGesture();
        StatusDetail = count == 0 ? "" : _hexMode switch
        {
            HexToolMode.Erase => $"Стёрто гексов: {count}.",
            HexToolMode.Region => _hexRegionErase ? $"Из регионов убрано гексов: {count}." : $"В регион «{ActiveHexLayer?.Region(_hexRegionId)?.Name}» вошло гексов: {count}.",
            HexToolMode.Fog => _hexFogReveal ? $"Игрокам открыто гексов: {count}." : $"Снова скрыто гексов: {count}.",
            _ => _hexMix.Count > 0 ? $"Поставлено гексов смеси: {count}." : $"Поставлено гексов «{HexTile.Name}»: {count}.",
        };
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    private string HexGestureName => _hexMode switch
    {
        HexToolMode.Erase => "Стереть гексы",
        HexToolMode.Region => "Регион гексов",
        HexToolMode.Fog => _hexFogReveal ? "Открыть гексы" : "Скрыть гексы",
        _ => "Поставить гексы",
    };

    /// <summary>Abandons a running drag and takes back what it wrote.</summary>
    public bool CancelHexGesture()
    {
        var cancelled = CancelHexWorkshopGesture();
        if (_hexGestureLayer is null) return cancelled;
        for (var i = 0; i < _hexGestureEntries; i++) Session.Undo();
        ResetHexGesture();
        AfterEdit();
        return true;
    }

    private void ResetHexGesture()
    {
        _hexGestureLayer = null;
        _hexGestureLast = null;
        _hexGestureTouched.Clear();
        _hexGestureEntries = 0;
    }

    private void WriteHexFootprints(HexLayer layer, IEnumerable<HexCoord> centers)
    {
        var after = new Dictionary<HexCoord, HexCell?>();
        var choice = HexChoice;
        foreach (var center in centers)
        {
            foreach (var hex in HexEditing.Footprint(layer.Board, center, _hexRadius))
            {
                // A drag writes each hex once: crossing it again must not re-roll its picture
                // or erase the terrain under a feature that was erased a moment ago.
                if (!_hexGestureTouched.Add(hex)) continue;
                var current = layer.Board[hex];
                after[hex] = _hexMode == HexToolMode.Erase
                    ? HexEditing.Erase(current, current?.Feature is null ? HexPaintTarget.Terrain : HexPaintTarget.Feature)
                    : HexEditing.Place(current, hex, choice);
            }
        }

        if (HexPaintCommand.Create(HexGestureName, layer, after) is { } command)
        {
            Session.Execute(command);
            _hexGestureEntries++;
            AfterEdit();
        }
        else if (after.Count > 0 && after.Values.All(cell => cell is null))
        {
            StatusDetail = choice.Target switch
            {
                HexPaintTarget.Feature when _hexMode == HexToolMode.Place => $"«{HexTile.Name}» ставится на местность: сначала поставьте гекс местности.",
                HexPaintTarget.Region or HexPaintTarget.Fog => "Регион и туман относятся к нарисованным гексам: здесь пусто.",
                _ => StatusDetail,
            };
        }
    }

    private void FillHexRegion(HexLayer layer, HexCoord start)
    {
        var region = HexEditing.FloodRegion(layer.Board, start);
        var choice = HexChoice;
        var after = region.ToDictionary(hex => hex, hex => HexEditing.Place(layer.Board[hex], hex, choice));
        if (HexPaintCommand.Create("Залить гексы", layer, after) is not { } command)
        {
            StatusDetail = choice.Target == HexPaintTarget.Feature && layer.Board[start] is null
                ? $"«{HexTile.Name}» ставится на местность: залейте сначала местность."
                : "Область уже такая.";
            return;
        }

        Session.Execute(command);
        AfterEdit();
        StatusDetail = $"Залито гексов «{HexTile.Name}»: {command.HexCount}"
            + (region.Count >= HexEditing.MaxFillHexes ? $" (предел заливки {HexEditing.MaxFillHexes})." : ".");
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    private void PickHexTile(HexCell? cell)
    {
        if (cell is null)
        {
            StatusDetail = "Пустой гекс: брать нечего.";
            return;
        }

        var id = cell.Feature ?? cell.Terrain;
        if (HexStarterTiles.Find(id) is null)
        {
            StatusDetail = "Этого тайла нет в библиотеке гексов этой сборки.";
            return;
        }

        _hexTileId = id;
        _hexMode = HexToolMode.Place;
        _hexMix.Clear();
        _hexRandomVariant = false;
        _hexFixedVariant = cell.Feature is null ? cell.Variant : cell.FeatureVariant;
        if (cell.Feature is null) _hexRotation = cell.Rotation;
        _hexRandomRotation = false;
        Raise(nameof(HexTileId));
        Raise(nameof(HexTile));
        Raise(nameof(HexMode));
        Raise(nameof(HexMix));
        Raise(nameof(HexRandomVariant));
        Raise(nameof(HexFixedVariant));
        Raise(nameof(HexRotation));
        Raise(nameof(HexRandomRotation));
        StatusDetail = $"Взят тайл «{HexTile.Name}», вариант {_hexFixedVariant + 1}.";
    }

    /// <summary>Shows or hides the hex grid of the hex layer the tool works on (undoable).</summary>
    public void SetHexGrid(Func<HexGridStyle, HexGridStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if ((ActiveHexLayer ?? Session.Document.Layers.OfType<HexLayer>().LastOrDefault()) is not { } layer) return;
        var after = change(layer.Grid);
        if (after == layer.Grid) return;
        Session.Execute(new UpdateHexGridCommand(layer.Id, layer.Grid, after));
        AfterEdit();
        Raise(nameof(HexGrid));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    /// <summary>The grid style of the hex layer the tool works on.</summary>
    public HexGridStyle? HexGrid => (ActiveHexLayer ?? Session.Document.Layers.OfType<HexLayer>().LastOrDefault())?.Grid;

    /// <summary>Board facts for the hex panel: size, orientation and how many hexes are painted.</summary>
    public string HexBoardSummary => ActiveHexLayer is { } layer
        ? $"{layer.Board.Columns} × {layer.Board.Rows} гексов · "
          + (layer.Board.Orientation == HexOrientation.Flat ? "плоский верх" : "острый верх")
          + $" · заполнено {layer.Board.CellCount} из {(long)layer.Board.Columns * layer.Board.Rows}"
          + (HexLevels.Count > 1 ? $" · {HexLevelName(layer.Level)}" : "")
        : "Слоя гексов нет";
}
