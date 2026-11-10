using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>R09d (DEC-094): a style pack sets the whole look of the map in one step that one Ctrl+Z takes back.</summary>
public sealed partial class EditorViewModel
{
    public static IReadOnlyList<MapStylePack> StylePacks => MapStylePack.Presets;

    /// <summary>Applies the pack; false when the map already looks so (nothing to undo).</summary>
    public bool ApplyStylePack(MapStylePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var before = Session.Document;
        var after = pack.ApplyTo(before);
        if (after.Layers.SequenceEqual(before.Layers) && after.Style == before.Style) return false;
        Session.Execute(new ApplyStylePackCommand(pack.Title, before, after));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        Raise(nameof(Style));
        StatusDetail = $"Пакет стиля «{pack.Title}»: стиль, обрывы, реки, дороги, море и подписи. Ctrl+Z вернёт прежний вид.";
        return true;
    }
}
