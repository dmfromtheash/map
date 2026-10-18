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
}

/// <summary>
/// The hex workshop: the tool of the hex category. A map of this category is built only from
/// ready painted hexes - no brush strokes - so the whole creative part is choosing a tile and
/// putting it on hexes. Everything else (canvas, layers, labels, notes, history, saving, export)
/// is the shared core of the editor.
/// </summary>
public sealed partial class EditorViewModel
{
    private string _hexTileId = HexStarterTiles.Prefix + "grass";
    private HexToolMode _hexMode = HexToolMode.Place;
    private int _hexRadius;
    private bool _hexRandomVariant = true;
    private int _hexFixedVariant;
    private int _hexSeed;
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
            if (_hexMode is HexToolMode.Erase or HexToolMode.Pick) _hexMode = HexToolMode.Place;
            Raise(nameof(HexTileId));
            Raise(nameof(HexTile));
            Raise(nameof(HexMode));
            Raise(nameof(HexFixedVariant));
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
            _hexMode = value;
            Raise(nameof(HexMode));
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
            RefreshHexCursor();
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

    /// <summary>Deals the random pictures again for the next gesture (the painted hexes keep theirs).</summary>
    public void ShuffleHexVariants()
    {
        _hexSeed++;
        StatusDetail = "Случайные варианты перемешаны для следующих гексов.";
    }

    /// <summary>The hex layer the tool writes: the active one, or the top visible unlocked hex layer.</summary>
    public HexLayer? ActiveHexLayer =>
        ActiveLayer as HexLayer
        ?? Session.Document.Layers.OfType<HexLayer>().LastOrDefault(layer => layer.Visible && !layer.Locked);

    /// <summary>The footprint under the pointer, outlined on the canvas while the hex tool is active.</summary>
    public HexCursorPreview? HexCursor => _hexCursor;

    private HexTileChoice HexChoice => new(
        HexTile.Id, HexTile.VariantCount, HexTile.Target, _hexRandomVariant, _hexFixedVariant, _hexSeed);

    /// <summary>The pointer moved over the canvas (null when it left); true when the outline changed.</summary>
    public bool UpdateHexHover(double? worldX, double? worldY)
    {
        _hexHover = worldX is { } x && worldY is { } y ? (x, y) : null;
        var before = _hexCursor;
        RefreshHexCursor();
        return !Equals(before?.Hexes.FirstOrDefault(), _hexCursor?.Hexes.FirstOrDefault())
            || before?.Hexes.Count != _hexCursor?.Hexes.Count || (before is null) != (_hexCursor is null);
    }

    private void RefreshHexCursor()
    {
        if (ActiveTool != EditorTool.Hex || _hexHover is not { } hover || ActiveHexLayer is not { } layer)
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

        var hexes = _hexMode is HexToolMode.Place or HexToolMode.Erase
            ? HexEditing.Footprint(layer.Board, center, _hexRadius).ToList()
            : [center];
        _hexCursor = new HexCursorPreview(layer.Id, hexes, _hexMode == HexToolMode.Erase);
    }

    /// <summary>True while a hex drag is running.</summary>
    public bool HasHexGesture => _hexGestureLayer is not null;

    /// <summary>A press of the hex tool. Returns true when a drag follows (place and erase).</summary>
    public bool BeginHexGesture(double worldX, double worldY)
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
        if (!layer.Board.Contains(hex))
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
        }

        _hexGestureLayer = layer.Id;
        _hexGestureLast = hex;
        _hexGestureTouched.Clear();
        _hexGestureEntries = 0;
        WriteHexFootprints(layer, [hex]);
        return true;
    }

    /// <summary>The drag reached a new point: every hex on the straight way from the previous one is written.</summary>
    public void ContinueHexGesture(double worldX, double worldY)
    {
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
        if (_hexGestureLayer is null) return;
        if (_hexGestureEntries > 1)
        {
            Session.MergeLastEntries(_hexGestureEntries, _hexMode == HexToolMode.Erase ? "Стереть гексы" : "Поставить гексы");
        }

        var count = _hexGestureTouched.Count;
        ResetHexGesture();
        StatusDetail = count == 0 ? "" : _hexMode == HexToolMode.Erase
            ? $"Стёрто гексов: {count}."
            : $"Поставлено гексов «{HexTile.Name}»: {count}.";
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    /// <summary>Abandons a running drag and takes back what it wrote.</summary>
    public bool CancelHexGesture()
    {
        if (_hexGestureLayer is null) return false;
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

        if (HexPaintCommand.Create(_hexMode == HexToolMode.Erase ? "Стереть гексы" : "Поставить гексы", layer, after) is { } command)
        {
            Session.Execute(command);
            _hexGestureEntries++;
            AfterEdit();
        }
        else if (choice.Target == HexPaintTarget.Feature && _hexMode == HexToolMode.Place
            && after.Count > 0 && after.Values.All(cell => cell is null))
        {
            StatusDetail = $"«{HexTile.Name}» ставится на местность: сначала поставьте гекс местности.";
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
        _hexRandomVariant = false;
        _hexFixedVariant = cell.Feature is null ? cell.Variant : cell.FeatureVariant;
        Raise(nameof(HexTileId));
        Raise(nameof(HexTile));
        Raise(nameof(HexMode));
        Raise(nameof(HexRandomVariant));
        Raise(nameof(HexFixedVariant));
        StatusDetail = $"Взят тайл «{HexTile.Name}», вариант {_hexFixedVariant + 1}.";
    }

    /// <summary>Shows or hides the hex grid of the hex layer the tool works on (undoable).</summary>
    public void SetHexGrid(Func<HexGridStyle, HexGridStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if ((ActiveLayer as HexLayer ?? Session.Document.Layers.OfType<HexLayer>().LastOrDefault()) is not { } layer) return;
        var after = change(layer.Grid);
        if (after == layer.Grid) return;
        Session.Execute(new UpdateHexGridCommand(layer.Id, layer.Grid, after));
        AfterEdit();
        Raise(nameof(HexGrid));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    /// <summary>The grid style of the hex layer the tool works on.</summary>
    public HexGridStyle? HexGrid =>
        (ActiveLayer as HexLayer ?? Session.Document.Layers.OfType<HexLayer>().LastOrDefault())?.Grid;

    /// <summary>Board facts for the hex panel: size, orientation and how many hexes are painted.</summary>
    public string HexBoardSummary => ActiveHexLayer is { } layer
        ? $"{layer.Board.Columns} × {layer.Board.Rows} гексов · "
          + (layer.Board.Orientation == HexOrientation.Flat ? "плоский верх" : "острый верх")
          + $" · заполнено {layer.Board.CellCount} из {(long)layer.Board.Columns * layer.Board.Rows}"
        : "Слоя гексов нет";
}
