namespace Map.Core.Documents;

/// <summary>
/// How the map is handed to the players as a sheet (R09f, schema v51, owner's choice 2026-11-11): the map laid into an old
/// sheet lying on a table. The look is applied only when the handout is exported; the canvas and the document stay as drawn.
/// </summary>
public enum MapHandout
{
    /// <summary>No handout: the map is exported as it is.</summary>
    None,

    /// <summary>«Найденная карта» - folded and carried for years: torn edge, crumples, stains, a dotted way to an X.</summary>
    Found,

    /// <summary>«Обгоревший лист» - snatched from the fire: charred uneven edge, scorch, two burnt holes.</summary>
    Burnt,

    /// <summary>«Свиток тушью» - rolled top and bottom, drawn in pen and wash, a red seal with ribbons.</summary>
    ScrollInk,

    /// <summary>«Свиток на костяном стержне» - the left end wound on a carved bone rod, a cracked blue seal.</summary>
    ScrollBone,

    /// <summary>«Портолан на коже» - a whole skin with a painted wind rose and a wind head, rhumb lines.</summary>
    PortolanSkin,
}

/// <summary>Names and file ids of <see cref="MapHandout"/>.</summary>
public static class MapHandouts
{
    public static IReadOnlyList<MapHandout> All { get; } =
        [MapHandout.None, MapHandout.Found, MapHandout.Burnt, MapHandout.ScrollInk, MapHandout.ScrollBone, MapHandout.PortolanSkin];

    /// <summary>What the owner sees.</summary>
    public static string Name(MapHandout handout) => handout switch
    {
        MapHandout.Found => "Найденная карта",
        MapHandout.Burnt => "Обгоревший лист",
        MapHandout.ScrollInk => "Свиток тушью",
        MapHandout.ScrollBone => "Свиток на костяном стержне",
        MapHandout.PortolanSkin => "Портолан на коже",
        _ => "Без хендаута",
    };

    /// <summary>The id written into the file; stable across renames.</summary>
    public static string Id(MapHandout handout) => handout switch
    {
        MapHandout.Found => "found",
        MapHandout.Burnt => "burnt",
        MapHandout.ScrollInk => "scroll-ink",
        MapHandout.ScrollBone => "scroll-bone",
        MapHandout.PortolanSkin => "portolan-skin",
        _ => "none",
    };

    public static bool TryParse(string? id, out MapHandout handout)
    {
        foreach (var candidate in All)
            if (string.Equals(Id(candidate), id, StringComparison.Ordinal)) { handout = candidate; return true; }
        handout = MapHandout.None;
        return false;
    }
}
