using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>
/// What stands on one hex: a ready painted terrain tile, and optionally a feature tile drawn over
/// it (a village, a tower, a ruin). Tiles are named by a stable id of the hex tile library and a
/// variant number, so a forest painted with random variants keeps exactly the pictures it got.
/// </summary>
public sealed record HexCell
{
    public const int MaxTileIdLength = 96;

    public HexCell(string terrain, int variant = 0, string? feature = null, int featureVariant = 0)
    {
        if (!IsValidTileId(terrain))
        {
            throw new ArgumentException($"'{terrain}' is not a hex tile id.", nameof(terrain));
        }

        if (feature is not null && !IsValidTileId(feature))
        {
            throw new ArgumentException($"'{feature}' is not a hex tile id.", nameof(feature));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(variant);
        ArgumentOutOfRangeException.ThrowIfNegative(featureVariant);
        Terrain = terrain;
        Variant = variant;
        Feature = feature;
        FeatureVariant = feature is null ? 0 : featureVariant;
    }

    public string Terrain { get; }

    public int Variant { get; }

    public string? Feature { get; }

    public int FeatureVariant { get; }

    public HexCell WithFeature(string? feature, int featureVariant = 0) =>
        new(Terrain, Variant, feature, featureVariant);

    /// <summary>a–z, 0–9, '-', '_', '.', '/' - a library-relative name such as <c>starter/forest</c>.</summary>
    public static bool IsValidTileId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTileIdLength || value[0] == '/' || value[^1] == '/')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '/'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A square block of <see cref="HexBoard.ChunkSize"/>² board cells in offset coordinates.</summary>
public readonly record struct HexChunkKey(int X, int Y)
{
    public override string ToString() => $"{X}_{Y}";
}

/// <summary>
/// One chunk of a board. Immutable: an edit copies only the chunks it touches, every other chunk
/// is shared with the previous snapshot by reference, so undo and the render cache both pay
/// for touched chunks only - the hex counterpart of <see cref="TileMap"/>.
/// </summary>
public sealed class HexChunk
{
    private readonly HexCell?[] _cells;

    internal HexChunk(HexCell?[] cells, int count)
    {
        _cells = cells;
        Count = count;
    }

    /// <summary>Cells present in this chunk.</summary>
    public int Count { get; }

    /// <summary>The cell at a chunk-local column and row.</summary>
    public HexCell? this[int column, int row] => _cells[row * HexBoard.ChunkSize + column];

    internal HexCell?[] CopyCells() => (HexCell?[])_cells.Clone();
}

/// <summary>
/// The hexes of one hex layer: a rectangle of <see cref="Columns"/> × <see cref="Rows"/> hexes laid
/// out by <see cref="Layout"/>, sparse - an empty hex costs nothing, and a 512 × 512 board with a
/// few painted hexes stays a few chunks.
/// </summary>
public sealed class HexBoard
{
    /// <summary>Edge of one chunk in hexes.</summary>
    public const int ChunkSize = 16;

    public const int MaxDimension = 2048;

    public const double MinHexSize = 8;

    public const double MaxHexSize = 1024;

    private readonly ImmutableDictionary<HexChunkKey, HexChunk> _chunks;

    private HexBoard(HexLayout layout, int columns, int rows, ImmutableDictionary<HexChunkKey, HexChunk> chunks, int cellCount)
    {
        Layout = layout;
        Columns = columns;
        Rows = rows;
        _chunks = chunks;
        CellCount = cellCount;
    }

    /// <summary>An empty board whose first hex touches the top-left corner of the canvas.</summary>
    public static HexBoard Create(HexOrientation orientation, double hexSize, int columns, int rows)
    {
        Validate(hexSize, columns, rows);
        var layout = orientation == HexOrientation.Flat
            ? new HexLayout(orientation, hexSize, hexSize, HexLayout.Sqrt3 / 2 * hexSize)
            : new HexLayout(orientation, hexSize, HexLayout.Sqrt3 / 2 * hexSize, hexSize);
        return new HexBoard(layout, columns, rows, ImmutableDictionary<HexChunkKey, HexChunk>.Empty, 0);
    }

    /// <summary>Rebuilds a board from stored cells; used by the file reader.</summary>
    public static HexBoard FromCells(HexLayout layout, int columns, int rows, IEnumerable<KeyValuePair<HexCoord, HexCell>> cells)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Validate(layout.Size, columns, rows);
        var board = new HexBoard(layout, columns, rows, ImmutableDictionary<HexChunkKey, HexChunk>.Empty, 0);
        return board.With(cells.Select(pair => new KeyValuePair<HexCoord, HexCell?>(pair.Key, pair.Value)));
    }

    /// <summary>Canvas size a board of this shape needs, rounded up to whole units.</summary>
    public static (int Width, int Height) CanvasSizeFor(HexOrientation orientation, double hexSize, int columns, int rows)
    {
        Validate(hexSize, columns, rows);
        var board = Create(orientation, hexSize, columns, rows);
        return ((int)Math.Ceiling(board.PixelWidth), (int)Math.Ceiling(board.PixelHeight));
    }

    private static void Validate(double hexSize, int columns, int rows)
    {
        if (!double.IsFinite(hexSize) || hexSize < MinHexSize || hexSize > MaxHexSize)
        {
            throw new ArgumentOutOfRangeException(nameof(hexSize), hexSize, $"Hex size is {MinHexSize}..{MaxHexSize}.");
        }

        if (columns is < 1 or > MaxDimension || rows is < 1 or > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), $"A board is 1..{MaxDimension} hexes on each side.");
        }
    }

    public HexLayout Layout { get; }

    public HexOrientation Orientation => Layout.Orientation;

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Painted hexes on the whole board.</summary>
    public int CellCount { get; }

    public int ChunkCount => _chunks.Count;

    public IEnumerable<HexChunkKey> ChunkKeys => _chunks.Keys;

    /// <summary>Width of the board's bounding box from canvas x = 0.</summary>
    public double PixelWidth => Orientation == HexOrientation.Flat
        ? Layout.Size * (1.5 * (Columns - 1) + 2)
        : Layout.Size * HexLayout.Sqrt3 * (Columns + (Rows > 1 ? 0.5 : 0));

    /// <summary>Height of the board's bounding box from canvas y = 0.</summary>
    public double PixelHeight => Orientation == HexOrientation.Flat
        ? Layout.Size * HexLayout.Sqrt3 * (Rows + (Columns > 1 ? 0.5 : 0))
        : Layout.Size * (1.5 * (Rows - 1) + 2);

    public bool Contains(HexCoord hex) => Contains(Layout.ToOffset(hex));

    public bool Contains(HexOffset offset) =>
        offset.Column >= 0 && offset.Column < Columns && offset.Row >= 0 && offset.Row < Rows;

    public HexCell? this[HexCoord hex]
    {
        get
        {
            var offset = Layout.ToOffset(hex);
            if (!Contains(offset))
            {
                return null;
            }

            return _chunks.TryGetValue(ChunkOf(offset), out var chunk)
                ? chunk[offset.Column % ChunkSize, offset.Row % ChunkSize]
                : null;
        }
    }

    public HexChunk? Chunk(HexChunkKey key) => _chunks.TryGetValue(key, out var chunk) ? chunk : null;

    public static HexChunkKey ChunkOf(HexOffset offset) => new(offset.Column / ChunkSize, offset.Row / ChunkSize);

    /// <summary>The hex at a chunk-local position of a chunk.</summary>
    public HexCoord CoordOf(HexChunkKey key, int column, int row) =>
        Layout.FromOffset(new HexOffset(key.X * ChunkSize + column, key.Y * ChunkSize + row));

    /// <summary>Canvas bounds of everything a chunk can draw, including the hexes' corners.</summary>
    public WorldBounds ChunkBounds(HexChunkKey key)
    {
        var first = Layout.Bounds(Layout.FromOffset(new HexOffset(key.X * ChunkSize, key.Y * ChunkSize)));
        var lastColumn = Math.Min(Columns, (key.X + 1) * ChunkSize) - 1;
        var lastRow = Math.Min(Rows, (key.Y + 1) * ChunkSize) - 1;
        var last = Layout.Bounds(Layout.FromOffset(new HexOffset(lastColumn, lastRow)));
        // Odd columns/rows are shifted by half a hex, so a chunk reaches half a hex past both ends.
        var padX = Orientation == HexOrientation.Pointy ? Layout.HexWidth / 2 : 0;
        var padY = Orientation == HexOrientation.Flat ? Layout.HexHeight / 2 : 0;
        return new WorldBounds(first.Left - padX, first.Top - padY, last.Right + padX, last.Bottom + padY);
    }

    /// <summary>Chunks of the board's extent that overlap a canvas rectangle (painted or not).</summary>
    public IEnumerable<HexChunkKey> ChunksIn(WorldBounds view)
    {
        var (first, last) = OffsetRangeIn(view);
        if (first.Column > last.Column || first.Row > last.Row)
        {
            yield break;
        }

        for (var y = first.Row / ChunkSize; y <= last.Row / ChunkSize; y++)
        {
            for (var x = first.Column / ChunkSize; x <= last.Column / ChunkSize; x++)
            {
                yield return new HexChunkKey(x, y);
            }
        }
    }

    /// <summary>Every board hex whose bounding box can overlap a canvas rectangle.</summary>
    public IEnumerable<HexCoord> HexesIn(WorldBounds view)
    {
        var (first, last) = OffsetRangeIn(view);
        for (var row = first.Row; row <= last.Row; row++)
        {
            for (var column = first.Column; column <= last.Column; column++)
            {
                yield return Layout.FromOffset(new HexOffset(column, row));
            }
        }
    }

    private (HexOffset First, HexOffset Last) OffsetRangeIn(WorldBounds view)
    {
        double stepX, stepY;
        if (Orientation == HexOrientation.Flat)
        {
            stepX = Layout.Size * 1.5;
            stepY = Layout.HexHeight;
        }
        else
        {
            stepX = Layout.HexWidth;
            stepY = Layout.Size * 1.5;
        }

        var firstColumn = (int)Math.Floor((view.Left - Layout.OriginX - Layout.HexWidth) / stepX);
        var lastColumn = (int)Math.Ceiling((view.Right - Layout.OriginX + Layout.HexWidth) / stepX);
        var firstRow = (int)Math.Floor((view.Top - Layout.OriginY - Layout.HexHeight) / stepY);
        var lastRow = (int)Math.Ceiling((view.Bottom - Layout.OriginY + Layout.HexHeight) / stepY);
        return (
            new HexOffset(Math.Max(0, firstColumn), Math.Max(0, firstRow)),
            new HexOffset(Math.Min(Columns - 1, lastColumn), Math.Min(Rows - 1, lastRow)));
    }

    /// <summary>Every painted hex, chunk by chunk.</summary>
    public IEnumerable<KeyValuePair<HexCoord, HexCell>> Cells
    {
        get
        {
            foreach (var (key, chunk) in _chunks.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
            {
                for (var row = 0; row < ChunkSize; row++)
                {
                    for (var column = 0; column < ChunkSize; column++)
                    {
                        if (chunk[column, row] is { } cell)
                        {
                            yield return new(CoordOf(key, column, row), cell);
                        }
                    }
                }
            }
        }
    }

    /// <summary>The same hexes moved on the canvas; used when the canvas grows or is cropped.</summary>
    public HexBoard Translated(double dx, double dy) => dx == 0 && dy == 0
        ? this
        : new HexBoard(new HexLayout(Orientation, Layout.Size, Layout.OriginX + dx, Layout.OriginY + dy),
            Columns, Rows, _chunks, CellCount);

    /// <summary>
    /// A new snapshot with the given hexes set (or cleared with null). Hexes outside the board are
    /// ignored; chunks no change touches are shared with this snapshot.
    /// </summary>
    public HexBoard With(IEnumerable<KeyValuePair<HexCoord, HexCell?>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Dictionary<HexChunkKey, (HexCell?[] Cells, int Count)>? edited = null;
        foreach (var (hex, cell) in changes)
        {
            var offset = Layout.ToOffset(hex);
            if (!Contains(offset))
            {
                continue;
            }

            var key = ChunkOf(offset);
            edited ??= [];
            if (!edited.TryGetValue(key, out var entry))
            {
                entry = _chunks.TryGetValue(key, out var existing)
                    ? (existing.CopyCells(), existing.Count)
                    : (new HexCell?[ChunkSize * ChunkSize], 0);
            }

            var index = offset.Row % ChunkSize * ChunkSize + offset.Column % ChunkSize;
            var before = entry.Cells[index];
            if (before is null && cell is not null) entry.Count++;
            else if (before is not null && cell is null) entry.Count--;
            entry.Cells[index] = cell;
            edited[key] = entry;
        }

        if (edited is null)
        {
            return this;
        }

        var builder = _chunks.ToBuilder();
        var count = CellCount;
        foreach (var (key, (cells, chunkCount)) in edited)
        {
            if (builder.TryGetValue(key, out var old))
            {
                count -= old.Count;
            }

            count += chunkCount;
            if (chunkCount == 0) builder.Remove(key);
            else builder[key] = new HexChunk(cells, chunkCount);
        }

        return new HexBoard(Layout, Columns, Rows, builder.ToImmutable(), count);
    }
}

/// <summary>How a hex layer draws its grid lines and numbers.</summary>
public sealed record HexGridStyle
{
    public bool Visible { get; init; } = true;

    /// <summary>Line colour as "#AARRGGBB".</summary>
    public string Color { get; init; } = "#66201810";

    /// <summary>Line width in canvas units.</summary>
    public double Width { get; init; } = 1.5;

    /// <summary>Prints the column-row number ("0101") at the top of every hex.</summary>
    public bool Numbers { get; init; }

    /// <summary>Also draws the outline of hexes nobody has painted yet.</summary>
    public bool ShowEmpty { get; init; } = true;
}

/// <summary>
/// A layer of hexes: the surface of the hex category. Every hex holds at most one terrain tile
/// and one feature tile; nothing is painted freely between them.
/// </summary>
public sealed record HexLayer : MapLayer
{
    public HexLayer(LayerId id, string name, bool visible, bool locked, double opacity, HexBoard board, LayerMask? mask = null)
        : base(id, name, visible, locked, opacity)
    {
        ArgumentNullException.ThrowIfNull(board);
        Board = board;
        Mask = mask;
    }

    public HexBoard Board { get; init; }

    public HexGridStyle Grid { get; init; } = new();

    public override LayerMask? Mask { get; init; }

    public override MapLayer WithNewIds() => this with { Id = LayerId.New() };

    public override MapLayer WithMask(LayerMask? mask) => this with { Mask = mask };
}

/// <summary>Builds the starting document of the hex category.</summary>
public static class HexMapFactory
{
    public const string TerrainLayerName = "Гексы";

    public const string LabelsLayerName = "Подписи";

    public static MapDocument Create(
        string title, DateTimeOffset createdUtc, MapCategoryId category,
        HexOrientation orientation, double hexSize, int columns, int rows, string background)
    {
        var board = HexBoard.Create(orientation, hexSize, columns, rows);
        var width = Math.Clamp((int)Math.Ceiling(board.PixelWidth), CanvasDimensionPolicy.Minimum, CanvasDimensionPolicy.Maximum);
        var height = Math.Clamp((int)Math.Ceiling(board.PixelHeight), CanvasDimensionPolicy.Minimum, CanvasDimensionPolicy.Maximum);
        return new MapDocument(
            DocumentId.New(),
            title,
            createdUtc,
            new CanvasDefinition(width, height, "px", background),
            new ReadOnlyCollection<MapLayer>(
            [
                new BaseColorLayer(LayerId.New(), "Цвет фона", visible: true, locked: false, opacity: 1.0, background),
                new HexLayer(LayerId.New(), TerrainLayerName, visible: true, locked: false, opacity: 1.0, board),
                new ObjectLayer(LayerId.New(), LabelsLayerName, visible: true, locked: false, opacity: 1.0, []),
            ]),
            category: category);
    }
}
