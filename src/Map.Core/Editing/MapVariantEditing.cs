using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// The named variants of a map (R09e): capture the map's look as a variant, put the map into a variant, make the
/// starting three «День», «Ночь» and «Политическая». Each change is one undo step (<see cref="MapVariantCommand"/>).
/// </summary>
public static class MapVariantEditing
{
    public const double DayTime = 0.0, NightTime = 1.0;

    /// <summary>The look the map has now, as a variant with the given id and name.</summary>
    public static MapVariant Capture(MapDocument document, Guid id, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new MapVariant(id, name)
        {
            Hidden = [.. document.Layers.Where(l => !l.Visible).Select(l => l.Id)],
            Style = document.Style,
            Lighting = document.Lighting is { } lighting ? lighting with { Sources = [] } : null,
            PoliticalMode = document.Timeline.PoliticalMode,
        };
    }

    /// <summary>The variants with the active one refreshed from the map: what is saved and what the pictures show.</summary>
    public static MapVariants Current(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var variants = document.Variants;
        if (variants.Active is not { } active || variants.Find(active) is not { } shown) return variants;
        var now = Capture(document, shown.Id, shown.Name);
        return now.SameLook(shown) ? variants
            : variants with { Items = [.. variants.Items.Select(v => v.Id == active ? now : v)] };
    }

    /// <summary>
    /// The map in the look of <paramref name="variant"/>: its shown layers (a layer made after the variant keeps how it
    /// is shown), style, light with the map's own fires, and political mode. Everything else stays as it is.
    /// </summary>
    public static MapDocument WithLook(MapDocument document, MapVariant variant)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(variant);
        var hidden = variant.Hidden.IsDefault ? [] : variant.Hidden.ToHashSet();
        var result = document;
        foreach (var layer in document.Layers)
        {
            var visible = !hidden.Contains(layer.Id);
            if (layer.Visible != visible) result = result.WithLayer(layer.Id, l => l with { Visible = visible });
        }
        var fires = document.Lighting?.Sources ?? [];
        var lighting = variant.Lighting is { } own ? own with { Sources = fires }
            : document.Lighting is { } current ? current with { Enabled = false } : null;
        return result with
        {
            Style = variant.Style,
            Lighting = lighting,
            Timeline = document.Timeline with { PoliticalMode = variant.PoliticalMode },
        };
    }

    /// <summary>The map shown in another variant; the variant left keeps the look the map had.</summary>
    public static MapDocument Switch(MapDocument document, Guid target)
    {
        ArgumentNullException.ThrowIfNull(document);
        var variants = Current(document);
        var next = variants.Find(target) ?? throw new ArgumentException("Такого варианта карты нет.", nameof(target));
        return WithLook(document, next) with { Variants = variants with { Active = target } };
    }

    /// <summary>The map's look kept as a new variant, which becomes the chosen one.</summary>
    public static MapDocument Add(MapDocument document, string name, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        var variants = Current(document);
        if (variants.Items.Length >= MapVariants.MaxCount)
            throw new InvalidOperationException($"Вариантов карты не больше {MapVariants.MaxCount}.");
        var added = variants with { Items = [.. variants.Items, Capture(document, id, Bounded(name))], Active = id };
        added.Validate();
        return document with { Variants = added };
    }

    /// <summary>
    /// The starting variants from the map's look: «День» (the style, light by day), «Ночь» (the style, light at night),
    /// «Политическая» (the political mode, no style and no light). The map is shown in «День».
    /// </summary>
    public static MapDocument AddStarters(MapDocument document, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newId);
        var variants = Current(document);
        if (variants.Items.Length + 3 > MapVariants.MaxCount)
            throw new InvalidOperationException($"Вариантов карты не больше {MapVariants.MaxCount}.");
        var look = Capture(document, Guid.Empty, "Вид");
        var light = look.Lighting ?? new MapLighting();
        var day = look with { Id = newId(), Name = "День", Lighting = light with { Enabled = true, TimeOfDay = DayTime }, PoliticalMode = false };
        var night = look with { Id = newId(), Name = "Ночь", Lighting = light with { Enabled = true, TimeOfDay = NightTime }, PoliticalMode = false };
        var political = look with
        {
            Id = newId(), Name = "Политическая", PoliticalMode = true,
            Style = look.Style is { } style ? style with { Enabled = false } : null,
            Lighting = look.Lighting is { } own ? own with { Enabled = false } : null,
        };
        var added = variants with { Items = [.. variants.Items, day, night, political], Active = day.Id };
        added.Validate();
        return WithLook(document, day) with { Variants = added };
    }

    public static MapDocument Rename(MapDocument document, Guid id, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        var variants = document.Variants;
        if (variants.Find(id) is null) throw new ArgumentException("Такого варианта карты нет.", nameof(id));
        var renamed = variants with { Items = [.. variants.Items.Select(v => v.Id == id ? v with { Name = Bounded(name) } : v)] };
        renamed.Validate();
        return document with { Variants = renamed };
    }

    /// <summary>The variant removed; the map keeps the look it has. Removing the chosen one leaves none chosen.</summary>
    public static MapDocument Remove(MapDocument document, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        var variants = document.Variants;
        if (variants.Find(id) is null) throw new ArgumentException("Такого варианта карты нет.", nameof(id));
        return document with
        {
            Variants = new MapVariants
            {
                Items = [.. variants.Items.Where(v => v.Id != id)],
                Active = variants.Active == id ? null : variants.Active,
            },
        };
    }

    private static string Bounded(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        return trimmed.Length <= MapVariants.MaxNameLength ? trimmed : trimmed[..MapVariants.MaxNameLength];
    }
}

/// <summary>
/// One change of the variants of a map as one undo step: the shown layers, the style, the light, the political mode and
/// the list of variants before and after. The rest of the map - its drawing, history and versions - stays as it is.
/// </summary>
public sealed class MapVariantCommand(string name, MapDocument before, MapDocument after) : IEditCommand
{
    public string Name { get; } = name;

    public MapDocument Apply(MapDocument document) => Restore(document, after);

    public MapDocument Revert(MapDocument document) => Restore(document, before);

    private static MapDocument Restore(MapDocument document, MapDocument state)
    {
        var shown = state.Layers.ToDictionary(l => l.Id, l => l.Visible);
        var result = document;
        foreach (var layer in document.Layers)
            if (shown.TryGetValue(layer.Id, out var visible) && layer.Visible != visible)
                result = result.WithLayer(layer.Id, l => l with { Visible = visible });
        return result with
        {
            Style = state.Style,
            Lighting = state.Lighting,
            Timeline = result.Timeline with { PoliticalMode = state.Timeline.PoliticalMode },
            Variants = state.Variants,
        };
    }
}
