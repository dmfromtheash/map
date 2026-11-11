using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>
/// One named variant of the map (R09e): the look the map has in it - which layers are shown, the style of the drawing,
/// the light and the time of day, the political mode. The drawing itself (land, mountains, rivers, names, the fires of
/// the light) is one for every variant.
/// </summary>
public sealed record MapVariant
{
    public MapVariant(Guid id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name.Trim();
    }

    public Guid Id { get; init; }
    public string Name { get; init; }

    /// <summary>The layers hidden in this variant; every other layer is shown.</summary>
    public ImmutableArray<LayerId> Hidden { get; init; } = [];

    /// <summary>The style of the drawing, or null for a map that never had one.</summary>
    public MapStyle? Style { get; init; }

    /// <summary>The light without its fires (they belong to the drawing), or null for a map that never had light.</summary>
    public MapLighting? Lighting { get; init; }

    public bool PoliticalMode { get; init; }

    /// <summary>Two variants give the map the same look (the names and ids aside).</summary>
    public bool SameLook(MapVariant other) =>
        Hidden.SequenceEqual(other.Hidden) && Equals(Style, other.Style) && Equals(Lighting, other.Lighting)
        && PoliticalMode == other.PoliticalMode;

    public bool Equals(MapVariant? other) => other is not null && Id == other.Id && Name == other.Name && SameLook(other);

    public override int GetHashCode() => HashCode.Combine(Id, Name, PoliticalMode);
}

/// <summary>
/// The named variants of a map (R09e, schema v50) and the one shown now. The map's own look is always the look of the
/// active variant: the stored entry of the active variant is refreshed from the map when another variant is chosen,
/// when the map is saved and when its picture is drawn (<see cref="Editing.MapVariantEditing.Current"/>), so an edit
/// of the light, the style or the shown layers made while a variant is chosen belongs to that variant.
/// </summary>
public sealed record MapVariants
{
    public const int MaxCount = 12;
    public const int MaxNameLength = 60;

    public ImmutableArray<MapVariant> Items { get; init; } = [];

    /// <summary>The variant the map shows now, or null when none is chosen.</summary>
    public Guid? Active { get; init; }

    public bool IsDefault => Items.IsDefaultOrEmpty && Active is null;

    public MapVariant? Find(Guid id) => Items.IsDefaultOrEmpty ? null : Items.FirstOrDefault(v => v.Id == id);

    public void Validate()
    {
        var items = Items.IsDefault ? [] : Items;
        if (items.Length > MaxCount) throw new ArgumentException($"Вариантов карты не больше {MaxCount}.");
        if (items.Select(v => v.Id).Distinct().Count() != items.Length) throw new ArgumentException("Варианты карты повторяются.");
        if (items.Any(v => string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > MaxNameLength))
            throw new ArgumentException($"Имя варианта карты — от 1 до {MaxNameLength} знаков.");
        if (Active is { } active && items.All(v => v.Id != active)) throw new ArgumentException("Выбранного варианта карты нет в списке.");
    }

    public bool Equals(MapVariants? other) =>
        other is not null && Active == other.Active
        && (Items.IsDefault ? [] : Items).SequenceEqual(other.Items.IsDefault ? [] : other.Items);

    public override int GetHashCode() => HashCode.Combine(Active, Items.IsDefaultOrEmpty ? 0 : Items.Length);
}
