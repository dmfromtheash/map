using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>A place of the pointcrawl (R32): a numbered point on the map, maybe a card of the world, visited or not.</summary>
public sealed record PointcrawlNode(Guid Id, string Name, double X, double Y)
{
    public Guid? EntityId { get; init; }
    public bool Visited { get; init; }
    public string Note { get; init; } = "";

    public bool IsValid => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 120 && double.IsFinite(X) && double.IsFinite(Y)
        && Math.Abs(X) <= 1e7 && Math.Abs(Y) <= 1e7 && Note is { Length: <= 4000 };
}

/// <summary>A way between two places (R32): hours of travel, danger 0…5, hidden from players, one way only.</summary>
public sealed record PointcrawlEdge(Guid Id, Guid From, Guid To)
{
    public string Name { get; init; } = "";
    public double Hours { get; init; } = 4;
    public int Danger { get; init; }
    public bool Hidden { get; init; }
    public bool OneWay { get; init; }

    public bool IsValid => Id != Guid.Empty && From != Guid.Empty && To != Guid.Empty && From != To && Name is { Length: <= 120 }
        && double.IsFinite(Hours) && Hours is >= 0 and <= 100_000 && Danger is >= 0 and <= 5;
}

/// <summary>
/// The pointcrawl of a map (R32): places and ways drawn over the painted map. With fog by visit
/// the players see the places visited, the ways leading out of them (not hidden ones) and, at
/// their far ends, unknown places marked «?».
/// </summary>
public sealed record MapPointcrawl
{
    public const int MaxNodes = 2048, MaxEdges = 8192;

    public ImmutableArray<PointcrawlNode> Nodes { get; init; } = [];
    public ImmutableArray<PointcrawlEdge> Edges { get; init; } = [];
    public bool FogByVisit { get; init; } = true;
    public bool Shown { get; init; } = true;

    public bool IsDefault => Nodes.IsDefaultOrEmpty && Edges.IsDefaultOrEmpty && FogByVisit && Shown;

    public int NumberOf(Guid node) => Nodes.IndexOf(Nodes.FirstOrDefault(n => n.Id == node)!) + 1;

    public void Validate()
    {
        if (Nodes.IsDefault || Edges.IsDefault || Nodes.Length > MaxNodes || Edges.Length > MaxEdges
            || Nodes.Any(n => n is null || !n.IsValid) || Edges.Any(e => e is null || !e.IsValid)
            || Nodes.Select(n => n.Id).Distinct().Count() != Nodes.Length || Edges.Select(e => e.Id).Distinct().Count() != Edges.Length)
            throw new ArgumentException("Повреждён пойнткроул.");
        var ids = Nodes.Select(n => n.Id).ToHashSet();
        if (Edges.Any(e => !ids.Contains(e.From) || !ids.Contains(e.To)))
            throw new ArgumentException("Путь пойнткроула ведёт к несуществующему месту.");
    }

    /// <summary>
    /// What the players see: every place and way without fog; with it the visited places, the
    /// not hidden ways out of them (a one-way way only from its start) and their far ends as unknown.
    /// </summary>
    public (IReadOnlyList<(PointcrawlNode Node, bool Known)> Nodes, IReadOnlyList<PointcrawlEdge> Edges) ForPlayers()
    {
        if (!FogByVisit)
            return ([.. Nodes.Select(n => (n, true))], [.. Edges.Where(e => !e.Hidden)]);
        var visited = Nodes.Where(n => n.Visited).Select(n => n.Id).ToHashSet();
        var edges = Edges.Where(e => !e.Hidden && (visited.Contains(e.From) || (!e.OneWay && visited.Contains(e.To)))).ToArray();
        var seen = edges.SelectMany(e => new[] { e.From, e.To }).Concat(visited).ToHashSet();
        return ([.. Nodes.Where(n => seen.Contains(n.Id)).Select(n => (n, visited.Contains(n.Id)))], edges);
    }
}
