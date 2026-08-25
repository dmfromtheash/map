using System.Collections.ObjectModel;

namespace Map.Core.Documents;

/// <summary>
/// A mask owned by exactly one layer. Coverage tiles: 255 = visible, 0 = hidden; a missing tile
/// means fully visible, so an untouched mask hides nothing. Disabling keeps the data.
/// </summary>
public sealed record LayerMask
{
    public LayerMask(TileMap tiles, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.BytesPerPixel != 1)
        {
            throw new ArgumentException("A mask stores single-channel coverage tiles.", nameof(tiles));
        }

        Tiles = tiles;
        Enabled = enabled;
    }

    public static LayerMask CreateEmpty() => new(TileMap.Empty(1), enabled: true);

    public TileMap Tiles { get; init; }
    public bool Enabled { get; init; }
}

/// <summary>
/// Base of every layer kind. Immutable, like the whole document snapshot; every subtype carries
/// the shared add/rename/reorder/duplicate/hide/lock surface through these properties.
/// </summary>
public abstract record MapLayer
{
    protected MapLayer(LayerId id, string name, bool visible, bool locked, double opacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (opacity is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be within [0, 1].");
        }

        Id = id;
        Name = name;
        Visible = visible;
        Locked = locked;
        Opacity = opacity;
    }

    public LayerId Id { get; init; }
    public string Name { get; init; }
    public bool Visible { get; init; }
    public bool Locked { get; init; }
    public double Opacity { get; init; }
    public string Blend { get; init; } = "Normal";

    /// <summary>The layer's mask, or null when it has none. Base colour cannot carry one.</summary>
    public abstract LayerMask? Mask { get; init; }

    /// <summary>A deep-identity copy with fresh IDs, used by the duplicate command.</summary>
    public abstract MapLayer WithNewIds();

    /// <summary>New snapshot of this layer with the given mask (or none).</summary>
    public abstract MapLayer WithMask(LayerMask? mask);
}

/// <summary>A layer whose primary content is a tiled raster (brush now, land later).</summary>
public interface IRasterContentLayer
{
    LayerId Id { get; }

    TileMap ContentTiles { get; }

    MapLayer WithContentTiles(TileMap tiles);
}

/// <summary>The mandatory background. Exactly one per document, always at the bottom, undeletable.</summary>
public sealed record BaseColorLayer : MapLayer
{
    public BaseColorLayer(LayerId id, string name, bool visible, bool locked, double opacity, string color)
        : base(id, name, visible, locked, opacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        Color = color;
    }

    /// <summary>Page colour as "#AARRGGBB".</summary>
    public string Color { get; init; }

    public override LayerMask? Mask
    {
        get => null;
        init
        {
            if (value is not null)
            {
                throw new ArgumentException("The base colour layer cannot carry a mask.");
            }
        }
    }

    public override MapLayer WithNewIds() => this with { Id = LayerId.New() };

    public override MapLayer WithMask(LayerMask? mask) =>
        mask is null ? this : throw new InvalidOperationException("The base colour layer cannot carry a mask.");
}

/// <summary>Vector objects (stamps, shapes) in draw order, with optional grouping and sublayers.</summary>
public sealed record ObjectLayer : MapLayer
{
    public ObjectLayer(
        LayerId id, string name, bool visible, bool locked, double opacity,
        IReadOnlyList<SceneObject> objects, LayerMask? mask = null)
        : base(id, name, visible, locked, opacity)
    {
        ArgumentNullException.ThrowIfNull(objects);
        Objects = new ReadOnlyCollection<SceneObject>([.. objects]);
        Mask = mask;
    }

    /// <summary>Draw order within each sublayer; sublayer is the coarse order (see renderer).</summary>
    public IReadOnlyList<SceneObject> Objects { get; init; }

    public override LayerMask? Mask { get; init; }

    public override MapLayer WithNewIds() => this with
    {
        Id = LayerId.New(),
        Objects = new ReadOnlyCollection<SceneObject>(
            [.. Objects.Select(VectorGeometry.WithNewIds)]),
    };

    public override MapLayer WithMask(LayerMask? mask) => this with { Mask = mask };
}

/// <summary>Raster painting: premultiplied RGBA tiles.</summary>
public sealed record BrushLayer : MapLayer, IRasterContentLayer
{
    public BrushLayer(
        LayerId id, string name, bool visible, bool locked, double opacity,
        TileMap tiles, LayerMask? mask = null)
        : base(id, name, visible, locked, opacity)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.BytesPerPixel != 4)
        {
            throw new ArgumentException("A brush layer stores RGBA tiles.", nameof(tiles));
        }

        Tiles = tiles;
        Mask = mask;
    }

    public static BrushLayer CreateEmpty(LayerId id, string name) =>
        new(id, name, visible: true, locked: false, opacity: 1.0, TileMap.Empty(4));

    public TileMap Tiles { get; init; }

    public override LayerMask? Mask { get; init; }

    TileMap IRasterContentLayer.ContentTiles => Tiles;

    public MapLayer WithContentTiles(TileMap tiles) => this with { Tiles = tiles };

    public override MapLayer WithNewIds() => this with { Id = LayerId.New() };

    public override MapLayer WithMask(LayerMask? mask) => this with { Mask = mask };
}

/// <summary>Appearance evaluated from land coverage at render time; never baked into geometry.</summary>
public sealed record LandEdgeStyle
{
    public string FillColor { get; init; } = "#FF8CA36B";
    public string OutlineColor { get; init; } = "#FF3F5131";
    public double OutlineWidth { get; init; } = 3;
    public double InnerShadowOpacity { get; init; } = 0.18;
    public double OuterShadowOpacity { get; init; } = 0.25;
    public double ShadowBlur { get; init; } = 8;
    public string RippleColor { get; init; } = "#665A83A1";
    public double RippleWidth { get; init; } = 2;
    public double RippleSpacing { get; init; } = 10;
    public int RippleCount { get; init; } = 2;
}

/// <summary>
/// Independent land coverage. Edge styling is evaluated from <see cref="GeometryTiles"/> and can
/// change without changing a single source byte; the ordinary layer mask remains separate.
/// </summary>
public sealed record LandLayer : MapLayer, IRasterContentLayer
{
    public LandLayer(
        LayerId id,
        string name,
        bool visible,
        bool locked,
        double opacity,
        TileMap geometryTiles,
        LandEdgeStyle edgeStyle,
        LayerMask? mask = null)
        : base(id, name, visible, locked, opacity)
    {
        ArgumentNullException.ThrowIfNull(geometryTiles);
        ArgumentNullException.ThrowIfNull(edgeStyle);
        if (geometryTiles.BytesPerPixel != 1)
        {
            throw new ArgumentException("Land geometry stores single-channel coverage.", nameof(geometryTiles));
        }

        GeometryTiles = geometryTiles;
        EdgeStyle = edgeStyle;
        Mask = mask;
    }

    public static LandLayer CreateEmpty(LayerId id, string name) => new(
        id, name, true, false, 1, TileMap.Empty(1), new LandEdgeStyle());

    public TileMap GeometryTiles { get; init; }
    public LandEdgeStyle EdgeStyle { get; init; }
    public override LayerMask? Mask { get; init; }

    TileMap IRasterContentLayer.ContentTiles => GeometryTiles;
    public MapLayer WithContentTiles(TileMap tiles) => this with { GeometryTiles = tiles };
    public override MapLayer WithNewIds() => this with { Id = LayerId.New() };
    public override MapLayer WithMask(LayerMask? mask) => this with { Mask = mask };
}
