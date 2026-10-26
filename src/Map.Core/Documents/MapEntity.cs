using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Map.Core.Documents;

/// <summary>What an entity of the world is (R26).</summary>
public enum EntityKind { Place, River, Region, Character, Faction, Item, Other }

/// <summary>What an entity is bound to on a map.</summary>
public enum EntityTargetKind { Object, River, Road, Note, Hex, Point, Area }

/// <summary>
/// One place of an entity on a map (R26): an object, a river, a road, a note, a hex (q, r in X, Y)
/// or a bare point. <see cref="MapId"/> null is this map; another id is a map of the atlas, so one
/// entity stands on the painted map and on the hex map alike.
/// </summary>
public sealed record EntityBinding(Guid? MapId, EntityTargetKind Kind, Guid TargetId, double X, double Y)
{
    public bool IsValid => Enum.IsDefined(Kind) && double.IsFinite(X) && double.IsFinite(Y)
        && (Kind is EntityTargetKind.Point or EntityTargetKind.Hex || TargetId != Guid.Empty);
}

/// <summary>
/// A card of the world (R26): a town, a river, a character, a faction - its name, kind, a Markdown
/// text with [[Имя]] links to other cards, the places it is bound to on this and other maps of the
/// atlas, and whether the players may see it. Kept with the map (schema v36).
/// </summary>
public sealed partial record MapEntity
{
    public const int MaxNameLength = 120;
    public const int MaxBindings = 256;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Сущность";
    public EntityKind Kind { get; init; } = EntityKind.Place;
    public string Markdown { get; init; } = "";
    public ImmutableArray<EntityBinding> Bindings { get; init; } = [];

    /// <summary>The players may see the card (exports for players include it); otherwise it is the game master's.</summary>
    public bool PlayerVisible { get; init; }

    [GeneratedRegex(@"\[\[([^\[\]\r\n]{1,120})\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex WikiLink();

    /// <summary>The names this card links to with [[Имя]], in order of appearance.</summary>
    public IEnumerable<string> LinkedNames() => WikiLink().Matches(Markdown).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > MaxNameLength || !Enum.IsDefined(Kind)
            || Markdown is null || Bindings.IsDefault || Bindings.Length > MaxBindings || Bindings.Any(b => b is null || !b.IsValid))
            throw new ArgumentException("Некорректная карточка сущности.");
        NoteMarkdown.Validate(Markdown);
    }
}

/// <summary>Search and links over the cards of a map (R26).</summary>
public static class MapEntities
{
    /// <summary>The cards whose name, kind or text contain the query (case-insensitive); all when the query is empty.</summary>
    public static IEnumerable<MapEntity> Search(IReadOnlyList<MapEntity> entities, string? query)
    {
        var q = query?.Trim() ?? "";
        return q.Length == 0 ? entities
            : entities.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Markdown.Contains(q, StringComparison.OrdinalIgnoreCase)
                || KindTitle(e.Kind).Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The cards that link to this one with [[its name]] (the backlinks).</summary>
    public static IEnumerable<MapEntity> Backlinks(IReadOnlyList<MapEntity> entities, MapEntity target) =>
        entities.Where(e => e.Id != target.Id && e.LinkedNames().Any(n => string.Equals(n, target.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The card a [[name]] link points to, or null when no card has that name.</summary>
    public static MapEntity? Resolve(IReadOnlyList<MapEntity> entities, string name) =>
        entities.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The cards bound to one target of this map.</summary>
    public static IEnumerable<MapEntity> BoundTo(IReadOnlyList<MapEntity> entities, Guid targetId) =>
        entities.Where(e => e.Bindings.Any(b => b.MapId is null && b.TargetId == targetId));

    public static string KindTitle(EntityKind kind) => kind switch
    {
        EntityKind.Place => "Место",
        EntityKind.River => "Река",
        EntityKind.Region => "Область",
        EntityKind.Character => "Персонаж",
        EntityKind.Faction => "Фракция",
        EntityKind.Item => "Предмет",
        _ => "Другое",
    };
}
