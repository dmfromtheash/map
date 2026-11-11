using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R09e: the named variants of the map («День», «Ночь», «Политическая» and the owner's own). A click shows the map in a
/// variant; while it is chosen, the light, the style and the shown layers set on the map are its own (owner, 2026-11-11:
/// «Вариант помнит правки сам»). Every change is one undo step; the variants live in the map's file.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>The variants of the map, the chosen one with the look the map has now.</summary>
    public MapVariants MapVariants => MapVariantEditing.Current(Session.Document);

    /// <summary>The map as a picture of a variant shows it (the political look included).</summary>
    public static MapDocument VariantPreview(MapDocument document, Guid id)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (MapVariantEditing.Current(document).Find(id) is not { } variant) return document;
        var look = MapVariantEditing.WithLook(document, variant);
        return look.Timeline.PoliticalMode ? TimelineView.At(look, look.Timeline.Enabled ? look.Timeline.Year : null, true) : look;
    }

    /// <summary>Shows the map in the variant; false when it is already shown.</summary>
    public bool SwitchMapVariant(Guid id)
    {
        if (Session.Document.Variants.Active == id || Session.Document.Variants.Find(id) is not { } variant) return false;
        if (!ExecuteVariants("Вариант «" + variant.Name + "»", MapVariantEditing.Switch(Session.Document, id))) return false;
        StatusDetail = $"Вариант «{variant.Name}»: слои, стиль, свет и политический режим этого варианта. Ctrl+Z вернёт прежний вид.";
        return true;
    }

    /// <summary>Keeps the look of the map as a new variant, which becomes the chosen one.</summary>
    public bool AddMapVariant(string? name = null)
    {
        if (Session.Document.Variants.Items.Length >= MapVariants.MaxCount)
        {
            StatusDetail = $"Вариантов карты не больше {MapVariants.MaxCount}.";
            return false;
        }
        name = string.IsNullOrWhiteSpace(name) ? NextVariantName() : name;
        if (!ExecuteVariants("Новый вариант «" + name.Trim() + "»", MapVariantEditing.Add(Session.Document, name, Guid.NewGuid()))) return false;
        StatusDetail = $"Вид карты запомнен как вариант «{name.Trim()}».";
        return true;
    }

    /// <summary>Adds «День», «Ночь», «Зима» and «Политическая» made from the look of the map, and shows «День».</summary>
    public bool AddStarterMapVariants()
    {
        if (Session.Document.Variants.Items.Length + 3 > MapVariants.MaxCount)
        {
            StatusDetail = $"Вариантов карты не больше {MapVariants.MaxCount}.";
            return false;
        }
        if (!ExecuteVariants("Варианты «День», «Ночь», «Зима», «Политическая»", MapVariantEditing.AddStarters(Session.Document, Guid.NewGuid))) return false;
        StatusDetail = "Добавлены варианты «День», «Ночь», «Зима» и «Политическая»; показан «День».";
        return true;
    }

    public bool RenameMapVariant(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Session.Document.Variants.Find(id) is not { } variant || variant.Name == name.Trim()) return false;
        return ExecuteVariants("Имя варианта «" + name.Trim() + "»", MapVariantEditing.Rename(Session.Document, id, name));
    }

    public bool RemoveMapVariant(Guid id)
    {
        if (Session.Document.Variants.Find(id) is not { } variant) return false;
        if (!ExecuteVariants("Удалён вариант «" + variant.Name + "»", MapVariantEditing.Remove(Session.Document, id))) return false;
        StatusDetail = $"Вариант «{variant.Name}» удалён; карта осталась в прежнем виде. Ctrl+Z вернёт вариант.";
        return true;
    }

    private string NextVariantName()
    {
        var names = Session.Document.Variants.Items.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++) if (!names.Contains("Вариант " + n)) return "Вариант " + n;
    }

    private bool ExecuteVariants(string name, MapDocument after)
    {
        var before = Session.Document;
        if (after.Variants.Equals(before.Variants) && ReferenceEquals(after.Layers, before.Layers)
            && Equals(after.Style, before.Style) && Equals(after.Lighting, before.Lighting) && after.Timeline == before.Timeline) return false;
        Session.Execute(new MapVariantCommand(name, before, after));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        Raise(nameof(Style));
        Raise(nameof(Lighting));
        Raise(nameof(Timeline));
        Raise(nameof(MapVariants));
        return true;
    }
}
