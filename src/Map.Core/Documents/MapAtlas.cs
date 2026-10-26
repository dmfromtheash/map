using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>Locator only. Map identity is the existing, persistent DocumentId.</summary>
public sealed record AtlasLocator(Guid Id, string File);

public sealed record AtlasMap(Guid Id, string File, string Title)
{
    /// <summary>The level of the world of the map (R29): sky, surface or underdark.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public WorldLevel Level { get; init; }
}

/// <summary>Axis-aligned parent area in pixels; the child uses its full canvas.</summary>
public sealed record AtlasTransform(double X, double Y, double Width, double Height, double ChildWidth, double ChildHeight)
{
    public (double X, double Y) ToChild(double x, double y) => ((x - X) * ChildWidth / Width, (y - Y) * ChildHeight / Height);
    public (double X, double Y) ToParent(double x, double y) => (X + x * Width / ChildWidth, Y + y * Height / ChildHeight);
    public bool IsValid => new[] { X, Y, Width, Height, ChildWidth, ChildHeight }.All(double.IsFinite)
        && Width > 0 && Height > 0 && ChildWidth > 0 && ChildHeight > 0;
}

public sealed record AtlasLink(Guid Parent, Guid Child, AtlasTransform Transform, string Kind);

/// <summary>Separate versioned manifest; documents remain independently readable.</summary>
public sealed record MapAtlas(Guid Id, ImmutableArray<AtlasMap> Maps, ImmutableArray<AtlasLink> Links)
{
    public string Format { get; init; } = "map-atlas";
    public int Version { get; init; } = 1;

    public void Validate()
    {
        if (Format != "map-atlas" || Version != 1 || Id == Guid.Empty || Maps.IsDefault || Links.IsDefault
            || Maps.Length is < 1 or > 4096 || Links.Length > 4096
            || Maps.Any(m => m is null || m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.File) || string.IsNullOrWhiteSpace(m.Title) || !Enum.IsDefined(m.Level))
            || Maps.Select(m => m.Id).Distinct().Count() != Maps.Length
            || Maps.Select(m => m.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Maps.Length)
            throw new InvalidOperationException("Неверный формат атласа или повторяющиеся карты.");
        var parents = new Dictionary<Guid, Guid>();
        var ids = Maps.Select(m => m.Id).ToHashSet();
        foreach (var link in Links)
        {
            if (link is null || link.Parent == link.Child || !ids.Contains(link.Parent) || !ids.Contains(link.Child)
                || link.Transform is null || !link.Transform.IsValid || link.Kind is not ("region" or "hex")
                || !parents.TryAdd(link.Child, link.Parent))
                throw new InvalidOperationException("Неверная связь или несколько родителей подкарты.");
        }
        foreach (var id in ids)
        {
            var seen = new HashSet<Guid>(); var current = id;
            while (parents.TryGetValue(current, out var parent))
            {
                if (!seen.Add(current)) throw new InvalidOperationException("В атласе найден круг родителей.");
                current = parent;
            }
        }
    }

    public ImmutableArray<AtlasMap> Breadcrumbs(Guid map)
    {
        Validate();
        var result = new List<AtlasMap>();
        while (Maps.FirstOrDefault(m => m.Id == map) is { } item)
        {
            result.Add(item);
            if (Links.FirstOrDefault(l => l.Child == map) is not { } link) break;
            map = link.Parent;
        }
        result.Reverse(); return [.. result];
    }
}
