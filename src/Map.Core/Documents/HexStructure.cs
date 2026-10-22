using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>One ordered point of interest; visibility belongs to the point, not its terrain.</summary>
public sealed record HexPoi
{
    public HexPoi(string type, string name = "", bool playerVisible = true, int variant = 0)
    {
        if (!HexCell.IsValidTileId(type) || variant < 0)
            throw new ArgumentException("Недопустимый значок точки интереса.");
        Type = type;
        Name = (name ?? "").Trim();
        if (Name.Length > 120) Name = Name[..120];
        PlayerVisible = playerVisible;
        Variant = variant;
    }

    public string Rumor { get; init; } = "";
    public string ApproximateLabel { get; init; } = "";

    public string Type { get; init; }
    public string Name { get; init; }
    public bool PlayerVisible { get; init; }
    public int Variant { get; init; }
}

public enum HexPresentation { Terrain, Politics, Travel, PlayerKnowledge }

public static class HexRegionHierarchy
{
    public static bool CanParent(IReadOnlyList<HexRegion> regions, int id, int parent)
    {
        if (!regions.Any(r => r.Id == id)) return false;
        var seen = new HashSet<int> { id };
        while (parent != 0)
        {
            if (!seen.Add(parent)) return false;
            var next = regions.FirstOrDefault(r => r.Id == parent);
            if (next is null) return false;
            parent = next.ParentRegion;
        }
        return true;
    }

    public static string Label(IReadOnlyList<HexRegion> regions, HexRegion region)
    {
        var names = new List<string> { region.Name };
        var seen = new HashSet<int> { region.Id };
        var parent = region.ParentRegion;
        while (parent != 0 && seen.Add(parent) && regions.FirstOrDefault(r => r.Id == parent) is { } next)
        {
            names.Insert(0, next.Name);
            parent = next.ParentRegion;
        }
        return string.Join(" → ", names);
    }
}
