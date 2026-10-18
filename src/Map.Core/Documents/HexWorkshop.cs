using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>Stable id of one path (river, road, trail, border line) of a hex layer.</summary>
public readonly record struct HexPathId(Guid Value)
{
    public static HexPathId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>What a hex path is: it decides the look, the default route and what crosses what.</summary>
public enum HexPathKind
{
    /// <summary>Water: thin at its source, wider downstream; a road crossing it gets a bridge.</summary>
    River,

    /// <summary>A made road; travel on it is faster.</summary>
    Road,

    /// <summary>A footpath: a thin dashed road.</summary>
    Trail,

    /// <summary>A line along hex edges: a frontier, a wall, a cliff line.</summary>
    Border,
}

/// <summary>How a path runs through the hexes (the Hexmaker routing modes).</summary>
public enum HexRouteStyle
{
    /// <summary>From hex centre to hex centre, crossing every edge at its middle.</summary>
    Centers,

    /// <summary>In and out through the edge middles, winding inside each hex: the river look.</summary>
    Meander,

    /// <summary>Along the hex edges from corner to corner: frontiers and walls.</summary>
    Edges,
}

/// <summary>
/// A hex corner: corner <see cref="Corner"/> (0..5, as <see cref="HexLayout.Corner"/> numbers them)
/// of hex <see cref="Hex"/>. Three hexes meet at every corner; <see cref="HexLayout.Canonical"/> gives
/// the one representation stored in documents.
/// </summary>
public readonly record struct HexVertex(HexCoord Hex, int Corner)
{
    public override string ToString() => $"{Hex}#{Corner}";
}

/// <summary>
/// One river, road, trail or border of a hex layer. Centre and meander paths are a run of
/// neighbouring hexes; edge paths are a run of neighbouring hex corners. A path never stores
/// pixels: it is drawn from the hexes, so it follows the board when the board moves.
/// </summary>
public sealed record HexPath
{
    public const int MaxNodes = 65_536;

    public const int MaxNameLength = 120;

    public HexPath(HexPathId id, HexPathKind kind, HexRouteStyle style,
        ImmutableArray<HexCoord> hexes, ImmutableArray<HexVertex> vertices,
        double width = 1, string? color = null, string name = "", int seed = 0)
    {
        if (hexes.IsDefault) hexes = [];
        if (vertices.IsDefault) vertices = [];
        if (style == HexRouteStyle.Edges ? !hexes.IsEmpty : !vertices.IsEmpty)
        {
            throw new ArgumentException("An edge path holds corners only, a centre or meander path hexes only.");
        }

        if (hexes.Length > MaxNodes || vertices.Length > MaxNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(hexes), $"A hex path holds at most {MaxNodes} nodes.");
        }

        for (var i = 1; i < hexes.Length; i++)
        {
            if (HexCoord.Distance(hexes[i - 1], hexes[i]) != 1)
            {
                throw new ArgumentException("The hexes of a path must be neighbours in order.", nameof(hexes));
            }
        }

        if (!double.IsFinite(width) || width is < 0.1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A path width is 0.1..8 (times the kind's width).");
        }

        if (color is not null && AppearanceColor.Normalize(color) is null)
        {
            throw new ArgumentException($"'{color}' is not a colour.", nameof(color));
        }

        Id = id;
        Kind = kind;
        Style = style;
        Hexes = hexes;
        Vertices = vertices;
        Width = width;
        Color = color is null ? null : AppearanceColor.Normalize(color);
        Name = (name ?? "").Length > MaxNameLength ? name![..MaxNameLength] : name ?? "";
        Seed = seed;
    }

    public HexPathId Id { get; }

    public HexPathKind Kind { get; }

    public HexRouteStyle Style { get; }

    /// <summary>The hexes of a centre or meander path, source first for a river.</summary>
    public ImmutableArray<HexCoord> Hexes { get; init; }

    /// <summary>The corners of an edge path.</summary>
    public ImmutableArray<HexVertex> Vertices { get; init; }

    /// <summary>Width relative to the kind's standard width.</summary>
    public double Width { get; init; }

    /// <summary>"#AARRGGBB", or null for the kind's colour.</summary>
    public string? Color { get; init; }

    public string Name { get; init; }

    /// <summary>Changes how a meander winds without changing where it goes.</summary>
    public int Seed { get; init; }

    public int NodeCount => Style == HexRouteStyle.Edges ? Vertices.Length : Hexes.Length;

    /// <summary>The style a new path of this kind uses unless the user picks another.</summary>
    public static HexRouteStyle DefaultStyle(HexPathKind kind) => kind switch
    {
        HexPathKind.River => HexRouteStyle.Meander,
        HexPathKind.Border => HexRouteStyle.Edges,
        _ => HexRouteStyle.Centers,
    };

    /// <summary>Record equality compares arrays by reference; this compares what the path is.</summary>
    public bool SameAs(HexPath? other) =>
        other is not null && Id == other.Id && Kind == other.Kind && Style == other.Style
        && Hexes.SequenceEqual(other.Hexes) && Vertices.SequenceEqual(other.Vertices)
        && Width.Equals(other.Width) && Color == other.Color && Name == other.Name && Seed == other.Seed;

    /// <summary>The same path moved by an axial offset (copy, paste, inserted rows).</summary>
    public HexPath Moved(Func<HexCoord, HexCoord> map) => this with
    {
        Hexes = [.. Hexes.Select(map)],
        Vertices = [.. Vertices.Select(v => v with { Hex = map(v.Hex) })],
    };
}

/// <summary>
/// A named region of a hex layer - a kingdom, a province, a hunting ground. Hexes join it through
/// <see cref="HexCell.Region"/>; the frontier is drawn wherever two different regions meet.
/// </summary>
public sealed record HexRegion
{
    public const int MaxNameLength = 80;

    public HexRegion(int id, string name, string color)
    {
        if (id is < 1 or > HexCell.MaxRegion)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"A region id is 1..{HexCell.MaxRegion}.");
        }

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? $"Регион {id}" : name.Trim().Length > MaxNameLength ? name.Trim()[..MaxNameLength] : name.Trim();
        Color = AppearanceColor.Normalize(color) ?? throw new ArgumentException($"'{color}' is not a colour.", nameof(color));
    }

    public int Id { get; }

    public string Name { get; init; }

    /// <summary>"#AARRGGBB": the tint of its hexes and the colour of its frontier.</summary>
    public string Color { get; init; }
}

/// <summary>What kind of entry a hex key is (the hex-crawl categories).</summary>
public enum HexNoteCategory
{
    Other,
    Encounter,
    Mystery,
    Landmark,
    Settlement,
    Hook,
    Danger,
    Treasure,
}

/// <summary>
/// The key of one hex for running a game: a short title shown on the map, what only the game
/// master knows, what the players may read, and optionally the child map that zooms into the hex.
/// </summary>
public sealed record HexNote
{
    public const int MaxTitleLength = 120;

    public const int MaxTextLength = 20_000;

    public HexNote(string title, HexNoteCategory category = HexNoteCategory.Other,
        string gmText = "", string playerText = "", string? childMap = null)
    {
        Title = Clip(title, MaxTitleLength).Trim();
        Category = Enum.IsDefined(category) ? category : HexNoteCategory.Other;
        GmText = Clip(gmText, MaxTextLength);
        PlayerText = Clip(playerText, MaxTextLength);
        ChildMap = string.IsNullOrWhiteSpace(childMap) ? null : childMap;
    }

    public string Title { get; init; }

    public HexNoteCategory Category { get; init; }

    /// <summary>Only the game master sees this; the player view and the player export leave it out.</summary>
    public string GmText { get; init; }

    /// <summary>What the players may read about the hex.</summary>
    public string PlayerText { get; init; }

    /// <summary>File name of the child map of this hex, next to this map's file (or an absolute path).</summary>
    public string? ChildMap { get; init; }

    public bool IsEmpty => Title.Length == 0 && GmText.Length == 0 && PlayerText.Length == 0 && ChildMap is null;

    private static string Clip(string? value, int max) => (value ?? "").Length > max ? value![..max] : value ?? "";
}

/// <summary>Distances of a hex layer for travel: how far one hex is and how far a day takes a party.</summary>
public sealed record HexTravelScale
{
    public HexTravelScale(double distancePerHex = 6, string unit = "миль", double distancePerDay = 24)
    {
        if (!double.IsFinite(distancePerHex) || distancePerHex is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(distancePerHex), distancePerHex, "Distance per hex is above 0.");
        }

        if (!double.IsFinite(distancePerDay) || distancePerDay is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(distancePerDay), distancePerDay, "Distance per day is above 0.");
        }

        DistancePerHex = distancePerHex;
        Unit = string.IsNullOrWhiteSpace(unit) ? "миль" : unit.Trim().Length > 24 ? unit.Trim()[..24] : unit.Trim();
        DistancePerDay = distancePerDay;
    }

    public static HexTravelScale Default { get; } = new();

    public double DistancePerHex { get; init; }

    public string Unit { get; init; }

    public double DistancePerDay { get; init; }
}
